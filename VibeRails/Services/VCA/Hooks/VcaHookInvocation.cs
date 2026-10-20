namespace VibeRails.Services.VCA.Hooks;

public enum VcaHookKind
{
    PreCommit,
    CommitMessage,
    AcknowledgeCommitMessage,
    Preview,

    // Commit-message cleanup on its own, with no validation and no UI. Runs ahead of any chained
    // commit-msg hook so that hook sees the message Git will actually record.
    CleanCommitMessage,

    // The Git Guard signing popup: a person picks a signing key, types its password, and the
    // signature for the staged tree is appended to the commit message. Runs only in a popup console
    // window (--console-window-attached), never inline in a terminal an agent may be driving.
    SignCommitMessage
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
    bool WorkingTreeScope = false,
    // Set by the hook script once it has run the CleanCommitMessage pass itself. Hook scripts
    // installed before that pass existed do not pass it, and those invocations still clean the
    // message inline so an un-upgraded repository keeps the policy.
    bool CoAuthorsAlreadyCleaned = false,
    // Only the installed pre-commit hook passes this. Manual validation and the Git Guard
    // console reuse the pre-commit pipeline, but must remain previews that do not start agents.
    bool EnqueueAutomatedJobs = false,
    // SignCommitMessage only: what to sign and what to show the person signing it.
    HumanSignaturePopupArguments? Signing = null);

/// <summary>
/// Arguments of the signing popup. The tree is computed by the commit-msg hook that opens the popup;
/// the popup signs it as given, and the hook re-validates the message afterwards, so a wrong value
/// here produces a failed check rather than a wrong approval.
/// </summary>
public sealed record HumanSignaturePopupArguments(
    string? TreeId,
    string? BaseTree,
    IReadOnlyList<string> Approvers,
    IReadOnlyList<string> Requirements);

/// <summary>
/// What a person must sign to resolve a signature-rule violation: the staged tree, and for
/// <c>Signed Approval</c> the accepted signer emails (empty means any registered key).
/// </summary>
public sealed record HumanSignatureRequest(
    string TreeId,
    string? BaseTree,
    IReadOnlyList<string> Approvers,
    string? Subject = null);

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
    string? Acknowledgment = null,
    // Present when a person can resolve this finding by signing in the Git Guard popup.
    HumanSignatureRequest? SignatureRequest = null);

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
