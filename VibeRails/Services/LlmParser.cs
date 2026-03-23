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

    public IReadOnlyList<LLM> All => AllValues;

    public LLM Parse(string? value)
    {
        if (Enum.TryParse<LLM>(value?.Trim(), ignoreCase: true, out var result) && result != LLM.NotSet)
            return result;

        return LLM.NotSet;
    }

    public string Normalize(string? value)
    {
        var result = Parse(value);
        return result != LLM.NotSet ? result.ToString() : string.Empty;
    }
}
