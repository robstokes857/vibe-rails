using System.Text.Json;
using VibeRails.Routes;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

public sealed class AppSettingsHttpRelayTests
{
    [Fact]
    public void OlderSettingsFile_DefaultsRelayRoutingOff()
    {
        var settings = JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Settings);

        Assert.NotNull(settings);
        Assert.False(settings!.RouteThroughVibeRailsAi);
    }

    [Theory]
    [InlineData(true, null, "key", true)]
    [InlineData(false, null, "key", false)]
    [InlineData(false, true, "key", true)]
    [InlineData(true, false, "key", false)]
    [InlineData(true, true, "", false)]
    [InlineData(true, null, " ", false)]
    public void SettingIsStaleClientSafe_AndCannotBeEffectiveWithoutAKey(
        bool storedValue,
        bool? requestedValue,
        string apiKey,
        bool expected)
    {
        Assert.Equal(
            expected,
            AppSettingsRoutes.ResolveHttpRelaySetting(storedValue, requestedValue, apiKey));
    }
}
