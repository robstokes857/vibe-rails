using VibeRails.Services.Terminal;

namespace VibeRails.Services.UserInOut;

/// <summary>
/// Finds the user's submitted prompt inside the TUI output bytes.
///
/// Invariant: the extractor must never return text that's WORSE than the raw
/// input. The raw input is always a valid fallback — extraction is only useful
/// when the raw capture is a partial keystroke tail (autocomplete / paste split
/// across chunks) and the TUI echo contains the fuller submission.
///
/// Algorithm (simplified from the old fuzzy-partial-ratio approach):
/// 1. Multi-line raws are authoritative — the TUI's column-wrapped rendering
///    of pasted blocks is unreliable, so just return raw.
/// 2. Anchor on the LLM's submit marker (<c>&gt;</c> or <c>›</c>). For each
///    occurrence in the sanitized TUI, the text after the marker is a
///    candidate submission.
/// 3. Accept a candidate only when BOTH its head and tail match the needle
///    (case-insensitive, first/last ~20 chars). If the raw begins with
///    whitespace (keystroke-tail capture — e.g. the user typed a TAB that
///    autocompleted a long prefix), tail-match alone suffices.
/// 4. Apply length bounds: candidates much longer than the needle are
///    decorative echoes (box drawing, spaced-out-char widget rows), candidates
///    much shorter are clipped.
/// 5. Prefer the candidate whose length is closest to the needle's. If nothing
///    qualifies, return raw.
/// </summary>
public sealed class TuiTextExtractor : ITuiTextExtractor
{
    private const int MinInputLength = 5;
    private const int AnchorMatchLength = 20;
    private const double MaxLengthRatio = 2.0;
    private const int MaxLengthAbsoluteSlack = 30;

    private static readonly char[] LineTrimChars = ['\r', '\0', ' ', '\t'];

    // `> ` is the default submit glyph for most shells / CLIs. `› ` (U+203A) is
    // Claude Code's submitted-prompt marker. `>>> ` is the Python REPL.
    private static readonly string[] PromptMarkers = ["› ", "> ", ">>> "];

    public string Extract(string rawInput, string tuiText)
    {
        if (string.IsNullOrWhiteSpace(rawInput) || string.IsNullOrWhiteSpace(tuiText))
            return rawInput;

        var needle = rawInput.Trim();
        if (needle.Length < MinInputLength)
            return rawInput;

        // Multi-line pastes: raw is always the full truth. TUI rendering of
        // pasted blocks mangles them with wrapping / autocomplete decoration,
        // so any extracted candidate would be worse.
        if (needle.Contains('\n'))
            return rawInput;

        var rawStartsWithKeystrokeArtifact = char.IsWhiteSpace(rawInput[0]);
        var stripped = TerminalTextSanitizer.ToPlainText(tuiText);

        return TryAnchoredMatch(needle, stripped, rawStartsWithKeystrokeArtifact)
            ?? rawInput;
    }

    private static string? TryAnchoredMatch(string needle, string tui, bool allowTailOnly)
    {
        var head = needle[..Math.Min(AnchorMatchLength, needle.Length)];
        var tail = needle[^Math.Min(AnchorMatchLength, needle.Length)..];

        var maxLen = (int)(needle.Length * MaxLengthRatio) + MaxLengthAbsoluteSlack;
        var minLen = Math.Max(MinInputLength, needle.Length / 2);

        var endsInSentencePunctuation = EndsInSentencePunctuation(needle);
        var distinctiveToken = endsInSentencePunctuation ? null : FindDistinctiveToken(needle);

        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var line in tui.Split('\n'))
        {
            var trimmed = line.Trim(LineTrimChars);
            if (trimmed.Length < MinInputLength)
                continue;

            foreach (var marker in PromptMarkers)
            {
                // Iterate every occurrence of the marker, not just the last —
                // a fused TUI line may contain previous prompts, the current
                // echo, AND decorative `>` glyphs. Any of them could be the
                // right anchor; length + head/tail checks pick the winner.
                var searchFrom = 0;
                while (searchFrom <= trimmed.Length)
                {
                    var idx = trimmed.IndexOf(marker, searchFrom, StringComparison.Ordinal);
                    if (idx < 0) break;

                    var candidate = trimmed[(idx + marker.Length)..].TrimEnd(LineTrimChars);
                    searchFrom = idx + marker.Length;

                    // For the distinctive-token path, allow longer candidates:
                    // autocomplete-expanded file paths can push total length
                    // well past the raw "filter" text.
                    var effectiveMax = distinctiveToken is not null ? maxLen * 2 : maxLen;
                    if (candidate.Length < minLen || candidate.Length > effectiveMax)
                        continue;

                    var tailMatches = candidate.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
                    var headMatches = candidate.StartsWith(head, StringComparison.OrdinalIgnoreCase);

                    // Three modes:
                    //  - Raw starts with whitespace (keystroke-tail capture):
                    //    tail match alone is enough.
                    //  - Raw ends in sentence punctuation (looks complete):
                    //    require BOTH head and tail — prevents a previous
                    //    message's tail from overwriting the current row.
                    //  - Otherwise (short partial input like an `@`-prefixed
                    //    autocomplete filter): a single distinctive token
                    //    (≥8 chars) appearing in the candidate is enough,
                    //    since the autocomplete rewrites the surrounding text.
                    bool accept;
                    if (allowTailOnly)
                        accept = tailMatches;
                    else if (endsInSentencePunctuation)
                        accept = headMatches && tailMatches;
                    else if (distinctiveToken is not null)
                        accept = candidate.Contains(distinctiveToken, StringComparison.OrdinalIgnoreCase);
                    else
                        accept = headMatches && tailMatches;

                    if (!accept)
                        continue;

                    var distance = Math.Abs(candidate.Length - needle.Length);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
            }
        }

        return best;
    }

    private static bool EndsInSentencePunctuation(string s)
    {
        var trimmed = s.TrimEnd();
        if (trimmed.Length == 0) return false;
        var last = trimmed[^1];
        return last is '.' or '!' or '?';
    }

    private static string? FindDistinctiveToken(string needle)
    {
        // Pick the longest whitespace-delimited token from the needle. This
        // is meant for cases like `@ WaitingForUserInputObserver` or
        // `add search functionality` — short non-sentence inputs where a
        // single long token is distinctive enough to re-identify the prompt
        // in the TUI even when the surrounding text was rewritten.
        string? best = null;
        foreach (var token in needle.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 8) continue;
            if (best is null || token.Length > best.Length)
                best = token;
        }
        return best;
    }
}
