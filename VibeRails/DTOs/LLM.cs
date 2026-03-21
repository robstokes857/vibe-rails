namespace VibeRails.Services
{
    public enum LLM
    {
        NotSet,
        Codex,
        Claude,
        Gemini,
        Copilot
    }

    public static class LLMParser
    {
        public static readonly IReadOnlyList<LLM> All =
            Enum.GetValues<LLM>().Where(l => l != LLM.NotSet).ToList().AsReadOnly();

        public static LLM Parse(string? value)
        {
            if (Enum.TryParse<LLM>(value?.Trim(), ignoreCase: true, out var result) && result != LLM.NotSet)
                return result;
            return LLM.NotSet;
        }

        public static string Normalize(string? value)
        {
            var result = Parse(value);
            return result != LLM.NotSet ? result.ToString() : string.Empty;
        }
    }
}
