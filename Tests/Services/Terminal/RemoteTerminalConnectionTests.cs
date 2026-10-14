using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;
using static VibeRails.Services.Terminal.RemoteTerminalConnection;

namespace Tests.Services.Terminal;

/// <summary>
/// VIBE-157: after a server recycle the relay had reaped this session's registration, and every
/// reconnect got 404 forever (attempts 32–65 across four terminals). These run the real client
/// against a local stand-in for /ws/v1/terminal.
/// </summary>
[Collection("ProcessEnvIsolation")]
public sealed class RemoteTerminalConnectionTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _originalUrl = ParserConfigs.GetFrontendUrl();
    private readonly string _originalKey = ParserConfigs.GetApiKey();
    private readonly FakeRelay _relay = new();

    [Theory]
    [InlineData(404, "SessionMissing")]
    [InlineData(400, "Rejected")]
    [InlineData(401, "Rejected")]
    [InlineData(403, "Rejected")]
    [InlineData(0, "HostUnavailable")]
    [InlineData(429, "HostUnavailable")]
    [InlineData(500, "HostUnavailable")]
    [InlineData(503, "HostUnavailable")]
    public void HandshakeStatusesAreClassified(int status, string expected)
    {
        Assert.Equal(expected, ClassifyFailure(status).ToString());
    }

    [Fact]
    public void RetryAfterAcceptsSecondsAndDates()
    {
        var now = new DateTimeOffset(2026, 10, 14, 5, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(120), ParseRetryAfter(Headers("retry-after", "120"), now));
        Assert.Equal(TimeSpan.FromMinutes(2), ParseRetryAfter(Headers("Retry-After", "Wed, 14 Oct 2026 05:02:00 GMT"), now));
        Assert.Equal(TimeSpan.Zero, ParseRetryAfter(Headers("Retry-After", "Wed, 14 Oct 2026 04:00:00 GMT"), now));
        Assert.Null(ParseRetryAfter(Headers("Retry-After", "soon"), now));
        Assert.Null(ParseRetryAfter(null, now));
    }

    [Fact]
    public async Task RelayThatLostTheSession_IsRegisteredAgain_AndConnects()
    {
        await StartRelayAsync();
        var reregistered = 0;
        await using var connection = new RemoteTerminalConnection(_ =>
        {
            Interlocked.Increment(ref reregistered);
            _relay.Registered = true;
            return Task.FromResult(true);
        }, new RemoteRelayBackoff());

        await connection.ConnectAsync(SessionId, Ct);

        Assert.True(connection.IsConnected);
        Assert.Equal(1, reregistered);
        Assert.Equal(2, _relay.Handshakes);
    }

    [Fact]
    public async Task ReconnectAfterTheRelayReapedTheSession_ReregistersInsteadOfLoopingOn404()
    {
        await StartRelayAsync(registered: true);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reregistered = 0;
        await using var connection = new RemoteTerminalConnection(_ =>
        {
            Interlocked.Increment(ref reregistered);
            _relay.Registered = true;
            return Task.FromResult(true);
        }, new RemoteRelayBackoff());
        connection.OnReconnected += () => reconnected.TrySetResult();
        await connection.ConnectAsync(SessionId, Ct);
        Assert.True(connection.IsConnected);

        // The server restarts: its row for this session is gone and the socket drops.
        _relay.Registered = false;
        await _relay.DropCurrentSocketAsync();

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.True(connection.IsConnected);
        Assert.Equal(1, reregistered);
        // First connect, one 404 after the drop, then the handshake after re-registering.
        Assert.Equal(3, _relay.Handshakes);
    }

    [Fact]
    public async Task SessionThatWasNeverRegistered_IsNotRegisteredByAReconnect()
    {
        // Job sessions are never exposed remotely; TerminalStateService answers false for them.
        await StartRelayAsync();
        await using var connection = new RemoteTerminalConnection(_ => Task.FromResult(false), new RemoteRelayBackoff());

        await connection.ConnectAsync(SessionId, Ct);

        Assert.False(connection.IsConnected);
        Assert.Equal(1, _relay.Handshakes);
    }

    [Fact]
    public async Task RejectedKey_DoesNotReregister()
    {
        await StartRelayAsync();
        _relay.Status = StatusCodes.Status401Unauthorized;
        var reregistered = 0;
        var backoff = new RemoteRelayBackoff();
        backoff.ReportHostFailure();
        await using var connection = new RemoteTerminalConnection(_ =>
        {
            Interlocked.Increment(ref reregistered);
            return Task.FromResult(true);
        }, backoff);

        await connection.ConnectAsync(SessionId, Ct);

        Assert.False(connection.IsConnected);
        Assert.Equal(0, reregistered);
        // A 401 is still an answer: the host is up, so the shared outage is cleared.
        Assert.False(backoff.IsInOutage);
    }

    [Fact]
    public async Task ServerError_RecordsAHostOutage()
    {
        await StartRelayAsync();
        _relay.Status = StatusCodes.Status503ServiceUnavailable;
        var backoff = new RemoteRelayBackoff();
        await using var connection = new RemoteTerminalConnection(_ => Task.FromResult(true), backoff);

        await connection.ConnectAsync(SessionId, Ct);

        Assert.False(connection.IsConnected);
        Assert.True(backoff.IsInOutage);
        Assert.Equal(1, _relay.Handshakes);
    }

    private const string SessionId = "3648c893-6634-42b3-8692-8988da904090";

    private async Task StartRelayAsync(bool registered = false)
    {
        _relay.Registered = registered;
        await _relay.StartAsync(Ct);
        ParserConfigs.SetFrontendUrl(_relay.Url);
        ParserConfigs.SetApiKey("synthetic-relay-key");
    }

    private static Dictionary<string, IEnumerable<string>> Headers(string name, string value) => new() { [name] = [value] };

    public async ValueTask DisposeAsync()
    {
        ParserConfigs.SetFrontendUrl(_originalUrl);
        ParserConfigs.SetApiKey(_originalKey);
        await _relay.DisposeAsync();
    }

    /// <summary>Answers /ws/v1/terminal like the hosted relay: 404 while the session has no row.</summary>
    private sealed class FakeRelay : IAsyncDisposable
    {
        private WebApplication? _app;
        private WebSocket? _current;
        private TaskCompletionSource? _release;
        private int _handshakes;

        public volatile bool Registered;
        public int? Status;
        public int Handshakes => Volatile.Read(ref _handshakes);
        public string Url => _app!.Urls.First();

        public async Task StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            _app = builder.Build();
            _app.UseWebSockets();
            _app.Map("/ws/v1/terminal", async context =>
            {
                Interlocked.Increment(ref _handshakes);
                if (context.Request.Headers["X-Api-Key"] != "synthetic-relay-key")
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                if (Status is { } status)
                {
                    context.Response.StatusCode = status;
                    return;
                }
                if (!Registered || !context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _current = socket;
                _release = release;
                // Keep reading like the real relay, so the client's close handshake completes.
                var reading = ReadUntilClosedAsync(socket);
                await Task.WhenAny(reading, release.Task);
                if (socket.State == WebSocketState.Open)
                {
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "restart", CancellationToken.None); }
                    catch (WebSocketException) { }
                }
                await reading;
            });
            await _app.StartAsync(ct);
        }

        private static async Task ReadUntilClosedAsync(WebSocket socket)
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType != WebSocketMessageType.Close)
                        continue;
                    if (socket.State == WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The client went away without a close frame.
            }
        }

        public async Task DropCurrentSocketAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (_release is null && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Interlocked.Exchange(ref _release, null)?.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            _release?.TrySetResult();
            _current?.Abort();
            if (_app is not null)
            {
                await _app.StopAsync(CancellationToken.None);
                await _app.DisposeAsync();
            }
        }
    }
}
