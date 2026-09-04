using VibeRails.Routes;
using Xunit;

namespace Tests.Routes;

public sealed class AppSettingsDataExportOptInTests
{
    [Theory]
    [InlineData(false, null, "saved-key", false)]
    [InlineData(true, null, "saved-key", true)]
    [InlineData(false, true, "saved-key", true)]
    [InlineData(true, false, "saved-key", false)]
    [InlineData(true, null, "", false)]
    [InlineData(false, true, "   ", false)]
    [InlineData(true, true, null, false)]
    public void ResolveDataExportOptIn_PreservesStaleClientsAndRequiresFinalApiKey(
        bool storedValue,
        bool? requestedValue,
        string? finalApiKey,
        bool expected)
    {
        Assert.Equal(
            expected,
            AppSettingsRoutes.ResolveDataExportOptIn(
                storedValue,
                requestedValue,
                finalApiKey));
    }
}
