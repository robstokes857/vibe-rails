namespace TokenSaver.Minify;

/// <summary>
/// Per-transform switches for <see cref="OutputCondenser"/>, the token saver's lossy second stage.
/// Deliberately NOT part of <see cref="MinifyFlags"/>: the condenser inserts marker text, so its
/// output is not a subsequence of its input — folding these into the minifier's flag set would
/// silently void the deletion-only proof its 32-combo property test pins. Each transform is
/// independently killable so misbehavior can be bisected without disabling the whole saver.
/// </summary>
/// <param name="DedupeConsecutiveLines">Transform D — collapse runs of ≥3 identical lines.</param>
/// <param name="TruncateLongOutput">Transform T — elide the middle of an over-long payload.</param>
/// <param name="PreserveVerbatimFileContents">
/// Not a transform switch: a per-payload BUDGET selector for T, set by the pipeline when the
/// producing command reads file contents (<see cref="Shape.CommandShapes.ReadsFileContents"/>). It
/// only ever makes T keep MORE, so it cannot be the cause of a lost line. Default false so
/// <c>default(CondenseOptions)</c> and every existing two-argument construction keep the original
/// 150/50 budget.
/// </param>
public readonly record struct CondenseOptions(
    bool DedupeConsecutiveLines,
    bool TruncateLongOutput,
    bool PreserveVerbatimFileContents = false)
{
    /// <summary>True when no transform is enabled, so condensing is a guaranteed no-op.</summary>
    public bool IsNoOp => !DedupeConsecutiveLines && !TruncateLongOutput;
}
