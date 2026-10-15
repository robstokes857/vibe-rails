using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeRails.Services.Terminal.Consumers;
using PtyTerminal = VibeRails.Services.Terminal.Terminal;

namespace VibeRails.Services.Terminal;

/// <summary>A server-resolved program and the viewer's terminal size.</summary>
public sealed record PrivatePtyLaunch(ProcessStartInfo Start, int Cols, int Rows);

/// <summary>A launch refusal whose message is safe to show the viewer.</summary>
public sealed class PrivatePtyRejectedException(string message) : Exception(message);

/// <summary>Viewer-facing wording for one kind of private terminal.</summary>
public sealed record PrivatePtyText(string InvalidRequest, string StartFailed, string Finished);

/// <summary>
/// One socket, one temporary PTY running a program the server chose from the first bounded
/// message. Deliberately has no tab, session recorder, input observer, replay, or remote-sharing
/// registration. Disconnecting owns process cleanup. Text frames from the server are
/// <c>ready</c>, <c>exit:{code}</c>, <c>stopped</c> and <c>error:{message}</c>; the viewer sends
/// raw input as binary frames and <c>stop</c> / resize commands as text.
/// </summary>
public static class PrivatePtySession
{
    /// <summary>Caps the launch message; it only ever carries IDs, a PIN and dimensions.</summary>
    public const int MaxLaunchMessageBytes = 16 * 1024;

    /// <summary>Runs an authenticated dashboard connection until the program ends or its window closes.</summary>
    public static async Task RunAsync(
        WebSocket socket,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<PrivatePtyLaunch>> prepare,
        PrivatePtyText text,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        PtyTerminal? terminal = null;
        Task<bool>? input = null;
        WebSocketConsumer? output = null;
        try
        {
            var launch = await PrepareAsync(socket, prepare, lifetime.Token);
            terminal = await PtyTerminal.CreateAsync(launch.Start.WorkingDirectory,
                launch.Start.Environment.Where(pair => pair.Value is not null)
                    .ToDictionary(pair => pair.Key, pair => pair.Value!),
                launch.Cols, launch.Rows, ct: lifetime.Token,
                app: launch.Start.FileName, argv: launch.Start.ArgumentList.ToArray());

            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.Exited += (_, code) => exited.TrySetResult(code);
            if (terminal.HasExited) exited.TrySetResult(terminal.ExitCode);
            output = new WebSocketConsumer(socket, lifetime.Token);
            using var subscription = terminal.Subscribe(output);
            await SendTextAsync(socket, "ready", lifetime.Token);
            terminal.StartReadLoop();
            input = ReadInputAsync(socket, terminal, lifetime.Token);
            var first = await Task.WhenAny(exited.Task, input, output.Completion);
            var stopped = first == input && await input;
            if (first != exited.Task)
            {
                terminal.KillProcessTree();
                if (!stopped) return; // closed/disconnected viewer
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
            }

            // Exit follows every output frame, including the program's final print.
            output.Dispose();
            await output.Completion.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
            await SendTextAsync(socket, stopped ? "stopped" : $"exit:{await exited.Task}", lifetime.Token);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, text.Finished, lifetime.Token);
            if (!input.IsCompleted)
                await input.WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
        }
        catch (PrivatePtyRejectedException error)
        {
            await SendErrorAsync(socket, error.Message);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            if (output is null) await SendErrorAsync(socket, text.InvalidRequest);
            else socket.Abort(); // Never race a validation response with the output sender.
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or TimeoutException)
        {
            // Closing the window, losing the connection, and shutting down all stop this PTY.
        }
        catch (Exception)
        {
            // Never log socket payloads or the program's environment (either may contain secrets).
            if (output is null) await SendErrorAsync(socket, text.StartFailed);
            else socket.Abort();
        }
        finally
        {
            output?.Dispose();
            await lifetime.CancelAsync();
            if (output is not null)
            {
                try { await output.Completion; } catch { /* disconnected/cancelled */ }
            }
            if (terminal is not null)
            {
                try { terminal.KillProcessTree(); } catch { /* may already have exited */ }
                await terminal.DisposeAsync();
            }
            if (input is not null)
            {
                try { await input; } catch { /* disconnect/cancellation */ }
            }
        }
    }

    /// <summary>Rejects terminal sizes outside what the viewer can legitimately ask for.</summary>
    public static void ValidateSize(int cols, int rows)
    {
        if (cols is < 10 or > 400 || rows is < 5 or > 150)
            throw new ArgumentException("Invalid terminal dimensions.");
    }

    private static async Task<PrivatePtyLaunch> PrepareAsync(
        WebSocket socket,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<PrivatePtyLaunch>> prepare,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var buffer = new byte[MaxLaunchMessageBytes];
        try
        {
            var (type, count) = await ReceiveAsync(socket, buffer, deadline.Token);
            if (type != WebSocketMessageType.Text) throw new ArgumentException("Expected launch request.");
            return await prepare(buffer.AsMemory(0, count), deadline.Token);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static async Task<bool> ReadInputAsync(WebSocket socket, PtyTerminal terminal, CancellationToken cancellationToken)
    {
        var buffer = new byte[TerminalControlProtocol.MaxMessageBytes];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var (type, count) = await ReceiveAsync(socket, buffer, cancellationToken);
                if (type == WebSocketMessageType.Close) return false;
                if (terminal.HasExited) continue;
                if (type == WebSocketMessageType.Binary)
                {
                    // Direct, serialized PTY input. Normal session routing records keystrokes;
                    // this private runner must never register input with history/observers.
                    await terminal.WriteBytesAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                else
                {
                    var command = Encoding.UTF8.GetString(buffer, 0, count);
                    if (command == "stop") return true;
                    if (TerminalControlProtocol.TryParseResizeCommand(command, out var cols, out var rows)
                        && cols <= 400 && rows <= 150)
                        terminal.Resize(cols, rows);
                }
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, count));
            }
            return false;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static async Task<(WebSocketMessageType Type, int Count)> ReceiveAsync(
        WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            if (count == buffer.Length) throw new ArgumentException("Message too large.");
            result = await socket.ReceiveAsync(buffer.AsMemory(count), cancellationToken);
            count += result.Count;
        } while (!result.EndOfMessage);
        return (result.MessageType, count);
    }

    private static Task SendTextAsync(WebSocket socket, string message, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);

    private static async Task SendErrorAsync(WebSocket socket, string message)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if (socket.State != WebSocketState.Open) return;
            await SendTextAsync(socket, $"error:{message}", deadline.Token);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Run failed", deadline.Token);
        }
        catch (Exception) { /* disconnected */ }
    }
}
