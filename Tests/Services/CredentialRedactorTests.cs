using System.Diagnostics;
using VibeRails.Security;
using Xunit;

namespace Tests.Services;

public sealed class CredentialRedactorTests
{
    private const string X = CredentialRedactor.TranscriptMask;

    [Theory]
    [InlineData("password=hunter2", "password=" + X)]
    [InlineData("DB_PASSWORD=hunter2", "DB_PASSWORD=" + X)]
    [InlineData("export OPENAI_API_KEY=abc123", "export OPENAI_API_KEY=" + X)]
    [InlineData("{\"password\":\"hunter2\"}", "{\"password\":\"" + X + "\"}")]
    [InlineData("{\\\"password\\\":\\\"hunter2\\\"}", "{\\\"password\\\":\\\"" + X + "\\\"}")]
    [InlineData("passphrase = \"correct horse battery\"", "passphrase = \"" + X + "\"")]
    [InlineData("config['password'] = 'hunter2'", "config['password'] = '" + X + "'")]
    [InlineData("Server=db;User Id=sa;Pwd=hunter2;Database=app", "Server=db;User Id=sa;Pwd=" + X + ";Database=app")]
    [InlineData("client_secret: s3cr3t", "client_secret: " + X)]
    [InlineData("token := \"abc\"", "token := \"" + X + "\"")]
    [InlineData("https://api.example.test/v1?access_token=abc&page=2", "https://api.example.test/v1?access_token=" + X + "&page=2")]
    [InlineData("tool --password hunter2 --verbose", "tool --password " + X + " --verbose")]
    [InlineData("Connect -Password \"hunter 2\"", "Connect -Password \"" + X + "\"")]
    [InlineData("gh auth login --api-key abc123", "gh auth login --api-key " + X)]
    [InlineData("[Environment]::SetEnvironmentVariable(\"OPENAI_API_KEY\", \"abc123\")", "[Environment]::SetEnvironmentVariable(\"OPENAI_API_KEY\", \"" + X + "\")")]
    [InlineData("Authorization: Bearer abc", "Authorization: Bearer " + X)]
    [InlineData("Proxy-Authorization: Basic dTpw", "Proxy-Authorization: Basic " + X)]
    [InlineData("curl -H \"Authorization: token abc\"", "curl -H \"Authorization: token " + X + "\"")]
    [InlineData("Cookie: sid=abc; theme=dark", "Cookie: " + X)]
    [InlineData("postgresql://admin:hunter2@db.example/app", "postgresql://admin:" + X + "@db.example/app")]
    [InlineData("redis://:hunter2@cache.example/0", "redis://:" + X + "@cache.example/0")]
    [InlineData("<password>hunter2</password>", "<password>" + X + "</password>")]
    [InlineData("<server><password>hunter2</password></server>", "<server><password>" + X + "</password></server>")]
    public void MasksCredentialValuesAndKeepsTheirLabels(string text, string expected) =>
        Assert.Equal(expected, CredentialRedactor.RedactTranscript(text));

