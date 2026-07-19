namespace VibeRails.Services;

public interface ILlmParser
{
    IReadOnlyList<LLM> All { get; }
    LLM Parse(string? value);
    string Normalize(string? value);
}

public sealed class LlmParser : ILlmParser
{
    private static readonly IReadOnlyList<LLM> AllValues =
        Enum.GetValues<LLM>().Where(llm => llm != LLM.NotSet).ToList().AsReadOnly();

    // C# enum names can't contain hyphens or periods, so the pseudo-CLI strings
    // "glm-5.2" and "kimi-k3" can't round-trip through Enum.TryParse. Map them
    // explicitly here. Both launch `opencode` with a pinned --model flag (see
    // CommandService.PrepareSession).
    private static readonly Dictionary<string, LLM> SpecialCaseMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["glm-5.2"] = LLM.Glm52,
        ["kimi-k3"] = LLM.KimiK3
    };

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

    public string Normalize(string? value)
    {
        var result = Parse(value);
        if (result == LLM.NotSet)
            return string.Empty;

        // Reverse-lookup the special-case strings so Normalize("glm-5.2") returns
        // "glm-5.2" (not "Glm52"), preserving the wire format callers expect.
        foreach (var (key, mapped) in SpecialCaseMap)
        {
            if (mapped == result)
                return key;
        }

        return result.ToString();
    }
}
