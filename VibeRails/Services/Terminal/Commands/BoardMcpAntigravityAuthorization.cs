using System.Text;
using System.Text.Json;
using Serilog;
using VibeRails.Interfaces;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Session-only <c>--settings</c> overrides for Antigravity (<c>agy</c>): Board MCP tool grants
/// and Gemini API key provider selection (<c>modelProvider: "gemini"</c>).
/// Because <c>agy --settings</c> replaces <c>permissions.allow</c> (while preserving
/// <c>permissions.deny</c>, <c>permissions.ask</c> and other settings keys from
/// <c>~/.gemini/antigravity-cli/settings.json</c>), this helper performs a read-only merge of
/// the user's existing allow entries with the exact reviewed Board tools and never writes to disk.
/// </summary>
public static class BoardMcpAntigravityAuthorization
{
    internal const string GeminiApiKeyVariable = "GEMINI_API_KEY";
    internal const string GeminiModelProvider = "gemini";
    internal const int MaxSettingsChars = 1024 * 1024;
    internal const int MaxSerializedSettingsChars = 8 * 1024;

    public static Task<string[]> AppendArgumentsAsync(
        LLM llm,
        string[] arguments,
        IFileService fileService,
        CancellationToken cancellationToken = default) =>
        AppendSessionSettingsAsync(llm, arguments, fileService, authorizeBoardTools: true, useGeminiApiKey: false, cancellationToken);

    public static async Task<string[]> AppendSessionSettingsAsync(
        LLM llm,
        string[] arguments,
        IFileService fileService,
        bool authorizeBoardTools,
        bool useGeminiApiKey,
        CancellationToken cancellationToken = default)
    {
        if (llm != LLM.Antigravity || (!authorizeBoardTools && !useGeminiApiKey))
            return arguments;

        if (HasExplicitSettingsFlag(arguments))
        {
            return useGeminiApiKey
                ? MergeModelProviderIntoExplicitSettings(arguments, GeminiModelProvider)
                : arguments;
        }

        var modelProvider = useGeminiApiKey ? GeminiModelProvider : null;
        string? settingsJson = null;

        if (authorizeBoardTools)
        {
            var userProfile = fileService.GetUserProfilePath();
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                var settingsPath = ResolveUserSettingsPath(userProfile);
                string? existingJson = null;
                var readFailed = false;
                try
                {
                    if (fileService.FileExists(settingsPath))
                        existingJson = await fileService.ReadAllTextAsync(settingsPath, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "[Antigravity] Could not read user settings.json; skipping session-only Board MCP --settings grants");
                    readFailed = true;
                }

                if (!readFailed && !TryBuildSettingsOverride(existingJson, out settingsJson, modelProvider))
                {
                    Log.Warning("[Antigravity] Skipping session-only Board MCP --settings grants because user settings.json could not be safely merged");
                    settingsJson = null;
                }
            }
        }

        // Even when Board grants are absent or fail closed, keep session-only Gemini API key routing.
        if (string.IsNullOrEmpty(settingsJson) && useGeminiApiKey)
            settingsJson = BuildModelProviderOnlySettings(GeminiModelProvider);

        if (string.IsNullOrEmpty(settingsJson))
            return arguments;

