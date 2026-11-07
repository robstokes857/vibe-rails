using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Environments;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using VibeRails.Launches;
using VibeRails.Services.Launches;

namespace VibeRails.Routes;

public static class TerminalRoutes
{
    private static async Task<IResult> StartSavedLaunchAsync(WebApplication app, HttpContext http,
        ITerminalSessionService terminal, IPromptPlaceholderService prompts, CancellationToken ct)
    {
        if (app.Services.GetService<ProcessRole>() is not { IsTerminalTabChild: true, IsActiveRootBackend: false })
            return Results.StatusCode(403);
        var store = http.RequestServices.GetRequiredService<ILaunchStore>();
        var service = http.RequestServices.GetRequiredService<LaunchService>();
        var id = http.Request.Headers["X-VibeRails-Launch-Run"].ToString();
        var token = http.Request.Headers["X-VibeRails-Launch-Claim"].ToString();
        var run = await store.GetRunAsync(id, ct);
        if (run?.Snapshot.Preset is null || run.Status != "claimed" || string.IsNullOrEmpty(token) || run.ClaimToken != token)
            return Results.Conflict(new ErrorResponse("The saved launch is no longer available to this process."));
        try
        {
            await service.ValidateSnapshotAsync(run.Snapshot, ct);
            if (http.Request.Headers["X-VibeRails-Tool-Session"].FirstOrDefault() is { } toolSession
                && http.Request.Headers["X-VibeRails-Tool-Tab"].FirstOrDefault() is { } toolTab
                && VibeRails.Auth.AgentCredentialService.IsAgentToken(toolSession)
                && VibeRails.Auth.AgentCredentialService.IsAgentToken(toolTab))
                http.RequestServices.GetRequiredService<VibeRails.Services.AgentTools.ILocalToolApiContext>().AcceptParentContact(toolSession, toolTab);
            var execution = new LaunchExecution(store, service, run, http.RequestServices.GetRequiredService<VibeRails.Services.Board.IBoardStore>());
            var snapshot = run.Snapshot;
            var template = snapshot.Context.ComposedPrompt ?? snapshot.Context.PromptPrefix + snapshot.Preset.InitialPrompt;
            var boardCard = snapshot.Context.CardId is null ? null : await http.RequestServices.GetRequiredService<VibeRails.Services.Board.IBoardStore>()
                .FindCardAsync(snapshot.Context.ProjectPath, snapshot.Context.CardId, ct);
            var success = await terminal.StartLaunchSessionAsync(execution, () => prompts.ResolveLaunchAsync(template,
                new PromptPlaceholderContext(snapshot.Context.WorkingDirectory, EnvironmentName: snapshot.Preset.Name,
                    BoardCardKey: boardCard?.Key), ct));
            return success ? Results.Ok(new TerminalStatusResponse(true, terminal.ActiveSessionId, terminal.ActiveCli, terminal.ActiveWorkingDirectory))
                : Results.Conflict(new ErrorResponse("The terminal already has a session."));
        }
        catch (Exception ex) when (ex is LaunchValidationException or LaunchConflictException or PromptTooLongException)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    public static void Map(WebApplication app, string launchDirectory)
    {
        // GET /api/v1/terminal/status - Check if terminal session is active
        app.MapGet("/api/v1/terminal/status", (ITerminalSessionService terminalService) =>
        {
            return Results.Ok(new TerminalStatusResponse(
                terminalService.HasActiveSession,
                terminalService.ActiveSessionId,
                terminalService.ActiveCli,
                terminalService.ActiveWorkingDirectory,
                CanReclaim: terminalService.CanReclaim));
        }).WithName("GetTerminalStatus");

        // POST /api/v1/terminal/start - Start a terminal session with LLM CLI
        app.MapPost("/api/v1/terminal/start", async (
            ITerminalSessionService terminalService,
            ILlmParser llmParser,
            IRepository repository,
            ISessionResumeService sessionResumeService,
            IPromptPlaceholderService promptPlaceholders,
            HttpContext context,
            StartTerminalRequest? request,
            CancellationToken cancellationToken) =>
        {
            if (terminalService.HasActiveSession)
            {
                return Results.BadRequest(new ErrorResponse("A terminal session is already active. Stop it first."));
            }

            if (context.Request.Headers.ContainsKey("X-VibeRails-Launch-Run"))
                return await StartSavedLaunchAsync(app, context, terminalService, promptPlaceholders, cancellationToken);

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

            BaseLlmOptions? baseOptions;
            try
            {
                if ((!string.IsNullOrEmpty(request.EnvironmentName) || request.EnvironmentId.HasValue) && request.BaseLlmOptions is not null)
                    return Results.BadRequest(new ErrorResponse("Base LLM options cannot override a saved environment."));
                baseOptions = BaseLlmOptionsBuilder.Normalize(llm, request.BaseLlmOptions);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }

            // Prefer an explicit working directory from the request (e.g. sandbox path);
            // otherwise fall back to the git root (project PK), then the launch directory.
            var workDir = request.WorkingDirectory;
            if (string.IsNullOrEmpty(workDir))
                workDir = ParserConfigs.GetRootPath();
            if (string.IsNullOrEmpty(workDir))
                workDir = launchDirectory;

            // Retired selections never fall back to a base CLI or a same-named preset.
            if (!string.IsNullOrEmpty(request.EnvironmentName) || request.EnvironmentId.HasValue)
                return Results.Json(new ErrorResponse("Custom environments are retired. Choose a launch preset."), statusCode: 410);

            string[]? extraArgs = BaseLlmOptionsBuilder.BuildArguments(llm, baseOptions);
            int? environmentId = null;

            // Resume summary: prefer the user-edited text from the modal,
            // fall back to generating one server-side if only a session ID was sent.
            var summary = request.ResumeSummary ?? "";
            if (summary.Length > 6000)
                return Results.BadRequest(new ErrorResponse("Resume summary exceeds 6000 character limit."));

            if (string.IsNullOrEmpty(summary) && !string.IsNullOrEmpty(request.ResumeSessionId))
            {
                try
                {
                    summary = await sessionResumeService.GetResumeSummaryAsync(request.ResumeSessionId, cancellationToken);
                }
                catch (RemoteSummaryException ex)
                {
                    return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.StatusCode);
                }
            }

            var requestedPrompt = request.InitialPrompt;

            // Resolve supported substitutions once, after the session owns the terminal slot.
            Func<Task<ResolvedPrompt>>? resolveInitialPrompt = string.IsNullOrWhiteSpace(requestedPrompt)
                ? null
                : () => promptPlaceholders.ResolveLaunchAsync(
                    requestedPrompt,
                    new PromptPlaceholderContext(workDir, environmentId, request.EnvironmentName),
                    cancellationToken);

            // Start the terminal session with the LLM CLI
            try
            {
                if (!string.IsNullOrEmpty(request.ResumeSessionId))
                    summary = await sessionResumeService.AppendBoardContextAsync(request.ResumeSessionId, summary, cancellationToken);

                if (app.Services.GetService<ProcessRole>() is { IsTerminalTabChild: true }
                    && context.Request.Headers["X-VibeRails-Tool-Session"].FirstOrDefault() is { } toolSession
                    && context.Request.Headers["X-VibeRails-Tool-Tab"].FirstOrDefault() is { } toolTab
                    && VibeRails.Auth.AgentCredentialService.IsAgentToken(toolSession)
                    && VibeRails.Auth.AgentCredentialService.IsAgentToken(toolTab))
                    app.Services.GetRequiredService<VibeRails.Services.AgentTools.ILocalToolApiContext>().AcceptParentContact(toolSession, toolTab);

                var success = await terminalService.StartSessionAsync(llm, workDir, request.EnvironmentName, extraArgs, request.Title, request.MakeRemote, resolveInitialPrompt, summary,
                    authorizeBoardTools: app.Services.GetService<ProcessRole>() is { IsActiveRootBackend: false, IsTerminalTabChild: true }
                        && context.Request.Headers["X-VibeRails-Board-Authorization"] == "true",
                    contextBoardId: request.ContextBoardId,
                    contextIncluded: request.ContextIncluded);

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
    }
}
