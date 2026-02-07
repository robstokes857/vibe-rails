using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.LlmClis;

namespace VibeRails.Routes;

public static class EnvironmentRoutes
{
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

        // POST /api/v1/environments - Create new environment
        app.MapPost("/api/v1/environments", async (
            LlmCliEnvironmentService envService,
            IRepository repository,
            CreateEnvironmentRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Name))
            {
                return Results.BadRequest(new ErrorResponse("Name is required"));
            }

            if (string.IsNullOrEmpty(request.Cli))
            {
                return Results.BadRequest(new ErrorResponse("CLI type is required"));
            }

            var llm = request.Cli.ToLowerInvariant() switch
            {
                "claude" => LLM.Claude,
                "codex" => LLM.Codex,
                "gemini" => LLM.Gemini,
                _ => LLM.NotSet
            };

            if (llm == LLM.NotSet)
            {
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {request.Cli}"));
            }

            var environment = new LLM_Environment
            {
                LLM = llm,
                CustomName = request.Name,
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
            var environments = await repository.GetAllEnvironmentsAsync(cancellationToken);
            var environment = environments.FirstOrDefault(e => e.CustomName == name);

            if (environment == null)
            {
                return Results.NotFound(new ErrorResponse($"Environment not found: {name}"));
            }

            if (request.CustomArgs != null)
            {
                environment.CustomArgs = request.CustomArgs;
            }

            if (request.CustomPrompt != null)
            {
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
            IRepository repository,
            string name,
            CancellationToken cancellationToken) =>
        {
            var environments = await repository.GetAllEnvironmentsAsync(cancellationToken);
            var environment = environments.FirstOrDefault(e => e.CustomName == name);

            if (environment == null)
            {
                return Results.NotFound(new ErrorResponse($"Environment not found: {name}"));
            }

            // Prevent deletion of default environments
            if (environment.CustomName == "Default")
            {
                return Results.BadRequest(new ErrorResponse("Cannot delete default environments"));
            }

            await repository.DeleteEnvironmentAsync(environment.Id, cancellationToken);
            return Results.Ok(new OK("Environment deleted"));
        }).WithName("DeleteEnvironment");
    }
}
