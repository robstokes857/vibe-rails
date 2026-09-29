using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Routes;

public sealed class RemoteAccountLinkRoutesTests
{
    private const string Route = "/api/v1/settings/remote-link";
    private const string DeviceSecret = "ddddddddddddddddddddddddddddddddddddddddddd";
    private static readonly string ApiKey = new('k', 134);

    [Fact]
    public async Task EveryVerbRequiresBothCredentialsBeforeAnyNetworkOrCredentialAccess()
    {
        using var outbound = new HttpClient(new Handler());
        var store = new KeyStore();
        await using var fixture = await CreateHostAsync(outbound, store);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.Urls.First()) };

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        foreach (var credentials in new[] { 0, 1, 2 })
        {
            using var request = Request(method, credentials);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal(0, store.ReadCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, store.ComputerNameReads);
    }

    [Fact]
    public async Task AuthenticatedRoutesStartLinkAndSaveOnlyOnTheBackendWithNoStoreResponses()
    {
        var handler = new Handler(allowNetwork: true);
        using var outbound = new HttpClient(handler);
        var store = new KeyStore();
        var clock = new ManualClock();
        await using var fixture = await CreateHostAsync(outbound, store, clock);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.Urls.First()) };

        using (var request = Request(HttpMethod.Post))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            AssertNoSecrets(text);
            using var json = JsonDocument.Parse(text);
            Assert.Equal("pending", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("BXQK-2M7T", json.RootElement.GetProperty("userCode").GetString());
        }

        clock.Advance(TimeSpan.FromSeconds(4));
        using (var request = Request(HttpMethod.Get))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            AssertNoSecrets(text);
            using var json = JsonDocument.Parse(text);
            Assert.Equal("linked", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("route-test@example.test", json.RootElement.GetProperty("account").GetProperty("email").GetString());
        }

        Assert.Equal(ApiKey, store.Value);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(2, handler.RequestCount);

        using (var request = Request(HttpMethod.Delete))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            AssertNoSecrets(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        // Cancel ends an attempt; it must not clear the durable account credential.
        Assert.Equal(ApiKey, store.Value);
        Assert.Equal(1, store.SaveCalls);
    }

    private static async Task<WebApplication> CreateHostAsync(HttpClient outbound, KeyStore store, TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ValidateToken(It.IsAny<string?>())).Returns((string? value) => value == "test-session");
        auth.Setup(a => a.ValidateTabToken(It.IsAny<string?>())).Returns((string? value) => value == "test-tab");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.AddSingleton(new RemoteAccountLinkService(outbound, new Uri("https://viberails.ai/"), store, new AppEventBus(), clock));
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        RemoteAccountLinkRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpRequestMessage Request(HttpMethod method, int credentials = 3)
    {
        var request = new HttpRequestMessage(method, Route);
        if ((credentials & 1) != 0) request.Headers.Add("viberails_session", "test-session");
        if ((credentials & 2) != 0) request.Headers.Add("viberails_tab", "test-tab");
        return request;
    }

    private static void AssertNoSecrets(string text)
    {
        Assert.DoesNotContain(ApiKey, text);
        Assert.DoesNotContain(DeviceSecret, text);
        Assert.DoesNotContain("deviceCode", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class KeyStore : IRemoteAccountKeyStore
    {
        public string Value { get; private set; } = "";
        public int ReadCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int ComputerNameReads { get; private set; }
        public string ComputerName { get { ComputerNameReads++; return "Route test"; } }
        public string Read() { ReadCalls++; return Value; }
        public bool TrySave(string apiKey, string expectedApiKey)
        {
            SaveCalls++;
            if (Value != expectedApiKey) return false;
            Value = apiKey;
            return true;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class Handler(bool allowNetwork = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!allowNetwork) throw new InvalidOperationException("Unauthorized route must never make an outbound request.");
            RequestCount++;
            var json = request.RequestUri!.AbsolutePath.EndsWith("/token")
                ? JsonSerializer.Serialize(new { apiKey = ApiKey, keyPrefix = "kkkk", keySuffix = "kkkk", account = new { email = "route-test@example.test", name = "Route test" } })
                : JsonSerializer.Serialize(new { deviceCode = DeviceSecret, userCode = "BXQK-2M7T", verificationUri = "https://viberails.ai/link", expiresIn = 600, interval = 3 });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
