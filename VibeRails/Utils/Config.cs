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
    // Proof-of-concept HTTP proxy. Off by default; the settings route also forces it off when
    // there is no saved cloud API key.
    public bool RouteThroughVibeRailsAi { get; set; } = false;
    public bool UseVsCodeTheme { get; set; } = false;
    // Vibe AI inspector in the nav. Off by default — the page is a power-user
    // capture/search surface, not part of the everyday workflow.
    public bool ShowVibeAiUi { get; set; } = false;
    // Retained for settings.json/API compatibility. MCP registration is always on.
    public bool McpEnabled { get; set; } = true;
    public string ComputerName { get; set; } = string.Empty;
    public bool CodexLlmProxyEnabled { get; set; } = false;
    public string CodexLlmProxyMode { get; set; } = "subscription";
    public bool ClaudeLlmProxyEnabled { get; set; } = false;
    // OpenCode (zai/Z.AI GLM + xai/Grok) LLM proxy. Routes OpenCode's zai and xai provider
    // traffic through the local token-saver proxy via the OPENCODE_CONFIG_CONTENT env var
    // (see LlmProxyZaiConfig / LlmProxyXaiConfig). Off by default, like the Claude/Codex
    // proxy toggles. Launch-flag-only — no opencode.json is written.
    public bool OpenCodeLlmProxyEnabled { get; set; } = false;
    public bool GrokLlmProxyEnabled { get; set; } = false;
    public string GrokLlmProxyMode { get; set; } = "subscription";
    // ---- Token saver: on/off per LLM is the whole user-facing surface (2026-07-18). ----
    // When a provider's saver is on (and that provider's proxy is on), the pipeline runs
    // CompressionCatalog.DefaultSelection — the curated safe set. The per-stage picker is gone.
    //
    // ClaudeTokenSaverEnabled keeps its legacy name because it is settings.json wire format: it
    // used to be the master kill switch for every provider. It is now the Claude toggle AND the
    // inherited default for the two nullable per-provider toggles below, so a pre-split file whose
    // owner had turned the master off stays entirely off until they choose otherwise. null on the
    // other two = key absent (pre-split file) → inherit; the settings route writes explicit
    // values, which severs the inheritance from then on.
    public bool ClaudeTokenSaverEnabled { get; set; } = true;
    public bool? CodexTokenSaverEnabled { get; set; }
    public bool? OpenCodeTokenSaverEnabled { get; set; }
    public bool? GrokTokenSaverEnabled { get; set; }

    // Git Guard commit-msg policy. Default-on for both new settings files and older files that
    // predate this property: System.Text.Json leaves the initializer in place when the key is
    // absent. The standalone hook process reloads this value for every commit.
    public bool RemoveCoAuthorTrailers { get; set; } = true;

    // Hand-edit escape hatch, deliberately not exposed in any UI: a non-null list of stage/scope
    // ids from CompressionCatalog replaces the curated set wholesale, so a misbehaving stage can
    // be bisected on live traffic without turning the whole saver off. null (the only value the
    // product itself writes) = curated defaults; [] = saver on but a no-op. Unknown ids are
    // ignored. Changing it mid-session busts the provider prompt cache once, by design.
    //
    // Deliberately NOT the old TokenSaverStages key: the retired stage picker persisted that key
    // on every save, and honoring it would freeze early adopters on whatever selection the picker
    // last wrote — silently exempting them from the curated set. The rename is the one-time
    // reset. The old key (and the pre-2026-07 tier/bool knobs) are ignored on read and dropped on
    // the next save.
    public List<string>? TokenSaverStageOverride { get; set; }

    // Raw before/after captures to state.db (see ICompressionCaptureSink). UNCAPPED by explicit
    // product decision (2026-07-15): captures are the only evidence that a stage is correct rather
    // than merely small, and capping or truncating them would preferentially destroy the
    // pathological inputs that are the entire reason to look. This table grows without bound;
    // DELETE /api/v1/compression/captures is the reset.
    public bool TokenSaverCaptureEnabled { get; set; } = false;
    public string PinHash { get; set; } = string.Empty;
    public string PinSalt { get; set; } = string.Empty;
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
        PrivateFilePermissions.EnsureDirectory(dir);
        _settingsPath = Path.Combine(dir, PathConstants.SETTINGS_FILENAME);
        PrivateFilePermissions.EnsureFile(_settingsPath);
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
        var settings = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Settings)
            ?? throw new InvalidOperationException($"Failed to deserialize {_settingsPath}");

        _settings = settings;

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
        PrivateFilePermissions.EnsureFile(_settingsPath);
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
    /// every change to disk, so a caller that must honor a just-toggled setting reads fresh here
    /// instead of trusting this process's stale in-memory copy. Reads under _gate so it can't
    /// observe a concurrent Save mid-write.
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
