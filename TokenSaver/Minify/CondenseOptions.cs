namespace TokenSaver.Minify;

/// <summary>
/// Per-transform switches for <see cref="OutputCondenser"/>, the token saver's lossy second stage.
/// Deliberately NOT part of <see cref="MinifyFlags"/>: the condenser inserts marker text, so its
/// output is not a subsequence of its input — folding these into the minifier's flag set would
/// silently void the deletion-only proof its 32-combo property test pins. Each transform is
/// independently killable so misbehavior can be bisected without disabling the whole saver.
/// </summary>
public readonly record struct CondenseOptions(
    bool DedupeConsecutiveLines,
    bool TruncateLongOutput)
{
    /// <summary>True when no transform is enabled, so condensing is a guaranteed no-op.</summary>
    public bool IsNoOp => !DedupeConsecutiveLines && !TruncateLongOutput;
}
