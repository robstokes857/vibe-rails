namespace TokenSaver.Minify;

/// <summary>
/// Per-transform switches for <see cref="OutputMinifier"/>. Each transform is independently
/// killable so any misbehavior can be bisected by flag without disabling the whole proxy
/// (token_saving_plan.md §5). Composition is safe: every transform is deletion-only and
/// idempotent, in any combination.
/// </summary>
public readonly record struct MinifyFlags(
    bool CollapseCrRedraws,
    bool StripAnsiStyling,
    bool StripTrailingWhitespace,
    bool TrimBlankLineEdges,
    bool CollapseBlankLineRuns)
{
    /// <summary>Plan defaults: transforms 1-3 and edge-trim on; blank-run collapse off.</summary>
    public static MinifyFlags Default => new(
        CollapseCrRedraws: true,
        StripAnsiStyling: true,
        StripTrailingWhitespace: true,
        TrimBlankLineEdges: true,
        CollapseBlankLineRuns: false);

    /// <summary>True when no transform is enabled, so minification is a guaranteed no-op.</summary>
    public bool IsNoOp =>
        !CollapseCrRedraws
        && !StripAnsiStyling
        && !StripTrailingWhitespace
        && !TrimBlankLineEdges
        && !CollapseBlankLineRuns;
}
