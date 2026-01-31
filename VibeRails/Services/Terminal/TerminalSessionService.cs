using System.Net.WebSockets;
using Pty.Net;

namespace VibeRails.Services.Terminal;

public interface ITerminalSessionService
{
    bool HasActiveSession { get; }
    Task<bool> StartSessionAsync(string workingDirectory);
    Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken);
    Task StopSessionAsync();
}

/// <summary>
/// Minimal terminal service - just PTY + WebSocket.
/// </summary>
public class TerminalSessionService : ITerminalSessionService
{
    private static readonly object s_lock = new();
    private static IPtyConnection? s_pty;
    private static WebSocket? s_activeWebSocket;

    public bool HasActiveSession => s_pty != null;

    public async Task<bool> StartSessionAsync(string workingDirectory)
    {
        lock (s_lock)
        {
            if (s_pty != null) return false;
        }

        // Inherit full environment from parent process
        var environment = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                environment[key] = value;
        }

        // Force UTF-8 encoding for proper Unicode support
        environment["LANG"] = "en_US.UTF-8";
        environment["LC_ALL"] = "en_US.UTF-8";
        environment["PYTHONIOENCODING"] = "utf-8";

        string shell;
        string[] commandLine;

        if (OperatingSystem.IsWindows())
        {
            shell = "pwsh.exe";
            // Launch PowerShell with UTF-8 output encoding set
            commandLine = ["-NoLogo", "-NoExit", "-Command", "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8"];
        }
        else
        {
            shell = "bash";
            commandLine = [];
        }

        var options = new PtyOptions
        {
            Name = "Terminal",
            Cols = 120,
            Rows = 30,
            Cwd = workingDirectory,
            App = shell,
            CommandLine = commandLine,
            Environment = environment
        };

        try
        {
            s_pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Terminal] Failed to start PTY: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    public async Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        if (s_pty == null)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "No active terminal session", cancellationToken);
            return;
        }

        lock (s_lock)
        {
            if (s_activeWebSocket != null)
            {
                _ = webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Another client is already connected", cancellationToken);
                return;
            }
            s_activeWebSocket = webSocket;
        }

        Task? readTask = null;
        try
        {
            // Read from PTY and send to WebSocket
            readTask = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (!cancellationToken.IsCancellationRequested && s_pty != null && webSocket.State == WebSocketState.Open)
                    {
                        var bytesRead = await s_pty.ReaderStream.ReadAsync(buffer, cancellationToken);
                        if (bytesRead == 0) break;
                        await webSocket.SendAsync(new ArraySegment<byte>(buffer, 0, bytesRead), WebSocketMessageType.Binary, true, cancellationToken);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.Error.WriteLine($"[Terminal] PTY read error: {ex.Message}"); }
            }, cancellationToken);

            // Read from WebSocket and send to PTY
            var receiveBuffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open && s_pty != null)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count > 0)
                {
                    await s_pty.WriterStream.WriteAsync(receiveBuffer, 0, result.Count, cancellationToken);
                    await s_pty.WriterStream.FlushAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[Terminal] WebSocket error: {ex.Message}"); }
        finally
        {
            lock (s_lock) { s_activeWebSocket = null; }
            if (readTask != null) try { await readTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            if (webSocket.State == WebSocketState.Open)
                try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); } catch { }
        }
    }

    public Task StopSessionAsync()
    {
        Cleanup();
        return Task.CompletedTask;
    }

    private static void Cleanup()
    {
        lock (s_lock)
        {
            s_pty?.Dispose();
            s_pty = null;
        }
    }
}
