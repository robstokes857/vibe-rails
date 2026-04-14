namespace VibeRails.Services.UserInOut;

/// <summary>
/// Matches accumulated input text against terminal screen lines to find the
/// TUI-painted version (which includes autocomplete, @file references, etc.).
/// All methods are pure — no I/O or DI.
/// </summary>
public static class ScreenTextMatcher
{
    private const int MinNeedleLength = 5;
    private const double JaccardThreshold = 0.6;
    private const int MaxConsecutiveJoin = 5;

    /// <summary>
    /// Given the accumulated (keyboard) input and current screen text lines,
    /// tries to find the TUI-painted version of the input. Returns the better
    /// text if found, otherwise null.
    /// </summary>
    public static string? TryMatch(string accumulatedInput, string[] screenLines)
    {
        if (screenLines.Length == 0)
            return null;

        var needle = InputEtlFilter.Normalize(accumulatedInput);
        if (needle.Length < MinNeedleLength)
            return null;

        string? bestMatch = null;

        // Walk bottom-to-top — user just pressed Enter, input is near the bottom
        for (int i = screenLines.Length - 1; i >= 0; i--)
        {
            var candidate = StripPrompt(screenLines[i]);
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (IsGoodMatch(needle, candidate))
            {
                if (bestMatch is null || candidate.Length > bestMatch.Length)
                    bestMatch = candidate;
                // Don't break — keep scanning for a longer (multi-line joined) match
            }

            // Try joining consecutive non-empty lines (handles wrapped/multi-line input)
            if (i > 0)
            {
                var joined = TryJoinConsecutive(screenLines, i, needle);
                if (joined is not null && (bestMatch is null || joined.Length > bestMatch.Length))
                    bestMatch = joined;
            }
        }

        // Only return if the match is actually better (longer or richer) than what we had
        if (bestMatch is not null && bestMatch.Length > needle.Length)
            return bestMatch;

        return null;
    }

    private static bool IsGoodMatch(string needle, string candidate)
    {
        // Containment: the screen line contains the accumulated text
        // and has more content (autocomplete, @file prefix, etc.)
        if (candidate.Length > needle.Length
            && candidate.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Token overlap (Jaccard similarity)
        var similarity = ComputeJaccard(needle, candidate);
        if (similarity >= JaccardThreshold && candidate.Length >= needle.Length)
            return true;

        return false;
    }

    private static string? TryJoinConsecutive(string[] screenLines, int startIndex, string needle)
    {
        var parts = new List<string>();
        var endIndex = Math.Max(0, startIndex - MaxConsecutiveJoin + 1);

        for (int j = startIndex; j >= endIndex; j--)
        {
            var line = StripPrompt(screenLines[j]);
            if (string.IsNullOrWhiteSpace(line))
                break; // Stop at blank line
            parts.Add(line);
        }

        if (parts.Count < 2)
            return null;

        // Reverse since we walked bottom-to-top
        parts.Reverse();
        var joined = string.Join(" ", parts);
        var normalizedJoined = InputEtlFilter.Normalize(joined);

        if (IsGoodMatch(needle, normalizedJoined))
            return normalizedJoined;

        return null;
    }

    private static double ComputeJaccard(string a, string b)
    {
        var tokensA = Tokenize(a);
        var tokensB = Tokenize(b);

        if (tokensA.Count == 0 && tokensB.Count == 0)
            return 1.0;
        if (tokensA.Count == 0 || tokensB.Count == 0)
            return 0.0;

        var intersection = tokensA.Intersect(tokensB, StringComparer.OrdinalIgnoreCase).Count();
        var union = tokensA.Union(tokensB, StringComparer.OrdinalIgnoreCase).Count();

        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string StripPrompt(string line)
    {
        return InputEtlFilter.Normalize(line);
    }
}
