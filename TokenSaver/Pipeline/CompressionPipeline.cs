using System.Buffers;
using TokenSaver.Minify;
using TokenSaver.Shape;

namespace TokenSaver.Pipeline;

/// <summary>
/// Rented working buffers for one request's worth of tool_result rewrites. One instance is sized to
/// the largest tool_result in the body and reused across every string in it — renting per string on
/// the LLM hot path would be the single largest allocation source in the proxy.
/// </summary>
public sealed class PipelineScratch(int maxCharLength) : IDisposable
{
    /// <summary>Minify destination. Deletion-only, so input.Length is always enough.</summary>
    public char[] Minified { get; private set; } = ArrayPool<char>.Shared.Rent(maxCharLength);

    /// <summary>Condense destination. No stage before it can grow, so the same size holds.</summary>
    public char[] Condensed { get; private set; } = ArrayPool<char>.Shared.Rent(maxCharLength);

    public void Dispose()
    {
        if (Minified.Length > 0)
            ArrayPool<char>.Shared.Return(Minified);
        if (Condensed.Length > 0)
            ArrayPool<char>.Shared.Return(Condensed);
        Minified = [];
        Condensed = [];
    }
}

/// <summary>
/// Runs one tool_result through the enabled stages, in <see cref="CompressionCatalog"/> order, and
/// records what each one did.
///
/// THIS IS THE FUNCTION TO BREAKPOINT. If you are here because a capture looks wrong, this is the
/// whole compression path for one string — there is no other. Everything upstream of here decides
/// WHICH strings arrive (allowlist/scope, JSON walking); everything downstream just splices bytes.
/// Set a breakpoint on <see cref="Run"/>, look at <paramref name="plan"/> for the config that was
/// resolved, and read the trace it appends for the per-stage verdict.
///
/// The trace is the debugging contract: every stage in the catalog appends exactly one
/// <see cref="StageTrace"/> on every call, including stages that were off or inapplicable. A stage
/// that is silently absent from a capture's trace is a bug in this file, not a stage that did
/// nothing — that distinction is the entire point, and it is what makes the Vibe AI capture view
/// able to show you a complete pipeline rather than a suggestive subset.
/// </summary>
public static class CompressionPipeline
{
    /// <summary>
    /// Runs the pipeline over <paramref name="input"/>. Returns the resulting span — which aliases
    /// <paramref name="scratch"/>'s buffers, an interned shape-filter string, or
    /// <paramref name="input"/> itself, so it is only valid until the next <see cref="Run"/> call
    /// on the same scratch.
    ///
    /// <paramref name="changed"/> is the only reliable "did anything happen" signal; do not infer
    /// it from span lengths, because a Reshaping stage can hold length constant while moving bytes.
    ///
    /// Never throws for transform reasons. Every stage either applies or declines.
    /// </summary>
    public static ReadOnlySpan<char> Run(
        ReadOnlySpan<char> input,
        CompressionPlan plan,
        CommandShape shape,
        PipelineScratch scratch,
        out bool changed,
        ref MinifyStats minifyStats,
        ref CondenseStats condenseStats,
        ICollection<StageTrace>? trace = null)
    {
        changed = false;
        var current = input;

        // ---- Stages 1-5: terminal cleanup. One fused scan; see CompressionCatalog remarks. ----
        var beforeMinify = minifyStats;
        if (!plan.Flags.IsNoOp
            && OutputMinifier.TryMinify(
                current, plan.Flags, scratch.Minified, out var minifyWritten, ref minifyStats))
        {
            current = scratch.Minified.AsSpan(0, minifyWritten);
            changed = true;
        }

        TraceMinifyStages(plan, beforeMinify, minifyStats, trace);

        // ---- Stages 6-9: shape filters. Keyed off the COMMAND, never off output sniffing. ----
        current = RunShape(current, plan, shape, ref changed, trace);

        // ---- Stages 10-11: condense. Fused, same reason as 1-5. ----
        var beforeCondense = condenseStats;
        if (!plan.Condense.IsNoOp
            && OutputCondenser.TryCondense(
                current, plan.Condense, scratch.Condensed, out var condensedWritten,
                ref condenseStats))
        {
            current = scratch.Condensed.AsSpan(0, condensedWritten);
            changed = true;
        }

        TraceCondenseStages(plan, beforeCondense, condenseStats, trace);

        return current;
    }

