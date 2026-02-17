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
            var projectPath = ParserConfigs.GetRootPath();
            if (string.IsNullOrEmpty(projectPath))
                return Results.BadRequest(new ErrorResponse("Not in a local project context"));

            var sandboxes = await sandboxService.GetSandboxesAsync(projectPath, cancellationToken);
            var response = sandboxes.Select(s => new SandboxResponse(
                s.Id, s.Name, s.Path, s.Branch, s.SourceBranch, s.CommitHash, s.RemoteUrl, s.CreatedUTC
            )).ToList();

            return Results.Ok(new SandboxListResponse(response));
        }).WithName("GetSandboxes");

        // POST /api/v1/sandboxes - Create a new sandbox
        app.MapPost("/api/v1/sandboxes", async (
            ISandboxService sandboxService,
            CreateSandboxRequest? request,
            CancellationToken cancellationToken) =>
        {
            var projectPath = ParserConfigs.GetRootPath();
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
                    sandbox.Branch, sandbox.SourceBranch, sandbox.CommitHash, sandbox.RemoteUrl, sandbox.CreatedUTC));
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

        // POST /api/v1/sandboxes/{id}/launch/shell - Launch a plain shell in sandbox directory
        app.MapPost("/api/v1/sandboxes/{id:int}/launch/shell", async (
            IRepository repository,
            int id,
            CancellationToken cancellationToken) =>
        {
            var sandbox = await repository.GetSandboxByIdAsync(id, cancellationToken);
            if (sandbox == null)
                return Results.NotFound(new ErrorResponse("Sandbox not found"));

            try
            {
                System.Diagnostics.Process? process;

                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                        System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    process = System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "pwsh",
                            Arguments = "-NoExit -NoProfile",
                            WorkingDirectory = sandbox.Path,
                            UseShellExecute = true
                        });
                }
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                             System.Runtime.InteropServices.OSPlatform.OSX))
                {
                    var script = $"tell application \"Terminal\" to do script \"cd '{sandbox.Path}'\"";
                    process = System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "osascript",
                            Arguments = $"-e \"{script}\"",
                            UseShellExecute = true
                        });
                }
                else
                {
                    // Linux: try common terminal emulators
                    process = null;
                    var terminals = new[] { "gnome-terminal", "konsole", "xfce4-terminal", "xterm" };
                    foreach (var term in terminals)
                    {
                        try
                        {
                            process = System.Diagnostics.Process.Start(
                                new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = term,
                                    Arguments = term == "gnome-terminal" ? $"--working-directory=\"{sandbox.Path}\"" : "",
                                    WorkingDirectory = sandbox.Path,
                                    UseShellExecute = true
                                });
                            break;
                        }
                        catch { /* try next */ }
                    }
                }

                if (process == null)
                    return Results.BadRequest(new ErrorResponse("Failed to launch shell. No supported terminal found."));

                return Results.Ok(new LaunchCliResponse(
                    Success: true,
                    ExitCode: 0,
                    Message: $"Shell launched in sandbox: {sandbox.Name}",
                    StandardOutput: "",
                    StandardError: ""
                ));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to launch shell: {ex.Message}"));
            }
        }).WithName("LaunchShellInSandbox");

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

        // GET /api/v1/sandboxes/{id}/diff - Get diff of changes in sandbox
        app.MapGet("/api/v1/sandboxes/{id:int}/diff", async (
            ISandboxService sandboxService,
            int id,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await sandboxService.GetDiffAsync(id, cancellationToken);
                var response = new SandboxDiffResponse(
                    result.Files.Select(f => new SandboxDiffFileResponse(
                        f.FileName, f.Language, f.OriginalContent, f.ModifiedContent
                    )).ToList(),
                    result.TotalChanges
                );
                return Results.Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("GetSandboxDiff");

        // POST /api/v1/sandboxes/{id}/push - Push sandbox branch to remote
        app.MapPost("/api/v1/sandboxes/{id:int}/push", async (
            ISandboxService sandboxService,
            int id,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var message = await sandboxService.PushToRemoteAsync(id, cancellationToken);
                return Results.Ok(new MergeBackResponse(true, message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("PushSandboxToRemote");

        // POST /api/v1/sandboxes/{id}/merge - Merge sandbox into source project locally
        app.MapPost("/api/v1/sandboxes/{id:int}/merge", async (
            ISandboxService sandboxService,
            int id,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var message = await sandboxService.MergeLocallyAsync(id, cancellationToken);
                return Results.Ok(new MergeBackResponse(true, message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("MergeSandboxLocally");
    }
}
