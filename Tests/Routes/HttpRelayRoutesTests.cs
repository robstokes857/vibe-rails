using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.HttpRelay;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class HttpRelayRoutesTests : IAsyncLifetime
{
    private readonly string _originalApiKey = ParserConfigs.GetApiKey();
    private readonly bool _originalRelaySetting = ParserConfigs.GetRouteThroughVibeRailsAi();
    private readonly FakeRelayClient _relay = new();
    private WebApplication? _app;
    private HttpClient? _client;

    [Theory]
    [InlineData("GET", "/api/v1/http-relay/test/posts/7", "https://jsonplaceholder.typicode.com/posts/7")]
    [InlineData("POST", "/api/v1/http-relay/test/posts", "https://jsonplaceholder.typicode.com/posts")]
    [InlineData("PUT", "/api/v1/http-relay/test/posts/7", "https://jsonplaceholder.typicode.com/posts/7")]
    [InlineData("DELETE", "/api/v1/http-relay/test/posts/7", "https://jsonplaceholder.typicode.com/posts/7")]
    public async Task ForwardsTheActualMethodPathHeadersAndBody(
        string method,
        string path,
        string expectedUri)
    {
        await EnsureHostAsync();
        ParserConfigs.SetRouteThroughVibeRailsAi(true);
        ParserConfigs.SetApiKey("test-key");

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (method is "POST" or "PUT")
            request.Content = new StringContent("{\"title\":\"relay test\"}", Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request, TestContext.Current.CancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var forwarded = Assert.Single(_relay.Requests);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"relay\":true}", responseText);
        Assert.Equal(method, forwarded.Method);
        Assert.Equal(expectedUri, forwarded.Uri);
        Assert.Equal(["application/json"], forwarded.Headers["accept"]);
        Assert.False(forwarded.Headers.ContainsKey("viberails_tab"));
        Assert.Equal("9", response.Headers.GetValues("X-VibeRails-Relay-Elapsed-Ms").Single());

        if (method is "POST" or "PUT")
        {
            Assert.NotNull(forwarded.Body);
            Assert.Equal(
                "{\"title\":\"relay test\"}",
                Encoding.UTF8.GetString(HttpRelayProtocol.DecodeBody(forwarded.Body)));
        }
        else
        {
            Assert.Null(forwarded.Body);
        }
    }

    [Fact]
    public async Task DisabledAndMissingKey_AreRejectedBeforeRelay()
    {
        await EnsureHostAsync();
        ParserConfigs.SetRouteThroughVibeRailsAi(false);
        ParserConfigs.SetApiKey("test-key");

        using var disabled = await _client!.GetAsync(
            "/api/v1/http-relay/test/posts/1",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);

        ParserConfigs.SetRouteThroughVibeRailsAi(true);
        ParserConfigs.SetApiKey(string.Empty);
        using var noKey = await _client.GetAsync(
            "/api/v1/http-relay/test/posts/1",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionFailed, noKey.StatusCode);
        Assert.Empty(_relay.Requests);
    }

    [Fact]
    public async Task BlankGet_ReturnsAllPostsTarget_AndQueryCannotBecomeAnId()
    {
        await EnsureHostAsync();
        ParserConfigs.SetRouteThroughVibeRailsAi(true);
        ParserConfigs.SetApiKey("test-key");

        using var response = await _client!.GetAsync(
            "/api/v1/http-relay/test/posts?id=99",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "https://jsonplaceholder.typicode.com/posts",
            Assert.Single(_relay.Requests).Uri);
    }

    [Fact]
    public async Task DeleteBody_IsForwardedInsteadOfSilentlyDiscarded()
    {
        await EnsureHostAsync();
        ParserConfigs.SetRouteThroughVibeRailsAi(true);
        ParserConfigs.SetApiKey("test-key");

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/api/v1/http-relay/test/posts/7")
        {
            Content = new StringContent("{\"reason\":\"relay test\"}", Encoding.UTF8, "application/json")
        };
        using var response = await _client!.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(_relay.Requests);
        Assert.NotNull(forwarded.Body);
        Assert.Equal(
            "{\"reason\":\"relay test\"}",
            Encoding.UTF8.GetString(HttpRelayProtocol.DecodeBody(forwarded.Body)));
    }

    private async Task EnsureHostAsync()
    {
        if (_app is not null)
            return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IRemoteHttpRelayClient>(_relay);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(
                0,
                AppJsonSerializerContext.Default));
        _app = builder.Build();
        HttpRelayRoutes.Map(_app);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        ParserConfigs.SetApiKey(_originalApiKey);
        ParserConfigs.SetRouteThroughVibeRailsAi(_originalRelaySetting);
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }

    private sealed class FakeRelayClient : IRemoteHttpRelayClient
    {
        public List<HttpRelayRequest> Requests { get; } = [];

        public Task<HttpRelayResponse> SendAsync(
            HttpRelayRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpRelayResponse(
                1,
                "http_response",
                request.RequestId,
                200,
                "OK",
                new Dictionary<string, string[]>
                {
                    ["content-type"] = ["application/json; charset=utf-8"],
                    ["set-cookie"] = ["must-not-escape=true"]
                },
                new HttpRelayBody("base64", Convert.ToBase64String("{\"relay\":true}"u8)),
                9));
        }

        public void Reset()
        {
        }
    }
}
