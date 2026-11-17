using System.Net;
using System.Net.Http.Json;
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
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

public sealed class RemotePasswordRoutesTests
{
    [Theory]
    [InlineData("1234")]
    [InlineData("123456")]
    [InlineData("Summer7")]
    [InlineData("  Summer7  ")]
    [InlineData("秘密の鍵7")]
    [InlineData("x")]
    [InlineData("123")]
    public async Task DirectApiAcceptsBoundedTextWithoutFrontendStrengthRules(string credential)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = await fixture.SaveAsync(credential, fixture.Presence.Challenge(false).Code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(RemoteConfig.VerifyPin(credential, fixture.Store));
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(credential, body, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonSerializer.Serialize(credential), await File.ReadAllTextAsync(fixture.SettingsPath, Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("123456")]
    public async Task LegacyHashesSurviveReadsAndUnrelatedSettingsAndAuthenticateChanges(string legacyPin)
    {
        await using var fixture = await Fixture.CreateAsync();
        var (salt, hash) = Hasher.Hash(legacyPin);
        fixture.Store.Save(new Settings { PinSalt = salt, PinHash = hash });
        using var status = await fixture.Client.GetAsync("/api/v1/settings/pin/status", Ct);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        AppSettingsRoutes.UpdateComputerName(new("Renamed computer"), fixture.Store);
        Assert.Equal(salt, fixture.Store.LoadFresh().PinSalt);
        Assert.Equal(hash, fixture.Store.LoadFresh().PinHash);
        Assert.True(RemoteConfig.VerifyPin(legacyPin, fixture.Store));

        using var denied = await fixture.SaveAsync("Summer7", "wrong-proof");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(hash, fixture.Store.LoadFresh().PinHash);
        using var changed = await fixture.SaveAsync("  Summer7  ", legacyPin);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.False(RemoteConfig.VerifyPin(legacyPin, fixture.Store));
        Assert.True(RemoteConfig.VerifyPin("  Summer7  ", fixture.Store));
        Assert.False(RemoteConfig.VerifyPin("Summer7", fixture.Store));

        using var changedAgain = await fixture.SaveAsync("Next8password", "  Summer7  ");
        Assert.Equal(HttpStatusCode.OK, changedAgain.StatusCode);
        using var cleared = await fixture.SendAsync(HttpMethod.Delete, new("", "Next8password"));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty(fixture.Store.LoadFresh().PinHash);
        Assert.Empty(fixture.Store.LoadFresh().PinSalt);
    }

    [Fact]
    public async Task BoundsAreEnforcedBeforeConsumingFirstSetupProof()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proof = fixture.Presence.Challenge(false).Code;
        // Whitespace-only passes the length bound but Hasher.Hash rejects it; it must be a 400
        // before Verify spends the proof, not a 500 after.
        foreach (var invalid in new[] { null, "", " ", " \t\n ", new string('a', 129) })
        {
            using var response = await fixture.SaveAsync(invalid!, proof);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(fixture.Store.LoadFresh().PinHash);
        }
        var boundary = new string('界', 128);
        using var accepted = await fixture.SaveAsync(boundary, proof);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True(RemoteConfig.VerifyPin(boundary, fixture.Store));
        Assert.False(RemoteConfig.VerifyPin(boundary + "界", fixture.Store));
    }

    [Fact]
    public async Task BlankStoredCredentialCountsAsUnsetForStatusChallengeAndVerification()
    {
        // A hand-edited or partly written settings file. Status and the challenge offer a dashboard
        // code; verification must accept that code, or the user can neither set nor clear a password.
        await using var fixture = await Fixture.CreateAsync();
        fixture.Store.Save(new Settings { PinSalt = " ", PinHash = " " });

        using var status = await fixture.Client.GetAsync("/api/v1/settings/pin/status", Ct);
        var body = await status.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.PinStatusResponse, Ct);
        Assert.False(body!.IsSet);

        using var saved = await fixture.SaveAsync("Summer7", fixture.Presence.Challenge(false).Code);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True(RemoteConfig.VerifyPin("Summer7", fixture.Store));
    }

    [Fact]
    public async Task AllRoutesRequireBothCredentialsBeforeReadingOrWriting()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.DefaultRequestHeaders.Clear();
        foreach (var credentials in new[] { 0, 1, 2 })
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method,
                method == HttpMethod.Get ? "/api/v1/settings/pin/status" : "/api/v1/settings/pin");
            if ((credentials & 1) != 0) request.Headers.Add("viberails_session", "test-session");
            if ((credentials & 2) != 0) request.Headers.Add("viberails_tab", "test-tab");
            if (method != HttpMethod.Get) request.Content = JsonContent.Create(new SetPinRequest("Summer7"),
                AppJsonSerializerContext.Default.SetPinRequest);
            using var response = await fixture.Client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Empty(fixture.Store.LoadFresh().PinHash);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "remote-password-tests-" + Guid.NewGuid().ToString("N"));
        private WebApplication app = null!;
        internal string SettingsPath => Path.Combine(directory, "settings.json");
        internal SettingsFile Store { get; }
        internal SettingsPresenceService Presence { get; } = new();
        internal HttpClient Client { get; private set; } = null!;

        private Fixture() => Store = new SettingsFile(SettingsPath);

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.Store.Save(new Settings());
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var auth = new Mock<IAuthService>();
            auth.Setup(a => a.ValidateToken(It.IsAny<string?>())).Returns((string? value) => value == "test-session");
            auth.Setup(a => a.ValidateTabToken(It.IsAny<string?>())).Returns((string? value) => value == "test-tab");
            builder.Services.AddSingleton(auth.Object);
            builder.Services.AddSingleton(fixture.Presence);
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
            fixture.app = builder.Build();
            fixture.app.UseMiddleware<CookieAuthMiddleware>();
            PinRoutes.Map(fixture.app, fixture.Store);
            await fixture.app.StartAsync(Ct);
            fixture.Client = new HttpClient { BaseAddress = new Uri(fixture.app.Urls.First()) };
            fixture.Client.DefaultRequestHeaders.Add("viberails_session", "test-session");
            fixture.Client.DefaultRequestHeaders.Add("viberails_tab", "test-tab");
            return fixture;
        }

        internal Task<HttpResponseMessage> SaveAsync(string credential, string? proof) =>
            SendAsync(HttpMethod.Post, new(credential, proof));

        internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, SetPinRequest value)
        {
            using var request = new HttpRequestMessage(method, "/api/v1/settings/pin")
            {
                Content = JsonContent.Create(value, AppJsonSerializerContext.Default.SetPinRequest)
            };
            return await Client.SendAsync(request, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            Store.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
