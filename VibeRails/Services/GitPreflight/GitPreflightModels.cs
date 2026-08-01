using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.GitPreflight;

public enum GitPreflightEventType
{
    RunStarted,
    StepStarted,
    StepOutput,
    StepFinished,
    RunFinished
}

public enum GitPreflightStepStatus
{
    Running,
    Passed,
    Warning,
    Blocked,
    Skipped,
    Error,
    Cancelled
}

public enum GitStagedChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    Unmerged,
    Unknown
}

public sealed record GitIndexTextFile(string RelativePath, string Content);

public sealed record GitStagedSnapshotIdentity(
    string? BaseCommit,
    string BaseTree,
    string StagedTree);

public sealed record GitStagedFileSnapshot(
    string RelativePath,
    string FullPath,
    GitStagedChangeKind ChangeKind,
    bool ExistsInIndex,
    bool IsBinary,
    int? ChangedLineCount,
    string? Content,
    string? PreviousRelativePath = null,
    string? PreviousContent = null,
    // The lines Git reports as additions for this change. An empty string is meaningful:
    // the file changed, but only through removals or a content-identical rename. Null is
    // reserved for legacy/test snapshots that did not capture a patch.
    string? AddedContent = null,
    // One entry per line in AddedContent, mapping the fragment line back to its line in
    // Content. MintLint scores the fragment while its report still links to the full file.
    IReadOnlyList<int>? AddedLineNumbers = null);

public sealed record GitStagedSnapshot(
    string RepositoryPath,
    IReadOnlyList<GitStagedFileSnapshot> Files,
    IReadOnlyList<GitIndexTextFile> AgentFiles,
    IReadOnlyList<string>? TrackedFiles = null,
    // Optional immutable source corpus used for impact ranking. Unpushed scans populate this
    // from HEAD blobs so index and working-tree changes cannot affect their reference counts.
    IReadOnlyList<GitIndexTextFile>? ImpactFiles = null,
    // Present only for the staged-index scope. These object ids bind validation and any queued VCA
    // job to one immutable base/tree pair even if the live index changes later in the preflight.
    GitStagedSnapshotIdentity? StagedIdentity = null)
{
    public static GitStagedSnapshot Preview(string repositoryPath) => new(
        Path.GetFullPath(repositoryPath),
        [
            new("src/security/AuthPolicy.cs", Path.Combine(repositoryPath, "src", "security", "AuthPolicy.cs"),
                GitStagedChangeKind.Modified, true, false, 18, "public class AuthPolicy { }"),
            new("src/payments/CardVault.cs", Path.Combine(repositoryPath, "src", "payments", "CardVault.cs"),
                GitStagedChangeKind.Added, true, false, 42, "public class CardVault { }"),
            new("package.json", Path.Combine(repositoryPath, "package.json"),
                GitStagedChangeKind.Modified, true, false, 4, "{\"scripts\":{}}")
        ],
        []);
}

public sealed record GitPreflightRequest(
    string WorkingDirectory,
    VcaHookInvocation Invocation,
    bool FullImpactScan = false,
    bool WorkingTreeChanges = false,
    // Set when scanning unpushed commits (@{upstream}..HEAD). Controls the scope label
    // in step output ("unpushed" instead of "changed"/"staged") so the user can tell
    // at a glance which scan they're reading.
    bool UnpushedChanges = false,
    // Only the standalone native pre-commit hook sets this. Browser/Rules previews use
    // the same pipeline but must never enqueue real automation.
    bool EnqueueAutomatedJobs = false);

public sealed record GitPreflightEvent(
    string RunId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    GitPreflightEventType Type,
    string? StepId,
    GitPreflightStepStatus Status,
    string Message,
    IReadOnlyDictionary<string, string>? Details = null,
    long? DurationMs = null,
    bool Blocking = false,
    bool? CommitAllowed = null,
    int? StepNumber = null,
    int? StepCount = null,
    // Set on the StepFinished event of the VCA step. Lets a consumer render the individual
    // rule findings (severity, rule, reason, acknowledgment token) instead of re-parsing them
    // out of the transcript text the step also emits.
    VcaHookValidationSummary? VcaSummary = null);

public sealed record GitPreflightStepResult(
    string StepId,
    string DisplayName,
    GitPreflightStepStatus Status,
    string Summary,
    IReadOnlyList<string> Output,
    long DurationMs,
    bool Blocking,
    IReadOnlyDictionary<string, string>? Details = null,
    VcaHookValidationSummary? VcaSummary = null);

public sealed record GitPreflightRunResult(
    string RunId,
    string RepositoryPath,
    IReadOnlyList<GitStagedFileSnapshot> StagedFiles,
    IReadOnlyList<GitPreflightStepResult> Steps,
    GitPreflightStepStatus Status,
    bool CommitAllowed,
    long DurationMs)
{
    public VcaHookValidationSummary? VcaSummary =>
        Steps.FirstOrDefault(step => step.StepId == VcaPreflightStep.Id)?.VcaSummary;
}

public interface IGitStagedSnapshotProvider
{
    Task<GitStagedSnapshot> CaptureAsync(string workingDirectory, CancellationToken cancellationToken);
}

public interface IGitWorkingTreeSnapshotProvider
{
    Task<GitStagedSnapshot> CaptureWorkingTreeAsync(
        string workingDirectory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Captures every file changed in commits that exist on the current branch but
    /// not on its upstream tracking branch (<c>@{upstream}..HEAD</c>). For each file,
    /// <c>Content</c> is the HEAD (current committed) version and <c>PreviousContent</c>
    /// is the upstream (last pushed) version. Throws <see cref="InvalidOperationException"/>
    /// when the branch has no upstream configured — the caller surfaces that to the user.
    /// </summary>
    Task<GitStagedSnapshot> CaptureUnpushedAsync(
        string workingDirectory,
        CancellationToken cancellationToken);
}

public interface IGitPreflightStep
{
    string StepId { get; }
    string DisplayName { get; }
    bool CanBlock { get; }

    Task<GitPreflightStepResult> ExecuteAsync(
        GitPreflightStepContext context,
        CancellationToken cancellationToken);
}

public sealed record GitPreflightStepContext(
    string RunId,
    GitPreflightRequest Request,
    GitStagedSnapshot Snapshot,
    Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, ValueTask> WriteOutputAsync,
    IReadOnlyList<GitPreflightStepResult>? CompletedSteps = null);

public interface IGitPreflightPipeline
{
    Task<GitPreflightRunResult> RunAsync(
        GitPreflightRequest request,
        Func<GitPreflightEvent, CancellationToken, ValueTask>? eventSink = null,
        CancellationToken cancellationToken = default);
}
