using VibeRails.DB;
using VibeRails.DTOs;
using Serilog;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
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
            var environments = await repository.GetCustomEnvironmentsAsync(cancellationToken);
            var response = environments
                .Select(e => new EnvironmentResponse(
                    e.Id,
                    e.CustomName,
                    LlmParser.ToWireName(e.LLM),
                    e.Path,
                    e.CustomArgs,
                    e.CustomPrompt,
                    LLM_Environment.DefaultPrompt,
                    e.LastUsedUTC
                ))
                .ToList();

            return Results.Ok(new EnvironmentListResponse(response));
        }).WithName("GetEnvironments");

        // GET /api/v1/environments/{name} - Fetch a single environment by name.
        // Includes default environments (the LIST endpoint hides them) so launchers
        // can resolve any env the UI exposes through the launch flow.
        app.MapGet("/api/v1/environments/{name}", async (
            IRepository repository,
            string name,
            CancellationToken cancellationToken) =>
        {
            var environment = await repository.FindEnvironmentByNameAsync(name, cancellationToken);

            if (environment == null)
            {
                return Results.NotFound(new ErrorResponse($"Environment not found: {name}"));
            }

            return Results.Ok(new EnvironmentResponse(
                environment.Id,
                environment.CustomName,
                LlmParser.ToWireName(environment.LLM),
                environment.Path,
                environment.CustomArgs,
                environment.CustomPrompt,
                LLM_Environment.DefaultPrompt,
                environment.LastUsedUTC
            ));
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
                CreatedUTC = DateTime.UtcNow,
                LastUsedUTC = DateTime.UtcNow
            };

            await envService.CreateEnvironmentAsync(environment, cancellationToken);
            await repository.SaveEnvironmentAsync(environment, cancellationToken);

            return Results.Ok(new EnvironmentResponse(
                environment.Id,
                environment.CustomName,
                LlmParser.ToWireName(environment.LLM),
                environment.Path,
                environment.CustomArgs,
                environment.CustomPrompt,
                LLM_Environment.DefaultPrompt,
                environment.LastUsedUTC
            ));
        }).WithName("CreateEnvironment");

        // PUT /api/v1/environments/{name} - Update environment
        app.MapPut("/api/v1/environments/{name}", async (
            IRepository repository,
            IJobStore jobStore,
            string name,
            UpdateEnvironmentRequest request,
            CancellationToken cancellationToken) =>
        {
            var environment = await repository.FindEnvironmentByNameAsync(name, cancellationToken);

            if (environment == null)
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

            environment.LastUsedUTC = DateTime.UtcNow;
            await repository.UpdateEnvironmentAsync(environment, cancellationToken);

            return Results.Ok(new EnvironmentResponse(
                environment.Id,
                environment.CustomName,
                LlmParser.ToWireName(environment.LLM),
                environment.Path,
                environment.CustomArgs,
                environment.CustomPrompt,
                LLM_Environment.DefaultPrompt,
                environment.LastUsedUTC
            ));
        }).WithName("UpdateEnvironment");

        // DELETE /api/v1/environments/{name} - Delete environment
        app.MapDelete("/api/v1/environments/{name}", async (
            LlmCliEnvironmentService envService,
            IRepository repository,
            IJobStore jobStore,
            string name,
            CancellationToken cancellationToken) =>
        {
            var environment = await repository.FindEnvironmentByNameAsync(name, cancellationToken);

            if (environment == null)
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

            return Results.Ok(new OK("Environment deleted"));
        }).WithName("DeleteEnvironment");
    }

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
