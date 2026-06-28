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
    public string ApiKey { get; set; } = string.Empty;
    public bool RemoteAccess { get; set; } = false;
    public bool EnablePrerelease { get; set; } = false;
    public bool DeveloperOptions { get; set; } = false;
    public bool UseVsCodeTheme { get; set; } = false;
    public bool McpEnabled { get; set; } = false;
    public string ComputerName { get; set; } = string.Empty;
    public string PinHash { get; set; } = string.Empty;
    public string PinSalt { get; set; } = string.Empty;
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

    // All settings.json reads/writes go through this gate. settings.json is shared mutable
    // state: a terminal launch re-reads it (LoadFresh) on a request thread while the settings
    // route may be writing it (Save). Serializing in-process turns "torn read of a half-written
    // file" into "wait for the write to finish, then read it whole".
    private static readonly object _gate = new();

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
        lock (_gate)
        {
            return LoadCore();
        }
    }

    // Caller must hold _gate.
    private static Settings LoadCore()
    {
        if (_settings != null)
            return _settings;

        if (!File.Exists(_settingsPath))
        {
            _settings = new Settings();
            SaveCore(_settings);
            return _settings;
        }

        var json = File.ReadAllText(_settingsPath);
        _settings = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Settings)
            ?? throw new InvalidOperationException($"Failed to deserialize {_settingsPath}");

        return _settings;
    }

    public static void Save(Settings settings)
    {
        lock (_gate)
        {
            SaveCore(settings);
        }
    }

    // Caller must hold _gate.
    private static void SaveCore(Settings settings)
    {
        var json = JsonSerializer.Serialize(settings, ConfigJsonContext.Default.Settings);
        File.WriteAllText(_settingsPath, json);
        _settings = settings;
    }

    public static void Reload()
    {
        lock (_gate)
        {
            _settings = null;
            LoadCore();
        }
    }

    /// <summary>
    /// Re-reads settings.json from disk, bypassing the in-memory cache, and returns it. Terminal
    /// tabs run in child processes that snapshot settings at their own startup; the parent persists
    /// every change to disk, so a caller that must honor a just-toggled setting (e.g. the MCP
    /// opt-out) reads fresh here instead of trusting this process's stale in-memory copy. Reads
    /// under _gate so it can't observe a concurrent Save mid-write.
    /// </summary>
    public static Settings LoadFresh()
    {
        lock (_gate)
        {
            _settings = null;
            return LoadCore();
        }
    }
}