        var result = new List<string>(arguments.Length + 2);
        result.AddRange(arguments);
        var delimiter = result.IndexOf("--");
        result.InsertRange(delimiter < 0 ? result.Count : delimiter, ["--settings", settingsJson]);
        return result.ToArray();
    }

    internal static string ResolveUserSettingsPath(string userProfilePath) =>
        Path.Combine(userProfilePath, ".gemini", "antigravity-cli", "settings.json");

    internal static bool HasExplicitSettingsFlag(string[] arguments) =>
        FindExplicitSettingsFlag(arguments).FlagIndex >= 0;

    private static (int FlagIndex, int ValueIndex, string? Prefix, string? Value) FindExplicitSettingsFlag(string[] arguments)
    {
        // agy uses Go's flag package, where repeated --settings flags take the last occurrence.
        var last = (-1, -1, (string?)null, (string?)null);
        for (var i = 0; i < arguments.Length; i++)
        {
            var arg = arguments[i];
            if (arg == "--")
                break;
            if (arg is "--settings" or "-settings")
            {
                if (i + 1 < arguments.Length)
                {
                    last = (i, i + 1, null, arguments[i + 1]);
                    i++;
                }
                else
                {
                    last = (i, -1, null, null);
                }
                continue;
            }
            if (arg.StartsWith("--settings=", StringComparison.Ordinal))
                last = (i, i, "--settings=", arg["--settings=".Length..]);
            else if (arg.StartsWith("-settings=", StringComparison.Ordinal))
                last = (i, i, "-settings=", arg["-settings=".Length..]);
        }
        return last;
    }

    private static string[] MergeModelProviderIntoExplicitSettings(string[] arguments, string modelProvider)
    {
        var (_, valueIndex, prefix, rawValue) = FindExplicitSettingsFlag(arguments);
        if (valueIndex < 0 || string.IsNullOrWhiteSpace(rawValue))
            return arguments;

        var trimmed = rawValue.TrimStart('\uFEFF').Trim();
        if (!trimmed.StartsWith('{') || trimmed.Length > MaxSerializedSettingsChars)
            return arguments;

        try
        {
            // Use JsonDocument + Utf8JsonWriter rather than JsonNode so duplicate JSON properties
            // (valid JSON accepted by Go's encoding/json with last-wins semantics) never throw
            // ArgumentException during launch preparation.
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return arguments;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("modelProvider"))
                    return arguments;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                    property.WriteTo(writer);
                writer.WriteString("modelProvider", modelProvider);
                writer.WriteEndObject();
            }

            var merged = Encoding.UTF8.GetString(buffer.ToArray());
            if (merged.Length > MaxSerializedSettingsChars)
                return arguments;

            var updated = (string[])arguments.Clone();
            updated[valueIndex] = prefix is null ? merged : prefix + merged;
            return updated;
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    private static string BuildModelProviderOnlySettings(string modelProvider)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("modelProvider", modelProvider);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static bool TryBuildSettingsOverride(string? existingSettingsJson, out string settingsJson) =>
        TryBuildSettingsOverride(existingSettingsJson, out settingsJson, modelProvider: null);

    internal static bool TryBuildSettingsOverride(string? existingSettingsJson, out string settingsJson, string? modelProvider)
    {
        settingsJson = string.Empty;
        var mergedAllows = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(existingSettingsJson))
        {
            if (existingSettingsJson.Length > MaxSettingsChars)
                return false;

            var trimmed = existingSettingsJson.TrimStart('\uFEFF');
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        return false;

                    if (document.RootElement.TryGetProperty("permissions", out var permissions)
                        && permissions.ValueKind != JsonValueKind.Null)
                    {
                        if (permissions.ValueKind != JsonValueKind.Object)
                            return false;

                        if (permissions.TryGetProperty("allow", out var allow)
                            && allow.ValueKind != JsonValueKind.Null)
                        {
                            if (allow.ValueKind != JsonValueKind.Array)
                                return false;

                            foreach (var entry in allow.EnumerateArray())
                            {
                                if (entry.ValueKind != JsonValueKind.String)
                                    return false;
                                var rule = entry.GetString()!;
                                if (seen.Add(rule))
                                    mergedAllows.Add(rule);
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    return false;
                }
            }
        }

        foreach (var tool in BoardMcpAuthorization.ToolNames)
        {
            var rule = $"mcp({BoardMcpAuthorization.ServerName}/{tool})";
            if (seen.Add(rule))
                mergedAllows.Add(rule);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(modelProvider))
                writer.WriteString("modelProvider", modelProvider);
            writer.WriteStartObject("permissions");
            writer.WriteStartArray("allow");
            foreach (var rule in mergedAllows)
                writer.WriteStringValue(rule);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var serialized = Encoding.UTF8.GetString(buffer.ToArray());
        if (serialized.Length > MaxSerializedSettingsChars)
            return false;

        settingsJson = serialized;
        return true;
    }
}
