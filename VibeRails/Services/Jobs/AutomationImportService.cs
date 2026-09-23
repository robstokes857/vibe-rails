using System.Text.RegularExpressions;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

public interface IAutomationImportService
{
    /// <summary>Every non-deleted Automation that belongs to another repository on this machine.</summary>
    Task<AutomationImportCatalogResponse> GetCatalogAsync(
        string currentProjectPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One-way copy of a source Automation into <paramref name="currentProjectPath"/>: missing
    /// scripts are copied from the source repository, the Worker is reused or cloned, and the
    /// Automation is created disabled.
    /// </summary>
    Task<AutomationImportResponse> ImportAsync(
        string currentProjectPath,
        AutomationImportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Cross-repository Automation import (VB-31). This is a copy, not a link: after the import the
/// two Automations and, when cloned, the two Workers are unrelated rows. The copy does remember
/// the id of the root Automation it came from (<c>Jobs.ImportedFromJobId</c>), but only so the
/// catalog can keep copies from being offered back to their source (VB-33). Nothing here bypasses
/// the normal create paths — the Worker goes through <see cref="LlmCliEnvironmentService"/> so
/// its config directory is seeded like any other, and the Automation goes through
/// <see cref="IJobService.CreateJobAsync"/> so every script is re-resolved and hash-pinned
/// against the target repository. A source hash is never trusted as a target approval.
/// </summary>
public sealed partial class AutomationImportService(
    IJobStore store,
    IRepository repository,
    IJobService jobService,
    IAutomationScriptService scriptService,
    LlmCliEnvironmentService environmentService,
    ILlmParser llmParser) : IAutomationImportService
{
    /// <summary>Mirrors <c>EnvironmentNameValidator</c>'s limit so a suggestion is never rejected for length.</summary>
    private const int MaxEnvironmentNameLength = 64;

    public async Task<AutomationImportCatalogResponse> GetCatalogAsync(
        string currentProjectPath,
        CancellationToken cancellationToken = default)
    {
        var currentRoot = NormalizeProjectPath(currentProjectPath);
        var jobs = await store.GetJobsAsync(projectPath: null, includeDeleted: false, cancellationToken);
        var foreign = WithoutRedundantCopies(
            jobs.Where(job => job.DeletedUtc is null && !ProjectPathComparer.Matches(job.ProjectPath, currentRoot)),
            jobs);
        if (foreign.Count == 0)
            return new AutomationImportCatalogResponse(currentRoot, []);

        var environments = await repository.GetAllEnvironmentsAsync(cancellationToken);
        var environmentsById = environments.ToDictionary(environment => environment.Id);
        var workerIds = foreign
            .SelectMany(job => ResolveActions(job))
            .Where(action => action.Kind == JobActionKind.Worker && action.EnvironmentId is not null)
            .Select(action => action.EnvironmentId!.Value)
            .Distinct()
            .ToList();
        var stepsByEnvironment = workerIds.Count > 0
            ? await repository.GetStepsForEnvironmentsAsync(workerIds, cancellationToken)
            : [];
        var targetRepositoryName = Path.GetFileName(currentRoot);

        var groups = new List<AutomationImportProjectGroup>();
        foreach (var group in foreign.GroupBy(job => NormalizeProjectPath(job.ProjectPath), PathComparer))
        {
            var sourceRoot = group.Key;
            var directoryExists = Directory.Exists(sourceRoot);
            var displayName = await ResolveDisplayNameAsync(sourceRoot, cancellationToken);
            var entries = group
                .Select(job => BuildEntry(
                    job, sourceRoot, directoryExists, displayName, currentRoot, targetRepositoryName,
                    environments, environmentsById, stepsByEnvironment))
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            groups.Add(new AutomationImportProjectGroup(sourceRoot, displayName, directoryExists, entries));
        }

        return new AutomationImportCatalogResponse(
            currentRoot,
            groups
                .OrderBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(group => group.ProjectPath, PathComparer)
                .ToList());
    }

    public async Task<AutomationImportResponse> ImportAsync(
        string currentProjectPath,
        AutomationImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentRoot = NormalizeProjectPath(currentProjectPath);
        var job = await store.GetJobAsync(request.SourceJobId, cancellationToken);
        if (job is null || job.DeletedUtc is not null)
            throw JobServiceException.NotFound("Automation not found.");
        if (ProjectPathComparer.Matches(job.ProjectPath, currentRoot))
            throw JobServiceException.BadRequest("That Automation already belongs to this repository.");

        var sourceRoot = NormalizeProjectPath(job.ProjectPath);
        var actions = ResolveActions(job);
        if (actions.Count == 0)
            throw JobServiceException.BadRequest("The source Automation has no actions to import.");

        // ----- Worker: reuse a visible one or prepare a clone. Nothing is written yet. -----
        var environments = await repository.GetAllEnvironmentsAsync(cancellationToken);
        LLM_Environment? sourceWorker = null;
        LLM_Environment? targetWorker = null;
        string? cloneName = null;
        List<EnvironmentStep> sourceSteps = [];
        var workerAction = actions.FirstOrDefault(action => action.Kind == JobActionKind.Worker);
        if (workerAction is not null)
        {
            sourceWorker = workerAction.EnvironmentId is int sourceEnvironmentId
                ? environments.FirstOrDefault(environment => environment.Id == sourceEnvironmentId)
                : null;
            if (sourceWorker is null)
                throw JobServiceException.BadRequest("The source Worker no longer exists, so this Automation cannot be imported.");

            targetWorker = FindReusableWorker(environments, sourceWorker, currentRoot);
            if (targetWorker is null)
            {
                cloneName = string.IsNullOrWhiteSpace(request.WorkerName)
                    ? SuggestCloneName(sourceWorker.CustomName, Path.GetFileName(currentRoot), environments.Select(environment => environment.CustomName))
                    : request.WorkerName.Trim();

                // The same three gates POST /api/v1/environments applies: the name becomes a
                // directory under ~/.vibe_rails/envs and a CLI-shaped name is unlaunchable.
                var nameError = EnvironmentNameValidator.Validate(cloneName);
                if (nameError is not null)
                    throw JobServiceException.BadRequest(nameError);
                if (llmParser.Parse(cloneName) != LLM.NotSet)
                {
                    throw JobServiceException.BadRequest(
                        $"'{cloneName}' is the name of a built-in CLI, so a Worker with that name could never be launched. Pick a different name.");
                }
                var collision = await repository.FindEnvironmentByNameIgnoreCaseAsync(cloneName, cancellationToken);
                if (collision is not null)
                {
                    throw JobServiceException.Conflict(
                        $"An environment named '{collision.CustomName}' already exists. Choose a different Worker name.");
                }
                if (string.IsNullOrWhiteSpace(sourceWorker.CustomPrompt))
                    throw JobServiceException.BadRequest("The source Worker has no Initial Message, so it cannot run as an Automation here.");

                sourceSteps = await repository.GetStepsForEnvironmentAsync(sourceWorker.Id, cancellationToken);
            }
        }

        // ----- Scripts: every check that can fail runs before the first byte is written. -----
        var scripts = actions.Where(action => action.Kind == JobActionKind.Script).ToList();
        foreach (var script in scripts)
        {
            if (script.ScriptRuntime is not JobScriptRuntime runtime)
                throw JobServiceException.BadRequest("A script action is missing its runtime.");
            var unavailable = scriptService.GetRuntimeUnavailableMessage(runtime);
            if (unavailable is not null)
                throw JobServiceException.BadRequest(unavailable);
            if (!scriptService.ScriptExists(currentRoot, script.ScriptPath)
                && !scriptService.ScriptExists(sourceRoot, script.ScriptPath))
            {
                throw JobServiceException.BadRequest(
                    $"Script '{script.ScriptPath}' was not found in this repository or in the source repository.");
            }
        }

        var copiedScripts = new List<string>();
        var createdDirectories = new List<string>();
        foreach (var script in scripts)
        {
            try
            {
                if (await scriptService.CopyScriptAsync(
                        sourceRoot, currentRoot, script.ScriptPath!, script.ScriptRuntime!.Value, cancellationToken))
                {
                    copiedScripts.Add(script.ScriptPath!);
                }
                if (EnsureWorkingDirectory(currentRoot, script.WorkingDirectory))
                    createdDirectories.Add(script.WorkingDirectory!);
            }
            catch (AutomationScriptValidationException ex)
            {
                throw JobServiceException.BadRequest(ex.Message);
            }
        }

        // ----- Worker clone. From here on a failure has something to undo. -----
        var cloned = false;
        if (sourceWorker is not null && targetWorker is null)
        {
            targetWorker = await CloneWorkerAsync(sourceWorker, sourceSteps, cloneName!, currentRoot, cancellationToken);
            cloned = true;
        }

        var createRequest = new CreateJobRequest(
            job.Name,
            currentRoot,
            LLM.NotSet,
            targetWorker?.Id,
            string.Empty,
            job.TimeoutMinutes,
            Enabled: false,
            PortableTriggers(job.Triggers),
            job.LaunchMinimized,
            actions.Select(action => action.Kind == JobActionKind.Worker
                ? new JobActionRequest(null, JobActionKind.Worker, targetWorker!.Id)
                : new JobActionRequest(
                    null,
                    JobActionKind.Script,
                    null,
                    action.ScriptPath,
                    action.ScriptRuntime,
                    action.Arguments.ToList(),
                    action.WorkingDirectory,
                    action.TimeoutSeconds)).ToList(),
            // Always the root of the chain: importing a copy still points at the original, so
            // every copy of one Automation shares one origin however it travelled.
            ImportedFromJobId: job.ImportedFromJobId ?? job.Id);

        JobResponse created;
        try
        {
            // Llm and Prompt are resolved from the Worker inside CreateJobAsync; the hashes are
            // deliberately absent so NormalizeAsync pins the bytes now sitting in this repository.
            created = await jobService.CreateJobAsync(createRequest, cancellationToken);
        }
        catch (Exception) when (cloned)
        {
            await RollbackCloneAsync(targetWorker!);
            throw;
        }

        return new AutomationImportResponse(
            created,
            sourceWorker is null
                ? AutomationImportWorkerOutcome.None
                : cloned ? AutomationImportWorkerOutcome.Created : AutomationImportWorkerOutcome.Reused,
            targetWorker?.CustomName,
            copiedScripts,
            createdDirectories);
    }

    // ----- Catalog helpers -----

    /// <summary>
    /// An imported Automation is an ordinary row in its target repository, so without this every
    /// repository that imported "Nightly review" would offer it straight back to its source, once
    /// per repository (VB-33). A copy is hidden while its origin is still a live Automation
    /// anywhere on this machine: the origin is either the current repository (nothing to offer) or
    /// listed under its own repository. Once the origin is gone the copies are the only thing left
    /// to import from, so exactly one of them, the oldest, is offered per origin.
    /// </summary>
    private static List<JobDefinitionRecord> WithoutRedundantCopies(
        IEnumerable<JobDefinitionRecord> foreign,
        IReadOnlyList<JobDefinitionRecord> liveJobs)
    {
        var liveIds = liveJobs.Where(job => job.DeletedUtc is null).Select(job => job.Id).ToHashSet();
        var offeredOrigins = new HashSet<long>();
        var kept = new List<JobDefinitionRecord>();
        foreach (var job in foreign.OrderBy(job => job.CreatedUtc).ThenBy(job => job.Id))
        {
            if (job.ImportedFromJobId is not long origin)
            {
                kept.Add(job);
                continue;
            }
            if (liveIds.Contains(origin))
                continue;
            if (offeredOrigins.Add(origin))
                kept.Add(job);
        }
        return kept;
    }

    private AutomationImportCatalogEntry BuildEntry(
        JobDefinitionRecord job,
        string sourceRoot,
        bool sourceDirectoryExists,
        string displayName,
        string currentRoot,
        string targetRepositoryName,
        List<LLM_Environment> environments,
        Dictionary<int, LLM_Environment> environmentsById,
        Dictionary<int, List<EnvironmentStep>> stepsByEnvironment)
    {
        var actions = ResolveActions(job);
        string? blocker = actions.Count == 0 ? "The source Automation has no actions." : null;

        AutomationImportWorker? worker = null;
        var workerAction = actions.FirstOrDefault(action => action.Kind == JobActionKind.Worker);
        if (workerAction is not null)
        {
            if (workerAction.EnvironmentId is int environmentId
                && environmentsById.TryGetValue(environmentId, out var environment))
            {
                var reusable = FindReusableWorker(environments, environment, currentRoot);
                worker = new AutomationImportWorker(
                    environment.Id,
                    environment.CustomName,
                    environment.LLM,
                    LlmParser.ToWireName(environment.LLM),
                    environment.CustomArgs ?? string.Empty,
                    environment.CustomPrompt ?? string.Empty,
                    (int)environment.WorkspaceMode,
                    ToStepDtos(stepsByEnvironment.GetValueOrDefault(environment.Id)),
                    reusable?.Id,
                    reusable?.CustomName,
                    SuggestCloneName(environment.CustomName, targetRepositoryName, environments.Select(item => item.CustomName)));
            }
            else
            {
                blocker ??= "The source Worker no longer exists.";
            }
        }

        var actionDtos = actions.Select(action => action.Kind == JobActionKind.Worker
            ? new AutomationImportAction(JobActionKind.Worker, null, null, [], null, null, false, false)
            : new AutomationImportAction(
                JobActionKind.Script,
                action.ScriptPath,
                action.ScriptRuntime,
                action.Arguments.ToList(),
                action.WorkingDirectory,
                action.TimeoutSeconds,
                scriptService.ScriptExists(currentRoot, action.ScriptPath),
                sourceDirectoryExists && scriptService.ScriptExists(sourceRoot, action.ScriptPath))).ToList();

        if (blocker is null)
        {
            var missing = actionDtos
                .Where(action => action.Kind == JobActionKind.Script
                    && !action.ExistsInTargetRepository
                    && !action.ExistsInSourceRepository)
                .Select(action => action.ScriptPath ?? "(missing path)")
                .ToList();
            if (missing.Count > 0)
            {
                blocker = missing.Count == 1
                    ? $"Script not found in either repository: {missing[0]}"
                    : $"Scripts not found in either repository: {string.Join(", ", missing)}";
            }
        }

        return new AutomationImportCatalogEntry(
            job.Id,
            job.Name,
            sourceRoot,
            displayName,
            sourceDirectoryExists,
            job.Enabled,
            job.UpdatedUtc,
            worker,
            actionDtos,
            job.TimeoutMinutes,
            job.LaunchMinimized,
            PortableTriggers(job.Triggers),
            blocker is null,
            blocker);
    }

    private async Task<string> ResolveDisplayNameAsync(string projectPath, CancellationToken cancellationToken)
    {
        try
        {
            var name = await repository.GetProjectDisplayNameAsync(projectPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Jobs] Could not resolve a display name for {ProjectPath}", projectPath);
        }

        var folder = Path.GetFileName(projectPath);
        return string.IsNullOrWhiteSpace(folder) ? projectPath : folder;
    }

    /// <summary>
    /// A Worker the target repository can already see with the same name and CLI. Same rule the
    /// file-based recipe import applies client-side, evaluated here because the browser cannot
    /// see other repositories' environments.
    /// </summary>
    private static LLM_Environment? FindReusableWorker(
        IEnumerable<LLM_Environment> environments,
        LLM_Environment source,
        string currentRoot) =>
        environments.FirstOrDefault(candidate =>
            candidate.LLM == source.LLM
            && string.Equals(candidate.CustomName, source.CustomName, StringComparison.OrdinalIgnoreCase)
            && ProjectPathComparer.IsVisibleIn(candidate.ProjectPath, currentRoot));

    /// <summary>
    /// Legacy rows carry the Worker on the Automation itself rather than as an action; they are
    /// presented and imported as a one-Worker workflow exactly as <c>JobService</c> treats them.
    /// </summary>
    private static List<JobActionRecord> ResolveActions(JobDefinitionRecord job)
    {
        if (job.Actions is { Count: > 0 })
            return job.Actions.OrderBy(action => action.Position).ToList();
        if (job.EnvironmentId is int environmentId)
        {
            return
            [
                new JobActionRecord(
                    Guid.NewGuid().ToString(), job.Id, 0, JobActionKind.Worker, environmentId,
                    job.EnvironmentName, job.Llm, null, null, [], null, null, null)
            ];
        }
        return [];
    }

    private static List<JobTriggerRequest> PortableTriggers(IEnumerable<JobTriggerDto> triggers) =>
        triggers
            .Where(trigger => trigger.Kind is JobTriggerKind.Schedule or JobTriggerKind.Commit or JobTriggerKind.PreCommit)
            .Select(trigger => new JobTriggerRequest(
                trigger.Kind,
                trigger.ScheduleKind,
                trigger.IntervalMinutes,
                trigger.LocalTime,
                trigger.DaysOfWeekMask,
                trigger.TimeZoneId))
            .ToList();

    private static List<EnvironmentStepDto> ToStepDtos(IEnumerable<EnvironmentStep>? steps) =>
        steps?
            .OrderBy(step => step.Phase)
            .ThenBy(step => step.Position)
            .Select(step => new EnvironmentStepDto(
                step.Id,
                (int)step.Phase,
                step.Position,
                step.Name,
                step.Command,
                step.StartMinimized,
                step.TimeoutSeconds,
                step.Enabled))
            .ToList() ?? [];

    /// <summary>
    /// "&lt;source name&gt; - &lt;target repository folder&gt;", reduced to the characters
    /// <c>EnvironmentNameValidator</c> accepts and made unique against every existing environment
    /// name (they are globally unique, case-insensitively, because each is a directory).
    /// </summary>
    internal static string SuggestCloneName(
        string sourceName,
        string targetRepositoryName,
        IEnumerable<string> existingNames)
    {
        var candidate = SanitizeName($"{sourceName} - {targetRepositoryName}");
        if (candidate.Length == 0)
            candidate = SanitizeName(sourceName);
        if (candidate.Length == 0)
            candidate = "Worker";

        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var result = candidate;
        for (var attempt = 2; taken.Contains(result); attempt++)
        {
            var suffix = $" {attempt}";
            var stem = candidate.Length + suffix.Length > MaxEnvironmentNameLength
                ? candidate[..(MaxEnvironmentNameLength - suffix.Length)].TrimEnd(' ', '-', '_')
                : candidate;
            result = stem + suffix;
        }
        return result;
    }

    private static string SanitizeName(string value)
    {
        // Anything the validator rejects (dots, parentheses, slashes, …) becomes a space so
        // "Nightly (v2)" reads as "Nightly v2" rather than sprouting hyphens.
        var text = new string(value
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or ' ' ? ch : ' ')
            .ToArray());
        text = RepeatedHyphens().Replace(text, "-");
        text = RepeatedSpaces().Replace(text, " ");
        text = text.Trim(' ', '-', '_');
        while (text.Length > 0 && !char.IsAsciiLetterOrDigit(text[0]))
            text = text[1..].TrimStart(' ', '-', '_');
        if (text.Length > MaxEnvironmentNameLength)
            text = text[..MaxEnvironmentNameLength].TrimEnd(' ', '-', '_');
        return text;
    }

    // ----- Import helpers -----

    private async Task<LLM_Environment> CloneWorkerAsync(
        LLM_Environment source,
        IReadOnlyList<EnvironmentStep> sourceSteps,
        string cloneName,
        string currentRoot,
        CancellationToken cancellationToken)
    {
        // Fresh ids: the save path is delete-all + reinsert keyed on (EnvironmentId, Id), and a
        // prompt token must point at the clone's own step, never back at the source's.
        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<EnvironmentStep>(sourceSteps.Count);
        foreach (var step in sourceSteps.OrderBy(step => step.Phase).ThenBy(step => step.Position))
        {
            var newId = Guid.NewGuid().ToString();
            idMap[NormalizeStepId(step.Id)] = newId;
            steps.Add(new EnvironmentStep
            {
                Id = newId,
                Phase = step.Phase,
                Position = step.Position,
                Name = step.Name,
                Command = step.Command,
                StartMinimized = step.StartMinimized,
                TimeoutSeconds = step.TimeoutSeconds,
                Enabled = step.Enabled
            });
        }

        var now = DateTime.UtcNow;
        var environment = new LLM_Environment
        {
            LLM = source.LLM,
            CustomName = cloneName,
            CustomArgs = source.CustomArgs ?? string.Empty,
            CustomPrompt = RemapStepPlaceholders(source.CustomPrompt ?? string.Empty, idMap),
            Hidden = false,
            // Installed through the Automation flow, so it is a Worker: listed by the Worker
            // picker, excluded from launch pickers — the same as a recipe import.
            AutomationWorker = true,
            WorkspaceMode = source.WorkspaceMode,
            ProjectPath = currentRoot,
            CreatedUTC = now,
            LastUsedUTC = now
        };

        await environmentService.CreateEnvironmentAsync(environment, cancellationToken);
        await repository.SaveEnvironmentAsync(environment, cancellationToken);
        if (steps.Count > 0)
            await repository.ReplaceStepsAsync(environment.Id, steps, cancellationToken);
        return environment;
    }

    /// <summary>
    /// Rewrites every <c>{{step:&lt;id&gt;}}</c> whose id is a cloned step to the clone's id. A
    /// token for a step that does not exist on the source stays as written and keeps resolving
    /// to the deleted-step text, exactly as it did there.
    /// </summary>
    internal static string RemapStepPlaceholders(string prompt, IReadOnlyDictionary<string, string> idMap)
    {
        if (prompt.Length == 0 || idMap.Count == 0)
            return prompt;
        return StepTokenRegex().Replace(prompt, match =>
            idMap.TryGetValue(NormalizeStepId(match.Groups[1].Value), out var replacement)
                ? "{{step:" + replacement + "}}"
                : match.Value);
    }

    private static string NormalizeStepId(string id) =>
        Guid.TryParse(id, out var guid) ? guid.ToString() : id;

    /// <summary>
    /// A script's working directory must exist for the Automation to save. An empty directory is
    /// invisible to Git, so creating it is the smallest change that makes the import land.
    /// </summary>
    private static bool EnsureWorkingDirectory(string currentRoot, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)
            || workingDirectory.Trim() is "." or "./" or @".\")
        {
            return false;
        }

        var raw = workingDirectory.Trim();
        if (Path.IsPathRooted(raw) || raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
            throw JobServiceException.BadRequest("Script working directories must be relative to the repository.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(currentRoot, raw.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw JobServiceException.BadRequest("Script working directory is invalid.");
        }

        var prefix = Path.EndsInDirectorySeparator(currentRoot) ? currentRoot : currentRoot + Path.DirectorySeparatorChar;
        if (!fullPath.Equals(currentRoot, PathComparison) && !fullPath.StartsWith(prefix, PathComparison))
            throw JobServiceException.BadRequest("Script working directory must stay inside the current repository.");
        if (Directory.Exists(fullPath))
            return false;
        if (File.Exists(fullPath))
            throw JobServiceException.BadRequest($"Script working directory '{raw}' is a file in this repository.");

        Directory.CreateDirectory(fullPath);
        return true;
    }

    /// <summary>
    /// Best effort only. The clone was made a moment ago and no Automation references it (the
    /// create that would have is what failed), so the guarded delete is expected to succeed; if
    /// it does not, the Worker is left for the user to remove and the original error still wins.
    /// </summary>
    private async Task RollbackCloneAsync(LLM_Environment environment)
    {
        try
        {
            await Routes.EnvironmentRoutes.TryDeleteEnvironmentSafelyAsync(
                environmentService, store, environment, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "[Jobs] The Automation import failed after cloning Worker {WorkerName}; the clone could not be removed",
                environment.CustomName);
        }
    }

    private static string NormalizeProjectPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    // Same shape PromptPlaceholderService recognises: a step token with a loose hex-and-dashes
    // argument, validated through Guid.TryParse at lookup time.
    [GeneratedRegex(@"\{\{\s*step\s*:\s*([0-9a-fA-F\-]+)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex StepTokenRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedHyphens();

    [GeneratedRegex(" {2,}")]
    private static partial Regex RepeatedSpaces();
}
