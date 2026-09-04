using VibeRails;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Services;

public sealed class DataExportEndpointConfigurationTests
{
    [Fact]
    public void NoRedirectHttpHandler_DisablesAutomaticRedirects()
    {
        // X-Api-Key is not stripped on a cross-host redirect the way Authorization is.
        using var handler = MapRegisterServices.CreateNoRedirectHttpMessageHandler();
        var httpHandler = Assert.IsType<HttpClientHandler>(handler);

        Assert.False(httpHandler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/export")]
    [InlineData("http://exports.example.test/upload")]
    [InlineData("https://exports.example.test/upload?tenant=x")]
    [InlineData("https://exports.example.test/upload#fragment")]
    public void TryParseExportUri_RejectsUnsafeOrUnusableValues(string? configured)
    {
        Assert.False(DataExportEndpointConfiguration.TryParseExportUri(configured, out _));
    }

    [Theory]
    [InlineData(
        "https://exports.example.test/api/v1/data-exports",
        "https://exports.example.test/api/v1/data-exports")]
    [InlineData(
        "  https://exports.example.test/api/v1/data-exports/  ",
        "https://exports.example.test/api/v1/data-exports/")]
    public void TryParseExportUri_AcceptsAbsoluteHttpsBaseUrls(
        string configured,
        string expected)
    {
        Assert.True(
            DataExportEndpointConfiguration.TryParseExportUri(configured, out var exportUri));
        Assert.Equal(expected, exportUri.AbsoluteUri);
    }
}
