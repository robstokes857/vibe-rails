using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VibeRails.Daemon.Ipc;

public interface IDaemonControlClient
{
    Task<DaemonControlClientResult> SendAsync(
        string pipeName,
        DaemonControlCommand command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<DaemonControlClientResult> SendAsync(
        string pipeName,
        DaemonControlRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

/// <summary>One-shot current-user named-pipe client.</summary>
public sealed class DaemonControlClient : IDaemonControlClient
{
    public Task<DaemonControlClientResult> SendAsync(
        string pipeName,
        DaemonControlCommand command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(pipeName, DaemonControlRequest.Create(command), timeout, cancellationToken);

    public async Task<DaemonControlClientResult> SendAsync(
        string pipeName,
        DaemonControlRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(request);
        var effectiveTimeout = timeout ?? DaemonControlProtocol.DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        try
        {
            var json = JsonSerializer.Serialize(request, DaemonControlJsonContext.Default.DaemonControlRequest);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > DaemonControlProtocol.MaximumRequestBytes)
                throw new ArgumentException("Daemon control request is too large.", nameof(request));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(effectiveTimeout);
            // CurrentUserOnly mirrors the server side: the OS refuses the connection when the
            // process on the other end is not the same user, so a squatted pipe name cannot
            // impersonate the daemon or receive control commands.
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await pipe.ConnectAsync((int)Math.Ceiling(effectiveTimeout.TotalMilliseconds), deadline.Token)
                .ConfigureAwait(false);
            await PipeProtocolIo.WriteLineAsync(
                pipe,
                json,
                DaemonControlProtocol.MaximumRequestBytes,
                deadline.Token).ConfigureAwait(false);
            var line = await PipeProtocolIo.ReadLineAsync(
                pipe,
                DaemonControlProtocol.MaximumResponseBytes,
                deadline.Token).ConfigureAwait(false);
            if (line is null)
                return new DaemonControlClientResult(
                    DaemonControlClientOutcome.InvalidResponse,
                    Error: "The daemon closed the pipe without a response.");

            var response = JsonSerializer.Deserialize(
                line,
                DaemonControlJsonContext.Default.DaemonControlResponse);
            if (response is null)
                return new DaemonControlClientResult(
                    DaemonControlClientOutcome.InvalidResponse,
                    Error: "The daemon returned an empty response.");

            if (response.ProtocolVersion != DaemonControlProtocol.Version ||
                response.ErrorCode == DaemonControlProtocol.ProtocolMismatchError)
            {
                return new DaemonControlClientResult(
                    DaemonControlClientOutcome.ProtocolMismatch,
                    response,
                    response.Error ?? "The daemon control protocol version does not match.");
            }

            return response.Success
                ? new DaemonControlClientResult(DaemonControlClientOutcome.Success, response)
                : new DaemonControlClientResult(
                    DaemonControlClientOutcome.Rejected,
                    response,
                    response.Error ?? "The daemon rejected the request.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DaemonControlClientResult(
                DaemonControlClientOutcome.Unreachable,
                Error: "The daemon control request timed out.");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return new DaemonControlClientResult(DaemonControlClientOutcome.Unreachable, Error: ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or DecoderFallbackException)
        {
            return new DaemonControlClientResult(DaemonControlClientOutcome.InvalidResponse, Error: ex.Message);
        }
    }
}
