using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using VibeRails.Utils;

namespace VibeRails.Services.HttpRelay;

/// <summary>
/// Persistent, on-demand HTTP relay connection. Requests are correlated by requestId and may
/// complete out of order. Sends are serialized because ClientWebSocket permits one concurrent
/// send and one concurrent receive, not multiple sends.
/// </summary>
public sealed class RemoteHttpRelayClient : IRemoteHttpRelayClient, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _requestSlots = new(8, 8);
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly object _stateGate = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private string? _connectionIdentity;
    private int _generation;
    private int _disposed;

    public async Task<HttpRelayResponse> SendAsync(
        HttpRelayRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        HttpRelayProtocol.ValidateRequest(request);
        var envelope = HttpRelayProtocol.SerializeRequest(request);

        using var timeoutCts = new CancellationTokenSource(request.TimeoutMs);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        var operationToken = operationCts.Token;

        try
        {
            await _requestSlots.WaitAsync(operationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The HTTP relay request timed out while waiting to start.");
        }

        ClientWebSocket? socket = null;
        var sent = false;
        var registered = false;
        var completion = new TaskCompletionSource<HttpRelayResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            socket = await GetConnectedSocketAsync(operationToken);
            if (!_pending.TryAdd(request.RequestId, new PendingRequest(socket, completion)))
                throw new HttpRelayProtocolException("A relay request with this requestId is already active.");
            registered = true;

            await SendTextAsync(socket, envelope, operationToken);
            sent = true;
            return await completion.Task.WaitAsync(operationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (sent && socket is not null)
                await SendCancelBestEffortAsync(socket, request.RequestId);
            throw new TimeoutException("The HTTP relay request timed out.");
        }
        catch (OperationCanceledException)
        {
            if (sent && socket is not null)
                await SendCancelBestEffortAsync(socket, request.RequestId);
            throw;
        }
        catch (WebSocketException ex)
        {
            if (socket is not null)
                Disconnect(socket, new HttpRelayTransportException("The HTTP relay connection was lost.", ex));
            throw new HttpRelayTransportException("The HTTP relay connection was lost.", ex);
        }
        finally
        {
            // A rejected duplicate never owned the dictionary entry. Removing by key in that
            // case would detach the original request and make its eventual response disappear.
            if (registered)
                _pending.TryRemove(request.RequestId, out _);
            _requestSlots.Release();
        }
    }

    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        ClientWebSocket? socket;
        CancellationTokenSource? connectionCts;
        lock (_stateGate)
        {
            socket = _socket;
            connectionCts = _connectionCts;
            _socket = null;
            _connectionCts = null;
            _receiveTask = null;
            _connectionIdentity = null;
        }

        connectionCts?.Cancel();
        try { socket?.Abort(); } catch { }
        socket?.Dispose();
        connectionCts?.Dispose();
        if (socket is not null)
        {
            FailPending(
                socket,
                new HttpRelayTransportException("The HTTP relay connection was reset."));
        }
    }

    private async Task<ClientWebSocket> GetConnectedSocketAsync(CancellationToken cancellationToken)
    {
        var frontendUrl = ParserConfigs.GetFrontendUrl();
        var apiKey = ParserConfigs.GetApiKey();
        if (string.IsNullOrWhiteSpace(frontendUrl))
            throw new HttpRelayConfigurationException("The VibeRails frontend URL is not configured.");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new HttpRelayConfigurationException("A VibeRails API key is required.");

        var endpoint = HttpRelayProtocol.CreateWebSocketUri(frontendUrl);
        var credentialSubprotocol = HttpRelayProtocol.CreateCredentialSubprotocol(apiKey);
        var identity = endpoint.AbsoluteUri + "\n" + credentialSubprotocol;

        lock (_stateGate)
        {
            if (_socket is { State: WebSocketState.Open } open
                && string.Equals(_connectionIdentity, identity, StringComparison.Ordinal))
            {
                return open;
            }
        }

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateGate)
            {
                if (_socket is { State: WebSocketState.Open } open
                    && string.Equals(_connectionIdentity, identity, StringComparison.Ordinal))
                {
                    return open;
                }
            }

            ResetCurrentConnectionForReconnect();
            var generation = Volatile.Read(ref _generation);
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = KeepAliveInterval;
            socket.Options.AddSubProtocol(HttpRelayProtocol.ApplicationSubprotocol);
            socket.Options.AddSubProtocol(credentialSubprotocol);

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(endpoint, connectCts.Token);

                if (!string.Equals(
                        socket.SubProtocol,
                        HttpRelayProtocol.ApplicationSubprotocol,
                        StringComparison.Ordinal))
                {
                    throw new HttpRelayProtocolException(
                        "The relay server did not negotiate the HTTP relay protocol.");
                }

                if (generation != Volatile.Read(ref _generation))
                    throw new HttpRelayTransportException(
                        "The HTTP relay configuration changed while connecting.");

                var connectionCts = new CancellationTokenSource();
                lock (_stateGate)
                {
                    // Reset increments the generation before taking this lock. Recheck while
                    // publishing the socket so a settings reset cannot slip between the earlier
                    // check and this assignment, leaving a just-connected stale-key socket live.
                    if (generation != Volatile.Read(ref _generation))
                    {
                        connectionCts.Dispose();
                        throw new HttpRelayTransportException(
                            "The HTTP relay configuration changed while connecting.");
                    }
                    _socket = socket;
                    _connectionCts = connectionCts;
                    _connectionIdentity = identity;
                    _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, connectionCts.Token));
                }
                return socket;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new HttpRelayTransportException("Connecting to the HTTP relay timed out.");
            }
            catch (HttpRelayException)
            {
                socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                socket.Dispose();
                throw new HttpRelayTransportException("Unable to connect to the HTTP relay.", ex);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task SendTextAsync(
        ClientWebSocket socket,
        byte[] utf8Json,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
                throw new HttpRelayTransportException("The HTTP relay connection is not open.");

            await socket.SendAsync(
                utf8Json,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task SendCancelBestEffortAsync(ClientWebSocket socket, string requestId)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await SendTextAsync(
                socket,
                HttpRelayProtocol.SerializeCancel(requestId),
                cancellation.Token);
        }
        catch
        {
            // Cancellation is advisory. The original timeout/cancellation remains the useful error.
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        Exception failure = new HttpRelayTransportException("The HTTP relay connection closed.");
        var buffer = new byte[8192];

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new HttpRelayTransportException("The HTTP relay server closed the connection.");
                    if (result.MessageType != WebSocketMessageType.Text)
                        throw new HttpRelayProtocolException("The HTTP relay returned a binary message.");
                    if (message.Length + result.Count > HttpRelayProtocol.MaxEnvelopeBytes)
                        throw new HttpRelayProtocolException("The relay response envelope is too large.");

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                object inbound;
                try
                {
                    // Throwing UTF-8 validation prevents replacement characters from changing the
                    // JSON message the server actually sent.
                    var strictUtf8 = new UTF8Encoding(false, true);
                    var text = strictUtf8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                    inbound = HttpRelayProtocol.DeserializeInbound(Encoding.UTF8.GetBytes(text));
                }
                catch (DecoderFallbackException ex)
                {
                    throw new HttpRelayProtocolException("The relay response is not valid UTF-8.", ex);
                }

                switch (inbound)
                {
                    case HttpRelayResponse response:
                        ValidateInboundEnvelope(response.Version, response.Type, response.RequestId);
                        if (response.StatusCode is < 100 or > 599)
                            throw new HttpRelayProtocolException("The relay returned an invalid HTTP status.");
                        if (response.Headers is null || response.ElapsedMs < 0)
                            throw new HttpRelayProtocolException("The relay returned an invalid HTTP response.");
                        if (_pending.TryGetValue(response.RequestId, out var responsePending)
                            && ReferenceEquals(responsePending.Socket, socket))
                        {
                            responsePending.Completion.TrySetResult(response);
                        }
                        break;

                    case HttpRelayError error:
                        ValidateInboundEnvelope(error.Version, error.Type, error.RequestId);
                        if (string.IsNullOrWhiteSpace(error.ErrorCode)
                            || string.IsNullOrWhiteSpace(error.Message))
                        {
                            throw new HttpRelayProtocolException("The relay returned an invalid error.");
                        }
                        if (_pending.TryGetValue(error.RequestId, out var errorPending)
                            && ReferenceEquals(errorPending.Socket, socket))
                        {
                            errorPending.Completion.TrySetException(new HttpRelayRemoteException(
                                error.ErrorCode,
                                error.Message,
                                error.Retryable));
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = new HttpRelayTransportException("The HTTP relay connection was reset.");
        }
        catch (Exception ex)
        {
            failure = ex is HttpRelayException
                ? ex
                : new HttpRelayTransportException("The HTTP relay receive loop failed.", ex);
        }
        finally
        {
            Disconnect(socket, failure);
        }
    }

    private static void ValidateInboundEnvelope(int version, string type, string requestId)
    {
        if (version != HttpRelayProtocol.Version
            || type is not (HttpRelayProtocol.ResponseType or HttpRelayProtocol.ErrorType)
            || !Guid.TryParse(requestId, out _))
        {
            throw new HttpRelayProtocolException("The relay returned an invalid envelope.");
        }
    }

    private void ResetCurrentConnectionForReconnect()
    {
        ClientWebSocket? socket;
        CancellationTokenSource? connectionCts;
        lock (_stateGate)
        {
            socket = _socket;
            connectionCts = _connectionCts;
            _socket = null;
            _connectionCts = null;
            _receiveTask = null;
            _connectionIdentity = null;
        }

        connectionCts?.Cancel();
        try { socket?.Abort(); } catch { }
        socket?.Dispose();
        connectionCts?.Dispose();
        if (socket is not null)
        {
            FailPending(
                socket,
                new HttpRelayTransportException("The HTTP relay connection was replaced."));
        }
    }

    private void Disconnect(ClientWebSocket socket, Exception failure)
    {
        CancellationTokenSource? connectionCts = null;
        lock (_stateGate)
        {
            if (ReferenceEquals(_socket, socket))
            {
                _socket = null;
                connectionCts = _connectionCts;
                _connectionCts = null;
                _receiveTask = null;
                _connectionIdentity = null;
            }
        }

        if (connectionCts is null)
            return;

        connectionCts.Cancel();
        try { socket.Abort(); } catch { }
        socket.Dispose();
        connectionCts.Dispose();
        FailPending(socket, failure);
    }

    private void FailPending(ClientWebSocket socket, Exception failure)
    {
        foreach (var pending in _pending.Values)
        {
            // Reset/disconnect cleanup may overlap a successful reconnect. Only requests sent
            // through the affected socket belong to this failure; newer-generation requests
            // must remain attached to their live connection.
            if (ReferenceEquals(pending.Socket, socket))
                pending.Completion.TrySetException(failure);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Reset();
        return ValueTask.CompletedTask;
    }

    private sealed record PendingRequest(
        ClientWebSocket Socket,
        TaskCompletionSource<HttpRelayResponse> Completion);
}
