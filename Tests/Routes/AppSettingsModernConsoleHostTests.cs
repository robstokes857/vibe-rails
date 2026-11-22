using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// VIBE-354: the opt-in setting for the bundled Windows console host. It must stay off unless the
/// user turns it on, survive a save from a client that does not know the field, and never need
/// more than an ordinary settings save.
/// </summary>
[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsModernConsoleHostTests
{
    [Fact]
    public void NewAndOlderSettingsLeaveItOff()
    {
        Assert.False(new Settings().ModernConsoleHost);
        Assert.False(JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Settings)!.ModernConsoleHost);
        // An older or cached client that omits the field must not change the saved choice.
        Assert.Null(JsonSerializer.Deserialize("{}", AppJsonSerializerContext.Default.AppSettingsDto)!.ModernConsoleHost);
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
            store.Save(new Settings { ModernConsoleHost = stored });
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            Assert.Equal(stored, read.ModernConsoleHost);

            var saved = AppSettingsRoutes.UpdateSettings(read with { ModernConsoleHost = requested }, store);
            Assert.Equal(expected, saved.ModernConsoleHost);
            using var reopened = new SettingsFile(Path.Combine(directory, "settings.json"));
            Assert.Equal(expected, reopened.LoadFresh().ModernConsoleHost);

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(saved, AppJsonSerializerContext.Default.AppSettingsDto));
            Assert.Equal(expected, json.RootElement.GetProperty("modernConsoleHost").GetBoolean());
            // It changes how new local terminals are hosted and nothing else, so it needs no
            // password, PIN or dashboard code.
            Assert.False(AppSettingsRoutes.RequiresPresence(read with { ModernConsoleHost = !stored }, store.LoadFresh()));
            // The choice a terminal start reads is the saved one.
            Assert.Equal(
                expected ? @"C:\app\conpty\conpty.dll" : null,
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
                Assert.NotNull(read.ModernConsoleHostAvailable);
                Assert.Equal(read.ModernConsoleHostAvailable == false, read.ModernConsoleHostUnavailableReason is not null);
            }
            else
            {
                // Anywhere else the page hides the switch, which it does on a missing value.
                Assert.Null(read.ModernConsoleHostAvailable);
                Assert.Null(read.ModernConsoleHostUnavailableReason);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
