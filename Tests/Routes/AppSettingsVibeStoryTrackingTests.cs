using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsVibeStoryTrackingTests
{
    [Fact]
    public void NewAndOlderSettingsFiles_DefaultStoryTrackingOn()
    {
        Assert.True(new Settings().CreateVibeStoryTracking);
        var settings = JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Settings);
        Assert.True(settings!.CreateVibeStoryTracking);

        var disabled = JsonSerializer.Deserialize("""{"CreateVibeStoryTracking":false}""", ConfigJsonContext.Default.Settings);
        Assert.False(disabled!.CreateVibeStoryTracking);
    }

    [Fact]
    public void OlderApiClients_OmitStoryTrackingWithoutResettingIt()
    {
        var dto = JsonSerializer.Deserialize("{}", AppJsonSerializerContext.Default.AppSettingsDto);
        Assert.Null(dto!.CreateVibeStoryTracking);
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void SettingsReadsAndWrites_PersistAndReportStoryTracking(bool stored, bool? requested, bool expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-story-settings-{Guid.NewGuid():N}");
        var remoteAccess = ParserConfigs.GetRemoteAccess();
        var apiKey = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            store.Save(new Settings { CreateVibeStoryTracking = stored });
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            Assert.Equal(stored, read.CreateVibeStoryTracking);
            Assert.Equal(stored, store.LoadFresh().CreateVibeStoryTracking);

            var saved = AppSettingsRoutes.UpdateSettings(read with { CreateVibeStoryTracking = requested }, store);
            Assert.Equal(expected, saved.CreateVibeStoryTracking);
            Assert.Equal(expected, store.LoadFresh().CreateVibeStoryTracking);

            var json = JsonSerializer.Serialize(saved, AppJsonSerializerContext.Default.AppSettingsDto);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(expected, document.RootElement.GetProperty("createVibeStoryTracking").GetBoolean());
        }
        finally
        {
            ParserConfigs.SetRemoteAccess(remoteAccess);
            ParserConfigs.SetApiKey(apiKey);
            ParserConfigs.SetUseVsCodeTheme(theme);
            ParserConfigs.SetMcpEnabled(mcp);
            ParserConfigs.SetRouteThroughVibeRailsAi(relay);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
