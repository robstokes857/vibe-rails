using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
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
        // TokensSaved = (8000 - 0) / 4 = 2000
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
            payload.RootElement.GetProperty("totalTokensSaved").GetInt64());
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
    public async Task ExecuteJob_RefreshesStoreBeforeReadingTotals()
    {
        // The savings are written by tab children, so an unrefreshed read publishes a stale
        // absolute total that overwrites a newer remote one with a lower number.
        ParserConfigs.SetApiKey("test-api-key");
        var calls = new List<string>();
        var store = new Mock<ITokenSavingsStore>();
        store.Setup(s => s.RefreshAsync())
            .Callback(() => calls.Add("refresh"))
            .Returns(Task.CompletedTask);
        store.Setup(s => s.GetTotals())
            .Callback(() => calls.Add("getTotals"))
            .Returns(new TokenSavingsTotals(8000, 0));

        await InvokeExecuteJobAsync(CreateJob(new CapturingHandler(), store));

        Assert.Equal(["refresh", "getTotals"], calls);
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
    public async Task ExecuteJob_ReadsTotalFromStore()
    {
        ParserConfigs.SetApiKey("test-api-key");
        // A different store value must produce a different posted total.
        var store = CreateStore(tokensSaved: 500_000);
        var handler = new CapturingHandler();
        var job = CreateJob(handler, store);

        await InvokeExecuteJobAsync(job);

        Assert.Equal(1, handler.RequestCount);
        Assert.NotNull(handler.RequestBody);
        using var payload = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(500_000,
            payload.RootElement.GetProperty("totalTokensSaved").GetInt64());
    }

    [Fact]
    public async Task ExecuteJob_EndpointUrl_FromConfig()
    {
        ParserConfigs.SetApiKey("test-api-key");
        const string customUrl = "https://localhost:5164/api/v1/token-savings";
        var handler = new CapturingHandler();
        var job = CreateJob(handler, configuration: BuildConfiguration(endpointUrl: customUrl));

        await InvokeExecuteJobAsync(job);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(customUrl, handler.RequestUri?.AbsoluteUri);
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
        store.Verify(s => s.RefreshAsync(), Times.Never);
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

    private static Mock<ITokenSavingsStore> CreateStore(long tokensSaved)
    {
        // TokensSaved = (BytesBefore - BytesAfter) / 4
        var store = new Mock<ITokenSavingsStore>();
        store.Setup(s => s.RefreshAsync()).Returns(Task.CompletedTask);
        store.Setup(s => s.GetTotals())
            .Returns(new TokenSavingsTotals(tokensSaved * 4, 0));
        return store;
    }

    private static TokenSavingsPublishJob CreateJob(
        CapturingHandler handler,
        Mock<ITokenSavingsStore>? store = null,
        IConfiguration? configuration = null) =>
        CreateJob(new StubHttpClientFactory(handler), store, configuration);

    private static TokenSavingsPublishJob CreateJob(
        IHttpClientFactory httpClientFactory,
        Mock<ITokenSavingsStore>? store = null,
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

            return _responder?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
