using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Serilog;

namespace VibeRails.Routes;

public static class TerminalRoutes
{
    public static void Map(WebApplication app, string launchDirectory)
    {
        // GET /api/v1/terminal/status - Check if terminal session is active
        app.MapGet("/api/v1/terminal/status", (ITerminalSessionService terminalService) =>
        {
            return Results.Ok(new TerminalStatusResponse(
                terminalService.HasActiveSession,
                terminalService.ActiveSessionId,
                terminalService.ActiveCli,
                terminalService.ActiveWorkingDirectory));
        }).WithName("GetTerminalStatus");

        // POST /api/v1/terminal/start - Start a terminal session with LLM CLI
        app.MapPost("/api/v1/terminal/start", async (
            ITerminalSessionService terminalService,
            ILlmParser llmParser,
            IRepository repository,
            ISessionResumeService sessionResumeService,
            StartTerminalRequest? request,
            CancellationToken cancellationToken) =>
        {
            if (terminalService.HasActiveSession)
            {
                return Results.BadRequest(new ErrorResponse("A terminal session is already active. Stop it first."));
            }

            // Validate required fields
            if (string.IsNullOrEmpty(request?.Cli))
            {
                return Results.BadRequest(new ErrorResponse("CLI type is required"));
            }

            // Resolve LLM type
            var llm = llmParser.Parse(request.Cli);

            if (llm == LLM.NotSet)
            {
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {request.Cli}"));
            }

            // Prefer an explicit working directory from the request (e.g. sandbox path);
            // otherwise fall back to the git root (project PK), then the launch directory.
            var workDir = request.WorkingDirectory;
            if (string.IsNullOrEmpty(workDir))
                workDir = ParserConfigs.GetRootPath();
            if (string.IsNullOrEmpty(workDir))
                workDir = launchDirectory;

            // Get custom args if environment specified
            string[]? extraArgs = null;
            string? environmentPrompt = null;
            if (!string.IsNullOrEmpty(request.EnvironmentName))
            {
                var environment = await repository.GetEnvironmentByNameAndLlmAsync(request.EnvironmentName, llm, cancellationToken);
                if (environment != null)
                {
                    if (!string.IsNullOrEmpty(environment.CustomArgs))
                    {
                        extraArgs = ShellArgSanitizer.ParseAndValidate(environment.CustomArgs);
                    }
                    environmentPrompt = environment.CustomPrompt;
                    environment.LastUsedUTC = DateTime.UtcNow;
                    await repository.UpdateEnvironmentAsync(environment, cancellationToken);
                }
            }

            // Resume summary: prefer the user-edited text from the modal,
            // fall back to generating one server-side if only a session ID was sent.
            var summary = request.ResumeSummary ?? "";
            if (summary.Length > 6000)
                return Results.BadRequest(new ErrorResponse("Resume summary exceeds 6000 character limit."));

            if (string.IsNullOrEmpty(summary) && !string.IsNullOrEmpty(request.ResumeSessionId))
                summary = await sessionResumeService.GetResumeSummaryAsync(request.ResumeSessionId, cancellationToken);

            var initialPrompt = request.InitialPrompt;
            if (string.IsNullOrWhiteSpace(initialPrompt) && !string.IsNullOrWhiteSpace(environmentPrompt))
                initialPrompt = environmentPrompt;

            // MCP opt-in is consulted at launch by CommandService via ParserConfigs. A terminal tab
            // runs in a child process whose ParserConfigs snapshot is from its own startup, so
            // re-read settings.json — which the parent persists on every settings change — to honor
            // an opt-out toggled after this child started. Without this an already-open tab could
            // still run `mcp add` after the user disabled MCP.
            //
            // Guard the disk read: a concurrent settings Save or a corrupt file can throw, and a
            // failed re-read must not abort the launch — fall back to the cached value (mirrors the
            // fail-safe in McpStdioHost).
            try
            {
                ParserConfigs.SetMcpEnabled(Config.LoadFresh().McpEnabled);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Terminal] Could not re-read settings for the MCP gate; using cached value");
            }

            // Start the terminal session with the LLM CLI
            try
            {
                var success = await terminalService.StartSessionAsync(llm, workDir, request.EnvironmentName, extraArgs, request.Title, request.MakeRemote, initialPrompt, summary);

                if (!success)
                {
                    return Results.BadRequest(new ErrorResponse("Failed to start terminal session"));
                }

                // Link parent session if this was resumed from a previous session
                if (!string.IsNullOrEmpty(request.ResumeSessionId) && !string.IsNullOrEmpty(terminalService.ActiveSessionId))
                {
                    await sessionResumeService.LinkParentSessionAsync(terminalService.ActiveSessionId, request.ResumeSessionId, llm.ToString(), cancellationToken);
                }

                return Results.Ok(new TerminalStatusResponse(
                    true,
                    terminalService.ActiveSessionId,
                    terminalService.ActiveCli,
                    terminalService.ActiveWorkingDirectory));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to start terminal session: {ex.Message}"));
            }
        }).WithName("StartTerminal");

