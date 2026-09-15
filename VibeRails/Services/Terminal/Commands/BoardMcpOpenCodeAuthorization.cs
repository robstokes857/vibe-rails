using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeRails.Services.Terminal;

/// <summary>Session-only Board grants for OpenCode and its pinned-model launchers.</summary>
public static class BoardMcpOpenCodeAuthorization
{
    public const string PermissionVariable = "OPENCODE_PERMISSION";

    public static void Apply(LLM llm, Dictionary<string, string> environment)
    {
        if (llm is not (LLM.OpenCode or LLM.Glm52 or LLM.Glm53 or LLM.DeepSeekV4Pro or LLM.KimiK3))
            return;

        var current = environment.TryGetValue(PermissionVariable, out var configured)
            ? configured : Environment.GetEnvironmentVariable(PermissionVariable);
        environment[PermissionVariable] = MergePermissionRules(current);
    }

    internal static string MergePermissionRules(string? current)
    {
        var permissions = ParsePermissions(current);
        // OpenCode creates MCP keys as sanitize(server) + "_" + sanitize(tool), where
        // sanitize replaces [^a-zA-Z0-9_-]. Both fixed identifiers already satisfy it.
        // OPENCODE_PERMISSION merges separately from OPENCODE_CONFIG_CONTENT, retaining
        // the user's provider/proxy config and every unrelated permission entry.
        foreach (var tool in BoardMcpAuthorization.ToolNames)
        {
            var name = BoardMcpAuthorization.ServerName + "_" + tool;
            if (permissions.Any(rule => MatchesPermission(rule.Key, name)
                && ContainsDeny(rule.Value)))
                continue;

            // Rule evaluation uses the last match. Move a preexisting Board ask entry
            // to the end so only this exact tool overrides an inherited ask default.
            permissions.Remove(name);
            permissions.Add(name, "allow");
        }
        return permissions.ToJsonString();
    }

    private static JsonObject ParsePermissions(string? current)
    {
        if (string.IsNullOrWhiteSpace(current)) return new JsonObject();
        try
        {
            if (JsonNode.Parse(current) is not JsonObject permissions)
                throw new JsonException();
            foreach (var rule in permissions)
            {
                if (string.IsNullOrEmpty(rule.Key)) throw new JsonException();
                if (rule.Value is JsonObject patterns)
                {
                    foreach (var pattern in patterns)
                        if (!IsAction(pattern.Value)) throw new JsonException();
                }
                else if (!IsAction(rule.Value)) throw new JsonException();
            }
            return permissions;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            // Never include config bytes in a launch error; they may contain private paths.
            throw new ArgumentException("The OpenCode permission environment is invalid. Correct OPENCODE_PERMISSION before launching a Board session.");
        }
    }

    private static bool IsAction(JsonNode? value) => value is JsonValue action
        && action.TryGetValue<string>(out var text) && text is "allow" or "ask" or "deny";

    private static bool ContainsDeny(JsonNode? value) => value is JsonObject patterns
        ? patterns.Any(pattern => ContainsDeny(pattern.Value))
        : value is JsonValue action && action.TryGetValue<string>(out var text) && text == "deny";

    private static bool MatchesPermission(string pattern, string name)
    {
        // OpenCode's wildcard matcher normalizes slashes, ignores case, supports * / ?,
        // and makes a trailing " *" optional (the same matcher handles shell rules).
        pattern = pattern.Replace('\\', '/');
        return FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)
            || (pattern.EndsWith(" *", StringComparison.Ordinal)
                && FileSystemName.MatchesSimpleExpression(pattern.AsSpan(0, pattern.Length - 2), name, ignoreCase: true));
    }
}