    [Theory]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("sk-proj-abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnop")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("xoxb-1234567890-abcdefghij")]
    [InlineData("sk_live_abcdefghijklmnop1234")]
    [InlineData("AIzaSyAbcdefghijklmnopqrstuvwxyz0123456")]
    [InlineData("glpat-abcdefghijklmnopqrst")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature_value_here")]
    [InlineData("Bearer abcdefghijklmnop1234")]
    public void MasksKnownTokenFormatsAnywhere(string token) =>
        Assert.Equal("use " + (token.StartsWith("Bearer ") ? "Bearer " : "") + X + " now",
            CredentialRedactor.RedactTranscript("use " + token + " now"));

    // Talking about credentials is far more common than pasting one; none of this is a secret.
    [Theory]
    [InlineData("I want my password alg to be BlowFish not this stupid PBKDF2 thing?")]
    [InlineData("Password reset email")]
    [InlineData("Token budget tuning")]
    [InlineData("User: Auth is broken")]
    [InlineData("Visit auth.example.com")]
    [InlineData("call getToken() before the request")]
    [InlineData("Update auth.ts login flow")]
    [InlineData("Fix password reset after refresh")]
    [InlineData("Support Basic authorization headers")]
    [InlineData("Bearer authentication-mechanisms")]
    [InlineData("Preserve cookie settings")]
    [InlineData("password: string;")]
    [InlineData("api_key: Optional[str] = None")]
    [InlineData("var token = GetToken(user);")]
    [InlineData("await store.SaveAsync(item, cancellationToken: ct);")]
    [InlineData("services.AddHostedService(stoppingToken: stoppingToken)")]
    [InlineData("password == \"\" is rejected")]
    [InlineData("max_tokens: 4096, tokenizer: bpe")]
    [InlineData("secrets: inherit")]
    [InlineData("Connect postgresql://db.example:5432/app")]
    [InlineData("-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA\n-----END PUBLIC KEY-----")]
    [InlineData("")]
    public void OrdinaryTextIsUnchanged(string text) =>
        Assert.Equal(text, CredentialRedactor.RedactTranscript(text));

    [Fact]
    public void MasksOnlyTheValueAndKeepsSurroundingLines()
    {
        const string text = "User: fix login\r\npassword=hunter2\r\nAgent: done, the rest stays";

        Assert.Equal("User: fix login\r\npassword=" + X + "\r\nAgent: done, the rest stays",
            CredentialRedactor.RedactTranscript(text));
    }

    [Fact]
    public void KnownSecretIsMaskedWhereverItAppears()
    {
        const string text = "Reference synthetic-private-value and synthetic-private-value";

        Assert.Equal("Reference " + X + " and " + X, CredentialRedactor.RedactTranscript(text, "synthetic-private-value"));
        Assert.Equal(text, CredentialRedactor.RedactTranscript(text));
    }

    [Theory]
    [InlineData("Before\n-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\nAbCd+/==\n-----END PRIVATE KEY-----\nAfter", "Before\n" + X + "\nAfter")]
    [InlineData("Before\n-----BEGIN RSA PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END RSA PRIVATE KEY-----", "Before\n" + X)]
    [InlineData("Before\n-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\nAfter, no footer", "Before\n" + X + "\nAfter, no footer")]
    [InlineData("{\"private_key\": \"-----BEGIN PRIVATE KEY-----\\nMIIEvQIBADAN\\n-----END PRIVATE KEY-----\\n\", \"id\": 1}", "{\"private_key\": \"" + X + "\\n\", \"id\": 1}")]
    [InlineData("{\"private_key\": \"-----BEGIN PRIVATE KEY-----\\nMIIEvQIBADAN\", \"id\": 1}", "{\"private_key\": \"" + X + "\", \"id\": 1}")]
    public void PrivateKeyBlocksBecomeOneMask(string text, string expected) =>
        Assert.Equal(expected, CredentialRedactor.RedactTranscript(text));

    [Fact]
    public void LargeTranscriptsAreMaskedQuickly()
    {
        var line = "User: please refactor the auth.ts login flow and keep cancellationToken: ct and password=hunter2 ok\n";
        var text = string.Concat(Enumerable.Repeat(line, 8 * 1024 * 1024 / line.Length));

        var stopwatch = Stopwatch.StartNew();
        var redacted = CredentialRedactor.RedactTranscript(text);
        stopwatch.Stop();

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("cancellationToken: ct", redacted, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"8 MB took {stopwatch.Elapsed}.");
    }

    [Theory]
    [InlineData("a.")]
    [InlineData("password ")]
    [InlineData("://a:")]
    [InlineData("<a")]
    [InlineData("--token ")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    public void PathologicalRepetitionStaysLinear(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 200_000));

        var stopwatch = Stopwatch.StartNew();
        CredentialRedactor.RedactTranscript(text);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"{unit} took {stopwatch.Elapsed}.");
    }

    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("Client-Secret")]
    [InlineData("Authorization")]
    public void ConfigurationPropertyDetectionNormalizesNames(string name) =>
        Assert.True(CredentialRedactor.IsCredentialProperty(name));

    [Fact]
    public void ConfigurationAndTranscriptBothKeepTheLabel()
    {
        const string value = "api_key=synthetic-private-value";

        Assert.Equal("api_key=" + CredentialRedactor.ConfigurationRedactionMarker,
            CredentialRedactor.RedactConfigurationValue(value));
        Assert.Equal("api_key=" + X, CredentialRedactor.RedactTranscript(value));
        Assert.False(CredentialRedactor.IsCredentialProperty("key")); // Keybinding, not a credential.
    }
}
