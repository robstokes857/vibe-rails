namespace VibeRails.Services.Board;

/// <summary>
/// The one place that turns text into a token count for Board context reporting (VB-63).
/// Nothing in this repository tokenizes for Claude, Codex or the others, so this is the
/// industry rule of thumb, four characters per token, rounded up. English prose lands close;
/// code and dense Markdown run about three characters per token, so the estimate is a floor
/// there. Every surface that shows the number labels it "≈". Swap the rule here, not at the
/// call sites, when a real tokenizer becomes available.
/// </summary>
public static class ContextTokenEstimator
{
    /// <summary>How the estimate was produced; stored with each sample so old numbers stay interpretable.</summary>
    public const string Method = "chars/4";

    public const int CharactersPerToken = 4;

    public static int Estimate(string? text) => text is null ? 0 : FromCharacters(text.Length);

    public static int FromCharacters(long characters) =>
        characters <= 0 ? 0 : (int)Math.Min(int.MaxValue, (characters + CharactersPerToken - 1) / CharactersPerToken);
}
