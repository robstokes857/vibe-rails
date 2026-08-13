using VibeRails.DB;
using VibeRails.DTOs;
using Serilog;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Workspaces;
using VibeRails.Utils;

namespace VibeRails.Routes;

public static class EnvironmentRoutes
{
    // Mirrors the resume-summary cap in TerminalRoutes; keeps the launch command
    // line tractable and matches what's already enforced for the resume path.
    private const int MaxCustomPromptLength = 6000;

    public static void Map(WebApplication app)
    {
        // GET /api/v1/environments - List all custom environments (excludes defaults)
        app.MapGet("/api/v1/environments", async (
            IRepository repository,
            CancellationToken cancellationToken) =>
        {
            var projectPath = ParserConfigs.GetRootPath();
            var environments = await repository.GetCustomEnvironmentsAsync(cancellationToken);

            // One query for every workspace in this project, indexed by owner, rather than a
            // per-environment lookup while building the list.
            var workspacesByEnvironment = (await repository.GetSandboxesByProjectAsync(projectPath, cancellationToken))
                .Where(s => s.EnvironmentId.HasValue)
                .GroupBy(s => s.EnvironmentId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CreatedUTC).First());

            var visible = environments
                .Where(e => ProjectPathComparer.IsVisibleIn(e.ProjectPath, projectPath))
                .ToList();

            // One bulk read for every visible environment's steps, indexed by owner — same reason
            // as the workspace lookup above: the list must not become an N+1.
            var stepsByEnvironment = await repository.GetStepsForEnvironmentsAsync(
                visible.Select(e => e.Id).ToList(),
                cancellationToken);

            var response = visible
                .Select(e => ToResponse(
                    e,
                    workspacesByEnvironment.TryGetValue(e.Id, out var workspace) ? workspace : null,
                    stepsByEnvironment.TryGetValue(e.Id, out var steps) ? steps : null))
                .ToList();

            return Results.Ok(new EnvironmentListResponse(response));
        }).WithName("GetEnvironments");

        // GET /api/v1/environments/{name} - Fetch a single environment by name.
        // Includes default environments (the LIST endpoint hides them) so launchers
        // can resolve any env the UI exposes through the launch flow.
        app.MapGet("/api/v1/environments/{name}", async (
            IRepository repository,
            IRunWorkspaceService workspaceService,
            string name,
            CancellationToken cancellationToken) =>
        {
            var environment = await repository.FindEnvironmentByNameAsync(name, cancellationToken);

            if (environment == null || !IsVisibleHere(environment))
            {
                return Results.NotFound(new ErrorResponse($"Environment not found: {name}"));
            }

            var workspace = await workspaceService.GetWorkspaceAsync(environment, cancellationToken);
            var steps = await repository.GetStepsForEnvironmentAsync(environment.Id, cancellationToken);
            return Results.Ok(ToResponse(environment, workspace, steps));
        }).WithName("GetEnvironmentByName");

