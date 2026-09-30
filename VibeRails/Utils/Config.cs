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
    /// <summary>Display email from account approval, valid only for the fingerprinted key.</summary>
    public string? RemoteAccountEmail { get; set; }
    /// <summary>Binds the display email to the saved credential, including across older writers.</summary>
    public string? RemoteAccountKeyFingerprint { get; set; }
    // Session sharing is always on. The property stays so older settings.json files still
    // deserialize; readers and the settings route ignore a stored false.
    public bool DataExportOptIn { get; set; } = true;
    // Local retention is always on. The property stays so older settings.json files still
    // deserialize; startup and settings saves overwrite a stored false.
    public bool DataRetentionEnabled { get; set; } = true;
    public bool RemoteAccess { get; set; } = false;
    // Proof-of-concept HTTP proxy. Off by default; the settings route also forces it off when
    // there is no saved cloud API key.
    public bool RouteThroughVibeRailsAi { get; set; } = false;
    public bool UseVsCodeTheme { get; set; } = false;
    // The Vibe AI inspector is always in the nav. The property stays so older settings.json
    // files still deserialize; readers and the settings route ignore a stored false.
    public bool ShowVibeAiUi { get; set; } = true;
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
    // absent. The standalone hook process reloads this value for every commit. Covers both
    // Co-authored-by and Claude-Session trailers; keep the persisted name for existing settings.
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

    // TokenSaverCaptureEnabled (2026-07-15 → 2026-09-17) is gone: the per-tool_result capture
    // table was retired, and the always-on exchange log (proxy_exchanges.db) holds the same
    // bytes. A settings.json that still carries the key is read fine; it is dropped on next save.
    public string PinHash { get; set; } = string.Empty;
    public string PinSalt { get; set; } = string.Empty;
}

public static class Config
{
    // Production always uses the normal application settings file. The internal file component
    // accepts a path so automated tests can exercise the same transaction with disposable files;
    // it does not add a runtime flag or alternate application data directory.
    internal static SettingsFile Store { get; } = new(
        Path.Combine(PathConstants.GetInstallDirPath(), PathConstants.SETTINGS_FILENAME));

    public static string SettingsDirectory => Store.DirectoryPath;

    public static Settings Load() => Store.Load();

    /// <summary>
    /// Saves a complete settings snapshot. Application read/modify/write operations must hold
    /// AcquireWriteLock from before LoadFresh through this save to preserve other roots' edits.
    /// </summary>
    public static void Save(Settings settings) => Store.Save(settings);

    public static void Reload() => Store.LoadFresh();

    /// <summary>Re-reads the shared settings file instead of the process's cached snapshot.</summary>
    public static Settings LoadFresh() => Store.LoadFresh();

    // Synchronous, thread-affine and reentrant. All writers use the same in-process then OS
    // lock order; never await while holding this lease.
    internal static IDisposable AcquireWriteLock() => Store.AcquireWriteLock();
}
