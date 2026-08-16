using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Environments;
using VibeRails.Services.Jobs;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Terminal;
using VibeRails.Utils;

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
            IPromptPlaceholderService promptPlaceholders,
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
            int? environmentId = null;
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
                    environmentId = environment.Id;
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

            var requestedPrompt = request.InitialPrompt;
            if (string.IsNullOrWhiteSpace(requestedPrompt) && !string.IsNullOrWhiteSpace(environmentPrompt))
                requestedPrompt = environmentPrompt;

            // The once-only placeholder pass for every in-process launch (web tabs, Multi Run,
            // the agent terminal tool): {{datetime}}-style built-ins fill in and {{step:<id>}}
            // references execute, and the same string then feeds both the seq-1 UserInputs
            // recording and the CLI argv. The resume summary below is generated text and is
            // deliberately never resolved.
            //
            // Deferred into StartSessionAsync rather than run here: the HasActiveSession check
            // above is a courtesy, not a reservation, so resolving at this point would let two
            // simultaneous requests both execute the referenced step commands even though only
            // one of them can end up owning a terminal.
            Func<Task<string?>>? resolveInitialPrompt = string.IsNullOrWhiteSpace(requestedPrompt)
                ? null
                : () => promptPlaceholders.ResolveAsync(
                    requestedPrompt,
                    new PromptPlaceholderContext(workDir, environmentId, request.EnvironmentName),
                    cancellationToken);

            // Start the terminal session with the LLM CLI
            try
            {
                var success = await terminalService.StartSessionAsync(llm, workDir, request.EnvironmentName, extraArgs, request.Title, request.MakeRemote, resolveInitialPrompt, summary);

                if (!success)
                {
                    return Results.BadRequest(new ErrorResponse("Failed to start terminal session"));
                }

                // Link parent session if this was resumed from a previous session
                if (!string.IsNullOrEmpty(request.ResumeSessionId) && !string.IsNullOrEmpty(terminalService.ActiveSessionId))
                {
                    await sessionResumeService.LinkParentSessionAsync(terminalService.ActiveSessionId, request.ResumeSessionId, LlmParser.ToWireName(llm), cancellationToken);
                }

                return Results.Ok(new TerminalStatusResponse(
                    true,
                    terminalService.ActiveSessionId,
                    terminalService.ActiveCli,
                    terminalService.ActiveWorkingDirectory));
            }
            catch (PromptTooLongException ex)
            {
                // Written for the user and naming the limit that was hit — surface it verbatim
                // rather than wrapping it in the generic launch-failure text below.
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to start terminal session: {ex.Message}"));
            }
        }).WithName("StartTerminal");

        // POST /api/v1/terminal/stop - Stop the current terminal session
        app.MapPost("/api/v1/terminal/stop", async (
            ITerminalSessionService terminalService,
            IJobStore jobStore) =>
        {
            if (!terminalService.HasActiveSession)
            {
                return Results.Ok(new TerminalStatusResponse(false, null));
            }

            if (terminalService.IsExternallyOwned)
            {
                // Interactive manual Automations are CLI-owned inside their terminal-tab child,
                // but their Web UI Stop control should still work. Use JobRunner's existing
                // cooperative cancellation path so it records Cancelled before ending the child.
                var jobRunId = ParserConfigs.GetArguments().JobRunId;
                if (!string.IsNullOrWhiteSpace(jobRunId))
                {
                    if (!await jobStore.RequestCancelAsync(jobRunId))
                        return Results.BadRequest(new ErrorResponse("This Automation run can no longer be stopped."));

                    return Results.Ok(new TerminalStatusResponse(
                        true,
                        terminalService.ActiveSessionId,
                        terminalService.ActiveCli,
                        terminalService.ActiveWorkingDirectory));
                }

                return Results.BadRequest(new ErrorResponse("Terminal is controlled from CLI. Stop it from the command line."));
            }

            await terminalService.StopSessionAsync();
            return Results.Ok(new TerminalStatusResponse(false, null));
        }).WithName("StopTerminal");

        // POST /api/v1/terminal/input - Send input without attaching/taking over the viewer WebSocket
        app.MapPost("/api/v1/terminal/input", async (
            ITerminalSessionService terminalService,
            TerminalInputRequest? request,
            CancellationToken cancellationToken) =>
        {
            if (request == null)
            {
                return Results.BadRequest(new ErrorResponse("Input request is required."));
            }

            var response = await terminalService.SendInputAsync(request, cancellationToken);
            return response.Success
                ? Results.Ok(response)
                : Results.BadRequest(new ErrorResponse(response.Message));
        }).WithName("SendTerminalInput");

        // GET /api/v1/terminal/snapshot - Plain-text snapshot of the current terminal screen
        app.MapGet("/api/v1/terminal/snapshot", async (
            ITerminalSessionService terminalService,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await terminalService.CaptureSnapshotAsync(cancellationToken);
            if (snapshot == null)
            {
                return Results.NotFound(new ErrorResponse("No active terminal session."));
            }

            return Results.Ok(new TerminalSnapshotResponse(
                null,
                snapshot.SessionId,
                snapshot.CapturedUtc,
                snapshot.Cols,
                snapshot.Rows,
                snapshot.ScreenText,
                snapshot.XtermUiBytes,
                snapshot.XtermPngString));
        }).WithName("GetTerminalSnapshot");

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
                    // The Initial Message is deliberately NOT baked into this command. This route
                    // returns a copyable string that may run later, repeatedly, or never — the
                    // spawned vb (CliLoop) resolves the prompt at actual run time, so
                    // {{step:...}}/{{datetime}} placeholders execute exactly once per real launch.
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
