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
    bool CollapseBlankLineRuns,
    bool NormalizeCrLf = false)
{
    /// <summary>
    /// Every lossless transform except blank-run collapse. NOT the shipped default — that is
    /// <see cref="Pipeline.CompressionCatalog.DefaultSelection"/>, which resolves to a different
    /// set (cr-collapse and ansi-strip off, blank-runs on). This is a convenience for callers and
    /// tests that want the fullest lossless pass; it is not a statement about what users run.
    ///
    /// <see cref="NormalizeCrLf"/> is listed explicitly even though <see cref="CollapseCrRedraws"/>
    /// already implies it via <see cref="DropsCrLf"/>. Leaving it to the implication is what created
    /// the defect the standalone stage exists to fix: <c>Default with { CollapseCrRedraws = false }</c>
    /// — an ordinary bisect of one transform — would silently take line-ending normalization down
    /// with it, and every shape and condense stage downstream fails open on a surviving \r. Stating
    /// it keeps the two switches independent, which is the whole point of splitting them.
    /// </summary>
    public static MinifyFlags Default => new(
        CollapseCrRedraws: true,
        StripAnsiStyling: true,
        StripTrailingWhitespace: true,
        TrimBlankLineEdges: true,
        CollapseBlankLineRuns: false,
        NormalizeCrLf: true);

    /// <summary>True when no transform is enabled, so minification is a guaranteed no-op.</summary>
    public bool IsNoOp =>
        !CollapseCrRedraws
        && !StripAnsiStyling
        && !StripTrailingWhitespace
        && !TrimBlankLineEdges
        && !CollapseBlankLineRuns
        && !NormalizeCrLf;

    /// <summary>
    /// True when a CRLF pair's <c>\r</c> should be dropped rather than restored. Redraw collapse
    /// normalizes line endings as a side effect of its own work, so either flag is sufficient.
    /// </summary>
    internal bool DropsCrLf => CollapseCrRedraws || NormalizeCrLf;
}
