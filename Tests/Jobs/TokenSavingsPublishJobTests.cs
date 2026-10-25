using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DB;
using VibeRails.Jobs;
using VibeRails.Services;
using VibeRails.Utils;
using Xunit;

namespace Tests.Jobs;

[Collection("ProcessEnvIsolation")]
public sealed class TokenSavingsPublishJobTests : IDisposable
{
    private readonly string _originalApiKey = ParserConfigs.GetApiKey();
    private readonly string _originalStatePath = ParserConfigs.GetStatePath();
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"viberails-token-publish-tests-{Guid.NewGuid():N}");

    public TokenSavingsPublishJobTests()
    {
        Directory.CreateDirectory(_testRoot);
        ParserConfigs.SetStatePath(Path.Combine(_testRoot, "state.db"));
    }

    [Fact]
    public async Task ExecuteJob_ApiKeyMissing_DoesNotSendRequest()
    {
        ParserConfigs.SetApiKey("   ");
        var handler = new CapturingHandler();
        var job = CreateJob(handler);

        await InvokeExecuteJobAsync(job);

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ExecuteJob_WithApiKey_PostsCorrectPayload()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var store = CreateStore(tokensSaved: 2000);
        var handler = new CapturingHandler();
        var job = CreateJob(handler, store);

        await InvokeExecuteJobAsync(job);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("test-api-key", handler.ApiKey);

        Assert.NotNull(handler.RequestBody);
        using var payload = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(ComputerNameFormatter.Machine(),
            payload.RootElement.GetProperty("computerName").GetString());
        Assert.Equal(2000,
            payload.RootElement.GetProperty("tokensSaved").GetInt64());
        Assert.Equal("tsr_test", payload.RootElement.GetProperty("reportId").GetString());
        Assert.Equal("cmp_test", payload.RootElement.GetProperty("computerId").GetString());
        Assert.Equal("/api/v2/token-savings", handler.RequestUri!.AbsolutePath);
        store.Verify(s => s.AcknowledgeAsync("tsr_test", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteJob_PostsNormalizedComputerName()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler();
        var job = CreateJob(handler);

        await InvokeExecuteJobAsync(job);

        using var payload = JsonDocument.Parse(handler.RequestBody!);
        var posted = payload.RootElement.GetProperty("computerName").GetString();
        // Same normalization AppSettingsRoutes applies, so one machine can't post records under
        // both a truncated and an untruncated name.
        Assert.Equal(ComputerNameFormatter.Machine(), posted);
        Assert.NotNull(posted);
        Assert.True(posted!.Length <= ComputerNameFormatter.MaxLength);
        Assert.Equal(posted.Trim(), posted);
    }

    [Fact]
    public async Task ExecuteJob_NoPendingIncrement_DoesNotSend()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var store = new Mock<ITokenSavingsPublishStore>();
        var handler = new CapturingHandler();
        await InvokeExecuteJobAsync(CreateJob(handler, store));
        Assert.Equal(0, handler.RequestCount);
        store.Verify(s => s.PrepareAsync(ComputerNameFormatter.Machine(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteJob_NonSuccessStatus_DoesNotThrow()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var job = CreateJob(handler);

        await InvokeExecuteJobAsync(job);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ExecuteJob_HttpRequestException_SwallowedAndDoesNotThrow()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler(() => throw new HttpRequestException("connection refused"));
        var job = CreateJob(handler);

        await InvokeExecuteJobAsync(job);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ExecuteJob_RequestTimeout_SwallowedAndDoesNotThrow()
    {
        // An HttpClient timeout is a TaskCanceledException raised while the caller's token is
        // NOT signalled. It must not escape to JobBase, which would log a full stack trace
        // through ILogger and Serilog on every tick forever.
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler(
            () => throw new TaskCanceledException("timed out", new TimeoutException()));
        var job = CreateJob(handler);

        await InvokeExecuteJobAsync(job);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ExecuteJob_ApiKeyWithNewline_SwallowsHeaderFormatException()
    {
        // Header construction sits inside the try, so a malformed stored key can't throw an
        // unhandled FormatException on every tick.
        ParserConfigs.SetApiKey("bad\nkey");
        var handler = new CapturingHandler();
        var job = CreateJob(handler);

        await InvokeExecuteJobAsync(job);

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ExecuteJob_CancellationRequested_OperationCanceledExceptionPropagates()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler();
        var job = CreateJob(handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeExecuteJobWithToken(job, cts.Token));
    }

    [Fact]
    public async Task ExecuteJob_ReadsPendingIncrementFromStore()
    {
        ParserConfigs.SetApiKey("test-api-key");
        // The payload must use the persisted pending amount.
        var store = CreateStore(tokensSaved: 500_000);
        var handler = new CapturingHandler();
        var job = CreateJob(handler, store);

        await InvokeExecuteJobAsync(job);

        Assert.Equal(1, handler.RequestCount);
        Assert.NotNull(handler.RequestBody);
        using var payload = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(500_000,
            payload.RootElement.GetProperty("tokensSaved").GetInt64());
    }

    [Fact]
    public async Task ExecuteJob_EndpointUrl_FromConfig()
    {
        ParserConfigs.SetApiKey("test-api-key");
        const string customUrl = "https://localhost:5164/api/v2/token-savings";
        var handler = new CapturingHandler();
        var job = CreateJob(handler, configuration: BuildConfiguration(endpointUrl: customUrl));

        await InvokeExecuteJobAsync(job);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(customUrl, handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ExecuteJob_ShippedConfiguration_UsesV2Endpoint()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();
        var handler = new CapturingHandler();

        await InvokeExecuteJobAsync(CreateJob(handler, configuration: configuration));

        Assert.Equal("https://viberails.ai/api/v2/token-savings", handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ExecuteJob_ResolvesTheConfiguredNamedClient()
    {
        // The hosted singleton must take the named client. Resolving the default unnamed one
        // would silently mean a 100s timeout and auto-redirects.
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler();
        using var factory = new StubHttpClientFactory(handler);

        await InvokeExecuteJobAsync(CreateJob(factory));

        Assert.Equal(TokenSavingsPublishJob.HttpClientName, factory.LastRequestedName);
    }

    [Theory]
    [InlineData("http://viberails.ai/api/v1/token-savings")]  // cleartext would expose X-Api-Key
    [InlineData("ftp://viberails.ai/token-savings")]
    [InlineData("not a url")]                                 // would throw UriFormatException
    [InlineData("")]
    public async Task ExecuteJob_UnusableEndpoint_DoesNotSendRequestAndDoesNotThrow(string endpointUrl)
    {
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler();
        var job = CreateJob(handler, configuration: BuildConfiguration(endpointUrl: endpointUrl));

        await InvokeExecuteJobAsync(job);

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ExecuteJob_Disabled_DoesNotSendRequest()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler();
        var job = CreateJob(handler, configuration: BuildConfiguration(enabled: false));

        await InvokeExecuteJobAsync(job);

        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-boolean")]
    [InlineData("1")]
    public async Task ExecuteJob_MalformedEnabledSetting_UsesSafeDefault(string configuredValue)
    {
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler();
        var job = CreateJob(
            handler,
            configuration: BuildConfiguration(enabledValue: configuredValue));

        await InvokeExecuteJobAsync(job);

        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-integer")]
    [InlineData("2147483648")]
    public async Task ExecuteJob_MalformedIntervalSetting_UsesSafeDefault(string configuredValue)
    {
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler();
        var job = CreateJob(
            handler,
            configuration: BuildConfiguration(intervalMinutesValue: configuredValue));

        await InvokeExecuteJobAsync(job);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ExecuteJob_CrossProcessLockHeld_SkipsPublish()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var handler = new CapturingHandler();
        var store = CreateStore(tokensSaved: 2000);
        var job = CreateJob(handler, store);

        using var heldLock = CrossProcessFileLock.TryAcquire(
            CrossProcessFileLock.BesideStateDatabase(
                ParserConfigs.GetStatePath(),
                TokenSavingsPublishJob.LockFileName));
        Assert.NotNull(heldLock);

        await InvokeExecuteJobAsync(job);

        Assert.Equal(0, handler.RequestCount);
        store.Verify(s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(404, "")]
    [InlineData(409, "")]
    [InlineData(500, "")]
    [InlineData(201, "{}")]
    [InlineData(200, "not json")]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"reportId\":\"tsr_test\",\"tokensSaved\":1000}")]
    [InlineData(200, "{\"reportId\":\"other\",\"tokensSaved\":1000,\"receivedUtc\":\"2026-10-25T00:00:00Z\"}")]
    [InlineData(200, "{\"reportId\":\"tsr_test\",\"tokensSaved\":999,\"receivedUtc\":\"2026-10-25T00:00:00Z\"}")]
    public async Task ExecuteJob_UnconfirmedReceipt_DoesNotAdvance(int status, string body)
    {
        ParserConfigs.SetApiKey("test-api-key");
        var store = CreateStore(1000);
        var handler = new CapturingHandler(() => new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(body) });
        await InvokeExecuteJobAsync(CreateJob(handler, store));
        store.Verify(s => s.AcknowledgeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteJob_LostAcknowledgement_RestartAndKeyRotationRetryDurableBatch()
    {
        ParserConfigs.SetApiKey("first-key");
        var connectionString = new SqliteConnectionStringBuilder
        { DataSource = ParserConfigs.GetStatePath(), Pooling = false }.ToString();
        var measurement = new TokenSavingsStore(connectionString);
        measurement.Record("test", 4000, 0);
        await measurement.LastPersist;
        var store = new TokenSavingsPublishStore(connectionString);
        var initial = new CapturingHandler();
        await InvokeExecuteJobAsync(CreatePersistentJob(initial, store));
        Assert.Equal(0, initial.RequestCount); // First activation does not replay history.

        measurement.Record("test", 400, 0);
        await measurement.LastPersist;
        var lost = new CapturingHandler(() => throw new HttpRequestException("response lost"));
        await InvokeExecuteJobAsync(CreatePersistentJob(lost, store));
        var pending = Assert.IsType<TokenSavingsPendingReport>(
            await new TokenSavingsPublishStore(connectionString).PrepareAsync("renamed", TestContext.Current.CancellationToken));
        Assert.Equal(100, pending.TokensSaved);
        Assert.Equal(ComputerNameFormatter.Machine(), pending.ComputerName);

        measurement.Record("test", 300, 0);
        await measurement.LastPersist;
        ParserConfigs.SetApiKey("replacement-key");
        var retry = new CapturingHandler();
        var restarted = new TokenSavingsPublishStore(connectionString);
        await InvokeExecuteJobAsync(CreatePersistentJob(retry, restarted));
        Assert.Equal(lost.RequestBody, retry.RequestBody);
        Assert.Equal("replacement-key", retry.ApiKey);
        var later = Assert.IsType<TokenSavingsPendingReport>(
            await restarted.PrepareAsync("renamed", TestContext.Current.CancellationToken));
        Assert.Equal(75, later.TokensSaved);
        Assert.Equal(pending.ComputerId, later.ComputerId);
        Assert.NotEqual(pending.ReportId, later.ReportId);
        Assert.Equal("renamed", later.ComputerName);
    }

    [Fact]
    public async Task ExecuteJob_SharedKey_IndependentStoresPublishIndependentIncrements()
    {
        ParserConfigs.SetApiKey("shared-key");
        var handlers = new List<CapturingHandler>();
        foreach (var tokens in new[] { 100, 200 })
        {
            var connectionString = new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(_testRoot, $"computer-{tokens}.db"), Pooling = false }.ToString();
            var store = new TokenSavingsPublishStore(connectionString);
            Assert.Null(await store.PrepareAsync("same-name", TestContext.Current.CancellationToken));
            var measurement = new TokenSavingsStore(connectionString);
            measurement.Record("test", tokens * 4, 0);
            await measurement.LastPersist;
            var handler = new CapturingHandler();
            await InvokeExecuteJobAsync(CreatePersistentJob(handler, store));
            handlers.Add(handler);
            Assert.Null(await store.PrepareAsync("same-name", TestContext.Current.CancellationToken));
        }

        using var first = JsonDocument.Parse(handlers[0].RequestBody!);
        using var second = JsonDocument.Parse(handlers[1].RequestBody!);
        Assert.All(handlers, handler => Assert.Equal("shared-key", handler.ApiKey));
        Assert.Equal(100, first.RootElement.GetProperty("tokensSaved").GetInt64());
        Assert.Equal(200, second.RootElement.GetProperty("tokensSaved").GetInt64());
        Assert.NotEqual(first.RootElement.GetProperty("computerId").GetString(),
            second.RootElement.GetProperty("computerId").GetString());
    }

    [Fact]
    public async Task ExecuteJob_OversizedReceipt_DoesNotAdvance()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var store = CreateStore(1000);
        var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string('x', 5000)) });
        await InvokeExecuteJobAsync(CreateJob(handler, store));
        store.Verify(s => s.AcknowledgeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteJob_StalledReceipt_TimesOutWithoutAdvancingAndReleasesLock()
    {
        ParserConfigs.SetApiKey("test-api-key");
        var store = CreateStore(1000);
        var stalled = new StalledReadStream();
        var handler = new CapturingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stalled) });
        using var factory = new StubHttpClientFactory(handler);
        factory.CreateClient(TokenSavingsPublishJob.HttpClientName).Timeout = TimeSpan.FromMilliseconds(100);

        await InvokeExecuteJobAsync(CreateJob(factory, store))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(stalled.ReadStarted);
        store.Verify(s => s.AcknowledgeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var retry = new CapturingHandler();
        await InvokeExecuteJobAsync(CreateJob(retry, store));
        Assert.Equal(handler.RequestBody, retry.RequestBody);
        store.Verify(s => s.AcknowledgeAsync("tsr_test", It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose()
    {
        ParserConfigs.SetApiKey(_originalApiKey);
        ParserConfigs.SetStatePath(_originalStatePath);
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static IConfiguration BuildConfiguration(
        bool? enabled = null,
        string? endpointUrl = null,
        int? intervalMinutes = null,
        string? enabledValue = null,
        string? intervalMinutesValue = null)
    {
        var dict = new Dictionary<string, string?>();
        if (enabledValue is not null)
            dict["VibeRails:TokenSavingsPublish:Enabled"] = enabledValue;
        else if (enabled.HasValue)
            dict["VibeRails:TokenSavingsPublish:Enabled"] = enabled.Value.ToString();
        if (endpointUrl is not null)
            dict["VibeRails:TokenSavingsPublish:EndpointUrl"] = endpointUrl;
        if (intervalMinutesValue is not null)
            dict["VibeRails:TokenSavingsPublish:IntervalMinutes"] = intervalMinutesValue;
        else if (intervalMinutes.HasValue)
            dict["VibeRails:TokenSavingsPublish:IntervalMinutes"] = intervalMinutes.Value.ToString();
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static Mock<ITokenSavingsPublishStore> CreateStore(long tokensSaved)
    {
        var store = new Mock<ITokenSavingsPublishStore>();
        store.Setup(s => s.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) =>
                new TokenSavingsPendingReport("tsr_test", "cmp_test", name, tokensSaved));
        return store;
    }

    private static TokenSavingsPublishJob CreateJob(
        CapturingHandler handler,
        Mock<ITokenSavingsPublishStore>? store = null,
        IConfiguration? configuration = null) =>
        CreateJob(new StubHttpClientFactory(handler), store, configuration);

    private static TokenSavingsPublishJob CreatePersistentJob(
        CapturingHandler handler, ITokenSavingsPublishStore store) => new(
            NullLogger<TokenSavingsPublishJob>.Instance,
            Mock.Of<ISystemResourceService>(), store, new StubHttpClientFactory(handler), BuildConfiguration());

    private static TokenSavingsPublishJob CreateJob(
        IHttpClientFactory httpClientFactory,
        Mock<ITokenSavingsPublishStore>? store = null,
        IConfiguration? configuration = null)
    {
        return new TokenSavingsPublishJob(
            NullLogger<TokenSavingsPublishJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            (store ?? CreateStore(1000)).Object,
            httpClientFactory,
            configuration ?? BuildConfiguration());
    }

    private static Task InvokeExecuteJobAsync(TokenSavingsPublishJob job) =>
        InvokeExecuteJobWithToken(job, TestContext.Current.CancellationToken);

    private static async Task InvokeExecuteJobWithToken(
        TokenSavingsPublishJob job, CancellationToken ct)
    {
        var method = typeof(TokenSavingsPublishJob).GetMethod(
            "ExecuteJob", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try
        {
            await (Task)method!.Invoke(job, [ct])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// One HttpClient per factory, reused across CreateClient calls — these handlers are
    /// in-memory, and a client per call is the habit that clogs the port table elsewhere.
    /// </summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);

        public string? LastRequestedName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastRequestedName = name;
            return _client;
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class StalledReadStream : Stream
    {
        public bool ReadStarted { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>? _responder;

        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? RequestBody { get; private set; }

        public CapturingHandler() : this(responder: null) { }

        public CapturingHandler(Func<HttpResponseMessage>? responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("X-Api-Key", out var values)
                ? values.SingleOrDefault()
                : null;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (_responder is not null)
                return _responder.Invoke();
            using var payload = JsonDocument.Parse(RequestBody!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    reportId = payload.RootElement.GetProperty("reportId").GetString(),
                    tokensSaved = payload.RootElement.GetProperty("tokensSaved").GetInt64(),
                    receivedUtc = DateTimeOffset.UtcNow
                }))
            };
        }
    }
}
