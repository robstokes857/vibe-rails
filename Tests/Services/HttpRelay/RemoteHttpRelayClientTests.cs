using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using VibeRails.Services.HttpRelay;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.HttpRelay;

[Collection("ProcessEnvIsolation")]
public sealed class RemoteHttpRelayClientTests : IAsyncLifetime
{
    private readonly string _originalFrontendUrl = ParserConfigs.GetFrontendUrl();
    private readonly string _originalApiKey = ParserConfigs.GetApiKey();
    private WebApplication? _app;

    [Fact]
    public async Task UsesBothSubprotocols_AndReusesOneConnection()
    {
        var connectionCount = 0;
        string[]? offeredProtocols = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/ws/v1/http-relay", async context =>
        {
            offeredProtocols = context.WebSockets.WebSocketRequestedProtocols.ToArray();
            Interlocked.Increment(ref connectionCount);
            using var socket = await context.WebSockets.AcceptWebSocketAsync(
                HttpRelayProtocol.ApplicationSubprotocol);
            var buffer = new byte[8192];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult frame;
                    do
                    {
                        frame = await socket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            context.RequestAborted);
                        if (frame.MessageType == WebSocketMessageType.Close)
                            return;
                        message.Write(buffer, 0, frame.Count);
                    }
                    while (!frame.EndOfMessage);

                    using var document = JsonDocument.Parse(message.ToArray());
                    if (document.RootElement.GetProperty("type").GetString()
                        != HttpRelayProtocol.RequestType)
                    {
                        continue;
                    }

                    var request = JsonSerializer.Deserialize(
                        message.ToArray(),
                        HttpRelayJsonContext.Default.HttpRelayRequest)!;
                    var response = new HttpRelayResponse(
                        1,
                        "http_response",
                        request.RequestId,
                        200,
                        "OK",
                        new Dictionary<string, string[]> { ["content-type"] = ["application/json"] },
                        new HttpRelayBody("base64", "e30="),
                        3);
                    var payload = JsonSerializer.SerializeToUtf8Bytes(
                        response,
                        HttpRelayJsonContext.Default.HttpRelayResponse);
                    await socket.SendAsync(
                        payload,
                        WebSocketMessageType.Text,
                        true,
                        context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        });
        await _app.StartAsync(TestContext.Current.CancellationToken);

        ParserConfigs.SetFrontendUrl(_app.Urls.Single());
        ParserConfigs.SetApiKey("test-key");
        await using var client = new RemoteHttpRelayClient();