    // `scoped ref` on `changed`: without it the compiler must assume the returned span could alias
    // the ref, which it never does — the span only ever points at input, scratch, or a shape
    // filter's string.
    private static ReadOnlySpan<char> RunShape(
        ReadOnlySpan<char> current,
        CompressionPlan plan,
        CommandShape shape,
        scoped ref bool changed,
        ICollection<StageTrace>? trace)
    {
        foreach (var (stageId, stageShape) in ShapeStages)
        {
            if (!plan.EnabledIds.Contains(stageId))
            {
                trace?.Add(new StageTrace(stageId, StageOutcome.Disabled));
                continue;
            }

            if (shape != stageShape)
            {
                trace?.Add(new StageTrace(stageId, StageOutcome.NotApplicable));
                continue;
            }

            // Only materialize a string once we know a filter both wants this input and is on —
            // shape filters fire on a small minority of tool calls and this is the hot path.
            var before = current.ToString();
            var after = ShapeFilters.Apply(before, stageShape);
            if (ReferenceEquals(before, after))
            {
                trace?.Add(new StageTrace(stageId, StageOutcome.NoChange));
                continue;
            }

            trace?.Add(new StageTrace(stageId, StageOutcome.Applied, before.Length - after.Length));
            changed = true;
            current = after.AsSpan();
        }

        return current;
    }

    /// <summary>
    /// Maps the minifier's per-transform counters onto catalog stage ids. The counters ARE the
    /// per-stage trace — the fused scan already attributes every removed char to the transform that
    /// removed it, so nothing here re-derives or estimates.
    /// </summary>
    private static void TraceMinifyStages(
        CompressionPlan plan, MinifyStats before, MinifyStats after, ICollection<StageTrace>? trace)
    {
        if (trace is null)
            return;

        // An abort is a whole-string outcome, so it lands on every enabled cleanup stage: none of
        // them ran, and pinning it on one would be a lie about which transform declined.
        var aborted = after.AbortedMalformedEscape && !before.AbortedMalformedEscape;

        Add(CompressionCatalog.CrCollapse, plan.Flags.CollapseCrRedraws,
            after.CrRedrawChars - before.CrRedrawChars);
        Add(CompressionCatalog.AnsiStrip, plan.Flags.StripAnsiStyling,
            after.AnsiChars - before.AnsiChars);
        Add(CompressionCatalog.TrailingWhitespace, plan.Flags.StripTrailingWhitespace,
            after.TrailingWhitespaceChars - before.TrailingWhitespaceChars);
        Add(CompressionCatalog.BlankEdges, plan.Flags.TrimBlankLineEdges,
            after.BlankEdgeChars - before.BlankEdgeChars);
        Add(CompressionCatalog.BlankRuns, plan.Flags.CollapseBlankLineRuns,
            after.BlankRunChars - before.BlankRunChars);

        void Add(string id, bool enabled, int removed)
        {
            if (!enabled)
                trace.Add(new StageTrace(id, StageOutcome.Disabled));
            else if (aborted)
                trace.Add(new StageTrace(id, StageOutcome.Aborted));
            else
                trace.Add(new StageTrace(
                    id, removed > 0 ? StageOutcome.Applied : StageOutcome.NoChange, removed));
        }
    }

    private static void TraceCondenseStages(
        CompressionPlan plan, CondenseStats before, CondenseStats after,
        ICollection<StageTrace>? trace)
    {
        if (trace is null)
            return;

        var aborted = after.AbortedControlChars && !before.AbortedControlChars;

        Add(CompressionCatalog.DedupeLines, plan.Condense.DedupeConsecutiveLines,
            after.DedupNetChars - before.DedupNetChars);
        Add(CompressionCatalog.TruncateLong, plan.Condense.TruncateLongOutput,
            after.TruncationNetChars - before.TruncationNetChars);

        void Add(string id, bool enabled, int removed)
        {
            if (!enabled)
                trace.Add(new StageTrace(id, StageOutcome.Disabled));
            else if (aborted)
                trace.Add(new StageTrace(id, StageOutcome.Aborted));
            else
                trace.Add(new StageTrace(
                    id, removed > 0 ? StageOutcome.Applied : StageOutcome.NoChange, removed));
        }
    }

    /// <summary>Shape stages in catalog order. Kept adjacent to the catalog constants on purpose —
    /// adding a shape filter means touching both, and a missing entry here shows up as a stage that
    /// never appears in any trace.</summary>
    private static readonly (string StageId, CommandShape Shape)[] ShapeStages =
    [
        (CompressionCatalog.GitStatusGroup, CommandShape.GitStatus),
        (CompressionCatalog.GrepGroup, CommandShape.GrepMatches),
        (CompressionCatalog.FindGroup, CommandShape.PathList),
        (CompressionCatalog.ElidePassedTests, CommandShape.TestRun),
    ];
}
