namespace TokenSaver.Minify;

/// <summary>
/// Per-transform switches for <see cref="OutputMinifier"/>, projected from catalog stages 1-5 (see
/// <see cref="Pipeline.CompressionCatalog"/>). Each transform is independently killable so any
/// misbehavior can be bisected by flag without disabling the whole proxy. Composition is safe:
/// every transform is deletion-only and idempotent, in any combination.
/// </summary>
public readonly record struct MinifyFlags(
    bool CollapseCrRedraws,
    bool StripAnsiStyling,
    bool StripTrailingWhitespace,
    bool TrimBlankLineEdges,
    bool CollapseBlankLineRuns)
{
    /// <summary>
    /// Every lossless transform except blank-run collapse. NOT the shipped default — that is
    /// <see cref="Pipeline.CompressionCatalog.DefaultSelection"/>, which resolves to a different
    /// set (cr-collapse and ansi-strip off, blank-runs on). This is a convenience for callers and
    /// tests that want the fullest lossless pass; it is not a statement about what users run.
    /// </summary>
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