        // POST /api/v1/environments - Create new environment
        app.MapPost("/api/v1/environments", async (
            LlmCliEnvironmentService envService,
            ILlmParser llmParser,
            IRepository repository,
            CreateEnvironmentRequest request,
            CancellationToken cancellationToken) =>
        {
            // request.Name flows directly into ~/.vibe_rails/envs/{Name} and into the
            // CLAUDE_CONFIG_DIR / CODEX_HOME / XDG_*_HOME env vars for spawned CLIs.
            // Reject path traversal, separators, control chars, etc. before doing
            // anything else.
            var nameError = EnvironmentNameValidator.Validate(request.Name);
            if (nameError != null)
            {
                return Results.BadRequest(new ErrorResponse(nameError));
            }

            // `vb --env <value>` resolves a CLI name before it ever looks up an environment, so an
            // environment named after one is unreachable: the launch silently starts the base CLI
            // with none of the environment's config. Note that ILlmParser accepts the enum's
            // numeric values too, so a name of "3" shadows a CLI just as effectively as "Claude".
            var shadowedCli = llmParser.Parse(request.Name.Trim());
            if (shadowedCli != LLM.NotSet)
            {
                return Results.BadRequest(new ErrorResponse(
                    $"'{request.Name.Trim()}' is the name of a built-in CLI ({shadowedCli}), so an environment " +
                    "with that name could never be launched. Pick a different name."));
            }

            if (string.IsNullOrEmpty(request.Cli))
            {
                return Results.BadRequest(new ErrorResponse("CLI type is required"));
            }

            var llm = llmParser.Parse(request.Cli);

            if (llm == LLM.NotSet)
            {
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {request.Cli}"));
            }

            // LLM.Shell is a terminal-only type — there is no per-environment config to manage
            // for a bare shell, and CreateEnvironmentAsync would throw. Reject with 400.
            if (llm == LLM.Shell)
            {
                return Results.BadRequest(new ErrorResponse("The plain shell terminal cannot back a custom environment."));
            }

            var argsError = ShellArgSanitizer.Validate(request.CustomArgs);
            if (argsError != null)
            {
                return Results.BadRequest(new ErrorResponse(argsError));
            }

            if ((request.CustomPrompt?.Length ?? 0) > MaxCustomPromptLength)
            {
                return Results.BadRequest(new ErrorResponse($"CustomPrompt exceeds {MaxCustomPromptLength} character limit."));
            }

            if (!TryParseWorkspaceMode(request.WorkspaceMode, out var workspaceMode))
            {
                return Results.BadRequest(new ErrorResponse(UnknownWorkspaceModeMessage(request.WorkspaceMode)));
            }

            // Validated before the environment directory is created, so a bad step list cannot
            // leave a half-made environment behind.
            if (!EnvironmentStepRoutes.TryBuildSteps(request.Steps, out var newSteps, out var stepError))
            {
                return Results.BadRequest(new ErrorResponse(stepError!));
            }

            var projectPath = ParserConfigs.GetRootPath();
            if (workspaceMode != EnvironmentWorkspaceMode.Project && !ParserConfigs.GetIsInGit())
            {
                return Results.BadRequest(new ErrorResponse(
                    "This workspace mode clones the project, so it needs the current project to be a git repository."));
            }

            // Reject case-insensitive name collisions across all LLMs. CustomName
            // becomes the path segment under ~/.vibe_rails/envs/ which is
            // case-insensitive on Windows ("Nightly" and "nightly" resolve to the
            // same directory). Per-LLM subdirectories inside that root would then
            // share credentials with whichever existing env grabbed the path first.
            var trimmedName = request.Name.Trim();
            var collision = await repository.FindEnvironmentByNameIgnoreCaseAsync(trimmedName, cancellationToken);
            if (collision != null)
            {
                return Results.Conflict(new ErrorResponse(
                    $"An environment named '{collision.CustomName}' already exists (matches case-insensitively because the name maps to a shared directory)."));
            }

            var environment = new LLM_Environment
            {
                LLM = llm,
                CustomName = trimmedName,
                CustomArgs = request.CustomArgs ?? "",
                CustomPrompt = request.CustomPrompt ?? "",
                Hidden = request.Hidden,
                AutomationWorker = request.AutomationWorker,
                WorkspaceMode = workspaceMode,
                // Every environment created from now on belongs to the project it was created
                // in. Existing rows keep a null scope and stay visible everywhere.
                ProjectPath = projectPath,
                CreatedUTC = DateTime.UtcNow,
                LastUsedUTC = DateTime.UtcNow
            };

            await envService.CreateEnvironmentAsync(environment, cancellationToken);
            await repository.SaveEnvironmentAsync(environment, cancellationToken);

            // After SaveEnvironmentAsync: the steps carry the FK, so the environment row has to
            // exist first. Skipped entirely when the client sent no steps.
            if (request.Steps != null)
            {
                await repository.ReplaceStepsAsync(environment.Id, newSteps, cancellationToken);
            }

            var savedSteps = request.Steps != null
                ? await repository.GetStepsForEnvironmentAsync(environment.Id, cancellationToken)
                : [];

            // The clone itself is deliberately NOT made here. Creating an environment stays a
            // fast, local operation; the first launch pays the clone, where there is already a
            // progress surface and where a failure can be reported against that launch.
            return Results.Ok(ToResponse(environment, workspace: null, savedSteps));
        }).WithName("CreateEnvironment");

