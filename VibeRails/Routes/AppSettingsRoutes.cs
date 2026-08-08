using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.HttpRelay;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;

namespace VibeRails.Routes;

public static class AppSettingsRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/settings/db-size", () =>
        {
            // ParserConfigs is the source used by DataExportService and respects a custom
            // install-directory configuration. The fallback also keeps this route useful in
            // small hosts/tests that map routes before FileService initializes ParserConfigs.
            var configuredPath = ParserConfigs.GetStatePath();
            var path = string.IsNullOrWhiteSpace(configuredPath)
                ? PathConstants.GetStateFilePath()
                : configuredPath;

            return Results.Ok(new StateDatabaseSizeResponse(SafeFileSize(path)));
        }).WithName("GetDbSize");

        // GET /api/v1/settings - Read current app settings
        app.MapGet("/api/v1/settings", () =>
        {
            return Results.Ok(BuildAppSettingsDto(Config.Load(), app.Configuration));
        }).WithName("GetAppSettings");

        // POST /api/v1/settings - Update app settings
        app.MapPost("/api/v1/settings", (AppSettingsDto settingsDto) =>
        {
            // Remote access requires a PIN — if none is set, force it off
            var remoteAccess = settingsDto.RemoteAccess && RemoteConfig.IsPinConfigured;

            // LoadFresh, not Load: this handler writes the WHOLE Settings object back, including
            // fields the UI doesn't expose (e.g. the hand-editable token-saver flags). Merging over
            // the process's cached copy would silently revert any settings.json edit made since
            // the cache was filled — including flipping the token-saver kill switch back on.
            var settings = Config.LoadFresh();
            var previousApiKey = settings.ApiKey;
            var previousRelaySetting = settings.RouteThroughVibeRailsAi;

            // A blank apiKey means "unchanged" (masked value was not edited), so removing a saved
            // key needs its own explicit signal — otherwise a stored key can never be cleared from
            // the UI. Also reject the masked placeholder itself (bullet chars, U+2022) so a client
            // that echoes it back can't overwrite the real key with dots.
            var clearApiKey = settingsDto.ClearApiKey == true;
            var apiKeyProvided = !clearApiKey
                && !string.IsNullOrWhiteSpace(settingsDto.ApiKey)
                && !settingsDto.ApiKey.Contains('•');

            // Update only the app settings fields exposed by the UI
            settings.RemoteAccess = remoteAccess;
            if (clearApiKey)
                settings.ApiKey = "";
            else if (apiKeyProvided)
                settings.ApiKey = settingsDto.ApiKey!;
            settings.UseVsCodeTheme = settingsDto.UseVsCodeTheme;
            // MCP registration is always on. Keep the field true for old clients/settings files.
            settings.McpEnabled = true;
            // Store the raw name (blank allowed). The machine-name default is resolved
            // at use (push notification) — never persisted — so a blank value keeps
            // tracking the live machine name and the field stays clearable.
            settings.ComputerName = NormalizeComputerName(settingsDto.ComputerName ?? settings.ComputerName);
            // Only overwrite proxy settings the client actually sent. A stale client (older cached
            // app.js) that omits these keys must not silently reset an enabled proxy — the DTO
            // fields are nullable for exactly this reason (mirrors ComputerName's stale-client guard).
            if (settingsDto.CodexLlmProxyEnabled.HasValue)
                settings.CodexLlmProxyEnabled = settingsDto.CodexLlmProxyEnabled.Value;
            if (settingsDto.CodexLlmProxyMode is not null)
                settings.CodexLlmProxyMode = CodexLlmProxySettings.NormalizeMode(settingsDto.CodexLlmProxyMode);
            if (settingsDto.ClaudeLlmProxyEnabled.HasValue)
                settings.ClaudeLlmProxyEnabled = settingsDto.ClaudeLlmProxyEnabled.Value;
            if (settingsDto.OpenCodeLlmProxyEnabled.HasValue)
                settings.OpenCodeLlmProxyEnabled = settingsDto.OpenCodeLlmProxyEnabled.Value;
            // Per-LLM saver toggles, same stale-client guard. Writing an explicit Codex/OpenCode
            // value deliberately severs the pre-split inheritance from the legacy master switch
            // (see Settings) — from then on each LLM stands on its own value.
            if (settingsDto.ClaudeTokenSaverEnabled.HasValue)
                settings.ClaudeTokenSaverEnabled = settingsDto.ClaudeTokenSaverEnabled.Value;
            if (settingsDto.CodexTokenSaverEnabled.HasValue)
                settings.CodexTokenSaverEnabled = settingsDto.CodexTokenSaverEnabled.Value;
            if (settingsDto.OpenCodeTokenSaverEnabled.HasValue)
                settings.OpenCodeTokenSaverEnabled = settingsDto.OpenCodeTokenSaverEnabled.Value;
            if (settingsDto.TokenSaverCaptureEnabled.HasValue)
                settings.TokenSaverCaptureEnabled = settingsDto.TokenSaverCaptureEnabled.Value;
            if (settingsDto.RemoveCoAuthorTrailers.HasValue)
                settings.RemoveCoAuthorTrailers = settingsDto.RemoveCoAuthorTrailers.Value;

            // Nullable is the stale-client guard. Enabling is effective only with the final raw
            // key after clear/replace semantics above have been applied.
            if (settingsDto.RouteThroughVibeRailsAi.HasValue)
            {
                settings.RouteThroughVibeRailsAi = ResolveHttpRelaySetting(
                    settings.RouteThroughVibeRailsAi,
                    settingsDto.RouteThroughVibeRailsAi,
                    settings.ApiKey);
            }
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
                settings.RouteThroughVibeRailsAi = false;

            // Save back to settings.json
            Config.Save(settings);

            // Update static Configs so runtime reflects the change immediately
            ParserConfigs.SetRemoteAccess(remoteAccess);
            if (clearApiKey)
                ParserConfigs.SetApiKey("");
            else if (apiKeyProvided)
                ParserConfigs.SetApiKey(settingsDto.ApiKey!);
            ParserConfigs.SetUseVsCodeTheme(settingsDto.UseVsCodeTheme);
            ParserConfigs.SetMcpEnabled(true);
            ParserConfigs.SetRouteThroughVibeRailsAi(settings.RouteThroughVibeRailsAi);

            // The credential is deliberately bound to the WebSocket handshake. A toggle or key
            // update therefore invalidates any established socket; the next test request creates
            // one with current settings.
            if (previousRelaySetting != settings.RouteThroughVibeRailsAi
                || !string.Equals(previousApiKey, settings.ApiKey, StringComparison.Ordinal))
            {
                app.Services.GetService<IRemoteHttpRelayClient>()?.Reset();
            }

            return Results.Ok(BuildAppSettingsDto(settings, app.Configuration));
        }).WithName("UpdateAppSettings");

        // POST /api/v1/settings/computer-name - Update ONLY the notification computer
        // name. Loads the current settings server-side and touches a single field, so
        // a save from the terminal panel can never overwrite unrelated settings with a
        // stale client-side copy (remoteAccess, mcpEnabled, theme, …).
        app.MapPost("/api/v1/settings/computer-name", (UpdateComputerNameDto dto) =>
        {
            // LoadFresh for the same reason as the main settings POST: never write a stale cached
            // snapshot back over hand-edited settings.json fields.
            var settings = Config.LoadFresh();
            settings.ComputerName = NormalizeComputerName(dto.ComputerName ?? settings.ComputerName);
            Config.Save(settings);
            return Results.Ok(BuildAppSettingsDto(settings, app.Configuration));
        }).WithName("UpdateComputerName");
    }

    private static AppSettingsDto BuildAppSettingsDto(Settings settings, IConfiguration configuration)
    {
        var maskedKey = string.IsNullOrWhiteSpace(settings.ApiKey)
            ? ""
            : settings.ApiKey.Length <= 4
                ? new string('•', settings.ApiKey.Length)
                : new string('•', settings.ApiKey.Length - 4) + settings.ApiKey[^4..];

        return new AppSettingsDto(
            settings.RemoteAccess,
            maskedKey,
            settings.UseVsCodeTheme,
            true,
            NormalizeComputerName(settings.ComputerName),
            settings.CodexLlmProxyEnabled,
            CodexLlmProxySettings.NormalizeMode(settings.CodexLlmProxyMode),
            settings.ClaudeLlmProxyEnabled,
            settings.OpenCodeLlmProxyEnabled,
            settings.ClaudeTokenSaverEnabled,
            // The effective values: an absent per-LLM key inherits the legacy master switch,
            // exactly as LlmProxySettingsService.Resolve does, so the UI shows what actually runs.
            settings.CodexTokenSaverEnabled ?? settings.ClaudeTokenSaverEnabled,
            settings.OpenCodeTokenSaverEnabled ?? settings.ClaudeTokenSaverEnabled,
            settings.TokenSaverCaptureEnabled,
            ComputerNameFormatter.Machine(),
            // Response-only; the request flag is never echoed back.
            ClearApiKey: null,
            // Asked of DataExportService itself so the button the client shows and the rule the
            // export enforces can't drift apart (placeholder value, non-HTTPS, unparseable).
            DataExportConfigured: DataExportService.TryParseExportUri(
                configuration[DataExportService.ExportUrlSettingKey],
                out _),
            RemoveCoAuthorTrailers: settings.RemoveCoAuthorTrailers,
            RouteThroughVibeRailsAi: settings.RouteThroughVibeRailsAi
        );
    }

    private static string NormalizeComputerName(string? value) =>
        ComputerNameFormatter.Normalize(value);

    internal static bool ResolveHttpRelaySetting(
        bool storedValue,
        bool? requestedValue,
        string? finalApiKey) =>
        !string.IsNullOrWhiteSpace(finalApiKey) && (requestedValue ?? storedValue);

    private static long SafeFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            // This value is display-only. A transient file-system race or permission problem
            // should not break Settings or prevent the export route from returning its own,
            // more specific result if the user still chooses to export.
            return 0;
        }
    }
}
