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

    // C# enum names can't contain hyphens or periods, so the pseudo-CLI strings
    // "glm-5.2" / "grok-4.6" can't round-trip through Enum.TryParse. Map them
    // explicitly here. Both launch `opencode` with a pinned --model flag (see
    // CommandService.PrepareSession).
    private static readonly Dictionary<string, LLM> SpecialCaseMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["glm-5.2"] = LLM.Glm52,
        ["grok-4.6"] = LLM.Grok46
    };

    private static readonly Dictionary<LLM, string> SpecialCaseWireNames =
        SpecialCaseMap.ToDictionary(pair => pair.Value, pair => pair.Key);

    public IReadOnlyList<LLM> All => AllValues;

    public LLM Parse(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return LLM.NotSet;

        if (SpecialCaseMap.TryGetValue(trimmed, out var special))
            return special;

        if (Enum.TryParse<LLM>(trimmed, ignoreCase: true, out var result) && result != LLM.NotSet)
            return result;

        return LLM.NotSet;
    }

    public string Normalize(string? value) => ToWireName(Parse(value));

    public string Normalize(LLM llm) => ToWireName(llm);

    // Reverse of Parse: maps an LLM enum back to the exact wire string the frontend keys on —
    // "glm-5.2" / "grok-4.6" for the pseudo-CLIs, the canonical enum name otherwise. Every outbound
    // boundary (session Cli persistence, PublishSessionStart, ActiveCli, parent-cli links, the
    // environments API) must round-trip through here so wire names match what Parse accepts and what
    // the frontend filter/brand maps expect. Static so callers without ILlmParser injected can use it.
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
