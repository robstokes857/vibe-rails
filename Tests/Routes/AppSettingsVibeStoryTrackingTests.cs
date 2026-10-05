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
    public void NewAndOlderSettingsFiles_DefaultBaseNudgeOnAndCustomEnvsOff()
    {
        Assert.True(new Settings().CreateVibeStoryTracking);
        Assert.False(new Settings().CreateVibeStoryTrackingCustomEnvs);
        var settings = JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Settings);
        Assert.True(settings!.CreateVibeStoryTracking);
        Assert.False(settings.CreateVibeStoryTrackingCustomEnvs);

        var disabled = JsonSerializer.Deserialize("""{"CreateVibeStoryTracking":false}""", ConfigJsonContext.Default.Settings);
        Assert.False(disabled!.CreateVibeStoryTracking);
        Assert.False(disabled.CreateVibeStoryTrackingCustomEnvs);
    }

    [Fact]
    public void OlderApiClients_OmitStoryTrackingWithoutResettingIt()
    {
        var dto = JsonSerializer.Deserialize("{}", AppJsonSerializerContext.Default.AppSettingsDto);
        Assert.Null(dto!.CreateVibeStoryTracking);
        Assert.Null(dto.CreateVibeStoryTrackingCustomEnvs);
    }

    public static TheoryData<bool, bool, bool?, bool?> NudgeSettingsUpdates
    {
        get
        {
            var data = new TheoryData<bool, bool, bool?, bool?>();
            foreach (var storedBase in new[] { false, true })
            foreach (var storedCustom in new[] { false, true })
            foreach (var requestedBase in new bool?[] { null, false, true })
            foreach (var requestedCustom in new bool?[] { null, false, true })
                data.Add(storedBase, storedCustom, requestedBase, requestedCustom);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NudgeSettingsUpdates))]
    public void SettingsReadsAndWrites_PersistAndReportIndependentNudges(
        bool storedBase, bool storedCustom, bool? requestedBase, bool? requestedCustom)
    {
        var expectedBase = requestedBase ?? storedBase;
        var expectedCustom = requestedCustom ?? storedCustom;
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-story-settings-{Guid.NewGuid():N}");
        var remoteAccess = ParserConfigs.GetRemoteAccess();
        var apiKey = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            store.Save(new Settings
            {
                CreateVibeStoryTracking = storedBase,
                CreateVibeStoryTrackingCustomEnvs = storedCustom
            });
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            Assert.Equal(storedBase, read.CreateVibeStoryTracking);
            Assert.Equal(storedCustom, read.CreateVibeStoryTrackingCustomEnvs);

            var saved = AppSettingsRoutes.UpdateSettings(read with
            {
                CreateVibeStoryTracking = requestedBase,
                CreateVibeStoryTrackingCustomEnvs = requestedCustom
            }, store);
            Assert.Equal(expectedBase, saved.CreateVibeStoryTracking);
            Assert.Equal(expectedCustom, saved.CreateVibeStoryTrackingCustomEnvs);
            using var reopenedStore = new SettingsFile(Path.Combine(directory, "settings.json"));
            Assert.Equal(expectedBase, reopenedStore.LoadFresh().CreateVibeStoryTracking);
            Assert.Equal(expectedCustom, reopenedStore.LoadFresh().CreateVibeStoryTrackingCustomEnvs);

            var json = JsonSerializer.Serialize(saved, AppJsonSerializerContext.Default.AppSettingsDto);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(expectedBase, document.RootElement.GetProperty("createVibeStoryTracking").GetBoolean());
            Assert.Equal(expectedCustom, document.RootElement.GetProperty("createVibeStoryTrackingCustomEnvs").GetBoolean());
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
