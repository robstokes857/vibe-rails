using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VibeRails.Daemon.Ipc;

/// <summary>
/// Bounded, one-request-per-connection current-user control server. It performs no command work of
/// its own and can therefore host any application handler without exposing arbitrary execution.
/// </summary>
public sealed class DaemonControlServer(
    string pipeName,
    IDaemonControlHandler handler,
    TimeSpan? requestTimeout = null)
{
    private readonly string _pipeName = string.IsNullOrWhiteSpace(pipeName)
        ? throw new ArgumentException("A pipe name is required.", nameof(pipeName))
        : pipeName;
    private readonly IDaemonControlHandler _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    private readonly TimeSpan _requestTimeout = ValidateTimeout(requestTimeout ?? DaemonControlProtocol.DefaultTimeout);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The pipe name is squatted with an incompatible DACL, or the previous instance
                // has not fully released its single server slot yet. Faulting the hosted service
                // here would tear down the whole daemon; back off and retry instead.
                if (!await DelayQuietlyAsync(stoppingToken).ConfigureAwait(false))
                    break;
                continue;
            }

            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                    using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    requestDeadline.CancelAfter(_requestTimeout);
                    await HandleConnectionAsync(pipe, requestDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // A connected client exhausted its bounded request window. Accept the next client.
                }
                catch (IOException)
                {
                    // The client disconnected mid-request. Accept the next client.
                }
                catch (InvalidDataException)
                {
                    // Oversized or malformed framing is rejected by closing this one connection.
                }
                catch (DecoderFallbackException)
                {
                    // Strict UTF-8 rejects malformed input without ending the server loop.
                }
            }
        }
    }

    private static async Task<bool> DelayQuietlyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private NamedPipeServerStream CreatePipe() => new(
        _pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        inBufferSize: DaemonControlProtocol.MaximumRequestBytes,
        outBufferSize: DaemonControlProtocol.MaximumResponseBytes);

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var line = await PipeProtocolIo.ReadLineAsync(
            pipe,
            DaemonControlProtocol.MaximumRequestBytes,
            cancellationToken).ConfigureAwait(false);
        if (line is null)
            return;

        DaemonControlRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(
                line,
                DaemonControlJsonContext.Default.DaemonControlRequest);
        }
        catch (JsonException ex)
        {
            await WriteResponseAsync(pipe, Error(
                DaemonControlProtocol.InvalidRequestError,
                $"Invalid JSON request: {ex.Message}"), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Command))
        {
            await WriteResponseAsync(pipe, Error(
                DaemonControlProtocol.InvalidRequestError,
                "A command is required."), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.ProtocolVersion != DaemonControlProtocol.Version)
        {
            await WriteResponseAsync(pipe, Error(
                DaemonControlProtocol.ProtocolMismatchError,
                $"Protocol {request.ProtocolVersion} is not supported; this daemon uses protocol {DaemonControlProtocol.Version}."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!DaemonControlProtocol.TryParseCommand(request.Command, out var command))
        {
            await WriteResponseAsync(pipe, Error(
                DaemonControlProtocol.UnknownCommandError,
                $"Unknown daemon command '{request.Command}'."), cancellationToken).ConfigureAwait(false);
            return;
        }

        DaemonControlHandlerResult result;
        try
        {
            result = await _handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = DaemonControlHandlerResult.Fail(ex.Message);
        }

        var response = result.Success
            ? new DaemonControlResponse(
                DaemonControlProtocol.Version,
                true,
                Message: result.Message,
                Payload: result.Payload)
            : Error(DaemonControlProtocol.HandlerError, result.Error ?? "The daemon command failed.");
        await WriteResponseAsync(pipe, response, cancellationToken).ConfigureAwait(false);

        // Some commands (notably SHUTDOWN) need to change host state only after the client has a
        // complete, flushed acknowledgement. Keeping that transition here prevents cancellation
        // of the server's stopping token from tearing down the pipe before the response is sent.
        if (result.AfterResponse is not null)
            await result.AfterResponse().ConfigureAwait(false);
    }

    private static Task WriteResponseAsync(
        Stream pipe,
        DaemonControlResponse response,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, DaemonControlJsonContext.Default.DaemonControlResponse);
        return PipeProtocolIo.WriteLineAsync(
            pipe,
            json,
            DaemonControlProtocol.MaximumResponseBytes,
            cancellationToken);
    }

    private static DaemonControlResponse Error(string code, string error) => new(
        DaemonControlProtocol.Version,
        false,
        ErrorCode: code,
        Error: error);

    private static TimeSpan ValidateTimeout(TimeSpan timeout) => timeout <= TimeSpan.Zero
        ? throw new ArgumentOutOfRangeException(nameof(timeout))
        : timeout;
}
