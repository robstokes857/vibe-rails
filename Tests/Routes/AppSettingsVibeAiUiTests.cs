using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsVibeAiUiTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Settings_PersistVisibilityAndPreserveItWhenOmitted(bool stored, bool? requested, bool expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-ai-ui-settings-{Guid.NewGuid():N}");
        var remoteAccess = ParserConfigs.GetRemoteAccess();
        var apiKey = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            store.Save(new Settings { ShowVibeAiUi = stored });

            // The narrow computer-name route must not reset the preference. Its response
            // uses the same builder as GET /settings.
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            Assert.Equal(stored, read.ShowVibeAiUi);
            Assert.Equal(stored, store.LoadFresh().ShowVibeAiUi);

            var saved = AppSettingsRoutes.UpdateSettings(read with { ShowVibeAiUi = requested }, store);
            Assert.Equal(expected, saved.ShowVibeAiUi);
            Assert.Equal(expected, store.LoadFresh().ShowVibeAiUi);
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
