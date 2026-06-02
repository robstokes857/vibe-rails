using VibeRails.DB;
using VibeRails.DTOs;
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
                    e.LLM.ToString(),
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
                environment.LLM.ToString(),
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
                environment.LLM.ToString(),
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
                environment.CustomPrompt = request.CustomPrompt;
            }

            environment.LastUsedUTC = DateTime.UtcNow;
            await repository.UpdateEnvironmentAsync(environment, cancellationToken);

            return Results.Ok(new EnvironmentResponse(
                environment.Id,
                environment.CustomName,
                environment.LLM.ToString(),
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

            await envService.DeleteEnvironmentAsync(environment, cancellationToken);
            await repository.DeleteEnvironmentAsync(environment.Id, cancellationToken);
            return Results.Ok(new OK("Environment deleted"));
        }).WithName("DeleteEnvironment");
    }
}
