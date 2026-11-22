using System.Text;
using System.Text.Json;
using Serilog;
using VibeRails.Interfaces;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Session-only Board MCP grants for Antigravity (<c>agy</c>) via <c>--settings</c>.
/// Because <c>agy --settings</c> replaces <c>permissions.allow</c> (while preserving
/// <c>permissions.deny</c>, <c>permissions.ask</c> and other settings keys from
/// <c>~/.gemini/antigravity-cli/settings.json</c>), this helper performs a read-only merge of
/// the user's existing allow entries with the exact reviewed Board tools and never writes to disk.
/// </summary>
public static class BoardMcpAntigravityAuthorization
{
    internal const int MaxSettingsChars = 1024 * 1024;
    internal const int MaxSerializedSettingsChars = 8 * 1024;

    public static async Task<string[]> AppendArgumentsAsync(
        LLM llm,
        string[] arguments,
        IFileService fileService,
        CancellationToken cancellationToken = default)
    {
        if (llm != LLM.Antigravity || HasExplicitSettingsFlag(arguments))
            return arguments;

        var userProfile = fileService.GetUserProfilePath();
        if (string.IsNullOrWhiteSpace(userProfile))
            return arguments;

        var settingsPath = ResolveUserSettingsPath(userProfile);
        string? existingJson = null;
        try
        {
            if (fileService.FileExists(settingsPath))
                existingJson = await fileService.ReadAllTextAsync(settingsPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "[Antigravity] Could not read user settings.json; skipping session-only Board MCP --settings grants");
            return arguments;
        }

        if (!TryBuildSettingsOverride(existingJson, out var settingsJson))
        {
            Log.Warning("[Antigravity] Skipping session-only Board MCP --settings grants because user settings.json could not be safely merged");
            return arguments;
        }

        var result = new List<string>(arguments.Length + 2);
        result.AddRange(arguments);
        var delimiter = result.IndexOf("--");
        result.InsertRange(delimiter < 0 ? result.Count : delimiter, ["--settings", settingsJson]);
        return result.ToArray();
    }

    internal static string ResolveUserSettingsPath(string userProfilePath) =>
        Path.Combine(userProfilePath, ".gemini", "antigravity-cli", "settings.json");

    internal static bool HasExplicitSettingsFlag(string[] arguments)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            var arg = arguments[i];
            if (arg == "--")
                break;
            if (arg is "--settings" or "-settings"
                || arg.StartsWith("--settings=", StringComparison.Ordinal)
                || arg.StartsWith("-settings=", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static bool TryBuildSettingsOverride(string? existingSettingsJson, out string settingsJson)
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
