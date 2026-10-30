using System.Text.Json;
using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public sealed class SettingsForwardCompatibilityTests
{
    [Fact]
    public void SharedSettingsSavePreservesUnknownFieldsButDropsRetiredKeys()
    {
        var directory = Path.Combine(Path.GetTempPath(), "settings-forward-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {"ComputerName":"Before","FutureGateway":{"token":"future-secret","modes":[1,true,null]},
                 "FutureEnabled":false,"TokenSaverStages":["retired"],"TokenSaverCaptureEnabled":true}
                """);
            using var store = new SettingsFile(path);
            var settings = store.LoadFresh();
            settings.ComputerName = "After";
            store.Save(settings);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("After", saved.RootElement.GetProperty("ComputerName").GetString());
            Assert.Equal("future-secret", saved.RootElement.GetProperty("FutureGateway").GetProperty("token").GetString());
            Assert.Equal(3, saved.RootElement.GetProperty("FutureGateway").GetProperty("modes").GetArrayLength());
            Assert.False(saved.RootElement.GetProperty("FutureEnabled").GetBoolean());
            Assert.False(saved.RootElement.TryGetProperty("TokenSaverStages", out _));
            Assert.False(saved.RootElement.TryGetProperty("TokenSaverCaptureEnabled", out _));
            Assert.Equal("future-secret", store.LoadFresh().AdditionalSettings!["FutureGateway"].GetProperty("token").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }
}
