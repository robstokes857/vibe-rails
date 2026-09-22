namespace VibeRails.Services;

public interface ILlmParser
{
    IReadOnlyList<LLM> All { get; }
    LLM Parse(string? value);
    string Normalize(string? value);
    string Normalize(LLM llm);
}

public sealed class LlmParser : ILlmParser
{
    private static readonly IReadOnlyList<LLM> AllValues =
        Enum.GetValues<LLM>().Where(llm => llm != LLM.NotSet).ToList().AsReadOnly();

    // C# enum names can't contain hyphens or periods, so the wire strings
    // "glm-5.2" / "grok" / "glm-5.3" / "deepseek-v4-pro" / "kimi-k3" can't round-trip
    // through Enum.TryParse ("grok" would, if the member were named Grok; it is Grok46).
    // Map them explicitly here. Glm52 / Glm53 / DeepSeekV4Pro / KimiK3 launch `opencode`
    // with a pinned --model flag; Grok46 launches the native `grok` binary
    // (see CommandService.PrepareSession). Model choice for Grok is a launch argument.
    private static readonly Dictionary<string, LLM> SpecialCaseMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["glm-5.2"] = LLM.Glm52,
        ["grok"] = LLM.Grok46,
        ["glm-5.3"] = LLM.Glm53,
        ["deepseek-v4-pro"] = LLM.DeepSeekV4Pro,
        ["kimi-k3"] = LLM.KimiK3
    };

    // Retired wire names that still parse. They are not emitted: ToWireName uses
    // SpecialCaseMap, so "grok-4.6" normalizes to "grok".
    private static readonly Dictionary<string, LLM> LegacyWireNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grok-4.6"] = LLM.Grok46
    };

    private static readonly Dictionary<LLM, string> SpecialCaseWireNames =
        SpecialCaseMap.ToDictionary(pair => pair.Value, pair => pair.Key);

    public IReadOnlyList<LLM> All => AllValues;

    public LLM Parse(string? value) => ParseValue(value);

    // Static counterpart of ToWireName below, for callers without an ILlmParser injected.
    // Comparing the parsed enum (== LLM.Codex) is the one sanctioned way to test a wire
    // string for a specific CLI — hand-rolled string compares drift when wire names change.
    public static LLM ParseValue(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return LLM.NotSet;

        if (SpecialCaseMap.TryGetValue(trimmed, out var special))
            return special;

        if (LegacyWireNames.TryGetValue(trimmed, out var legacy))
            return legacy;

        if (Enum.TryParse<LLM>(trimmed, ignoreCase: true, out var result) && result != LLM.NotSet)
            return result;

        return LLM.NotSet;
    }

    public string Normalize(string? value) => ToWireName(Parse(value));

    public string Normalize(LLM llm) => ToWireName(llm);

    // Reverse of Parse: maps an LLM enum back to the exact wire string the frontend keys on —
    // "glm-5.2" / "grok" / "glm-5.3" / "deepseek-v4-pro" / "kimi-k3" for the special-case
    // wire names, the canonical enum name otherwise. Every outbound boundary (session Cli persistence, PublishSessionStart,
    // ActiveCli, parent-cli links, the environments API) must round-trip through here so wire
    // names match what Parse accepts and what the frontend filter/brand maps expect. Static so
    // callers without ILlmParser injected can use it.
    public static string ToWireName(LLM llm)
    {
        if (llm == LLM.NotSet)
            return string.Empty;

        // Keep reverse lookup constant-time because this method sits on session and route
        // serialization boundaries.
        if (SpecialCaseWireNames.TryGetValue(llm, out var wireName))
            return wireName;

        return llm.ToString();
    }
}
