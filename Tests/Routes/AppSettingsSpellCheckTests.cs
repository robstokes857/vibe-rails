using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsSpellCheckTests
{
    [Fact]
    public void NewAndOlderSettings_DefaultSpellCheckOff()
    {
        Assert.False(new Settings().VsCodeSpellCheck);
        Assert.False(JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Settings)!.VsCodeSpellCheck);
        Assert.Null(JsonSerializer.Deserialize("{}", AppJsonSerializerContext.Default.AppSettingsDto)!.VsCodeSpellCheck);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void SettingsRoundTrip_PreservesOmittedValueAndSavesExplicitChoices(bool stored, bool? requested, bool expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-spelling-settings-{Guid.NewGuid():N}");
        var remote = ParserConfigs.GetRemoteAccess();
        var key = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            store.Save(new Settings { VsCodeSpellCheck = stored });
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            Assert.Equal(stored, read.VsCodeSpellCheck);
            var saved = AppSettingsRoutes.UpdateSettings(read with { VsCodeSpellCheck = requested }, store);
            Assert.Equal(expected, saved.VsCodeSpellCheck);
            using var reopened = new SettingsFile(Path.Combine(directory, "settings.json"));
            Assert.Equal(expected, reopened.LoadFresh().VsCodeSpellCheck);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(saved, AppJsonSerializerContext.Default.AppSettingsDto));
            Assert.Equal(expected, json.RootElement.GetProperty("vsCodeSpellCheck").GetBoolean());
            Assert.False(AppSettingsRoutes.RequiresPresence(read with { VsCodeSpellCheck = !stored }, store.LoadFresh()));
        }
        finally
        {
            ParserConfigs.SetRemoteAccess(remote);
            ParserConfigs.SetApiKey(key);
            ParserConfigs.SetUseVsCodeTheme(theme);
            ParserConfigs.SetMcpEnabled(mcp);
            ParserConfigs.SetRouteThroughVibeRailsAi(relay);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