        var first = await client.SendAsync(CreateRequest(), TestContext.Current.CancellationToken);
        var second = await client.SendAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal(1, connectionCount);
        Assert.NotNull(offeredProtocols);
        Assert.Equal(2, offeredProtocols!.Length);
        Assert.Contains(HttpRelayProtocol.ApplicationSubprotocol, offeredProtocols!);
        Assert.Contains(
            HttpRelayProtocol.CreateCredentialSubprotocol("test-key"),
            offeredProtocols!);
    }

    [Fact]
    public async Task ConcurrentRequests_AreCorrelatedWhenResponsesArriveOutOfOrder()
    {
        var secondResponseSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await StartRelayServerAsync(async (socket, cancellationToken) =>
        {
            var firstInbound = await ReceiveRequestAsync(socket, cancellationToken);
            var secondInbound = await ReceiveRequestAsync(socket, cancellationToken);

            await SendResponseAsync(
                socket,
                CreateResponse(secondInbound.RequestId, 202, "second"),
                cancellationToken);
            secondResponseSent.TrySetResult();

            await releaseFirstResponse.Task.WaitAsync(cancellationToken);
            await SendResponseAsync(
                socket,
                CreateResponse(firstInbound.RequestId, 201, "first"),
                cancellationToken);
        });

        await using var client = new RemoteHttpRelayClient();
        var firstRequest = CreateRequest();
        var secondRequest = CreateRequest();

        var firstTask = client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        var secondTask = client.SendAsync(secondRequest, TestContext.Current.CancellationToken);

        await secondResponseSent.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var second = await secondTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(202, second.StatusCode);
        Assert.Equal("second", DecodeBody(second));
        Assert.False(firstTask.IsCompleted);

        releaseFirstResponse.TrySetResult();
        var first = await firstTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(201, first.StatusCode);
        Assert.Equal("first", DecodeBody(first));
    }

    [Fact]
    public async Task DuplicateRequestId_IsRejectedWithoutDetachingOriginalRequest()
    {
        var requestReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await StartRelayServerAsync(async (socket, cancellationToken) =>
        {
            var request = await ReceiveRequestAsync(socket, cancellationToken);
            requestReceived.TrySetResult();
            await releaseResponse.Task.WaitAsync(cancellationToken);
            await SendResponseAsync(
                socket,
                CreateResponse(request.RequestId, 200, "original"),
                cancellationToken);
        });

        await using var client = new RemoteHttpRelayClient();
        var request = CreateRequest();
        var originalTask = client.SendAsync(request, TestContext.Current.CancellationToken);

        await requestReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<HttpRelayProtocolException>(() =>
            client.SendAsync(request, TestContext.Current.CancellationToken));

        releaseResponse.TrySetResult();
        var original = await originalTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(200, original.StatusCode);
        Assert.Equal("original", DecodeBody(original));
    }

    [Fact]
    public async Task Reset_FailsOldSocketAndNextRequestReconnects()
    {
        var connectionCount = 0;
        var firstRequestReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequestReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await StartRelayServerAsync(async (socket, cancellationToken) =>
        {
            var connectionNumber = Interlocked.Increment(ref connectionCount);
            var request = await ReceiveRequestAsync(socket, cancellationToken);
            if (connectionNumber == 1)
            {
                firstRequestReceived.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            secondRequestReceived.TrySetResult();
            await SendResponseAsync(
                socket,
                CreateResponse(request.RequestId, 200, "reconnected"),
                cancellationToken);
        });

        await using var client = new RemoteHttpRelayClient();
        var firstTask = client.SendAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await firstRequestReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        client.Reset();
        await Assert.ThrowsAsync<HttpRelayTransportException>(() => firstTask);

        var second = await client.SendAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);
        await secondRequestReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, connectionCount);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal("reconnected", DecodeBody(second));
    }

    [Fact]
    public async Task FragmentedTextResponse_IsReassembledBeforeDeserialization()
    {
        await StartRelayServerAsync(async (socket, cancellationToken) =>
        {
            var request = await ReceiveRequestAsync(socket, cancellationToken);
            var response = CreateResponse(request.RequestId, 206, "fragmented response");
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                response,
                HttpRelayJsonContext.Default.HttpRelayResponse);

            var firstLength = 7;
            var secondLength = 19;
            await socket.SendAsync(
                payload.AsMemory(0, firstLength),
                WebSocketMessageType.Text,
                endOfMessage: false,
                cancellationToken);
            await socket.SendAsync(
                payload.AsMemory(firstLength, secondLength),
                WebSocketMessageType.Text,
                endOfMessage: false,
                cancellationToken);
            await socket.SendAsync(
                payload.AsMemory(firstLength + secondLength),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        });

        await using var client = new RemoteHttpRelayClient();
        var response = await client.SendAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(206, response.StatusCode);
        Assert.Equal("fragmented response", DecodeBody(response));
        Assert.Equal(["yes"], response.Headers["x-fragmented"]);
    }

    [Fact]
    public async Task CallerCancellation_SendsAdvisoryCancelEnvelope()
    {
        var requestReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelReceived = new TaskCompletionSource<HttpRelayCancel>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await StartRelayServerAsync(async (socket, cancellationToken) =>
        {
            await ReceiveRequestAsync(socket, cancellationToken);
            requestReceived.TrySetResult();

            var payload = await ReceiveMessageAsync(socket, cancellationToken);
            var cancel = JsonSerializer.Deserialize(
                payload,
                HttpRelayJsonContext.Default.HttpRelayCancel)
                ?? throw new InvalidOperationException("The cancellation envelope was empty.");
            cancelReceived.TrySetResult(cancel);
        });

        await using var client = new RemoteHttpRelayClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var request = CreateRequest();
        var responseTask = client.SendAsync(request, cancellation.Token);

        await requestReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
        var cancel = await cancelReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpRelayProtocol.Version, cancel.Version);
        Assert.Equal(HttpRelayProtocol.CancelType, cancel.Type);
        Assert.Equal(request.RequestId, cancel.RequestId);
    }

    private async Task StartRelayServerAsync(
        Func<WebSocket, CancellationToken, Task> runSessionAsync)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/ws/v1/http-relay", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(
                HttpRelayProtocol.ApplicationSubprotocol);
            try
            {
                await runSessionAsync(socket, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
            }
        });
        await _app.StartAsync(TestContext.Current.CancellationToken);

        ParserConfigs.SetFrontendUrl(_app.Urls.Single());
        ParserConfigs.SetApiKey("test-key");
    }

    private static async Task<HttpRelayRequest> ReceiveRequestAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var payload = await ReceiveMessageAsync(socket, cancellationToken);
        return JsonSerializer.Deserialize(
            payload,
            HttpRelayJsonContext.Default.HttpRelayRequest)
            ?? throw new InvalidOperationException("The request envelope was empty.");
    }

    private static async Task<byte[]> ReceiveMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken);
            if (frame.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("Expected a WebSocket text message.");
            message.Write(buffer, 0, frame.Count);
        }
        while (!frame.EndOfMessage);

        return message.ToArray();
    }

    private static async Task SendResponseAsync(
        WebSocket socket,
        HttpRelayResponse response,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            response,
            HttpRelayJsonContext.Default.HttpRelayResponse);
        await socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static HttpRelayResponse CreateResponse(
        string requestId,
        int statusCode,
        string body) => new(
            HttpRelayProtocol.Version,
            HttpRelayProtocol.ResponseType,
            requestId,
            statusCode,
            null,
            new Dictionary<string, string[]> { ["x-fragmented"] = ["yes"] },
            new HttpRelayBody("base64", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(body))),
            1);

    private static string DecodeBody(HttpRelayResponse response) =>
        System.Text.Encoding.UTF8.GetString(HttpRelayProtocol.DecodeBody(response.Body));

    private static HttpRelayRequest CreateRequest() => new(
        1,
        "http_request",
        Guid.NewGuid().ToString("D"),
        "GET",
        "https://jsonplaceholder.typicode.com/posts/1",
        new Dictionary<string, string[]> { ["accept"] = ["application/json"] },
        null,
        30_000);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        ParserConfigs.SetFrontendUrl(_originalFrontendUrl);
        ParserConfigs.SetApiKey(_originalApiKey);
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}
