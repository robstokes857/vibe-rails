using System.Text.Json;
using System.Text.Json.Serialization;
using TokenSaver.Pipeline;

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
    public bool UseVsCodeTheme { get; set; } = false;
    // Retained for settings.json/API compatibility. MCP registration is always on.
    public bool McpEnabled { get; set; } = true;
    public string ComputerName { get; set; } = string.Empty;
    public bool CodexLlmProxyEnabled { get; set; } = false;
    public string CodexLlmProxyMode { get; set; } = "subscription";
    public bool ClaudeLlmProxyEnabled { get; set; } = false;
    // OpenCode (zai/Z.AI GLM) LLM proxy. Routes OpenCode's zai provider traffic through the local
    // token-saver proxy via the OPENCODE_CONFIG_CONTENT env var (see LlmProxyZaiConfig). Off by
    // default, like the Claude/Codex proxy toggles. Launch-flag-only — no opencode.json is written.
    public bool OpenCodeLlmProxyEnabled { get; set; } = false;
    // Token saver. TokenSaverStages is the ONE knob: the set of enabled stage and scope ids drawn
    // from TokenSaver.Pipeline.CompressionCatalog (e.g. "ansi-strip", "scope-shell"). It replaced
    // the old TokenSaverLevel tier string and the five per-transform bools in 2026-07 — a tier
    // could only ever express the combinations we thought of in advance, and the whole point of
    // this feature is that we are still learning which combinations are good.
    //
    // null means "never configured" and resolves to CompressionCatalog.DefaultSelection. An EMPTY
    // list is a real, honored choice (everything off) — the two are NOT interchangeable, which is
    // why this is nullable rather than defaulting to an empty list. Unknown ids are ignored, so a
    // settings.json written by a newer build degrades to "that stage is off here" instead of
    // failing an LLM request.
    //
    // Despite its legacy provider-specific name, ClaudeTokenSaverEnabled is the master saver kill
    // switch for Claude, Codex, and OpenCode; false force-disables all rewriting regardless of stages. A
    // provider's saver only runs when that provider's proxy is also enabled. Changing the selection
    // mid-session busts the provider prompt cache once — the transforms must be deterministic
    // across turns, so the cost of a config change is paid at the change, by design.
    public List<string>? TokenSaverStages { get; set; }
    public bool ClaudeTokenSaverEnabled { get; set; } = true;

    // ---- Legacy token-saver knobs. Superseded by TokenSaverStages. ----
    // Retained so existing settings.json files can be migrated once by Config.LoadCore. Runtime
    // compression reads only TokenSaverStages; do not wire these back into the UI.
    public string TokenSaverLevel { get; set; } = "safest";
    public bool TokenSaverCollapseCrRedraws { get; set; } = true;
    public bool TokenSaverStripAnsi { get; set; } = true;
    public bool TokenSaverStripTrailingWhitespace { get; set; } = true;
    public bool TokenSaverTrimBlankLines { get; set; } = true;
    public bool TokenSaverCollapseBlankRuns { get; set; } = false;

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
        var settings = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Settings)
            ?? throw new InvalidOperationException($"Failed to deserialize {_settingsPath}");

        // TokenSaverStages did not exist in older settings files. Preserve the user's old tier
        // (especially the explicit "off" choice) once, then persist the new representation so all
        // subsequent reads have one source of truth. A present JSON property whose value is null is
        // already the new "use catalog defaults" state and must not be mistaken for legacy data.
        if (!LegacyTokenSaverSettingsMigration.HasStageProperty(json))
        {
            settings.TokenSaverStages = LegacyTokenSaverSettingsMigration.FromLegacy(settings);
            SaveCore(settings);
            return settings;
        }

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

internal static class LegacyTokenSaverSettingsMigration
{
    internal static bool HasStageProperty(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Any(property =>
            property.Name.Equals(nameof(Settings.TokenSaverStages), StringComparison.OrdinalIgnoreCase));
    }

    internal static List<string> FromLegacy(Settings settings)
    {
        var level = (settings.TokenSaverLevel ?? string.Empty).Trim().ToLowerInvariant();
        if (level == "off")
            return [];

        var ids = new List<string>();
        void Add(bool enabled, string id)
        {
            if (enabled)
                ids.Add(id);
        }

        if (level is "safest" or "safe" or "medium" or "high")
        {
            Add(true, CompressionCatalog.CrCollapse);
            Add(true, CompressionCatalog.AnsiStrip);
            Add(true, CompressionCatalog.TrailingWhitespace);
            Add(true, CompressionCatalog.BlankEdges);
            Add(level is "safe" or "medium" or "high", CompressionCatalog.BlankRuns);
            Add(level is "medium" or "high", CompressionCatalog.DedupeLines);
            Add(level == "high", CompressionCatalog.TruncateLong);
            Add(true, CompressionCatalog.ScopeShell);
            Add(level is "safe" or "medium" or "high", CompressionCatalog.ScopeShellBackground);
            return ids;
        }

        // Legacy "custom" and unrecognised strings used the five persisted bools and foreground
        // shell tools only. Preserve that exact escape-hatch behaviour.
        Add(settings.TokenSaverCollapseCrRedraws, CompressionCatalog.CrCollapse);
        Add(settings.TokenSaverStripAnsi, CompressionCatalog.AnsiStrip);
        Add(settings.TokenSaverStripTrailingWhitespace, CompressionCatalog.TrailingWhitespace);
        Add(settings.TokenSaverTrimBlankLines, CompressionCatalog.BlankEdges);
        Add(settings.TokenSaverCollapseBlankRuns, CompressionCatalog.BlankRuns);
        Add(true, CompressionCatalog.ScopeShell);
        return ids;
    }
}
