using VibeRails.DTOs;
using VibeRails.Launches;

namespace VibeRails.Services.Launches;

public sealed record SavePresetRequest(string Name, string? Description, string Cli, LaunchOptions? Options = null,
    string[]? AdditionalArgv = null, string? InitialPrompt = null, bool Enabled = true, long ExpectedRevision = 0,
    int? TimeoutMinutes = null, string Purpose = "work", string[]? ProjectPaths = null);
public sealed record SaveActionRequest(string Name, string? Description, string Kind, ScriptAction? Script = null,
    CheckAction? Check = null, bool Enabled = true, long ExpectedRevision = 0, string[]? ProjectPaths = null);
public sealed record SaveBindingRequest(string DefinitionId, string DefinitionType, JobTriggerRequest Trigger,
    bool Enabled = true, long ExpectedRevision = 0, RuleLaunchTrigger? Rule = null);
public sealed record LaunchRequest(string DefinitionId, string DefinitionType = "preset", string? OccurrenceId = null,
    string? PromptPrefix = null, string? InitialPrompt = null, long? ExpectedRevision = null,
    bool NativeTerminal = false, string? UserMessage = null, bool TerminalTab = false);
public sealed record ResolveUncertainLaunchRequest(bool ConfirmedStopped);
public sealed record RetireLaunchRequest(long ExpectedRevision);
public sealed record LaunchError(string Error, string Code, long? CurrentRevision = null);
/// <summary>A catalog row. <paramref name="Check"/> is a check Action's saved scope, so pickers can label
/// and reuse checks by what they analyze (VIBE-271); null for presets and scripts. <paramref name="Script"/>
/// is a script Action's path and interpreter, so pickers can say what it runs (VIBE-337); null otherwise.</summary>
public sealed record LaunchCatalogItem(string Id, string DefinitionType, string Kind, string Name, string Description,
    long Revision, bool Enabled, bool Retired, string? Cli, bool CanLaunch, bool CanRunAutomatically,
    string? IneligibleReason, bool SupportsAttachments, bool InProject = true, CheckAction? Check = null,
    LaunchCatalogScript? Script = null);
/// <summary>What a script Action runs, as the lane picker describes it. Arguments and directories stay in
/// the definition itself.</summary>
public sealed record LaunchCatalogScript(string Path, JobScriptRuntime Runtime);
public sealed record LaunchProject(string Id, string Name, string Path, bool IsCurrent);
public sealed record LaunchCatalog(IReadOnlyList<LaunchCatalogItem> Items);
public sealed record SavedPreset(LaunchPreset Preset, IReadOnlyList<LaunchTriggerBinding> AffectedBindings);
public sealed record LaunchAttachmentRequest(string Name, string DataUrl);
