using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.HttpRelay;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using VibeRails.Auth;
using VibeRails.Interfaces;

namespace VibeRails.Routes;

public static class AppSettingsRoutes
{
    public static void Map(WebApplication app, bool allowWrites = true)
    {
        // GET /api/v1/settings - Read current app settings
        app.MapGet("/api/v1/settings", () =>
        {
            return Results.Ok(BuildAppSettingsDto(Config.LoadFresh()));
        }).WithName("GetAppSettings");

        if (!allowWrites) return;

        app.MapGet("/api/v1/settings/security-challenge", (HttpContext context, SettingsPresenceService presence) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(presence.Challenge(RemoteConfig.IsPinConfigured));
        });

        // POST /api/v1/settings - Update app settings
        app.MapPost("/api/v1/settings", (AppSettingsDto settingsDto, HttpContext context, SettingsPresenceService presence) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var lease = Config.Store.AcquireWriteLock();
            var settings = Config.LoadFresh();
            if (RequiresPresence(settingsDto, settings) && !presence.Verify(settingsDto.PresenceProof, settings))
                return Results.Json(new ErrorResponse("Confirm this change with your current PIN or a fresh dashboard code."), statusCode: 403);
            var result = UpdateSettings(settingsDto, Config.Store, app.Services.GetService<IRemoteHttpRelayClient>());
            if (settings.ApiKey != Config.LoadFresh().ApiKey)
            {
                Serilog.Log.Warning("[Settings] Account API key changed from the root dashboard");
                app.Services.GetService<IAppEventBus>()?.Publish("account_key_changed", new ErrorResponse("Account API key changed."), AppJsonSerializerContext.Default.ErrorResponse);
            }
            return Results.Ok(result);
        })
            .WithName("UpdateAppSettings");

        // POST /api/v1/settings/computer-name - Update ONLY the notification computer
        // name. Loads the current settings server-side and touches a single field, so
        // a save from the terminal panel can never overwrite unrelated settings with a
        // stale client-side copy (remoteAccess, mcpEnabled, theme, …).
        app.MapPost("/api/v1/settings/computer-name", (UpdateComputerNameDto dto) =>
            Results.Ok(UpdateComputerName(dto, Config.Store))).WithName("UpdateComputerName");
    }

    internal static bool RequiresPresence(AppSettingsDto request, Settings settings) =>
        request.RemoteAccess != settings.RemoteAccess
        || request.RouteThroughVibeRailsAi.HasValue && request.RouteThroughVibeRailsAi.Value != settings.RouteThroughVibeRailsAi
        || request.ClearApiKey == true && !string.IsNullOrEmpty(settings.ApiKey)
        || !string.IsNullOrWhiteSpace(request.ApiKey) && !request.ApiKey.Contains('•') && request.ApiKey != settings.ApiKey;

    // The internal store parameter is solely for isolated regression fixtures. Production calls
    // always use Config.Store and therefore the normal application settings path.
    internal static AppSettingsDto UpdateSettings(AppSettingsDto settingsDto, SettingsFile store, IRemoteHttpRelayClient? relay = null)
    {
        using (store.AcquireWriteLock())
        {
            // LoadFresh, not Load: this handler writes the WHOLE Settings object back, including
            // fields the UI doesn't expose (e.g. the hand-editable token-saver flags). Merging over
            // the process's cached copy would silently revert any settings.json edit made since
            // the cache was filled — including flipping the token-saver kill switch back on.
            var settings = store.LoadFresh();
            // Use the same locked snapshot for PIN readiness and the settings write.
            var remoteAccess = settingsDto.RemoteAccess
                && !string.IsNullOrWhiteSpace(settings.PinHash) && !string.IsNullOrWhiteSpace(settings.PinSalt);
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
            if (!string.Equals(previousApiKey, settings.ApiKey, StringComparison.Ordinal))
            {
                settings.RemoteAccountEmail = null;
                settings.RemoteAccountKeyFingerprint = null;
            }
            settings.UseVsCodeTheme = settingsDto.UseVsCodeTheme;
            if (settingsDto.CreateVibeStoryTracking.HasValue)
                settings.CreateVibeStoryTracking = settingsDto.CreateVibeStoryTracking.Value;
            if (settingsDto.CreateVibeStoryTrackingCustomEnvs.HasValue)
                settings.CreateVibeStoryTrackingCustomEnvs = settingsDto.CreateVibeStoryTrackingCustomEnvs.Value;
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
            if (settingsDto.GrokLlmProxyEnabled.HasValue)
                settings.GrokLlmProxyEnabled = settingsDto.GrokLlmProxyEnabled.Value;
            if (settingsDto.GrokLlmProxyMode is not null)
                settings.GrokLlmProxyMode = LlmProxyCliChatConfig.NormalizeMode(settingsDto.GrokLlmProxyMode);
            // Per-LLM saver toggles, same stale-client guard. Writing an explicit Codex/OpenCode
            // value deliberately severs the pre-split inheritance from the legacy master switch
            // (see Settings) — from then on each LLM stands on its own value.
            if (settingsDto.ClaudeTokenSaverEnabled.HasValue)
                settings.ClaudeTokenSaverEnabled = settingsDto.ClaudeTokenSaverEnabled.Value;
            if (settingsDto.CodexTokenSaverEnabled.HasValue)
                settings.CodexTokenSaverEnabled = settingsDto.CodexTokenSaverEnabled.Value;
            if (settingsDto.OpenCodeTokenSaverEnabled.HasValue)
                settings.OpenCodeTokenSaverEnabled = settingsDto.OpenCodeTokenSaverEnabled.Value;
            if (settingsDto.GrokTokenSaverEnabled.HasValue)
                settings.GrokTokenSaverEnabled = settingsDto.GrokTokenSaverEnabled.Value;
            // Trailer cleanup is unconditional. Ignore the legacy request field and preserve
            // its stored value for older versions that still expose the setting.

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
            // Vibe AI navigation is always shown. Ignore the legacy request field and preserve
            // its stored value for older versions that still expose the setting.
            // Always share completed sessions, and always delete local copies once they are
            // backed up. A stored false is overwritten on the next settings save.
            settings.DataExportOptIn = true;
            settings.DataRetentionEnabled = true;

            // Save back to settings.json
            store.Save(settings);

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
                relay?.Reset();
            }

            return BuildAppSettingsDto(settings);
        }
    }

    internal static AppSettingsDto UpdateComputerName(UpdateComputerNameDto dto, SettingsFile store)
    {
        using (store.AcquireWriteLock())
        {
            // LoadFresh for the same reason as the main settings POST: never write a stale cached
            // snapshot back over hand-edited settings.json fields.
            var settings = store.LoadFresh();
            settings.ComputerName = NormalizeComputerName(dto.ComputerName ?? settings.ComputerName);
            settings.DataExportOptIn = true;
            settings.DataRetentionEnabled = true;
            store.Save(settings);
            return BuildAppSettingsDto(settings);
        }
    }

    private static AppSettingsDto BuildAppSettingsDto(Settings settings)
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
            ComputerNameFormatter.Machine(),
            // Response-only; the request flag is never echoed back.
            ClearApiKey: null,
            // The one-shot export button is gone. Kept on the wire so older clients still
            // deserialize; responses always report true.
            DataExportConfigured: true,
            RemoveCoAuthorTrailers: true,
            RouteThroughVibeRailsAi: settings.RouteThroughVibeRailsAi,
            ShowVibeAiUi: true,
            settings.GrokLlmProxyEnabled,
            LlmProxyCliChatConfig.NormalizeMode(settings.GrokLlmProxyMode),
            settings.GrokTokenSaverEnabled ?? settings.OpenCodeTokenSaverEnabled ?? settings.ClaudeTokenSaverEnabled,
            DataExportOptIn: true,
            RemoteAccountEmail: ApiKeyStore.GetAccountEmail(settings),
            CreateVibeStoryTracking: settings.CreateVibeStoryTracking,
            CreateVibeStoryTrackingCustomEnvs: settings.CreateVibeStoryTrackingCustomEnvs
        );
    }

    private static string NormalizeComputerName(string? value) =>
        ComputerNameFormatter.Normalize(value);

    internal static bool ResolveHttpRelaySetting(
        bool storedValue,
        bool? requestedValue,
        string? finalApiKey) =>
        !string.IsNullOrWhiteSpace(finalApiKey) && (requestedValue ?? storedValue);

}
