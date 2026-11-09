using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails;
using VibeRails.Jobs;
using VibeRails.Services;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Jobs;

public sealed class RunbookRepoCheckJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(200, true)]
    [InlineData(204, false)]
    [InlineData(302, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(500, false)]
    public async Task HourlyCheckLogsOnlyExact200AndNeverReadsTheBody(int status, bool ok)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new UnreadableContent()
        }));
        var keys = new Mock<IRemoteAccountKeyStore>();
        keys.SetupSequence(k => k.Read()).Returns("fixture-first-key").Returns("fixture-second-key");
        var logger = new CaptureLogger();
        using var job = Job(handler, keys.Object, logger);

        Assert.Equal(TimeSpan.FromHours(1), await job.RunTickAsync(Ct));
        Assert.Equal(TimeSpan.FromHours(1), await job.RunTickAsync(Ct));
        Assert.Equal(new[] { "fixture-first-key", "fixture-second-key" }, handler.Keys);
        Assert.All(logger.Results, result =>
        {
            Assert.Equal(ok, result["Ok"]);
            Assert.Equal(status, result["Status"]);
            Assert.Equal("http_response", result["Outcome"]);
        });
        Assert.Equal(2, logger.Results.Count);
        Assert.DoesNotContain("fixture-", string.Join("\n", logger.Messages));
    }

    [Fact]
    public async Task MissingKeySkipsNetworkAndDoesNotStopFutureTicks()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var keys = new Mock<IRemoteAccountKeyStore>();
        keys.SetupSequence(k => k.Read()).Returns(" ").Returns("fixture-later-key");
        var logger = new CaptureLogger();
        using var job = Job(handler, keys.Object, logger);
        await job.RunTickAsync(Ct);
        Assert.Empty(handler.Keys);
        Assert.Equal("no_api_key", Assert.Single(logger.Results)["Outcome"]);
        await job.RunTickAsync(Ct);
        Assert.Single(handler.Keys);
        Assert.Equal(true, logger.Results.Last()["Ok"]);
    }

    [Theory]
    [InlineData(false, "network_error")]
    [InlineData(true, "timeout")]
    public async Task FailuresAreBoundedSanitizedAndRetriedOnlyOnTheNextTick(bool timeout, string outcome)
    {
        using var handler = new Handler(async (_, ct) =>
        {
            if (!timeout) throw new HttpRequestException("fixture-secret-in-exception");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var logger = new CaptureLogger();
        using var job = Job(handler, Keys(), logger);
        await job.RunTickAsync(Ct);
        Assert.Single(handler.Keys);
        Assert.Equal(false, Assert.Single(logger.Results)["Ok"]);
        Assert.Equal(outcome, logger.Results[0]["Outcome"]);
        Assert.DoesNotContain("fixture-secret", string.Join("\n", logger.Messages));
        Assert.All(logger.Exceptions, Assert.Null);
        await job.RunTickAsync(Ct);
        Assert.Equal(2, handler.Keys.Count);
    }

    [Fact]
    public async Task ShutdownCancelsTheInFlightRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var logger = new CaptureLogger();
        using var job = Job(handler, Keys(), logger, TimeSpan.FromSeconds(20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var tick = job.RunTickAsync(stop.Token);
        await entered.Task.WaitAsync(Ct);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick);
        Assert.Empty(logger.Results);
    }

    [Fact]
    public void ProductionTransportDisablesRedirectsCookiesAndBodyDrain()
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, ["--web"], "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(RunbookRepoCheckJob.HttpClientName);
        Assert.Equal(TimeSpan.FromSeconds(20), client.Timeout);
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(RunbookRepoCheckJob.HttpClientName);
        while (handler is DelegatingHandler wrapper) handler = wrapper.InnerHandler!;
        var transport = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseCookies);
        Assert.Equal(16, transport.MaxResponseHeadersLength);
        Assert.Equal(0, transport.MaxResponseDrainSize);
        Assert.Equal(TimeSpan.FromSeconds(10), transport.ConnectTimeout);
    }

    private static IRemoteAccountKeyStore Keys()
    {
        var keys = new Mock<IRemoteAccountKeyStore>();
        keys.Setup(k => k.Read()).Returns("fixture-secret");
        return keys.Object;
    }

    private static RunbookRepoCheckJob Job(Handler handler, IRemoteAccountKeyStore keys,
        CaptureLogger logger, TimeSpan? timeout = null) =>
        new(logger, Mock.Of<ISystemResourceService>(), new Factory(handler, timeout ?? TimeSpan.FromMilliseconds(100)), keys);

    private sealed class Factory(Handler handler, TimeSpan timeout) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(RunbookRepoCheckJob.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string> Keys { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://viberails.ai/api/v1/runbook_repo", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            Assert.False(request.Headers.Contains("Cookie"));
            Keys.Add(Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return send(request, ct);
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Response body must not be read");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class CaptureLogger : ILogger<RunbookRepoCheckJob>
    {
        public List<Dictionary<string, object?>> Results { get; } = [];
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                var fields = values.ToDictionary(pair => pair.Key, pair => pair.Value);
                if (fields.ContainsKey("Outcome")) Results.Add(fields);
            }
        }
    }
}
