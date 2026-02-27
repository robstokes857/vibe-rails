using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;

namespace VibeRails.Routes;

public static class CliLaunchRoutes
{
    public static void Map(WebApplication app, string launchDirectory)
    {
        app.MapGet("/api/v1/environments/{name}/launch", (LlmCliEnvironmentService envService, string name, LLM llm) =>
        {
            var envVars = envService.GetEnvironmentVariables(name, llm);
            return Results.Ok(envVars);
        }).WithName("GetLaunchEnvironment");

        app.MapPost("/api/v1/cli/launch/{cli}", async (
            ILaunchLLMService launchService,
            IRepository repository,
            string cli,
            LaunchCliRequest? request,
            CancellationToken cancellationToken) =>
        {
            var llm = cli.ToLowerInvariant() switch
            {
                "claude" => LLM.Claude,
                "codex" => LLM.Codex,
                "gemini" => LLM.Gemini,
                "copilot" => LLM.Copilot,
                _ => LLM.NotSet
            };

            if (llm == LLM.NotSet)
            {
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {cli}"));
            }

            var workingDirectory = request?.WorkingDirectory ?? launchDirectory;
            var args = request?.Args?.ToList() ?? new List<string>();
            var envName = request?.EnvironmentName;

            // If using a custom environment, look up its custom args and update last used
            if (!string.IsNullOrEmpty(envName))
            {
                var environment = await repository.GetEnvironmentByNameAndLlmAsync(envName, llm, cancellationToken);

                if (environment != null)
                {
                    if (!string.IsNullOrEmpty(environment.CustomArgs))
                    {
                        args.InsertRange(0, ShellArgSanitizer.ParseAndValidate(environment.CustomArgs));
                    }

                    // Update last used timestamp
                    environment.LastUsedUTC = DateTime.UtcNow;
                    await repository.UpdateEnvironmentAsync(environment, cancellationToken);
                }
            }

            var result = launchService.LaunchInTerminal(llm, envName, workingDirectory, args.ToArray());

            return Results.Ok(new LaunchCliResponse(
                Success: result.Success,
                ExitCode: 0,
                Message: result.Message,
                StandardOutput: "",
                StandardError: result.Success ? "" : result.Message
            ));
        }).WithName("LaunchCli");

        app.MapPost("/api/v1/cli/launch/vscode", () =>
        {
            try
            {
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "code",
                    Arguments = ".",
                    WorkingDirectory = launchDirectory,
                    UseShellExecute = true
                });

                if (process == null)
                {
                    return Results.BadRequest(new ErrorResponse("Failed to start VS Code. Make sure 'code' command is in your PATH."));
                }

                return Results.Ok(new LaunchCliResponse(
                    Success: true,
                    ExitCode: 0,
                    Message: $"VS Code launched in {launchDirectory}",
                    StandardOutput: "",
                    StandardError: ""
                ));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to launch VS Code: {ex.Message}"));
            }
        }).WithName("LaunchVSCode");
    }
}
