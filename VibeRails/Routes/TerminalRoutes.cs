using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;

namespace VibeRails.Routes;

public static class TerminalRoutes
{
    public static void Map(WebApplication app, string launchDirectory)
    {
        // GET /api/v1/terminal/status - Check if terminal session is active
        app.MapGet("/api/v1/terminal/status", (ITerminalSessionService terminalService) =>
        {
            return Results.Ok(new TerminalStatusResponse(terminalService.HasActiveSession, terminalService.ActiveSessionId));
        }).WithName("GetTerminalStatus");

        // POST /api/v1/terminal/start - Start a terminal session with LLM CLI
        app.MapPost("/api/v1/terminal/start", async (
            ITerminalSessionService terminalService,
            IRepository repository,
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

            // Resolve working directory
            var workDir = request.WorkingDirectory ?? launchDirectory;

            // Get custom args if environment specified
            string[]? extraArgs = null;
            if (!string.IsNullOrEmpty(request.EnvironmentName))
            {
                var environment = await repository.GetEnvironmentByNameAndLlmAsync(request.EnvironmentName, llm, cancellationToken);
                if (environment != null)
                {
                    if (!string.IsNullOrEmpty(environment.CustomArgs))
                    {
                        extraArgs = environment.CustomArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    }
                    environment.LastUsedUTC = DateTime.UtcNow;
                    await repository.UpdateEnvironmentAsync(environment, cancellationToken);
                }
            }

            // Start the terminal session with the LLM CLI
            var success = await terminalService.StartSessionAsync(llm, workDir, request.EnvironmentName, extraArgs);

            if (!success)
            {
                return Results.BadRequest(new ErrorResponse("Failed to start terminal session"));
            }

            return Results.Ok(new TerminalStatusResponse(true, terminalService.ActiveSessionId));
        }).WithName("StartTerminal");

        // POST /api/v1/terminal/stop - Stop the current terminal session
        app.MapPost("/api/v1/terminal/stop", async (ITerminalSessionService terminalService) =>
        {
            if (!terminalService.HasActiveSession)
            {
                return Results.Ok(new TerminalStatusResponse(false, null));
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

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await terminalService.HandleWebSocketAsync(webSocket, context.RequestAborted);
        });

        // GET /api/v1/terminal/bootstrap-command - Get the command to launch an LLM CLI in a terminal session
        app.MapGet("/api/v1/terminal/bootstrap-command", async (
            IRepository repository,
            string cli,
            string? environmentName,
            CancellationToken cancellationToken) =>
        {
            var llm = cli.ToLowerInvariant() switch
            {
                "claude" => LLM.Claude,
                "codex" => LLM.Codex,
                "gemini" => LLM.Gemini,
                _ => LLM.NotSet
            };

            if (llm == LLM.NotSet)
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {cli}"));

            var exePath = Environment.ProcessPath ?? "vb";
            var workDir = launchDirectory;
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
                        var customArgs = environment.CustomArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        extraArgs.AddRange(customArgs);
                    }
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
                bootstrapArgs += " -- " + string.Join(" ", extraArgs);

            string command;
            if (OperatingSystem.IsWindows())
                command = $"& \"{exePath}\" {bootstrapArgs}";
            else
                command = $"\"{exePath}\" {bootstrapArgs}";

            return Results.Ok(new BootstrapCommandResponse(command));
        }).WithName("GetTerminalBootstrapCommand");
    }
}
