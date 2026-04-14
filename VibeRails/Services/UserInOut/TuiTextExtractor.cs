using FuzzySharp;
using VibeRails.Services.Terminal;

namespace VibeRails.Services.UserInOut;

public sealed class TuiTextExtractor : ITuiTextExtractor
{
    private const int MinInputLength = 5;
    private const int FuzzyThreshold = 70;
    private static readonly char[] LineTrimChars = ['\r', '\0', ' ', '\t'];

    public string Extract(string rawInput, string tuiText)
    {
        if (string.IsNullOrWhiteSpace(rawInput) || string.IsNullOrWhiteSpace(tuiText))
            return rawInput;

        var needle = rawInput.Trim();
        if (needle.Length < MinInputLength)
            return rawInput;

        var stripped = TerminalTextSanitizer.ToPlainText(tuiText);

        return TryContainingLine(needle, stripped)
            ?? TryContainingLine(needle, tuiText)
            ?? TryFuzzyLine(needle, stripped)
            ?? rawInput;
    }

    private static string? TryContainingLine(string needle, string tui)
    {
        string? best = null;
        foreach (var raw in tui.Split('\n'))
        {
            var line = raw.Trim(LineTrimChars);
            if (line.Length <= needle.Length)
                continue;
            if (!line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            if (best is null || line.Length > best.Length)
                best = line;
        }
        return best;
    }

    private static string? TryFuzzyLine(string needle, string tui)
    {
        string? best = null;
        var bestScore = FuzzyThreshold;
        foreach (var raw in tui.Split('\n'))
        {
            var line = raw.Trim(LineTrimChars);
            if (line.Length < MinInputLength)
                continue;

            var score = Fuzz.PartialRatio(needle, line);
            if (score > bestScore)
            {
                bestScore = score;
                best = line;
            }
        }
        return best;
    }
}
