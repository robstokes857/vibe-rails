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
            IRepository repository,
            CancellationToken cancellationToken) =>
        {
            var projectPath = ParserConfigs.GetRootPath();
            if (!ParserConfigs.GetIsInGit())
                return Results.BadRequest(new ErrorResponse("Sandboxes require a git repository"));

            var sandboxes = await sandboxService.GetSandboxesAsync(projectPath, cancellationToken);

            // Owned workspaces stay in the payload rather than being filtered out: the client
            // renders standalone sandboxes on the Sandboxes card and owned ones on their
            // environment's row, and it needs both to decide which is which.
            var ownerNames = await ResolveOwnerNamesAsync(repository, sandboxes, cancellationToken);
            var response = sandboxes.Select(s => new SandboxResponse(
                s.Id, s.Name, s.Path, s.Branch, s.SourceBranch, s.CommitHash, s.RemoteUrl, s.CreatedUTC,
                s.EnvironmentId,
                s.EnvironmentId.HasValue && ownerNames.TryGetValue(s.EnvironmentId.Value, out var owner) ? owner : null
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
            if (!ParserConfigs.GetIsInGit())
                return Results.BadRequest(new ErrorResponse("Sandboxes require a git repository"));

            if (string.IsNullOrWhiteSpace(request?.Name))
                return Results.BadRequest(new ErrorResponse("Sandbox name is required"));

            try
            {
                var sandbox = await sandboxService.CreateSandboxAsync(
                    request.Name, projectPath, options: null, cancellationToken);
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
            IRepository repository,
            int id,
            CancellationToken cancellationToken) =>
        {
            // A sandbox that belongs to an environment is that environment's workspace, and a
            // run may be using it right now. Deleting it from here would be a destructive edit
            // to an environment the caller never named, so ownership has to be released first —
            // by changing the workspace mode, or by deleting the environment.
            var existing = await repository.GetSandboxByIdAsync(id, cancellationToken);
            if (existing?.EnvironmentId is int ownerId)
            {
                var owner = await repository.GetEnvironmentByIdAsync(ownerId, cancellationToken);
                var ownerName = owner?.CustomName ?? $"environment {ownerId}";
                return Results.Conflict(new ErrorResponse(
                    $"This sandbox is the workspace for '{ownerName}'. Change that environment's workspace mode, or delete the environment, to release it first."));
            }

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
                            FileName = ShellDefaults.WindowsCommandShell,
                            Arguments = "-NoExit -NoProfile",
                            WorkingDirectory = sandbox.Path,
                            UseShellExecute = true
                        });
                }
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                             System.Runtime.InteropServices.OSPlatform.OSX))
                {
                    var terminalCommand = MacTerminalCommandBuilder.BuildOpenDirectoryCommand(sandbox.Path);
                    process = System.Diagnostics.Process.Start(
                        MacTerminalCommandBuilder.BuildStartInfo(terminalCommand));
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
            ILlmParser llmParser,
            int id,
            string cli,
            LaunchCliRequest? request,
            CancellationToken cancellationToken) =>
        {
            var sandbox = await repository.GetSandboxByIdAsync(id, cancellationToken);
            if (sandbox == null)
                return Results.NotFound(new ErrorResponse("Sandbox not found"));

            var llm = llmParser.Parse(cli);

            if (llm == LLM.NotSet)
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {cli}"));

            // LLM.Shell is a terminal-only type with no launcher — reject rather than 500.
            if (llm == LLM.Shell)
                return Results.BadRequest(new ErrorResponse("The plain shell terminal is not a launchable agent CLI."));

            var args = request?.Args?.ToList() ?? new List<string>();
            var envName = request?.EnvironmentName;

            // If using a custom environment, look up its custom args. The Initial Message is NOT
            // appended here: LaunchInTerminal always spawns vb, and CliLoop resolves the prompt
            // ({{step:...}}/{{datetime}} placeholders included) in the process that owns the PTY,
            // exactly once per launch.
            if (!string.IsNullOrEmpty(envName))
            {
                var environment = await repository.GetEnvironmentByNameAndLlmAsync(envName, llm, cancellationToken);
                if (environment != null)
                {
                    if (!string.IsNullOrEmpty(environment.CustomArgs))
                    {
                        var customArgs = ShellArgSanitizer.ParseAndValidate(environment.CustomArgs);
                        args.InsertRange(0, customArgs);
                    }
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

    /// <summary>
    /// Display names for the environments owning any of these sandboxes, keyed by id. A
    /// workspace whose environment has since been deleted simply resolves to no name — the
    /// row is a standalone sandbox at that point anyway.
    /// </summary>
    private static async Task<Dictionary<int, string>> ResolveOwnerNamesAsync(
        IRepository repository,
        IReadOnlyCollection<Sandbox> sandboxes,
        CancellationToken cancellationToken)
    {
        var ownerIds = sandboxes
            .Where(s => s.EnvironmentId.HasValue)
            .Select(s => s.EnvironmentId!.Value)
            .Distinct()
            .ToList();

        var names = new Dictionary<int, string>(ownerIds.Count);
        foreach (var ownerId in ownerIds)
        {
            var environment = await repository.GetEnvironmentByIdAsync(ownerId, cancellationToken);
            if (environment is not null)
                names[ownerId] = environment.CustomName;
        }

        return names;
    }
}
