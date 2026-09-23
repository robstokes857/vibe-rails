using VibeRails.Services;

namespace VibeRails.DTOs;

// Cross-repository Automation import (VB-31). The catalog is built server-side because
// GET /api/v1/environments is project-filtered, so the browser can never see another
// repository's Worker. Everything a review modal needs to show is on the entry; nothing here is
// a stored record — the import re-resolves and re-pins every script in the target repository.

/// <summary>One repository on this machine that owns at least one importable Automation.</summary>
public sealed record AutomationImportProjectGroup(
    string ProjectPath,
    string DisplayName,
    bool DirectoryExists,
    List<AutomationImportCatalogEntry> Automations);

public sealed record AutomationImportCatalogResponse(
    string CurrentProjectPath,
    List<AutomationImportProjectGroup> Projects);

/// <summary>
/// The source Worker as the target repository will see it. <c>ReusableEnvironmentId</c> is set
/// when a Worker with the same name and CLI is already visible in the target repository, in
/// which case the import reuses it instead of cloning; otherwise the clone is created under
/// <c>SuggestedCloneName</c> (editable by the client).
/// </summary>
public sealed record AutomationImportWorker(
    int SourceEnvironmentId,
    string Name,
    LLM Llm,
    string Cli,
    string CustomArgs,
    string Prompt,
    int WorkspaceMode,
    List<EnvironmentStepDto> Steps,
    int? ReusableEnvironmentId,
    string? ReusableEnvironmentName,
    string SuggestedCloneName);

/// <summary>
/// One ordered action of the source Automation. For scripts, <c>ExistsInTargetRepository</c>
/// means the import will keep the target's copy untouched; otherwise the file is copied from
/// the source repository when <c>ExistsInSourceRepository</c>, and the import is blocked when
/// it exists in neither.
/// </summary>
public sealed record AutomationImportAction(
    JobActionKind Kind,
    string? ScriptPath,
    JobScriptRuntime? ScriptRuntime,
    List<string> Arguments,
    string? WorkingDirectory,
    int? TimeoutSeconds,
    bool ExistsInTargetRepository,
    bool ExistsInSourceRepository);

public sealed record AutomationImportCatalogEntry(
    long SourceJobId,
    string Name,
    string ProjectPath,
    string ProjectDisplayName,
    bool ProjectDirectoryExists,
    bool Enabled,
    DateTime UpdatedUtc,
    AutomationImportWorker? Worker,
    List<AutomationImportAction> Actions,
    int? TimeoutMinutes,
    bool LaunchMinimized,
    List<JobTriggerRequest> Triggers,
    bool CanImport,
    string? Blocker,
    bool LaunchInTerminalTab = false);

/// <summary>
/// <c>WorkerName</c> is the clone's name when the Worker has to be cloned; it is ignored when a
/// visible Worker is reused. Null falls back to the catalog's suggestion.
/// </summary>
public sealed record AutomationImportRequest(
    long SourceJobId,
    string? WorkerName = null);

public sealed record AutomationImportResponse(
    JobResponse Job,
    string WorkerOutcome,
    string? WorkerName,
    List<string> CopiedScripts,
    List<string> CreatedDirectories);

public static class AutomationImportWorkerOutcome
{
    public const string None = "none";
    public const string Reused = "reused";
    public const string Created = "created";
}
