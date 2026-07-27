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
            try
            {
                var envVars = envService.GetEnvironmentVariables(name, llm);
                return Results.Ok(envVars);
            }
            catch (ArgumentException ex)
            {
                // Name escaped the envs root (containment guard) — reject rather than 500.
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("GetLaunchEnvironment");

        app.MapPost("/api/v1/cli/launch/{cli}", async (
            IEnvironmentLaunchService environmentLaunchService,
            ILlmParser llmParser,
            string cli,
            LaunchCliRequest? request,
            CancellationToken cancellationToken) =>
        {
            var llm = llmParser.Parse(cli);

            if (llm == LLM.NotSet)
            {
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {cli}"));
            }

            // LLM.Shell is a terminal-only type (a bare PTY, no agent) and has no launcher;
            // reject it here so it returns 400 instead of throwing 500 inside LaunchLLMService.
            if (llm == LLM.Shell)
            {
                return Results.BadRequest(new ErrorResponse("The plain shell terminal is not a launchable agent CLI."));
            }

            var result = await environmentLaunchService.LaunchAsync(
                llm,
                request ?? new LaunchCliRequest(),
                launchDirectory,
                cancellationToken: cancellationToken);

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
