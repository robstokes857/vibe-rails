using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using VibeRails.Utils;
using VibeRails.Services.Terminal;

namespace VibeRails.Services.Launches;

/// <summary>Shared HTTP/MCP/domain boundary for definition saves and immutable run admission.</summary>
/// <remarks>
/// Without <paramref name="boards"/> (the git-hook enqueue path, which must never open board.db), a
/// preset snapshot leaves the nudge and App/Board context to the root that starts its session.
/// </remarks>
public sealed class LaunchService(ILaunchStore store, IAutomationScriptService scripts, IBoardStore? boards = null)
{
    /// <summary>Comparison and lease key. Upper-cased where paths compare case-insensitively; never a working directory.</summary>
    public static string ProjectKey(string project) => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? ProjectRoot(project).ToUpperInvariant()
        : ProjectRoot(project);

    /// <summary>The full path with its original casing, for anything that touches the filesystem or the user.</summary>
    public static string ProjectRoot(string project) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));

    public async Task<LaunchCatalog> CatalogAsync(string project, CancellationToken ct = default)
    {
        var presets = await store.ListPresetsAsync(ct);
        var actions = await store.ListActionsAsync(ct);
        var items = new List<LaunchCatalogItem>();
        foreach (var p in presets)
        {
            string? reason = p.RetiredUtc is not null ? "Retired" : !p.Enabled ? "Disabled" : null;
            try { _ = LaunchOptionBuilder.Normalize(p); }
            catch (LaunchValidationException ex) { reason ??= ex.Message; }
            items.Add(new(p.Id, "preset", p.Kind, p.Name, p.Description, p.Revision, p.Enabled, p.RetiredUtc is not null,
                LlmParser.ToWireName(p.Cli), reason is null, reason is null && p.Kind == "agent", reason, true));
        }
        foreach (var a in actions)
        {
            string? reason = a.RetiredUtc is not null ? "Retired" : !a.Enabled ? "Disabled" : null;
            try { await ValidateActionForProjectAsync(a, project, false, ct); }
            catch (Exception ex) when (ex is LaunchValidationException or AutomationScriptValidationException or ArgumentException) { reason ??= ex.Message; }
            items.Add(new(a.Id, "action", a.Kind, a.Name, a.Description, a.Revision, a.Enabled, a.RetiredUtc is not null,
                null, reason is null, reason is null, reason, false));
        }
        return new(items);
    }

    public async Task<SavedPreset> SavePresetAsync(string? id, SavePresetRequest request, CancellationToken ct = default)
    {
        if (id is not null && request.ExpectedRevision <= 0) throw new LaunchConflictException("Updates require the current positive revision.");
        if (id is null && request.ExpectedRevision != 0) throw new LaunchConflictException("New presets require revision 0.");
        var preset = LaunchOptionBuilder.Normalize(new(id ?? "preset_" + Guid.NewGuid().ToString("N"), request.Name,
            request.Description ?? "", LlmParser.ParseValue(request.Cli), request.Options ?? new(), request.AdditionalArgv ?? [],
            request.InitialPrompt, request.Enabled, 0, default, default, TimeoutMinutes: request.TimeoutMinutes, Purpose: request.Purpose));
        preset = await store.SavePresetAsync(preset, request.ExpectedRevision, ct);
        var affected = preset.Kind == "quick-launch" || !preset.Enabled
            ? await store.ListBindingsAsync(definitionId: preset.Id, ct: ct) : [];
        return new(preset, affected);
    }

    public async Task<LaunchAction> SaveActionAsync(string project, string? id, SaveActionRequest request, CancellationToken ct = default)
    {
        if (id is not null && request.ExpectedRevision <= 0) throw new LaunchConflictException("Updates require the current positive revision.");
        if (id is null && request.ExpectedRevision != 0) throw new LaunchConflictException("New Actions require revision 0.");
        var action = new LaunchAction(id ?? "action_" + Guid.NewGuid().ToString("N"), request.Name?.Trim() ?? "",
            request.Description ?? "", request.Kind, request.Script, request.Check, request.Enabled, 0, default, default);
        ValidateAction(action);
        // Saving explicitly approves these bytes only in the current authorized project.
        string? hash = null;
        if (action.Script is { } script)
        {
            var normalized = await scripts.NormalizeAsync(project, new JobActionRequest(null, JobActionKind.Script,
                ScriptPath: script.Path, ScriptRuntime: script.Runtime, Arguments: script.Arguments.ToList(),
                WorkingDirectory: script.WorkingDirectory, TimeoutSeconds: script.TimeoutSeconds), ct);
            hash = normalized.ApprovedHash;
            action = action with { Script = new(normalized.ScriptPath!, normalized.ScriptRuntime!.Value,
                normalized.Arguments!.ToArray(), normalized.WorkingDirectory, normalized.TimeoutSeconds) };
        }
        action = await store.SaveApprovedActionAsync(action, request.ExpectedRevision, ProjectKey(project), hash, ct);
        return action;
    }

    public async Task RetireAsync(string type, string id, long expectedRevision, CancellationToken ct = default)
    {
        if (type == "preset")
        {
            var p = await store.GetPresetAsync(id, ct) ?? throw new LaunchValidationException("Preset not found.");
            await store.SavePresetAsync(p with { Enabled = false, RetiredUtc = DateTime.UtcNow }, expectedRevision, ct);
        }
        else if (type == "action")
        {
            var a = await store.GetActionAsync(id, ct) ?? throw new LaunchValidationException("Action not found.");
            await store.SaveActionAsync(a with { Enabled = false, RetiredUtc = DateTime.UtcNow }, expectedRevision, ct);
        }
        else throw new LaunchValidationException("Unknown definition type.");
    }

    public async Task<LaunchTriggerBinding> SaveBindingAsync(string project, string? id, SaveBindingRequest request, CancellationToken ct = default)
    {
        if (id is not null && request.ExpectedRevision <= 0 || id is null && request.ExpectedRevision != 0)
            throw new LaunchConflictException("New bindings require revision 0; updates require the current positive revision.");
        if (request.Trigger is null) throw new LaunchValidationException("A trigger is required.");
        if (string.IsNullOrWhiteSpace(request.DefinitionId) || request.DefinitionType is not ("preset" or "action"))
            throw new LaunchValidationException("Select a preset or Action by stable ID.");
        var error = JobScheduleCalculator.Validate(request.Trigger);
        if (error is not null) throw new LaunchValidationException(error);
        if (request.Trigger.Kind is not (JobTriggerKind.Schedule or JobTriggerKind.PreCommit or JobTriggerKind.Commit))
            throw new LaunchValidationException("Use a schedule, before-commit or after-commit binding. Board lanes own their own ordered selections.");
        if (request.Enabled) await SnapshotAsync(request.DefinitionType, request.DefinitionId,
            new(ProjectKey(project), project, "binding", "validation"), ct);
        var binding = new LaunchTriggerBinding(id ?? "binding_" + Guid.NewGuid().ToString("N"), request.DefinitionId,
            request.DefinitionType, ProjectKey(project), request.Trigger, request.Enabled, 0, default, default,
            request.Trigger.Kind == JobTriggerKind.Schedule ? JobScheduleCalculator.ComputeNext(request.Trigger, DateTime.UtcNow) : null,
            ProjectRoot(project));
        return await store.SaveBindingAsync(binding, request.ExpectedRevision, ct);
    }

    public async Task<LaunchRun> EnqueueAsync(string type, string id, LaunchExecutionContext context, CancellationToken ct = default, long? expectedRevision = null)
    {
        if (await store.FindOccurrenceAsync(ProjectKey(context.ProjectPath), context.OccurrenceId, ct) is { } existing)
        {
            if (existing.DefinitionId != id || existing.DefinitionType != type)
                throw new LaunchConflictException("This occurrence already belongs to a different definition.");
            LaunchValidation.ValidateSnapshot(existing.Snapshot);
            return existing;
        }
        var snapshot = await SnapshotAsync(type, id, context with
        {
            ProjectPath = ProjectKey(context.ProjectPath),
            ProjectRoot = context.ProjectRoot ?? ProjectRoot(context.ProjectPath)
        }, ct);
        if (expectedRevision is not null && snapshot.Revision != expectedRevision)
            throw new LaunchConflictException("Definition changed while composing the launch. Try again.", snapshot.Revision);
        return await store.EnqueueAsync(snapshot, ct: ct);
    }

    public async Task<LaunchRun> RetryAsync(string project, string id, CancellationToken ct = default)
    {
        var run = await GetRunAsync(project, id, ct) ?? throw new LaunchValidationException("Run not found in this project.");
        if (!run.IsTerminal) throw new LaunchConflictException("Wait for the original run to finish before retrying.");
        await ValidateSnapshotAsync(run.Snapshot, ct);
        var snapshot = run.Snapshot with { Context = run.Snapshot.Context with { OccurrenceId = "retry:" + Guid.NewGuid().ToString("N") } };
        var retry = await store.EnqueueAsync(snapshot, id, ct);
        if (snapshot.Context.LaneEntryId is { } priorEntry && boards is not null)
            await boards.UpdateLaunchEventAsync(priorEntry, id, run.Status, ct);
        if (snapshot.Context.LaneEntryId is { } entry && boards is not null
            && !await boards.RetryLaunchEventAsync(entry, id, retry.Id, ct))
        {
            await store.CancelAsync(retry.Id, retry.ProjectPath, CancellationToken.None);
            throw new LaunchConflictException("The lane entry changed or was skipped. This retry was cancelled.");
        }
        return retry;
    }

    /// <summary>Explicit recovery after a user has checked an unacknowledged script and stopped its descendants.</summary>
    public async Task<LaunchRun> ResolveUncertainAsync(string project, string id, bool confirmedStopped, CancellationToken ct = default)
    {
        var run = await GetRunAsync(project, id, ct) ?? throw new LaunchValidationException("Run not found in this project.");
        if (!confirmedStopped) throw new LaunchValidationException("Inspect the retained recording and stop any surviving child processes before confirming recovery.");
        if (run.Status != "uncertain" || run.ClaimToken is null)
            throw new LaunchConflictException("Only an uncertain run can be resolved.");
        if (LaunchScheduler.ProcessAlive(run.OwnerPid, run.OwnerStartedUtc) != false
            || run.ExecutionPid is not null && LaunchScheduler.ProcessAlive(run.ExecutionPid, run.ExecutionStartedUtc) != false)
            throw new LaunchConflictException("The recorded execution owner or child is still alive or cannot be verified.");
        await store.CompleteAsync(new(run.Id, run.ClaimToken), "interrupted",
            error: "User confirmed surviving child processes were stopped after inspecting the uncertain launch. Retry remains explicit.", ct: ct);
        return (await store.GetRunAsync(id, ct))!;
    }

    public async Task<LaunchRun?> GetRunAsync(string project, string id, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(id, ct);
        return run is not null && ProjectPathComparer.Matches(project, run.ProjectPath) ? run : null;
    }

    private async Task<LaunchSnapshot> SnapshotAsync(string type, string id, LaunchExecutionContext context, CancellationToken ct)
    {
        if (!Directory.Exists(context.WorkingDirectory)) throw new LaunchValidationException("The execution project is unavailable.");
        LaunchSnapshot snapshot;
        if (type == "preset")
        {
            var p = await store.GetPresetAsync(id, ct) ?? throw new LaunchValidationException("Preset is unavailable. Replace the saved selection.");
            if (!p.Enabled || p.RetiredUtc is not null) throw new LaunchValidationException("This preset is disabled or retired.");
            snapshot = new(context, LaunchOptionBuilder.Normalize(p), AttachmentIds: (await store.ListAttachmentsAsync(p.Id, ct)).Select(a => a.Id).ToArray());
        }
        else if (type == "action")
        {
            var a = await store.GetActionAsync(id, ct) ?? throw new LaunchValidationException("Action is unavailable. Replace the saved selection.");
            if (!a.Enabled || a.RetiredUtc is not null) throw new LaunchValidationException("This Action is disabled or retired.");
            var hash = await ValidateActionForProjectAsync(a, context.Root, false, ct);
            snapshot = new(context, Action: a, ApprovedScriptHash: hash);
        }
        else throw new LaunchValidationException("Unknown definition type.");
        if (snapshot.Preset is { } preset && context.ComposedPrompt is null)
        {
            var prompt = string.IsNullOrEmpty(context.PromptPrefix) ? context.PromptOverride ?? preset.InitialPrompt
                : context.PromptPrefix + "\n\n" + (context.PromptOverride ?? preset.InitialPrompt);
            if (boards is null)
            {
                // Enqueue-only hosts (git hooks) must keep working when board.db is busy, newer or
                // damaged, so they never open it. The root's session start adds the nudge and
                // App/Board context instead, as the retired Jobs hook path did (VIBE-296).
                snapshot = snapshot with { Context = context with { ComposedPrompt = prompt, ContextIncluded = false } };
            }
            else
            {
                var nudge = Config.LoadFresh().CreateVibeStoryTracking;
                if (nudge && string.IsNullOrWhiteSpace(prompt)) prompt = VibeStoryTrackingPrompt.Guidance;
                if (nudge)
                    prompt = VibeRails.Services.Terminal.ContextLevels.Prefix(await VibeRails.Services.Terminal.ContextLevels.ResolveNudgedAsync(boards, context.Root, context.BoardId, ct)) + prompt;
                snapshot = snapshot with { Context = context with { ComposedPrompt = prompt, ContextIncluded = true } };
            }
        }
        LaunchValidation.ValidateSnapshot(snapshot);
        return snapshot;
    }

    public async Task ValidateSnapshotAsync(LaunchSnapshot snapshot, CancellationToken ct = default)
    {
        LaunchValidation.ValidateSnapshot(snapshot);
        // Current retirement/disablement revokes execution; current prompt edits do not rewrite a queued Agent.
        if (snapshot.Preset is { } p)
        {
            _ = LaunchOptionBuilder.Normalize(p);
            var current = await store.GetPresetAsync(p.Id, ct);
            if (current is null || !current.Enabled || current.RetiredUtc is not null) throw new LaunchValidationException("The preset is now disabled or retired.");
        }
        if (snapshot.Action is { } a)
        {
            var current = await store.GetActionAsync(a.Id, ct);
            if (current is null || !current.Enabled || current.RetiredUtc is not null) throw new LaunchValidationException("The Action is now disabled or retired.");
            var hash = await ValidateActionForProjectAsync(a, snapshot.Context.Root, false, ct);
            if (hash != snapshot.ApprovedScriptHash) throw new LaunchValidationException("The approved script content no longer matches this run.");
        }
    }

    private async Task<string?> ValidateActionForProjectAsync(LaunchAction action, string project, bool approve, CancellationToken ct)
    {
        ValidateAction(action);
        if (action.Script is not { } s) return null;
        var normalized = await scripts.NormalizeAsync(project, new JobActionRequest(Id: null, Kind: JobActionKind.Script,
            ScriptPath: s.Path, ScriptRuntime: s.Runtime, Arguments: s.Arguments.ToList(), WorkingDirectory: s.WorkingDirectory,
            TimeoutSeconds: s.TimeoutSeconds), ct);
        if (!approve && !await store.IsScriptApprovedAsync(ProjectKey(project), action.Id, normalized.ApprovedHash!, ct))
            throw new LaunchValidationException("Review and save this script Action in the current project to approve its content.");
        return normalized.ApprovedHash;
    }

    public static void ValidateAction(LaunchAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Name) || action.Name.Length > 300 || action.Description?.Length > 100_000)
            throw new LaunchValidationException("Action name or description exceeds its limit.");
        if (action.Kind == "script" && action.Script is { } script && action.Check is null)
        {
            if (string.IsNullOrWhiteSpace(script.Path) || script.Arguments is null
                || script.Arguments.Length > 64 || script.Arguments.Any(a => a is null || a.Length > 8_000 || a.Any(char.IsControl)))
                throw new LaunchValidationException("A script requires a path and at most 64 valid argv values.");
            return;
        }
        if (action.Kind is "vca" or "code-quality" && action.Check is not null && action.Script is null)
        {
            if (string.IsNullOrWhiteSpace(action.Check.Scope)) throw new LaunchValidationException("A check scope is required.");
            try { _ = JobCheckScope.Parse(action.Kind == "vca" ? JobActionKind.Vca : JobActionKind.CodeQuality, CheckArguments(action.Check)); }
            catch (ArgumentException ex) { throw new LaunchValidationException(ex.Message); }
            return;
        }
        throw new LaunchValidationException("An Action contains exactly one script, VCA check or code-quality check.");
    }
    public static string[] CheckArguments(CheckAction check)
    {
        List<string> args = [check.Scope];
        if (check.Scope == "range") args.AddRange([check.BaseCommit ?? "", check.HeadCommit ?? ""]);
        else if (check.Scope == "commit") args.Add(check.HeadCommit ?? "");
        if (check.FailGrade is not null) args.AddRange(["minimum", check.FailGrade]);
        return args.ToArray();
    }
}