        // POST /api/v1/terminal/stop - Stop the current terminal session
        app.MapPost("/api/v1/terminal/stop", async (ITerminalSessionService terminalService) =>
        {
            if (!terminalService.HasActiveSession)
            {
                return Results.Ok(new TerminalStatusResponse(false, null));
            }

            if (terminalService.IsExternallyOwned)
            {
                return Results.BadRequest(new ErrorResponse("Terminal is controlled from CLI. Stop it from the command line."));
            }

            await terminalService.StopSessionAsync();
            return Results.Ok(new TerminalStatusResponse(false, null));
        }).WithName("StopTerminal");

        // WebSocket endpoint for terminal I/O
        app.Map("/api/v1/terminal/ws", async (HttpContext context, ITerminalSessionService terminalService) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            if (!terminalService.HasActiveSession)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("No active terminal session. Start one first via POST /api/v1/terminal/start");
                return;
            }

            var acceptedSubprotocol = context.Items["viberails_accepted_subprotocol"] as string;
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync(acceptedSubprotocol);

            int? cols = null;
            int? rows = null;
            if (context.Request.Query.TryGetValue("cols", out var colsStr) && int.TryParse(colsStr, out var c) && c > 0)
                cols = c;
            if (context.Request.Query.TryGetValue("rows", out var rowsStr) && int.TryParse(rowsStr, out var r) && r > 0)
                rows = r;

            await terminalService.HandleWebSocketAsync(webSocket, context.RequestAborted, cols, rows);
        });

        // GET /api/v1/terminal/bootstrap-command - Get the command to launch an LLM CLI in a terminal session
        app.MapGet("/api/v1/terminal/bootstrap-command", async (
            ILlmParser llmParser,
            IRepository repository,
            string cli,
            string? environmentName,
            CancellationToken cancellationToken) =>
        {
            var llm = llmParser.Parse(cli);

            if (llm == LLM.NotSet)
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {cli}"));

            var exePath = Environment.ProcessPath ?? "vb";
            var workDir = ParserConfigs.GetRootPath();
            if (string.IsNullOrEmpty(workDir))
                workDir = launchDirectory;
            var extraArgs = new List<string>();

            // Determine the --env value: custom env name or base CLI name
            string envValue;
            if (!string.IsNullOrEmpty(environmentName))
            {
                envValue = $"\"{environmentName}\"";

                // Look up custom args and update last used
                var environment = await repository.GetEnvironmentByNameAndLlmAsync(environmentName, llm, cancellationToken);
                if (environment != null)
                {
                    if (!string.IsNullOrEmpty(environment.CustomArgs))
                    {
                        extraArgs.AddRange(ShellArgSanitizer.ParseAndValidate(environment.CustomArgs));
                    }
                    LlmPromptArgvBuilder.AppendInitialPrompt(extraArgs, llm, environment.CustomPrompt);
                    environment.LastUsedUTC = DateTime.UtcNow;
                    await repository.UpdateEnvironmentAsync(environment, cancellationToken);
                }
            }
            else
            {
                envValue = cli;
            }

            // Build command
            var bootstrapArgs = $"--env {envValue} --workdir \"{workDir}\"";
            if (extraArgs.Count > 0)
                bootstrapArgs += " -- " + ShellArgSanitizer.BuildSafeArgString(extraArgs.ToArray());

            string command;
            if (OperatingSystem.IsWindows())
                command = $"& \"{exePath}\" {bootstrapArgs}";
            else
                command = $"\"{exePath}\" {bootstrapArgs}";

            return Results.Ok(new BootstrapCommandResponse(command));
        }).WithName("GetTerminalBootstrapCommand");
    }
}
