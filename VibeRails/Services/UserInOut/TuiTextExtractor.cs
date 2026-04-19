using FuzzySharp;
using VibeRails.Services.Terminal;

namespace VibeRails.Services.UserInOut;

public sealed class TuiTextExtractor : ITuiTextExtractor
{
    private const int MinInputLength = 5;
    private const int FuzzyThreshold = 70;
    private const int PromptFuzzyThreshold = 75;
    private static readonly char[] LineTrimChars = ['\r', '\0', ' ', '\t'];
    // `› ` (U+203A) is Claude Code's submitted-prompt glyph; ASCII variants handle shells.
    private static readonly string[] PromptMarkers = ["› ", "> ", "$ ", "# ", "% ", ">>> "];

    public string Extract(string rawInput, string tuiText)
    {
        if (string.IsNullOrWhiteSpace(rawInput) || string.IsNullOrWhiteSpace(tuiText))
            return rawInput;

        var needle = rawInput.Trim();
        if (needle.Length < MinInputLength)
            return rawInput;

        var stripped = TerminalTextSanitizer.ToPlainText(tuiText);

        // Prefer content after a prompt marker — that's what the user actually submitted,
        // as opposed to autocomplete dropdowns, keystroke echoes, or status regions that
        // happen to contain the same substrings. Prompt detection runs first because TUIs
        // (Claude Code in particular) render the finalized submission on the same sanitized
        // line as neighboring decoration, which fools a plain "line contains needle" match.
        return TryPromptLine(needle, stripped)
            ?? TryContainingLine(needle, stripped)
            ?? TryContainingLine(needle, tuiText)
            ?? TryFuzzyLine(needle, stripped)
            ?? rawInput;
    }

    private static string? TryPromptLine(string needle, string tui)
    {
        string? best = null;
        var bestScore = PromptFuzzyThreshold;

        foreach (var raw in tui.Split('\n'))
        {
            var line = raw.Trim(LineTrimChars);
            if (line.Length < MinInputLength)
                continue;

            // Each line may have several prompt markers; the LAST one wins because
            // that's the most recent submission / the one not shadowed by an echo region.
            foreach (var marker in PromptMarkers)
            {
                var idx = line.LastIndexOf(marker, StringComparison.Ordinal);
                if (idx < 0)
                    continue;

                var candidate = line[(idx + marker.Length)..].TrimEnd(LineTrimChars);
                if (candidate.Length < MinInputLength)
                    continue;

                var score = Fuzz.PartialRatio(needle, candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
        }

        return best;
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
