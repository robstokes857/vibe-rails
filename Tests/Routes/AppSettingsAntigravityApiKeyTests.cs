using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsAntigravityApiKeyTests
{
    [Fact]
    public void NewAndOlderSettingsDefaultToNoAntigravityApiKey()
    {
        Assert.Equal(string.Empty, new Settings().AntigravityApiKey);
        Assert.Equal(string.Empty, JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Settings)!.AntigravityApiKey);

        var dto = JsonSerializer.Deserialize("{}", AppJsonSerializerContext.Default.AppSettingsDto)!;
        Assert.Null(dto.AntigravityApiKey);
        Assert.Null(dto.ClearAntigravityApiKey);
        Assert.False(dto.HasAntigravityApiKey);
    }

    [Fact]
    public void Save_IsWriteOnlyPreservesOmittedClearsExplicitlyAndValidatesAtomically()
    {
        WithStore((directory, store) =>
        {
            store.Save(new Settings());
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            Assert.False(read.HasAntigravityApiKey);
            Assert.Null(read.AntigravityApiKey);

            // Saving a key trims whitespace, requires no presence proof, and never returns secret bytes.
            var saveRequest = read with { AntigravityApiKey = "  AIzaSyTestKey12345  " };
            Assert.False(AppSettingsRoutes.RequiresPresence(saveRequest, store.LoadFresh()));
            var saved = AppSettingsRoutes.UpdateSettings(saveRequest, store);
            Assert.True(saved.HasAntigravityApiKey);
            Assert.Null(saved.AntigravityApiKey);
            Assert.Null(saved.ClearAntigravityApiKey);
            var serialized = JsonSerializer.Serialize(saved, AppJsonSerializerContext.Default.AppSettingsDto);
            Assert.DoesNotContain("AIzaSyTestKey12345", serialized);
            Assert.Equal("AIzaSyTestKey12345", store.LoadFresh().AntigravityApiKey);

            // Blank or omitted values preserve the saved key (including older clients).
            var blankPreserved = AppSettingsRoutes.UpdateSettings(read with { AntigravityApiKey = "" }, store);
            Assert.True(blankPreserved.HasAntigravityApiKey);
            Assert.Equal("AIzaSyTestKey12345", store.LoadFresh().AntigravityApiKey);

            var omittedPreserved = AppSettingsRoutes.UpdateSettings(read with { AntigravityApiKey = null, ClearAntigravityApiKey = null }, store);
            Assert.True(omittedPreserved.HasAntigravityApiKey);
            Assert.Equal("AIzaSyTestKey12345", store.LoadFresh().AntigravityApiKey);

            // Validation rejects spaces/control characters/oversized values atomically without mutating disk.
            var before = File.ReadAllText(Path.Combine(directory, "settings.json"));
            Assert.Throws<ArgumentException>(() => AppSettingsRoutes.UpdateSettings(read with
            {
                ComputerName = "ShouldNotSave",
                AntigravityApiKey = "AIza bad space"
            }, store));
            Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "settings.json")));

            Assert.Throws<ArgumentException>(() => AppSettingsRoutes.UpdateSettings(read with
            {
                AntigravityApiKey = "AIza\r\nInjected"
            }, store));
            Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "settings.json")));

            Assert.Throws<ArgumentException>(() => AppSettingsRoutes.UpdateSettings(read with
            {
                AntigravityApiKey = new string('a', 16385)
            }, store));
            Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "settings.json")));

            // Explicit clear removes the saved key even if stray invalid text accompanied it.
            var cleared = AppSettingsRoutes.UpdateSettings(read with
            {
                AntigravityApiKey = "ignored bad text",
                ClearAntigravityApiKey = true
            }, store);
            Assert.False(cleared.HasAntigravityApiKey);
            Assert.Null(cleared.AntigravityApiKey);
            Assert.Null(cleared.ClearAntigravityApiKey);
            Assert.Empty(store.LoadFresh().AntigravityApiKey);
        });
    }

    private static void WithStore(Action<string, SettingsFile> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-antigravity-key-{Guid.NewGuid():N}");
        var remote = ParserConfigs.GetRemoteAccess();
        var key = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            test(directory, store);
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
