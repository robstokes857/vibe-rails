using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.Launches;

/// <summary>A global, lightweight launch definition. No CLI home or installation is owned by a preset.</summary>
public sealed record LaunchPreset(string Id, string Name, string Description, LLM Cli,
    LaunchOptions Options, string[] AdditionalArgv, string? InitialPrompt, bool Enabled,
    long Revision, DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? RetiredUtc = null,
    int? TimeoutMinutes = null, string Purpose = "work")
{
    public string Kind => string.IsNullOrWhiteSpace(InitialPrompt) ? "quick-launch" : "agent";
}

/// <summary>Typed process-local provider options in addition to the shared model/effort/mode controls.</summary>
public sealed record LaunchOptions(BaseLlmOptions? Base = null,
    ClaudeLaunchOptions? Claude = null, CodexLaunchOptions? Codex = null,
    OpenCodeLaunchOptions? OpenCode = null);

public sealed record ClaudeLaunchOptions(bool FastMode = false, bool NoSessionPersistence = false,
    string? SystemPrompt = null, bool Bare = false, bool Debug = false);
public sealed record CodexLaunchOptions(bool NoAltScreen = false);
public sealed record OpenCodeLaunchOptions(bool DisablePlugins = false);

/// <summary>Exactly one typed action, never an ordered array of actions.</summary>
public sealed record LaunchAction(string Id, string Name, string Description, string Kind,
    ScriptAction? Script, CheckAction? Check, bool Enabled, long Revision,
    DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? RetiredUtc = null);

public sealed record ScriptAction(string Path, JobScriptRuntime Runtime, string[] Arguments,
    string? WorkingDirectory = null, int? TimeoutSeconds = null);
public sealed record CheckAction(string Scope, string? FailGrade = null, string? BaseCommit = null, string? HeadCommit = null);

/// <summary>A schedule/event is scoped to an explicitly authorized project, independently of global visibility.</summary>
/// <remarks><see cref="ProjectPath"/> is the comparison key (upper-cased on Windows/macOS); <see cref="ProjectRoot"/>
/// is the path as the authorizing root spelled it, for anything that touches the filesystem. Bindings saved
/// before VIBE-296 have no root and fall back to the key.</remarks>
public sealed record LaunchTriggerBinding(string Id, string DefinitionId, string DefinitionType,
    string ProjectPath, JobTriggerRequest Trigger, bool Enabled, long Revision,
    DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? NextRunUtc = null, string? ProjectRoot = null);

public sealed record LaunchExecutionContext(string ProjectPath, string WorkingDirectory,
    string TriggerKind, string OccurrenceId, string? BindingId = null, string? BoardId = null,
    string? CardId = null, string? LaneEntryId = null, string? PromptPrefix = null,
    bool AuthorizeBoardTools = false, bool ContextIncluded = false, string? ComposedPrompt = null,
    string? SessionOrigin = null, string? Selection = null, string? PromptOverride = null,
    string? ProjectRoot = null, string? SessionTitle = null, bool NativeTerminal = false)
{
    /// <summary>
    /// The project as the queuing root spelled it. <see cref="ProjectPath"/> is the comparison key
    /// (upper-cased on Windows/macOS) and is never a working directory: a case-sensitive volume
    /// cannot open it. Runs queued before VIBE-296 have no root and fall back to the key.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Root => ProjectRoot ?? ProjectPath;
}

/// <summary>The complete immutable input to one run; retries copy it rather than reading current options.</summary>
public sealed record LaunchSnapshot(LaunchExecutionContext Context, LaunchPreset? Preset = null,
    LaunchAction? Action = null, string? ApprovedScriptHash = null, string[]? AttachmentIds = null)
{
    public string DefinitionId => Preset?.Id ?? Action!.Id;
    public string DefinitionType => Preset is null ? "action" : "preset";
    public long Revision => Preset?.Revision ?? Action!.Revision;
}

/// <summary>Process outcome and explicit check/review evidence are intentionally distinct.</summary>
public sealed record LaunchRun(string Id, long Sequence, string DefinitionId, string DefinitionType,
    string ProjectPath, string OccurrenceId, LaunchSnapshot Snapshot, string Status,
    DateTime QueuedUtc, string? SourceRunId = null, [property: System.Text.Json.Serialization.JsonIgnore] string? ClaimToken = null,
    int? OwnerPid = null, DateTime? OwnerStartedUtc = null, DateTime? ClaimedUtc = null,
    DateTime? StartedUtc = null, DateTime? EndedUtc = null, bool CancelRequested = false,
    string? SessionId = null, string? TabId = null, string? ResolvedPrompt = null,
    string? ResolvedWorkingDirectory = null, int? ExitCode = null, string? Error = null,
    string? EvidenceId = null, string? Verdict = null, DateTime? DeadlineUtc = null,
    int? ExecutionPid = null, DateTime? ExecutionStartedUtc = null)
{
    public bool IsTerminal => Status is "succeeded" or "failed" or "cancelled" or "timed-out" or "interrupted";
}

public sealed record LaunchRunPage(IReadOnlyList<LaunchRun> Runs, long? NextCursor);
public sealed record LaunchClaim(string RunId, string Token);
public sealed record LaunchAttachment(string Id, string DefinitionId, string Name, string MimeType,
    long Size, DateTime CreatedUtc, DateTime? RemovedUtc = null);
public sealed record LaunchAttachmentContent(LaunchAttachment Attachment, byte[] Content);

public sealed class LaunchValidationException(string message) : Exception(message);
public sealed class LaunchConflictException(string message, long? currentRevision = null) : Exception(message)
{
    public long? CurrentRevision { get; } = currentRevision;
}
