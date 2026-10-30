using System.Net.Http;
using TokenSaver;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class EnterpriseGatewayCredentialsTests
{
    [Theory]
    [InlineData("api-key")]
    [InlineData("x-api-key")]
    [InlineData("X-Corporate-Api-Key")]
    public void ApiKeyIsSentRawInTheChosenHeader(string header)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gateway.example/v1/responses");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer personal-login");
        request.Headers.TryAddWithoutValidation("x-api-key", "personal-key");
        EnterpriseGatewayCredentials.Apply(request, " corporate-secret ", "api-key", header);
        Assert.Equal("corporate-secret", Assert.Single(request.Headers.GetValues(header)));
        Assert.False(request.Headers.Contains("Authorization"));
        if (!header.Equals("x-api-key", StringComparison.OrdinalIgnoreCase))
            Assert.False(request.Headers.Contains("x-api-key"));
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Cookie")]
    [InlineData("Connection")]
    [InlineData("Content-Length")]
    [InlineData("Expires")]
    [InlineData("Proxy-Authorization")]
    [InlineData("viberails_session")]
    [InlineData("VIBERAILS_TAB")]
    [InlineData("X-Key\r\nInjected")]
    [InlineData("not a header")]
    [InlineData("X-Kéy")]
    public void RoutingAndMalformedHeaderNamesAreRejected(string header) =>
        Assert.Throws<ArgumentException>(() => EnterpriseGatewayCredentials.NormalizeApiKeyHeader(header));

    [Fact]
    public void ModesValidateWithoutLeakingSuppliedSecrets()
    {
        Assert.Equal("corporate-secret", EnterpriseGatewayCredentials.NormalizeSecret("Bearer corporate-secret", "bearer"));
        Assert.Equal("Bearer", EnterpriseGatewayCredentials.NormalizeSecret("Bearer", "api-key"));
        Assert.Throws<ArgumentException>(() => EnterpriseGatewayCredentials.NormalizeMode("unknown"));
        var error = Assert.Throws<ArgumentException>(() => EnterpriseGatewayCredentials.NormalizeSecret("private\r\nkey", "api-key"));
        Assert.DoesNotContain("private", error.Message);
    }
}
