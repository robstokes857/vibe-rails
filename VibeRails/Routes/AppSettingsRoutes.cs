using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.HttpRelay;
using VibeRails.Services.LlmProxy;
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
        app.MapGet("/api/v1/settings", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(BuildAppSettingsDto(Config.LoadFresh()));
        }).WithName("GetAppSettings");

        if (!allowWrites) return;

        app.MapGet("/api/v1/settings/security-challenge", (HttpContext context, SettingsPresenceService presence) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(presence.Challenge(RemoteConfig.IsPinConfigured));
        });

        // POST /api/v1/settings/enterprise-network-proxy/test - one credential-free request to the
        // entered gateway URL (saved or not) through the relay's transport. Reports only the status
        // class; see EnterpriseNetworkProxyProbe and API_SEC.md.
        app.MapPost("/api/v1/settings/enterprise-network-proxy/test", async (
            EnterpriseNetworkProxyTestRequest request, HttpContext context, EnterpriseNetworkProxyProbe probe) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!EnterpriseNetworkProxyProbe.IsKnownProvider(request.Provider))
                return Results.BadRequest(new ErrorResponse("Unknown provider."));
            return Results.Ok(await probe.TestAsync(request.Provider!, request.Url, context.RequestAborted));
        }).WithName("TestEnterpriseNetworkProxy");

        // POST /api/v1/settings - Update app settings
        app.MapPost("/api/v1/settings", (AppSettingsDto settingsDto, HttpContext context, SettingsPresenceService presence) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var lease = Config.Store.AcquireWriteLock();
            var settings = Config.LoadFresh();
            // Refuse an invalid gateway before presence so a typo never consumes a confirmation.
            if (!EnterpriseNetworkProxiesAreValid(settingsDto))
                return Results.BadRequest(new ErrorResponse(EnterpriseNetworkProxy.InvalidMessage));
            try { ValidateGatewayCredentials(settingsDto, settings); }
            catch (ArgumentException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
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
        || !string.IsNullOrWhiteSpace(request.ApiKey) && !request.ApiKey.Contains('•') && request.ApiKey != settings.ApiKey
        || PointsGatewayElsewhere(request.CodexEnterpriseNetworkProxy, settings.CodexEnterpriseNetworkProxy)
        || PointsGatewayElsewhere(request.ClaudeEnterpriseNetworkProxy, settings.ClaudeEnterpriseNetworkProxy)
        || PointsGatewayElsewhere(request.OpenCodeEnterpriseNetworkProxy, settings.OpenCodeEnterpriseNetworkProxy)
        || PointsGatewayElsewhere(request.GrokEnterpriseNetworkProxy, settings.GrokEnterpriseNetworkProxy);

    // A gateway receives that CLI's prompts, code and provider credentials, so pointing one at a
    // new host needs the same confirmation as the relay toggle. Clearing it restores the fixed
    // provider destination and does not. Both sides are normalized, so a hand-edited spelling of
    // the saved gateway that the client echoes back is not a change. An invalid value counts as
    // one; the route refuses it before asking.
    private static bool PointsGatewayElsewhere(string? requested, string saved) =>
        !string.IsNullOrWhiteSpace(requested)
        && (!EnterpriseNetworkProxy.TryNormalize(requested, out var normalized)
            || !string.Equals(normalized,
                EnterpriseNetworkProxy.TryNormalize(saved, out var savedNormalized) ? savedNormalized : saved,
                StringComparison.Ordinal));

    internal static bool EnterpriseNetworkProxiesAreValid(AppSettingsDto request) =>
        EnterpriseNetworkProxy.TryNormalize(request.CodexEnterpriseNetworkProxy, out _)
        && EnterpriseNetworkProxy.TryNormalize(request.ClaudeEnterpriseNetworkProxy, out _)
        && EnterpriseNetworkProxy.TryNormalize(request.OpenCodeEnterpriseNetworkProxy, out _)
        && EnterpriseNetworkProxy.TryNormalize(request.GrokEnterpriseNetworkProxy, out _);

    private static void ValidateGatewayCredentials(AppSettingsDto request, Settings saved)
    {
        Validate(request.CodexGatewayBearerToken, request.ClearCodexGatewayBearerToken,
            request.CodexEnterpriseNetworkProxy ?? saved.CodexEnterpriseNetworkProxy,
            request.CodexGatewayAuthMode ?? saved.CodexGatewayAuthMode,
            request.CodexGatewayApiKeyHeader ?? saved.CodexGatewayApiKeyHeader);
        Validate(request.ClaudeGatewayBearerToken, request.ClearClaudeGatewayBearerToken,
            request.ClaudeEnterpriseNetworkProxy ?? saved.ClaudeEnterpriseNetworkProxy,
            request.ClaudeGatewayAuthMode ?? saved.ClaudeGatewayAuthMode,
            request.ClaudeGatewayApiKeyHeader ?? saved.ClaudeGatewayApiKeyHeader);

        static void Validate(string? token, bool? clear, string? gateway, string mode, string header)
        {
            // The header matters only in API-key mode. The form always sends its hidden header
            // field, so a half-typed name left behind in Bearer mode must not block every save.
            if (EnterpriseGatewayCredentials.NormalizeMode(mode) == EnterpriseGatewayCredentials.ApiKeyMode)
                EnterpriseGatewayCredentials.NormalizeApiKeyHeader(header);
            if (clear == true) return;
            if (EnterpriseGatewayCredentials.NormalizeSecret(token, mode).Length > 0 && string.IsNullOrWhiteSpace(gateway))
                throw new ArgumentException("Enter and save a gateway URL with its credential.");
        }
    }

    // The internal store parameter is solely for isolated regression fixtures. Production calls
    // always use Config.Store and therefore the normal application settings path.
    internal static AppSettingsDto UpdateSettings(AppSettingsDto settingsDto, SettingsFile store, IRemoteHttpRelayClient? relay = null)
    {
        // Validate every supplied URL before changing or saving any settings.
        var codexProxy = settingsDto.CodexEnterpriseNetworkProxy is null ? null : EnterpriseNetworkProxy.Normalize(settingsDto.CodexEnterpriseNetworkProxy);
        var claudeProxy = settingsDto.ClaudeEnterpriseNetworkProxy is null ? null : EnterpriseNetworkProxy.Normalize(settingsDto.ClaudeEnterpriseNetworkProxy);
        var openCodeProxy = settingsDto.OpenCodeEnterpriseNetworkProxy is null ? null : EnterpriseNetworkProxy.Normalize(settingsDto.OpenCodeEnterpriseNetworkProxy);
        var grokProxy = settingsDto.GrokEnterpriseNetworkProxy is null ? null : EnterpriseNetworkProxy.Normalize(settingsDto.GrokEnterpriseNetworkProxy);
        using (store.AcquireWriteLock())
        {
            // LoadFresh, not Load: this handler writes the WHOLE Settings object back, including
            // fields the UI doesn't expose (e.g. the hand-editable token-saver flags). Merging over
            // the process's cached copy would silently revert any settings.json edit made since
            // the cache was filled — including flipping the token-saver kill switch back on.
            var settings = store.LoadFresh();
            ValidateGatewayCredentials(settingsDto, settings);
            // Use the same locked snapshot for PIN readiness and the settings write.
            var remoteAccess = settingsDto.RemoteAccess
                && !string.IsNullOrWhiteSpace(settings.PinHash) && !string.IsNullOrWhiteSpace(settings.PinSalt);
            var previousApiKey = settings.ApiKey;
            var previousRelaySetting = settings.RouteThroughVibeRailsAi;
            var previousCodexGateway = settings.CodexEnterpriseNetworkProxy;
            var previousClaudeGateway = settings.ClaudeEnterpriseNetworkProxy;

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
            // Local relays are always on. Preserve retired switch values for older versions and
            // ignore switch fields sent by a cached client. Authentication modes remain editable.
            if (settingsDto.CodexLlmProxyMode is not null)
                settings.CodexLlmProxyMode = CodexLlmProxySettings.NormalizeMode(settingsDto.CodexLlmProxyMode);
            if (settingsDto.GrokLlmProxyMode is not null)
                settings.GrokLlmProxyMode = LlmProxyCliChatConfig.NormalizeMode(settingsDto.GrokLlmProxyMode);
            if (codexProxy is not null) settings.CodexEnterpriseNetworkProxy = codexProxy;
            if (claudeProxy is not null) settings.ClaudeEnterpriseNetworkProxy = claudeProxy;
            if (openCodeProxy is not null) settings.OpenCodeEnterpriseNetworkProxy = openCodeProxy;
            if (grokProxy is not null) settings.GrokEnterpriseNetworkProxy = grokProxy;
            if (settingsDto.CodexGatewayAuthMode is not null)
                settings.CodexGatewayAuthMode = EnterpriseGatewayCredentials.NormalizeMode(settingsDto.CodexGatewayAuthMode);
            if (settingsDto.ClaudeGatewayAuthMode is not null)
                settings.ClaudeGatewayAuthMode = EnterpriseGatewayCredentials.NormalizeMode(settingsDto.ClaudeGatewayAuthMode);
            if (settingsDto.CodexGatewayApiKeyHeader is not null && UsesApiKeyHeader(settings.CodexGatewayAuthMode))
                settings.CodexGatewayApiKeyHeader = EnterpriseGatewayCredentials.NormalizeApiKeyHeader(settingsDto.CodexGatewayApiKeyHeader);
            if (settingsDto.ClaudeGatewayApiKeyHeader is not null && UsesApiKeyHeader(settings.ClaudeGatewayAuthMode))
                settings.ClaudeGatewayApiKeyHeader = EnterpriseGatewayCredentials.NormalizeApiKeyHeader(settingsDto.ClaudeGatewayApiKeyHeader);
            if (settingsDto.ClaudeGatewaySupportsToolSearch.HasValue)
                settings.ClaudeGatewaySupportsToolSearch = settingsDto.ClaudeGatewaySupportsToolSearch.Value;
            // Codex's API auth while a credential is saved is derived (LlmProxySettingsService), not
            // stored, so removing the credential returns a subscription user to their own login.
            settings.CodexGatewayBearerToken = MergeGatewayToken(settings.CodexGatewayBearerToken,
                settingsDto.CodexGatewayBearerToken, settingsDto.ClearCodexGatewayBearerToken, settings.CodexGatewayAuthMode,
                SameGatewayOrigin(previousCodexGateway, settings.CodexEnterpriseNetworkProxy));
            settings.ClaudeGatewayBearerToken = MergeGatewayToken(settings.ClaudeGatewayBearerToken,
                settingsDto.ClaudeGatewayBearerToken, settingsDto.ClearClaudeGatewayBearerToken, settings.ClaudeGatewayAuthMode,
                SameGatewayOrigin(previousClaudeGateway, settings.ClaudeEnterpriseNetworkProxy));
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
            true,
            CodexLlmProxySettings.NormalizeMode(settings.CodexLlmProxyMode),
            true,
            true,
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
            true,
            LlmProxyCliChatConfig.NormalizeMode(settings.GrokLlmProxyMode),
            settings.GrokTokenSaverEnabled ?? settings.OpenCodeTokenSaverEnabled ?? settings.ClaudeTokenSaverEnabled,
            DataExportOptIn: true,
            RemoteAccountEmail: ApiKeyStore.GetAccountEmail(settings),
            CreateVibeStoryTracking: settings.CreateVibeStoryTracking,
            CreateVibeStoryTrackingCustomEnvs: settings.CreateVibeStoryTrackingCustomEnvs,
            CodexEnterpriseNetworkProxy: settings.CodexEnterpriseNetworkProxy,
            ClaudeEnterpriseNetworkProxy: settings.ClaudeEnterpriseNetworkProxy,
            OpenCodeEnterpriseNetworkProxy: settings.OpenCodeEnterpriseNetworkProxy,
            GrokEnterpriseNetworkProxy: settings.GrokEnterpriseNetworkProxy,
            HasCodexGatewayBearerToken: !string.IsNullOrEmpty(settings.CodexGatewayBearerToken),
            HasClaudeGatewayBearerToken: !string.IsNullOrEmpty(settings.ClaudeGatewayBearerToken),
            CodexInheritedApiBaseUrl: SafeInheritedBase("OPENAI_BASE_URL", null),
            ClaudeInheritedApiBaseUrl: SafeInheritedBase(LlmProxyClaudeConfig.BaseUrlVariable, LlmProxyUpstream.ClaudeVariable),
            CodexGatewayAuthMode: settings.CodexGatewayAuthMode,
            CodexGatewayApiKeyHeader: settings.CodexGatewayApiKeyHeader,
            ClaudeGatewayAuthMode: settings.ClaudeGatewayAuthMode,
            ClaudeGatewayApiKeyHeader: settings.ClaudeGatewayApiKeyHeader,
            ClaudeGatewaySupportsToolSearch: settings.ClaudeGatewaySupportsToolSearch,
            CodexInheritedBaseBypassesRelay: InheritedBaseBypassesRelay("OPENAI_BASE_URL", null),
            ClaudeInheritedBaseBypassesRelay: InheritedBaseBypassesRelay(LlmProxyClaudeConfig.BaseUrlVariable, LlmProxyUpstream.ClaudeVariable)
        );
    }

    // A saved credential belongs to the gateway it was entered for. Blank input keeps it only while
    // the gateway stays on that origin; a new host, scheme or port, or a cleared URL, drops it
    // unless the same save supplies a replacement, so it is never sent somewhere new.
    private static string MergeGatewayToken(string saved, string? supplied, bool? clear, string mode, bool sameGateway) =>
        clear == true ? "" : !string.IsNullOrWhiteSpace(supplied) ? EnterpriseGatewayCredentials.NormalizeSecret(supplied, mode)
            : sameGateway ? saved : "";

    /// <summary>
    /// True when both values name the same gateway origin (scheme, host and port), or are the same
    /// blank or unchanged value. A path change on the same origin keeps the credential.
    /// </summary>
    internal static bool SameGatewayOrigin(string? previous, string? current)
    {
        if (string.Equals((previous ?? "").Trim(), (current ?? "").Trim(), StringComparison.Ordinal)) return true;
        return Origin(previous) is { } before && Origin(current) is { } after
            && string.Equals(before, after, StringComparison.OrdinalIgnoreCase);

        static string? Origin(string? value) =>
            EnterpriseNetworkProxy.TryNormalize(value, out var normalized) && normalized.Length > 0
                ? new Uri(normalized).GetLeftPart(UriPartial.Authority) : null;
    }

    private static bool UsesApiKeyHeader(string mode) =>
        EnterpriseGatewayCredentials.NormalizeMode(mode) == EnterpriseGatewayCredentials.ApiKeyMode;

    private static string? SafeInheritedBase(string variable, string? capturedVariable)
    {
        var value = LlmProxySettingsService.ResolveGateway("", Environment.GetEnvironmentVariable(variable),
            capturedVariable is null ? null : Environment.GetEnvironmentVariable(capturedVariable));
        // Never return an invalid URL that might contain embedded credentials to the browser.
        return !string.IsNullOrEmpty(value) && EnterpriseNetworkProxy.TryNormalize(value, out var normalized)
            ? normalized : null;
    }

    // Like SafeInheritedBase, describes the inherited fallback even while a saved URL masks it.
    // That lets the UI preview clearing a saved gateway without exposing the rejected URL.
    private static bool InheritedBaseBypassesRelay(string variable, string? capturedVariable)
    {
        var inherited = Environment.GetEnvironmentVariable(variable)?.Trim();
        var resolved = LlmProxySettingsService.ResolveGateway("", inherited,
            capturedVariable is null ? null : Environment.GetEnvironmentVariable(capturedVariable));
        return !string.IsNullOrEmpty(inherited)
            && string.Equals(resolved, inherited, StringComparison.Ordinal)
            && !EnterpriseNetworkProxy.TryNormalize(inherited, out _);
    }

    private static string NormalizeComputerName(string? value) =>
        ComputerNameFormatter.Normalize(value);

    internal static bool ResolveHttpRelaySetting(
        bool storedValue,
        bool? requestedValue,
        string? finalApiKey) =>
        !string.IsNullOrWhiteSpace(finalApiKey) && (requestedValue ?? storedValue);

}
