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
        app.MapPost("/api/v1/cli/launch/vscode", (LaunchVsCodeRequest? request) =>
        {
            try
            {
                var startInfo = VsCodeLauncher.CreateStartInfo(launchDirectory, request?.Path);
                using var process = System.Diagnostics.Process.Start(startInfo);

                if (process == null)
                {
                    return Results.BadRequest(new ErrorResponse("Failed to start VS Code. Make sure 'code' command is in your PATH."));
                }

                return Results.Ok(new LaunchCliResponse(
                    Success: true,
                    ExitCode: 0,
                    Message: string.IsNullOrWhiteSpace(request?.Path)
                        ? $"VS Code launched in {launchDirectory}"
                        : $"Opened {System.IO.Path.GetFileName(request.Path)} in VS Code",
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
