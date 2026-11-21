using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeRails.Auth;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// The dashboard's view of the account's hosted plan. Both routes need both local credentials and
/// then always answer 200 with one shape: an upstream 401, 404 or outage is a state, never a local
/// status (a local 401 would make the dashboard re-bootstrap its own credentials).
/// </summary>
public sealed class AccountEntitlementRoutesTests
{
    private const string Route = "/api/v1/account/entitlements";
    private const string Key = "route-fixture-account-key";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BothVerbsRequireBothCredentialsBeforeAnyOutboundRequest()
    {
        var upstream = new Upstream(_ => throw new Xunit.Sdk.XunitException("No request without credentials"));
        await using var host = await HostAsync(upstream);
        using var client = new HttpClient { BaseAddress = new Uri(host.App.Urls.Single()) };

        foreach (var (method, path) in new[] { (HttpMethod.Get, Route), (HttpMethod.Post, Route + "/refresh") })
        foreach (var (session, tab) in new[] { (false, false), (true, false), (false, true) })
        {
            using var request = new HttpRequestMessage(method, path);
            if (session) request.Headers.Add("viberails_session", host.Auth.GetInstanceToken());
            if (tab) request.Headers.Add("viberails_tab", host.Auth.GetTabToken());
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task ReturnsTheDocumentedShape_CachesTheRead_AndRefreshForcesAnother()
    {
        var access = "free";
        var upstream = new Upstream(_ => Json($$"""
            {"version":1,"plan":"{{access}}","access":"{{access}}","enforced":true,
             "features":{"remote_boards":{{(access == "pro" ? "true" : "false")}},"remote_terminals":false,"session_sharing":true,"card_sharing":false},
             "billingPath":"/Billing","refreshAfterSeconds":300}
            """));
        await using var host = await HostAsync(upstream);
        using var client = host.Client();

        using (var response = await client.GetAsync(Route, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(Key, text);
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            Assert.Equal(["state", "enforced", "features", "billingUrl", "checkedUtc"], root.EnumerateObject().Select(p => p.Name));
            Assert.Equal("free", root.GetProperty("state").GetString());
            Assert.True(root.GetProperty("enforced").GetBoolean());
            var features = root.GetProperty("features");
            Assert.Equal(["remote_boards", "remote_terminals", "session_sharing", "card_sharing"], features.EnumerateObject().Select(p => p.Name));
            Assert.False(features.GetProperty("remote_boards").GetBoolean());
            Assert.True(features.GetProperty("session_sharing").GetBoolean());
            Assert.Equal("https://plans.example.test/Billing", root.GetProperty("billingUrl").GetString());
            Assert.Equal(DateTimeKind.Utc, root.GetProperty("checkedUtc").GetDateTime().Kind);
            Assert.EndsWith("Z", root.GetProperty("checkedUtc").GetString());
        }

        // The user upgraded on the Billing page: a plain read is still the cached answer...
        access = "pro";
        using (var cached = await client.GetAsync(Route, Ct))
            Assert.Equal("free", (await ReadAsync(cached)).GetProperty("state").GetString());
        Assert.Equal(1, upstream.Calls);

        // ...and the refresh route reads the server again.
        using (var refreshed = await client.PostAsync(Route + "/refresh", null, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            Assert.True(refreshed.Headers.CacheControl!.NoStore);
            var root = await ReadAsync(refreshed);
            Assert.Equal("pro", root.GetProperty("state").GetString());
            Assert.True(root.GetProperty("features").GetProperty("remote_boards").GetBoolean());
        }
        Assert.Equal(2, upstream.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(0)] // no response at all
    public async Task UpstreamFailuresAreTheUnknownState_NeverALocalErrorStatus(int upstreamStatus)
    {
        var upstream = new Upstream(_ => upstreamStatus == 0
            ? throw new HttpRequestException("offline " + Key)
            : new HttpResponseMessage((HttpStatusCode)upstreamStatus) { Content = new StringContent("remote error " + Key) });
        await using var host = await HostAsync(upstream);
        using var client = host.Client();

        foreach (var send in new Func<Task<HttpResponseMessage>>[] { () => client.GetAsync(Route, Ct), () => client.PostAsync(Route + "/refresh", null, Ct) })
        {
            using var response = await send();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(Key, text);
            Assert.DoesNotContain("remote error", text);
            using var json = JsonDocument.Parse(text);
            Assert.Equal("unknown", json.RootElement.GetProperty("state").GetString());
            Assert.False(json.RootElement.GetProperty("enforced").GetBoolean());
            Assert.All(json.RootElement.GetProperty("features").EnumerateObject(), feature => Assert.True(feature.Value.GetBoolean()));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("checkedUtc").ValueKind);
        }
    }

    [Fact]
    public async Task SignedOutAnswersWithEveryFeatureOff_AndNoOutboundRequest()
    {
        var upstream = new Upstream(_ => throw new Xunit.Sdk.XunitException("Signed out sends nothing"));
        await using var host = await HostAsync(upstream, key: "");
        using var client = host.Client();

        using var response = await client.PostAsync(Route + "/refresh", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await ReadAsync(response);
        Assert.Equal("signed_out", root.GetProperty("state").GetString());
        Assert.All(root.GetProperty("features").EnumerateObject(), feature => Assert.False(feature.Value.GetBoolean()));
        Assert.Equal("https://plans.example.test/Billing", root.GetProperty("billingUrl").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("checkedUtc").ValueKind);
        Assert.Equal(0, upstream.Calls);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.Clone();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<Host> HostAsync(Upstream upstream, string key = Key)
    {
        var auth = new AuthService();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAuthService>(auth);
        builder.Services.AddSingleton<IAccountEntitlements>(new AccountEntitlementsClient(
            new Factory(upstream), () => new Uri("https://plans.example.test"), () => key));
        var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        AccountEntitlementRoutes.Map(app);
        await app.StartAsync(Ct);
        return new(app, auth);
    }

    private sealed record Host(WebApplication App, AuthService Auth) : IAsyncDisposable
    {
        public HttpClient Client()
        {
            var client = new HttpClient { BaseAddress = new Uri(App.Urls.Single()) };
            client.DefaultRequestHeaders.Add("viberails_session", Auth.GetInstanceToken());
            client.DefaultRequestHeaders.Add("viberails_tab", Auth.GetTabToken());
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync(CancellationToken.None);
            await App.DisposeAsync();
        }
    }

    private sealed class Factory(Upstream upstream) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(upstream, disposeHandler: false);
    }

    private sealed class Upstream(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }
}
