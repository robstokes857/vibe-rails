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

public sealed record GitStagedFileSnapshot(
    string RelativePath,
    string FullPath,
    GitStagedChangeKind ChangeKind,
    bool ExistsInIndex,
    bool IsBinary,
    int? ChangedLineCount,
    string? Content,
    string? PreviousRelativePath = null,
    string? PreviousContent = null);

public sealed record GitStagedSnapshot(
    string RepositoryPath,
    IReadOnlyList<GitStagedFileSnapshot> Files,
    IReadOnlyList<GitIndexTextFile> AgentFiles,
    IReadOnlyList<string>? TrackedFiles = null)
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
    bool WorkingTreeChanges = false);

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
    int? StepCount = null);

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
    Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, ValueTask> WriteOutputAsync);

public interface IGitPreflightPipeline
{
    Task<GitPreflightRunResult> RunAsync(
        GitPreflightRequest request,
        Func<GitPreflightEvent, CancellationToken, ValueTask>? eventSink = null,
        CancellationToken cancellationToken = default);
}
