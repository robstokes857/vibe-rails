namespace VibeRails.Services.VCA.Hooks;

public enum VcaHookKind
{
    PreCommit,
    CommitMessage,
    AcknowledgeCommitMessage,
    Preview
}

public sealed record VcaHookInvocation(
    VcaHookKind Kind,
    string? CommitMessagePath,
    string? WorkingDirectory,
    bool DemoUi,
    TimeSpan DemoDuration,
    bool PromptForAcknowledgment,
    bool ShowConsoleWindow = false);

public sealed record VcaHookDisplayInfo(
    string Title,
    string Subtitle,
    string Reason,
    IReadOnlyList<string> Files,
    string? RepositoryPath = null,
    TimeSpan? Timeout = null);

public sealed record VcaHookValidationSummary(
    bool HasError,
    bool HasStopViolation,
    bool HasCommitViolations,
    IReadOnlyList<string> RequiredAcknowledgments);

public sealed record VcaHookValidationResult(
    string Output,
    VcaHookValidationSummary Summary);
