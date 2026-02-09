using System.Net.WebSockets;
using Pty.Net;
using VibeRails.Services.LlmClis;

namespace VibeRails.Services.Terminal;

public class TerminalRunner
{
    private readonly ITerminalStateService _stateService;
    private readonly LlmCliEnvironmentService _envService;

    public TerminalRunner(ITerminalStateService stateService, LlmCliEnvironmentService envService)
    {
        _stateService = stateService;
        _envService = envService;
    }

    /// <summary>
    /// CLI path: Uses EzTerminal to spawn shell, send command, and handle Console I/O + DB tracking.
    /// Blocks until terminal exits.
    /// </summary>
    public async Task<int> RunCliAsync(LLM llm, string workDir, string? envName, string[]? extraArgs, CancellationToken ct)
    {
        var sessionId = await _stateService.CreateSessionAsync(llm.ToString(), workDir, envName, ct);

        var cli = llm.ToString().ToLower();
        var command = extraArgs?.Length > 0
            ? $"{cli} {string.Join(" ", extraArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}"
            : cli;

        var exitCode = 0;
        try
        {
            using var terminal = new EzTerminal(
                userOutputHandler: input => _stateService.RecordInput(sessionId, input),
                terminalOutputHandler: output =>
                {
                    Console.Write(output);
                    _stateService.LogOutput(sessionId, output);
                },
                cancellationToken: ct,
                onExit: () => { });

            terminal.WithWorkingDirectory(workDir);

            // Apply env isolation if custom environment
            if (!string.IsNullOrEmpty(envName))
            {
                var envVars = _envService.GetEnvironmentVariables(envName, llm);
                terminal.WithEnvironment(envVars);
            }

            await terminal.Run(command, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Terminal error: {ex.Message}");
            exitCode = 1;
        }
        finally
        {
            await _stateService.CompleteSessionAsync(sessionId, exitCode);
        }

        return exitCode;
    }

    /// <summary>
    /// Web UI path: Spawns PTY directly and returns connection + session ID. Caller uses HandleWebSocketAsync to pump WebSocket ↔ PTY.
    /// </summary>
    public async Task<(IPtyConnection pty, string sessionId)> StartWebAsync(LLM llm, string workDir, string? envName, string[]? extraArgs, CancellationToken ct)
    {
        var sessionId = await _stateService.CreateSessionAsync(llm.ToString(), workDir, envName, ct);

        var cli = llm.ToString().ToLower();
        var command = extraArgs?.Length > 0
            ? $"{cli} {string.Join(" ", extraArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}"
            : cli;

        var shell = OperatingSystem.IsWindows() ? "pwsh.exe" : "bash";

        var environment = new Dictionary<string, string>
        {
            ["LANG"] = "en_US.UTF-8",
            ["LC_ALL"] = "en_US.UTF-8",
            ["PYTHONIOENCODING"] = "utf-8"
        };

        // Apply env isolation if custom environment
        if (!string.IsNullOrEmpty(envName))
        {
            var envVars = _envService.GetEnvironmentVariables(envName, llm);
            foreach (var kvp in envVars)
                environment[kvp.Key] = kvp.Value;
        }

        var options = new PtyOptions
        {
            Name = "VibeRails-WebTerminal",
            Cols = 120,
            Rows = 30,
            Cwd = workDir,
            App = shell,
            CommandLine = Array.Empty<string>(),
            Environment = environment
        };

        var pty = await PtyProvider.SpawnAsync(options, ct);

        // Send the command to the shell
        var fullCommand = $"{command}\r";
        var cmdBytes = System.Text.Encoding.UTF8.GetBytes(fullCommand);
        await pty.WriterStream.WriteAsync(cmdBytes, ct);
        await pty.WriterStream.FlushAsync(ct);

        return (pty, sessionId);
    }

    /// <summary>
    /// Web UI path: Pipe WebSocket ↔ PTY bidirectionally.
    /// </summary>
    public async Task HandleWebSocketAsync(IPtyConnection pty, string sessionId, WebSocket webSocket, CancellationToken ct)
    {
        try
        {
            // PTY output → WebSocket
            var outputTask = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
                    {
                        var bytesRead = await pty.ReaderStream.ReadAsync(buffer, ct);
                        if (bytesRead == 0) break;

                        // Log to DB
                        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        _stateService.LogOutput(sessionId, text);

                        // Send to WebSocket
                        await webSocket.SendAsync(new ArraySegment<byte>(buffer, 0, bytesRead), WebSocketMessageType.Binary, true, ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }, ct);

            // WebSocket input → PTY
            var inputTask = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
                    {
                        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count > 0)
                        {
                            // Log input to DB
                            var input = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                            _stateService.RecordInput(sessionId, input);

                            // Write to PTY
                            await pty.WriterStream.WriteAsync(buffer, 0, result.Count, ct);
                            await pty.WriterStream.FlushAsync(ct);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }, ct);

            // Wait for either to complete
            await Task.WhenAny(outputTask, inputTask);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WebSocket error: {ex.Message}");
        }
        finally
        {
            // Just close WebSocket gracefully if still open
            if (webSocket.State == WebSocketState.Open)
                try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); } catch { }

            // DON'T dispose PTY or complete session here!
            // PTY should only be disposed by TerminalSessionService.CleanupAsync() on explicit stop
        }
    }
}
