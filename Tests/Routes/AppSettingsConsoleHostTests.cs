using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// VIBE-354, VIBE-372: the bundled Windows console host is the default for terminal tabs, and
/// "Windows built-in console host" is the setting that opts out. It must stay off unless the user
/// turns it on, survive a save from a client that does not know the field, and never need more
/// than an ordinary settings save.
/// </summary>
[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsConsoleHostTests
{
    [Fact]
    public void NewAndOlderSettingsLeaveTheOptOutOff()
    {
        Assert.False(new Settings().BuiltInConsoleHost);
        Assert.False(JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Settings)!.BuiltInConsoleHost);
        // An older or cached client that omits the field must not change the saved choice.
        Assert.Null(JsonSerializer.Deserialize("{}", AppJsonSerializerContext.Default.AppSettingsDto)!.BuiltInConsoleHost);
        // The retired switch's field is no longer part of the settings API: a cached page that
        // still posts it changes nothing.
        var stale = JsonSerializer.Deserialize("{\"modernConsoleHost\":true}", AppJsonSerializerContext.Default.AppSettingsDto)!;
        Assert.Null(stale.BuiltInConsoleHost);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void SettingsRoundTripPreservesAnOmittedValueAndSavesAnExplicitChoice(bool stored, bool? requested, bool expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-console-host-settings-{Guid.NewGuid():N}");
        var remote = ParserConfigs.GetRemoteAccess();
        var key = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            // The retired opt-in rides along untouched, whatever is saved here.
            store.Save(new Settings { BuiltInConsoleHost = stored, ModernConsoleHost = true });
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            Assert.Equal(stored, read.BuiltInConsoleHost);

            var saved = AppSettingsRoutes.UpdateSettings(read with { BuiltInConsoleHost = requested }, store);
            Assert.Equal(expected, saved.BuiltInConsoleHost);
            using var reopened = new SettingsFile(Path.Combine(directory, "settings.json"));
            Assert.Equal(expected, reopened.LoadFresh().BuiltInConsoleHost);
            Assert.True(reopened.LoadFresh().ModernConsoleHost);

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(saved, AppJsonSerializerContext.Default.AppSettingsDto));
            Assert.Equal(expected, json.RootElement.GetProperty("builtInConsoleHost").GetBoolean());
            Assert.False(json.RootElement.TryGetProperty("modernConsoleHost", out _));
            // It changes how new local terminals are hosted and nothing else, so it needs no
            // password, PIN or dashboard code.
            Assert.False(AppSettingsRoutes.RequiresPresence(read with { BuiltInConsoleHost = !stored }, store.LoadFresh()));
            // The choice a terminal start reads is the saved one: the bundled host unless opted out.
            Assert.Equal(
                expected ? null : @"C:\app\conpty\conpty.dll",
                BundledConsoleHost.ResolveForNewTerminal(
                    reopened.LoadFresh(), nativeConsole: false, supportedPlatform: true, @"C:\app\conpty\conpty.dll"));
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

    [Fact]
    public void TheResponseSaysWhetherThisBuildCanUseItAndOmitsThatWhereItCannotApply()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-console-host-settings-{Guid.NewGuid():N}");
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            store.Save(new Settings());
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);

            if (BundledConsoleHost.IsSupportedPlatform)
            {
                // Windows x64: an explicit answer, and a reason exactly when the answer is no.
                Assert.NotNull(read.BundledConsoleHostAvailable);
                Assert.Equal(read.BundledConsoleHostAvailable == false, read.BundledConsoleHostUnavailableReason is not null);
            }
            else
            {
                // Anywhere else the page hides the switch, which it does on a missing value.
                Assert.Null(read.BundledConsoleHostAvailable);
                Assert.Null(read.BundledConsoleHostUnavailableReason);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
