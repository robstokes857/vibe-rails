using System.Net;
using System.Text;
using System.Text.Json;
using VibeRails.Services.VCA;
using Xunit;

namespace Tests.Services.VCA;

public sealed class CommitSignatureVerifierTests
{
    private static readonly CommitSignature Signature = new(
        Guid.Parse("8b3f6a52-6c43-4f6e-9a1e-2d7f1c0b9e44"),
        new string('c', 64),
        new string('d', 40),
        Convert.ToBase64String(Enumerable.Repeat((byte)9, 512).ToArray()));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendsOnlyKeyFingerprintPayloadAndSignature_ToTheFixedPublicEndpoint()
    {
        var handler = new FakeHandler(_ => Reply(Valid("Owner@Example.com")));
        using var verifier = new ViberailsCommitSignatureVerifier(new HttpClient(handler), ownsClient: true);

        var result = await verifier.VerifyAsync(Signature, Ct);

        Assert.Equal(CommitSignatureStatus.Valid, result.Status);
        Assert.Equal("Owner@Example.com", result.SignerEmail);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://viberails.ai/public/api/v1/signatures/verify", request.Uri);
        Assert.Null(request.ApiKey);
        using var body = JsonDocument.Parse(request.Body);
        // VibeRails-Front disallows unmapped members, so the names must match its request exactly.
        Assert.Equal(["algorithm", "fingerprint", "keyId", "payloadBase64", "signatureBase64"],
            body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Signature.KeyId, body.RootElement.GetProperty("keyId").GetGuid());
        Assert.Equal("RSA-PSS-SHA256", body.RootElement.GetProperty("algorithm").GetString());
        Assert.Equal(Signature.Fingerprint, body.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal(Signature.SignatureBase64, body.RootElement.GetProperty("signatureBase64").GetString());
        Assert.Equal($"VibeRails human-verified commit v1\ntree {Signature.TreeId}\n",
            Encoding.UTF8.GetString(Convert.FromBase64String(body.RootElement.GetProperty("payloadBase64").GetString()!)));
    }

    [Theory]
    [InlineData("key_not_found", CommitSignatureStatus.Rejected, "not registered")]
    [InlineData("key_revoked", CommitSignatureStatus.Rejected, "revoked")]
    [InlineData("fingerprint_mismatch", CommitSignatureStatus.Rejected, "fingerprint")]
    [InlineData("signature_mismatch", CommitSignatureStatus.Rejected, "staged content")]
    [InlineData("registration_challenge", CommitSignatureStatus.Rejected, "did not accept")]
    [InlineData("signer_email_unverified", CommitSignatureStatus.Rejected, "no verified email")]
    [InlineData("signer_email_unconfirmed", CommitSignatureStatus.Unavailable, "right now")]
    public async Task MapsServerReasons(string reason, CommitSignatureStatus expected, string detail)
    {
        var valid = reason.StartsWith("signer_email", StringComparison.Ordinal);
        var handler = new FakeHandler(_ => Reply(Response(valid, reason, null)));
        using var verifier = new ViberailsCommitSignatureVerifier(new HttpClient(handler), ownsClient: true);

        var result = await verifier.VerifyAsync(Signature, Ct);

        Assert.Equal(expected, result.Status);
        Assert.Contains(detail, result.Detail);
        Assert.Null(result.SignerEmail);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, CommitSignatureStatus.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, CommitSignatureStatus.Unavailable)]
    [InlineData(HttpStatusCode.Found, CommitSignatureStatus.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, CommitSignatureStatus.Rejected)]
    public async Task NonSuccessStatusesNeverCountAsValid(HttpStatusCode status, CommitSignatureStatus expected)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("{\"valid\":true}") });
        using var verifier = new ViberailsCommitSignatureVerifier(new HttpClient(handler), ownsClient: true);

        Assert.Equal(expected, (await verifier.VerifyAsync(Signature, Ct)).Status);
    }

    [Fact]
    public async Task ResponseAboutAnotherKeyOrFingerprintIsNotTrusted()
    {
        var otherKey = Valid("owner@example.com").Replace(Signature.KeyId.ToString("D"), Guid.NewGuid().ToString("D"));
        var otherFingerprint = Valid("owner@example.com").Replace(Signature.Fingerprint, new string('e', 64));
        foreach (var body in new[] { otherKey, otherFingerprint, "not json", "null" })
        {
            var handler = new FakeHandler(_ => Reply(body));
            using var verifier = new ViberailsCommitSignatureVerifier(new HttpClient(handler), ownsClient: true);
            var result = await verifier.VerifyAsync(Signature, Ct);
            Assert.Equal(CommitSignatureStatus.Unavailable, result.Status);
            Assert.Null(result.SignerEmail);
        }
    }

    [Fact]
    public async Task NetworkFailureIsUnavailableWithoutLeakingExceptionText()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("proxy.internal.example refused 10.0.0.7"));
        using var verifier = new ViberailsCommitSignatureVerifier(new HttpClient(handler), ownsClient: true);

        var result = await verifier.VerifyAsync(Signature, Ct);

        Assert.Equal(CommitSignatureStatus.Unavailable, result.Status);
        Assert.DoesNotContain("10.0.0.7", result.Detail);
    }

    [Fact]
    public async Task CachesDefinitiveAnswersButRetriesUnavailableOnes()
    {
        var answers = new Queue<Func<HttpResponseMessage>>([
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => Reply(Valid("owner@example.com"))
        ]);
        var handler = new FakeHandler(_ => answers.Dequeue()());
        using var verifier = new ViberailsCommitSignatureVerifier(new HttpClient(handler), ownsClient: true);

        Assert.Equal(CommitSignatureStatus.Unavailable, (await verifier.VerifyAsync(Signature, Ct)).Status);
        Assert.Equal(CommitSignatureStatus.Valid, (await verifier.VerifyAsync(Signature, Ct)).Status);
        Assert.Equal(CommitSignatureStatus.Valid, (await verifier.VerifyAsync(Signature, Ct)).Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static string Valid(string email) => Response(true, "valid", email);

    // Shaped like VibeRails-Front's PublicSignatureVerificationResponse as ASP.NET serializes it.
    private static string Response(bool valid, string reason, string? email) =>
        $$"""
        {"valid":{{(valid ? "true" : "false")}},"reason":"{{reason}}","keyId":"{{Signature.KeyId:D}}",
         "fingerprint":"{{Signature.Fingerprint}}","signerEmail":{{(email is null ? "null" : $"\"{email}\"")}},
         "algorithm":"RSA-PSS-SHA256","payloadSha256":"{{new string('0', 64)}}","verifiedUtc":"2026-10-20T12:00:00Z"}
        """;

    private static HttpResponseMessage Reply(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Captured(HttpMethod Method, string? Uri, string? ApiKey, string Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Captured(request.Method, request.RequestUri?.AbsoluteUri,
                request.Headers.TryGetValues("X-Api-Key", out var values) ? values.Single() : null,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
