using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsTrailerRemovalTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Settings_AlwaysReportCleanupEnabledAndPreserveTheLegacyValue(bool stored, bool? requested)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-trailer-settings-{Guid.NewGuid():N}");
        var remoteAccess = ParserConfigs.GetRemoteAccess();
        var apiKey = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            store.Save(new Settings { RemoveCoAuthorTrailers = stored });

            // The shared response builder also serves GET /settings. Reading settings does not
            // expose a legacy false as an effective opt-out.
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            Assert.True(read.RemoveCoAuthorTrailers);

            var saved = AppSettingsRoutes.UpdateSettings(read with { RemoveCoAuthorTrailers = requested }, store);

            Assert.True(saved.RemoveCoAuthorTrailers);
            Assert.Equal(stored, store.LoadFresh().RemoveCoAuthorTrailers);
        }
        finally
        {
            ParserConfigs.SetRemoteAccess(remoteAccess);
            ParserConfigs.SetApiKey(apiKey);
            ParserConfigs.SetUseVsCodeTheme(theme);
            ParserConfigs.SetMcpEnabled(mcp);
            ParserConfigs.SetRouteThroughVibeRailsAi(relay);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
