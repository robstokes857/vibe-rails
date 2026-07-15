namespace TokenSaver.Minify;

/// <summary>
/// Per-transform counters for <see cref="OutputCondenser"/>, accumulated across every string a
/// request rewrite touches. Kept apart from <see cref="MinifyStats"/> so the minifier's
/// counters-sum-to-chars-removed conservation law stays provable on its own. The char counters here
/// are NET (chars removed minus marker chars inserted) with their own conservation law:
/// <c>DedupNetChars + TruncationNetChars</c> equals exactly the shrinkage of each condensed string.
/// Diagnostics only — the persisted savings tally uses the rewriter's wire-byte counts.
/// </summary>
public struct CondenseStats
{
    /// <summary>Runs of identical consecutive lines collapsed to a single marker-suffixed line.</summary>
    public int DedupRunsCollapsed;
    public int DedupNetChars;

    /// <summary>Strings whose middle was replaced by an elision marker line.</summary>
    public int TruncationsApplied;
    public int TruncationNetChars;

    /// <summary>True when at least one string was changed by condensing.</summary>
    public bool Changed;

    /// <summary>
    /// True when at least one string contained ESC/BEL/CR and was left untouched (the whole-string
    /// fail-open that keeps line-based edits away from anything the minifier kept verbatim).
    /// </summary>
    public bool AbortedControlChars;
}
