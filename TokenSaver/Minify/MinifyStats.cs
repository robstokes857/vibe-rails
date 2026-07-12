namespace TokenSaver.Minify;

/// <summary>
/// Per-transform removal counters, accumulated across every string a request rewrite touches.
/// Counters are in the char domain (what the minifier sees), not wire bytes — JSON escaping means
/// one stripped ESC is 6 wire bytes while a stripped space is 1. They exist for the diagnostics
/// log line only; the persisted savings tally uses the rewriter's byte counts, which are the
/// honest wire numbers.
/// </summary>
public struct MinifyStats
{
    public int CrRedrawChars;
    public int AnsiChars;
    public int TrailingWhitespaceChars;
    public int BlankEdgeChars;
    public int BlankRunChars;

    /// <summary>True when at least one string was changed by minification.</summary>
    public bool Changed;

    /// <summary>
    /// True when at least one string contained a malformed/truncated escape sequence and was left
    /// untouched (the whole-string fail-open that keeps minification provably idempotent).
    /// </summary>
    public bool AbortedMalformedEscape;
}
