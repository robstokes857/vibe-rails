using VibeRails.DTOs;
using VibeRails.Launches;

namespace VibeRails.Services.Launches;

public sealed record SavePresetRequest(string Name, string? Description, string Cli, LaunchOptions? Options = null,
    string[]? AdditionalArgv = null, string? InitialPrompt = null, bool Enabled = true, long ExpectedRevision = 0,
    int? TimeoutMinutes = null, string Purpose = "work", string[]? ProjectPaths = null);
public sealed record SaveActionRequest(string Name, string? Description, string Kind, ScriptAction? Script = null,
    CheckAction? Check = null, bool Enabled = true, long ExpectedRevision = 0, string[]? ProjectPaths = null);
public sealed record SaveBindingRequest(string DefinitionId, string DefinitionType, JobTriggerRequest Trigger,
    bool Enabled = true, long ExpectedRevision = 0);
public sealed record LaunchRequest(string DefinitionId, string DefinitionType = "preset", string? OccurrenceId = null,
    string? PromptPrefix = null, string? InitialPrompt = null, long? ExpectedRevision = null,
    bool NativeTerminal = false);
public sealed record ResolveUncertainLaunchRequest(bool ConfirmedStopped);
public sealed record RetireLaunchRequest(long ExpectedRevision);
public sealed record LaunchError(string Error, string Code, long? CurrentRevision = null);
public sealed record LaunchCatalogItem(string Id, string DefinitionType, string Kind, string Name, string Description,
    long Revision, bool Enabled, bool Retired, string? Cli, bool CanLaunch, bool CanRunAutomatically,
    string? IneligibleReason, bool SupportsAttachments, bool InProject = true);
public sealed record LaunchProject(string Id, string Name, string Path, bool IsCurrent);
public sealed record LaunchCatalog(IReadOnlyList<LaunchCatalogItem> Items);
public sealed record LaunchLaneEntry(VibeRails.Services.Board.BoardLaunchEvent Entry, string CardLabel,
    string DefinitionName, LaunchRun? Run);
public sealed record SavedPreset(LaunchPreset Preset, IReadOnlyList<LaunchTriggerBinding> AffectedBindings);
public sealed record LaunchAttachmentRequest(string Name, string DataUrl);
