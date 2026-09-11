using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
using VibeRails.Services.SigningKeys;
using Xunit;

namespace Tests.Routes;

public sealed class SigningKeyRoutesTests
{
    [Fact]
    public async Task RoutesRequireBothCredentials_BoundRequests_AndRoundTripEncryptedKeys()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "viberails-signing-route-tests", Guid.NewGuid().ToString("N"));
        using var outbound = new HttpClient(new NoNetworkHandler());
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ValidateToken(It.IsAny<string?>())).Returns((string? s) => s == "test-session");
        auth.Setup(a => a.ValidateTabToken(It.IsAny<string?>())).Returns((string? s) => s == "test-tab");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.AddSingleton(new SigningKeyService(outbound, new SigningKeyStore(directory), () => ""));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        SigningKeyRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        try
        {
            var id = Guid.NewGuid();
            (HttpMethod Method, string Path)[] routes = [
                (HttpMethod.Get, "/api/v1/settings/keys"), (HttpMethod.Post, "/api/v1/settings/keys"),
                (HttpMethod.Post, $"/api/v1/settings/keys/{id}/sync"),
                (HttpMethod.Post, $"/api/v1/settings/keys/{id}/export"),
                (HttpMethod.Post, $"/api/v1/settings/keys/{id}/sign")];
            foreach (var (method, path) in routes)
            foreach (var credentials in new[] { 0, 1, 2 })
            {
                using var denied = await Send(method, path, "{}", credentials);
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            Assert.False(Directory.Exists(directory)); // Middleware blocks before even opening the key store.

            foreach (var password in new[] { "", "   ", "123", "abcdefg", "12345678" })
            {
                using var invalid = await Send(HttpMethod.Post, routes[0].Path, JsonSerializer.Serialize(new { name = "test", password }));
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            }
            using var oversized = await Send(HttpMethod.Post, routes[0].Path, new string(' ', 97 * 1024));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            Assert.False(Directory.Exists(directory));

            using var created = await Send(HttpMethod.Post, routes[0].Path, "{\"name\":\"My test key\",\"password\":\"correct horse battery\"}");
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            Assert.True(created.Headers.CacheControl?.NoStore);
            var createdText = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("PRIVATE KEY", createdText);
            Assert.DoesNotContain("password", createdText, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(createdText);
            Assert.Equal("not_configured", json.RootElement.GetProperty("syncStatus").GetString());
            var key = json.RootElement.GetProperty("key");
            id = key.GetProperty("id").GetGuid();
            var publicPem = key.GetProperty("publicKeyPem").GetString()!;
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicPem);
            Assert.Equal(4096, rsa.KeySize);

            using var list = await Send(HttpMethod.Get, routes[0].Path);
            var listText = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("encryptedPrivate", listText);
            Assert.DoesNotContain("PRIVATE KEY", listText);

            using var wrongPassword = await Send(HttpMethod.Post, $"/api/v1/settings/keys/{id}/export", "{\"password\":\"wrong\"}");
            Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
            using var exported = await Send(HttpMethod.Post, $"/api/v1/settings/keys/{id}/export", "{\"password\":\"correct horse battery\"}");
            var exportedBody = await exported.Content.ReadFromJsonAsync<SigningKeyExportResponse>(TestContext.Current.CancellationToken);
            Assert.NotNull(exportedBody);
            using var imported = RSA.Create();
            imported.ImportFromEncryptedPem(exportedBody.EncryptedPrivateKeyPem, "correct horse battery");
            Assert.Equal(publicPem, imported.ExportSubjectPublicKeyInfoPem());

            var payload = Encoding.UTF8.GetBytes("Signed precisely: café\n");
            using var signed = await Send(HttpMethod.Post, $"/api/v1/settings/keys/{id}/sign",
                JsonSerializer.Serialize(new { password = "correct horse battery", payloadBase64 = Convert.ToBase64String(payload) }));
            var signedBody = await signed.Content.ReadFromJsonAsync<SignedPayloadResponse>(TestContext.Current.CancellationToken);
            Assert.NotNull(signedBody);
            Assert.Equal("RSA-PSS-SHA256", signedBody.Algorithm);
            Assert.Equal(publicPem, signedBody.PublicKeyPem);
            Assert.True(rsa.VerifyData(payload, Convert.FromBase64String(signedBody.SignatureBase64), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            payload[0] ^= 1;
            Assert.False(rsa.VerifyData(payload, Convert.FromBase64String(signedBody.SignatureBase64), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            using var registrationAsMessage = await Send(HttpMethod.Post, $"/api/v1/settings/keys/{id}/sign",
                JsonSerializer.Serialize(new { password = "correct horse battery", payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(SigningKeyService.ChallengePrefix + "untrusted challenge")) }));
            Assert.Equal(HttpStatusCode.BadRequest, registrationAsMessage.StatusCode);
            using var missing = await Send(HttpMethod.Post, $"/api/v1/settings/keys/{Guid.NewGuid()}/sync", "{\"password\":\"correct horse battery\"}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // Five wrong unlocks lock the key for the window; the sixth attempt is refused with 429
            // and Retry-After even when the password is right, and never touches the key.
            for (var attempt = 0; attempt < SigningKeyService.MaxUnlockFailures; attempt++)
            {
                using var wrong = await Send(HttpMethod.Post, $"/api/v1/settings/keys/{id}/export", "{\"password\":\"wrong-password\"}");
                Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            }
            using var locked = await Send(HttpMethod.Post, $"/api/v1/settings/keys/{id}/export", "{\"password\":\"correct horse battery\"}");
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
            Assert.Equal("60", locked.Headers.RetryAfter?.ToString());
            Assert.DoesNotContain("PRIVATE KEY", await locked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            var expectedRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "viberails-signing-route-tests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? json = null, int credentials = 3)
        {
            using var request = new HttpRequestMessage(method, path);
            if ((credentials & 1) != 0) request.Headers.Add("viberails_session", "test-session");
            if ((credentials & 2) != 0) request.Headers.Add("viberails_tab", "test-tab");
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Tests must never call the live API.");
    }
}
