using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsVibeAiUiTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Settings_AlwaysReportVibeAiShown_AndPreserveTheRetiredStoredValue(bool stored, bool? requested)
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

            // The narrow computer-name route uses the same response builder as GET /settings.
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            Assert.True(read.ShowVibeAiUi);
            Assert.Equal(stored, store.LoadFresh().ShowVibeAiUi);

            // A request value, including an older client's false, cannot hide Vibe AI. Older
            // versions on the same machine still read their own stored preference.
            var saved = AppSettingsRoutes.UpdateSettings(read with { ShowVibeAiUi = requested }, store);
            Assert.True(saved.ShowVibeAiUi);
            Assert.Equal(stored, store.LoadFresh().ShowVibeAiUi);
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
