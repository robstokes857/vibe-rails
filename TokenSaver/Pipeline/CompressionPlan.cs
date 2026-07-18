using TokenSaver.Minify;
using TokenSaver.Shape;

namespace TokenSaver.Pipeline;

/// <summary>
/// A resolved <see cref="CompressionCatalog"/> selection: the concrete knobs the rewriters take,
/// plus the id set that produced them. Built once per request from a settings snapshot — never
/// re-read mid-request, so a settings save can't tear a single body's rewrite in half.
///
/// <paramref name="EnabledIds"/> is carried through to the capture row rather than recomputed from
/// the flags. The mapping is lossy in that direction (an all-false MinifyFlags cannot tell you
/// whether the user disabled five stages or picked a scope that never ran), and a capture that
/// can't state the exact config it ran under is a capture you can't judge against.
/// </summary>
public sealed record CompressionPlan(
    MinifyFlags Flags,
    CondenseOptions Condense,
    IReadOnlySet<CommandShape> Shapes,
    IReadOnlyList<string> AnthropicAllowlist,
    IReadOnlyList<string> CodexAllowlist,
    IReadOnlyList<string> ZaiAllowlist,
    IReadOnlySet<string> EnabledIds)
{
    /// <summary>Compatibility bridge for callers that still provide the original loose flags and
    /// allowlists. New host code should resolve catalog ids instead.</summary>
    public static CompressionPlan FromLegacy(
        MinifyFlags flags,
        CondenseOptions condense,
        IReadOnlyList<string>? anthropicAllowlist = null,
        IReadOnlyList<string>? codexAllowlist = null,
        IReadOnlyList<string>? zaiAllowlist = null) =>
        new(
            flags,
            condense,
            new HashSet<CommandShape>(),
            anthropicAllowlist ?? [],
            codexAllowlist ?? [],
            zaiAllowlist ?? [],
            new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// True when no stage can change a byte, so the whole rewrite is a guaranteed passthrough.
    /// Note this is about STAGES only — an empty allowlist also yields a passthrough, but that is
    /// the rewriter's check to make, because "nothing to rewrite" and "nothing to rewrite with"
    /// are different diagnoses and the capture log should be able to tell them apart.
    /// </summary>
    public bool IsNoOp => Flags.IsNoOp && Condense.IsNoOp && Shapes.Count == 0;
}