        // PUT /api/v1/environments/{name} - Update environment
        app.MapPut("/api/v1/environments/{name}", async (
            IRepository repository,
            IJobStore jobStore,
            IRunWorkspaceService workspaceService,
            string name,
            UpdateEnvironmentRequest request,
            CancellationToken cancellationToken) =>
        {
            var environment = await repository.FindEnvironmentByNameAsync(name, cancellationToken);

            if (environment == null || !IsVisibleHere(environment))
            {
                return Results.NotFound(new ErrorResponse($"Environment not found: {name}"));
            }

            if (request.CustomArgs != null)
            {
                var argsError = ShellArgSanitizer.Validate(request.CustomArgs);
                if (argsError != null)
                {
                    return Results.BadRequest(new ErrorResponse(argsError));
                }
                environment.CustomArgs = request.CustomArgs;
            }

            if (request.CustomPrompt != null)
            {
                if (request.CustomPrompt.Length > MaxCustomPromptLength)
                {
                    return Results.BadRequest(new ErrorResponse($"CustomPrompt exceeds {MaxCustomPromptLength} character limit."));
                }
                if (string.IsNullOrWhiteSpace(request.CustomPrompt)
                    && await jobStore.CountJobsForEnvironmentAsync(environment.Id, cancellationToken) > 0)
                {
                    return Results.BadRequest(new ErrorResponse(
                        "This Environment is used by an Automation, so its Initial Message cannot be empty."));
                }
                environment.CustomPrompt = request.CustomPrompt;
            }

            if (request.Hidden.HasValue)
            {
                environment.Hidden = request.Hidden.Value;
            }

            // null means "leave them untouched" — the steps editor is a separate modal, so an env
            // form saved without opening it must not silently wipe the list. Validated before any
            // write so a bad step list rejects the whole update rather than half-applying it.
            if (!EnvironmentStepRoutes.TryBuildSteps(request.Steps, out var updatedSteps, out var stepError))
            {
                return Results.BadRequest(new ErrorResponse(stepError!));
            }

            var workspaceModeChanged = false;
            if (request.WorkspaceMode.HasValue)
            {
                if (!TryParseWorkspaceMode(request.WorkspaceMode.Value, out var requestedMode))
                {
                    return Results.BadRequest(new ErrorResponse(UnknownWorkspaceModeMessage(request.WorkspaceMode.Value)));
                }

                if (requestedMode != EnvironmentWorkspaceMode.Project && !ParserConfigs.GetIsInGit())
                {
                    return Results.BadRequest(new ErrorResponse(
                        "This workspace mode clones the project, so it needs the current project to be a git repository."));
                }

                workspaceModeChanged = requestedMode != environment.WorkspaceMode;
                environment.WorkspaceMode = requestedMode;
            }

            environment.LastUsedUTC = DateTime.UtcNow;
            await repository.UpdateEnvironmentAsync(environment, cancellationToken);

            if (request.Steps != null)
            {
                await repository.ReplaceStepsAsync(environment.Id, updatedSteps, cancellationToken);
            }

            // A mode change detaches the old workspace but never deletes it. The user may have
            // uncommitted work in there, and changing a dropdown is not consent to destroy it —
            // it becomes a standalone sandbox they can diff, push, or delete deliberately.
            // (Deleting the environment is different, and does try to remove the directory.)
            if (workspaceModeChanged)
            {
                await workspaceService.DetachAsync(environment.Id, cancellationToken);
            }

            var workspace = await workspaceService.GetWorkspaceAsync(environment, cancellationToken);
            var steps = await repository.GetStepsForEnvironmentAsync(environment.Id, cancellationToken);
            return Results.Ok(ToResponse(environment, workspace, steps));
        }).WithName("UpdateEnvironment");

