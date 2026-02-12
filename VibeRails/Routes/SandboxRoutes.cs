using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;

namespace VibeRails.Routes;

public static class SandboxRoutes
{
    public static void Map(WebApplication app, string launchDirectory)
    {
        // GET /api/v1/sandboxes - List sandboxes for current project
        app.MapGet("/api/v1/sandboxes", async (
            ISandboxService sandboxService,
            CancellationToken cancellationToken) =>
        {
            var projectPath = Configs.GetRootPath();
            if (string.IsNullOrEmpty(projectPath))
                return Results.BadRequest(new ErrorResponse("Not in a local project context"));

            var sandboxes = await sandboxService.GetSandboxesAsync(projectPath, cancellationToken);
            var response = sandboxes.Select(s => new SandboxResponse(
                s.Id, s.Name, s.Path, s.Branch, s.CommitHash, s.CreatedUTC
            )).ToList();

            return Results.Ok(new SandboxListResponse(response));
        }).WithName("GetSandboxes");

        // POST /api/v1/sandboxes - Create a new sandbox
        app.MapPost("/api/v1/sandboxes", async (
            ISandboxService sandboxService,
            CreateSandboxRequest? request,
            CancellationToken cancellationToken) =>
        {
            var projectPath = Configs.GetRootPath();
            if (string.IsNullOrEmpty(projectPath))
                return Results.BadRequest(new ErrorResponse("Not in a local project context"));

            if (string.IsNullOrWhiteSpace(request?.Name))
                return Results.BadRequest(new ErrorResponse("Sandbox name is required"));

            try
            {
                var sandbox = await sandboxService.CreateSandboxAsync(
                    request.Name, projectPath, cancellationToken);
                return Results.Ok(new SandboxResponse(
                    sandbox.Id, sandbox.Name, sandbox.Path,
                    sandbox.Branch, sandbox.CommitHash, sandbox.CreatedUTC));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("CreateSandbox");

        // DELETE /api/v1/sandboxes/{id} - Delete a sandbox
        app.MapDelete("/api/v1/sandboxes/{id:int}", async (
            ISandboxService sandboxService,
            int id,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await sandboxService.DeleteSandboxAsync(id, cancellationToken);
                return Results.Ok(new OK("Sandbox deleted"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("DeleteSandbox");

        // POST /api/v1/sandboxes/{id}/launch/vscode - Launch VS Code in sandbox directory
        app.MapPost("/api/v1/sandboxes/{id:int}/launch/vscode", async (
            IRepository repository,
            int id,
            CancellationToken cancellationToken) =>
        {
            var sandbox = await repository.GetSandboxByIdAsync(id, cancellationToken);
            if (sandbox == null)
                return Results.NotFound(new ErrorResponse("Sandbox not found"));

            try
            {
                var process = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "code",
                        Arguments = ".",
                        WorkingDirectory = sandbox.Path,
                        UseShellExecute = true
                    });

                if (process == null)
                    return Results.BadRequest(new ErrorResponse("Failed to start VS Code. Make sure 'code' command is in your PATH."));

                return Results.Ok(new LaunchCliResponse(
                    Success: true,
                    ExitCode: 0,
                    Message: $"VS Code launched in sandbox: {sandbox.Name}",
                    StandardOutput: "",
                    StandardError: ""
                ));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to launch VS Code: {ex.Message}"));
            }
        }).WithName("LaunchVSCodeInSandbox");

        // POST /api/v1/sandboxes/{id}/launch/{cli} - Launch CLI in external terminal in sandbox directory
        app.MapPost("/api/v1/sandboxes/{id:int}/launch/{cli}", async (
            IRepository repository,
            ILaunchLLMService launchService,
            int id,
            string cli,
            LaunchCliRequest? request,
            CancellationToken cancellationToken) =>
        {
            var sandbox = await repository.GetSandboxByIdAsync(id, cancellationToken);
            if (sandbox == null)
                return Results.NotFound(new ErrorResponse("Sandbox not found"));

            var llm = cli.ToLowerInvariant() switch
            {
                "claude" => LLM.Claude,
                "codex" => LLM.Codex,
                "gemini" => LLM.Gemini,
                _ => LLM.NotSet
            };

            if (llm == LLM.NotSet)
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {cli}"));

            var args = request?.Args?.ToList() ?? new List<string>();
            var envName = request?.EnvironmentName;

            // If using a custom environment, look up its custom args
            if (!string.IsNullOrEmpty(envName))
            {
                var environment = await repository.GetEnvironmentByNameAndLlmAsync(envName, llm, cancellationToken);
                if (environment != null && !string.IsNullOrEmpty(environment.CustomArgs))
                {
                    var customArgs = environment.CustomArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    args.InsertRange(0, customArgs);
                }
            }

            var result = launchService.LaunchInTerminal(llm, envName, sandbox.Path, args.ToArray());

            return Results.Ok(new LaunchCliResponse(
                Success: result.Success,
                ExitCode: 0,
                Message: result.Message,
                StandardOutput: "",
                StandardError: result.Success ? "" : result.Message
            ));
        }).WithName("LaunchCliInSandbox");
    }
}
