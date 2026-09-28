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

    [Fact]
    public void ExportUri_IsTheFixedVibeRailsHost()
    {
        Assert.Equal("https://viberails.ai/api/v1/data-exports", DataExportEndpointConfiguration.ExportUrl);
        Assert.Equal(DataExportEndpointConfiguration.ExportUrl, DataExportEndpointConfiguration.ExportUri.AbsoluteUri);
        Assert.Equal(Uri.UriSchemeHttps, DataExportEndpointConfiguration.ExportUri.Scheme);
        Assert.Equal("viberails.ai", DataExportEndpointConfiguration.ExportUri.Host);
        Assert.Equal(string.Empty, DataExportEndpointConfiguration.ExportUri.Query);
        Assert.Equal(string.Empty, DataExportEndpointConfiguration.ExportUri.Fragment);
    }
}
