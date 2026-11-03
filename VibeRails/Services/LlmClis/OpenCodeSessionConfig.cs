using System.Text.Json;
using System.Text.Json.Nodes;
using TokenSaver;

namespace VibeRails.Services.LlmClis;

/// <summary>
/// OpenCode v2 adaptation for session options. No released v2 binary accepts the v1 TUI flags
/// <c>--model</c>, <c>--agent</c> or <c>--pure</c> on the root command (only the <c>run</c> and
/// <c>mini</c> subcommands kept them, and the current upstream docs' TUI flag table wrongly
/// copies those rows), and an unrecognized flag aborts the whole launch. The supported
/// launch-time mechanism is the <c>OPENCODE_CONFIG_CONTENT</c> env var — inline JSON that
/// OpenCode resolves as its highest-precedence config document — using the <c>model</c>
/// ("provider/model") and <c>default_agent</c> keys, plus the <c>OPENCODE_PURE</c> env var for a
/// plugins-off launch. VibeRails keeps its own <c>--model</c>/<c>--agent</c>/<c>--pure</c>
/// contract in <c>CustomArgs</c> and Board launch options; <see cref="CommandService"/> extracts
/// those flags from argv and re-expresses them here.
/// </summary>
internal static class OpenCodeSessionConfig
{
    /// <summary>Env var OpenCode reads to run without external plugins (no v2 flag equivalent).</summary>
    public const string PureVariable = "OPENCODE_PURE";

    /// <summary>
    /// Merges <paramref name="model"/> and <paramref name="defaultAgent"/> into
    /// <paramref name="existingContent"/>, the launch's current <c>OPENCODE_CONFIG_CONTENT</c>
    /// value (null when unset — the TokenSaver proxy payload or a custom environment's explicit
    /// value). Returns the merged JSON, or the original value when there is nothing to add.
    /// Keys already present are overwritten — VibeRails' own model/agent choice wins over an
    /// inherited one — while every other key is preserved verbatim. Content that does not parse
    /// as a JSON object is returned unchanged: an explicit value is respected rather than
    /// silently dropping the user's keys. Empty/whitespace model or agent values are skipped,
    /// which is how an explicit empty <c>--model=</c> restores OpenCode's own default.
    /// </summary>
    public static string? ApplySessionKeys(string? existingContent, string? model, string? defaultAgent)
    {
        var modelValue = model?.Trim();
        var agentValue = defaultAgent?.Trim();
        if (modelValue is not { Length: > 0 } && agentValue is not { Length: > 0 })
            return existingContent;

        JsonObject obj;
        if (string.IsNullOrWhiteSpace(existingContent))
        {
            obj = [];
        }
        else
        {
            try
            {
                if (JsonNode.Parse(existingContent) is not { } root || root is not JsonObject rootObject)
                    return existingContent;
                obj = rootObject;
            }
            catch (JsonException)
            {
                return existingContent;
            }
        }

        if (modelValue is { Length: > 0 })
            obj["model"] = modelValue;
        if (agentValue is { Length: > 0 })
            obj["default_agent"] = agentValue;
        return obj.ToJsonString();
    }
}