        // DELETE /api/v1/environments/{name} - Delete environment
        app.MapDelete("/api/v1/environments/{name}", async (
            LlmCliEnvironmentService envService,
            IRepository repository,
            IJobStore jobStore,
            IServiceScopeFactory scopeFactory,
            string name,
            CancellationToken cancellationToken) =>
        {
            var environment = await repository.FindEnvironmentByNameAsync(name, cancellationToken);

            if (environment == null || !IsVisibleHere(environment))
            {
                return Results.NotFound(new ErrorResponse($"Environment not found: {name}"));
            }

            // Prevent deletion of default environments
            if (environment.CustomName == "Default")
            {
                return Results.BadRequest(new ErrorResponse("Cannot delete default environments"));
            }

            // Count ALL referencing jobs, not just enabled ones: jobs default to disabled, so an
            // "enabled only" guard let a freshly created job silently have its EnvironmentId nulled
            // (ON DELETE SET NULL) when the environment was deleted.
            var referencingJobCount = await jobStore.CountJobsForEnvironmentAsync(environment.Id, cancellationToken);
            if (referencingJobCount > 0)
            {
                return Results.Conflict(new ErrorResponse(EnvironmentInUseMessage(referencingJobCount)));
            }

            try
            {
                if (!await TryDeleteEnvironmentSafelyAsync(
                    envService,
                    jobStore,
                    environment,
                    cancellationToken))
                {
                    referencingJobCount = await jobStore.CountJobsForEnvironmentAsync(environment.Id, cancellationToken);
                    return referencingJobCount > 0
                        ? Results.Conflict(new ErrorResponse(EnvironmentInUseMessage(referencingJobCount)))
                        : Results.Conflict(new ErrorResponse(
                            "The environment changed while it was being deleted. Refresh and try again."));
                }
            }
            catch (LlmCliEnvironmentService.EnvironmentDeletionInProgressException)
            {
                return Results.Conflict(new ErrorResponse(
                    "This environment is already being deleted. Refresh and try again."));
            }

            // Only after the environment row is genuinely gone. ReleaseAsync orphans first and
            // then tries to remove each workspace directory; a clone still locked by a running
            // CLI simply stays behind as a standalone sandbox instead of failing this delete.
            //
            // Not awaited into the response — a multi-GB tree takes a while to remove and the
            // durable outcome (the rows are released) is already committed. It therefore needs
            // its OWN scope: the request scope, and every scoped service resolved from it, is
            // disposed the moment this handler returns.
            ReleaseWorkspacesInBackground(scopeFactory, environment.Id);

            return Results.Ok(new OK("Environment deleted"));
        }).WithName("DeleteEnvironment");
    }

    /// <summary>
    /// Removes an environment's workspace directories after its delete has already been
    /// committed and answered. Runs in a fresh DI scope because the request scope is gone by
    /// then, and swallows nothing silently — a workspace that will not delete is already safe
    /// (it was orphaned first), so the only thing left to do about a failure is log it.
    /// </summary>
    private static void ReleaseWorkspacesInBackground(IServiceScopeFactory scopeFactory, int environmentId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var workspaceService = scope.ServiceProvider.GetRequiredService<IRunWorkspaceService>();
                await workspaceService.ReleaseAsync(environmentId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "[Environments] Background workspace release failed for environment {EnvironmentId}; its workspaces remain as standalone sandboxes",
                    environmentId);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Whether this request's project may see the environment at all.
    ///
    /// The name lookups behind GET/PUT/DELETE are global — the unique key is (CustomName, LLM),
    /// with no project in it — so without this a request could read, edit, or delete an
    /// environment belonging to another project simply by naming it. Answering 404 rather than
    /// 403 keeps the two cases indistinguishable: whether a name exists elsewhere is itself
    /// information this project has no business learning.
    ///
    /// A null scope is an environment that predates project scoping and stays visible
    /// everywhere — the no-backfill guarantee.
    /// </summary>
    private static bool IsVisibleHere(LLM_Environment environment) =>
        ProjectPathComparer.IsVisibleIn(environment.ProjectPath, ParserConfigs.GetRootPath());

    private static EnvironmentResponse ToResponse(
        LLM_Environment environment,
        Sandbox? workspace = null,
        IEnumerable<EnvironmentStep>? steps = null) =>
        new(
            environment.Id,
            environment.CustomName,
            LlmParser.ToWireName(environment.LLM),
            environment.Path,
            environment.CustomArgs,
            environment.CustomPrompt,
            LLM_Environment.DefaultPrompt,
            environment.LastUsedUTC,
            environment.Hidden,
            environment.AutomationWorker,
            (int)environment.WorkspaceMode,
            workspace?.Id,
            workspace?.Path,
            workspace?.Branch,
            EnvironmentStepRoutes.ToDtos(steps));

    /// <summary>
    /// Parses a wire workspace mode. Explicit rather than casting the int straight to the enum:
    /// an unknown value from a newer or corrupted client must be a 400 the user can read, not a
    /// silently stored mode that no launch path knows how to honour.
    /// </summary>
    private static bool TryParseWorkspaceMode(int value, out EnvironmentWorkspaceMode mode)
    {
        if (Enum.IsDefined(typeof(EnvironmentWorkspaceMode), value))
        {
            mode = (EnvironmentWorkspaceMode)value;
            return true;
        }

        mode = EnvironmentWorkspaceMode.Project;
        return false;
    }

    private static string UnknownWorkspaceModeMessage(int value) =>
        $"Unknown workspace mode: {value}. Expected 0 (project directory), 1 (persistent clone), or 2 (fresh clone per run).";

    private static string EnvironmentInUseMessage(int automationCount) =>
        automationCount == 1
            ? "Cannot delete this Environment because 1 Automation uses it. Delete or reassign that Automation first."
            : $"Cannot delete this Environment because {automationCount} Automations use it. Delete or reassign those Automations first.";

    /// <summary>
    /// Coordinates the filesystem rename with the guarded database deletion. The database result
    /// is the irreversible boundary: after it succeeds, this method never restores or otherwise
    /// touches the original path because another request may already have recreated it.
    /// </summary>
    internal static async Task<bool> TryDeleteEnvironmentSafelyAsync(
        LlmCliEnvironmentService envService,
        IJobStore jobStore,
        LLM_Environment environment,
        CancellationToken cancellationToken)
    {
        LlmCliEnvironmentService.StagedEnvironmentDirectory? stagedDirectory = null;
        bool deleted;
        try
        {
            // JobStore takes BEGIN IMMEDIATE, proves this exact row still exists and has no live
            // jobs, then invokes the rename while the SQLite writer lock prevents environment/job
            // writers from racing the proof. A stale request never invokes this callback.
            deleted = await jobStore.TryDeleteEnvironmentIfUnusedAsync(
                environment.Id,
                () => stagedDirectory = envService.StageEnvironmentDirectoryForDeletion(environment),
                cancellationToken);
        }
        catch (EnvironmentDeleteRollbackException rollbackException)
        {
            // Commit/rollback outcome could not be proven. Leave any tombstone quarantined; an
            // attempted restore could overwrite a newly recreated environment after a commit.
            Log.Error(
                rollbackException,
                "[Environment] Could not confirm rollback for environment {EnvironmentId}; its staged directory remains quarantined.",
                environment.Id);
            throw;
        }
        catch
        {
            // JobStore only propagates an ordinary failure after confirming rollback. Restore the
            // same directory staged by this request; if staging never ran there is nothing to do.
            if (stagedDirectory != null)
            {
                try
                {
                    envService.RestoreStagedEnvironmentDirectory(stagedDirectory);
                }
                catch (Exception restoreException)
                {
                    Log.Error(
                        restoreException,
                        "[Environment] The guarded delete for environment {EnvironmentId} rolled back, but its staged directory could not be restored and remains quarantined.",
                        environment.Id);
                }
            }
            throw;
        }

        if (!deleted)
        {
            // The callback is contractually never invoked for a missing/referenced row. In
            // particular, a stale delete returns here without touching a replacement's path.
            return false;
        }

        if (stagedDirectory == null)
        {
            throw new InvalidOperationException(
                "The environment row was deleted without staging its filesystem directory.");
        }

        // Crossing the committed DB-delete boundary makes restoration unsafe. A cleanup error
        // leaves the unique tombstone for later/manual cleanup and still reports success.
        TryFinalizeStagedDirectory(envService, environment, stagedDirectory);
        return true;
    }

    private static void TryFinalizeStagedDirectory(
        LlmCliEnvironmentService envService,
        LLM_Environment environment,
        LlmCliEnvironmentService.StagedEnvironmentDirectory stagedDirectory)
    {
        try
        {
            envService.FinalizeStagedEnvironmentDirectoryDeletion(stagedDirectory);
        }
        catch (Exception cleanupException)
        {
            Log.Error(
                cleanupException,
                "[Environment] Environment {EnvironmentId} was deleted, but its staged directory could not be cleaned up and remains quarantined.",
                environment.Id);
        }
    }
}
