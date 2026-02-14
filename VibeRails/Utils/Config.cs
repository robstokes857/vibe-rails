using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeRails.Utils;

[JsonSerializable(typeof(Settings))]
internal partial class ConfigJsonContext : JsonSerializerContext
{
}

public class Settings
{
    public string InstallDirName { get; set; } = PathConstants.DEFAULT_INSTALL_DIR_NAME;
    public string Version { get; set; } = "1.0.0";
    public string ApiKey { get; set; } = string.Empty;
    public bool RemoteAccess { get; set; } = false;
    public HookSettings Hooks { get; set; } = new();
}

public class HookSettings
{
    public bool InstallOnStartup { get; set; } = false;
}

public static class Config
{
    private static Settings? _settings;
    private static readonly string _settingsPath;

    static Config()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Use the consolidated directory
        var dir = Path.Combine(home, PathConstants.DEFAULT_INSTALL_DIR_NAME);
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, PathConstants.SETTINGS_FILENAME);
    }

    public static string SettingsDirectory => Path.GetDirectoryName(_settingsPath)!;

    public static Settings Load()
    {
        if (_settings != null)
            return _settings;

        if (!File.Exists(_settingsPath))
        {
            _settings = new Settings();
            Save(_settings);
            return _settings;
        }

        var json = File.ReadAllText(_settingsPath);
        _settings = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Settings)
            ?? throw new InvalidOperationException($"Failed to deserialize {_settingsPath}");

        return _settings;
    }

    public static void Save(Settings settings)
    {
        var json = JsonSerializer.Serialize(settings, ConfigJsonContext.Default.Settings);
        File.WriteAllText(_settingsPath, json);
        _settings = settings;
    }

    public static void Reload()
    {
        _settings = null;
        Load();
    }
}
