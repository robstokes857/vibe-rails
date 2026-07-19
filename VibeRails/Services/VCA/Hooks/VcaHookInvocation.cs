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
    bool ShowConsoleWindow = false,
    bool ConsoleWindowAttached = false,
    // True when the snapshot being validated is the whole working tree (the Rules page
    // preview) rather than the staged index (the real commit hooks). Only affects how
    // results are worded — the validation logic is identical.
    bool WorkingTreeScope = false);

public sealed record VcaHookDisplayInfo(
    string Title,
    string Subtitle,
    string Reason,
    IReadOnlyList<string> Files,
    string? RepositoryPath = null,
    TimeSpan? Timeout = null);

public enum VcaRuleFindingKind
{
    Warning,
    Deferred,
    AcknowledgmentRequired,
    Blocked
}

public sealed record VcaRuleFinding(
    VcaRuleFindingKind Kind,
    string Enforcement,
    string Rule,
    string Reason,
    string SourcePath,
    string Guidance,
    string? Acknowledgment = null);

public sealed record VcaHookValidationSummary(
    bool HasError,
    bool HasStopViolation,
    bool HasCommitViolations,
    IReadOnlyList<string> RequiredAcknowledgments,
    int StagedFileCount = 0,
    int ApplicableRuleCount = 0,
    IReadOnlyList<VcaRuleFinding>? Findings = null);

public sealed record VcaHookValidationResult(
    string Output,
    VcaHookValidationSummary Summary);
