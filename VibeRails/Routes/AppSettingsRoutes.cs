using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails.Routes;

public static class AppSettingsRoutes
{
    private const int MaxComputerNameLength = 80;

    public static void Map(WebApplication app)
    {
        // GET /api/v1/settings - Read current app settings
        app.MapGet("/api/v1/settings", () =>
        {
            return Results.Ok(BuildAppSettingsDto(Config.Load()));
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

            // Empty apiKey means "unchanged" (masked value was not edited). Also reject the masked
            // placeholder itself (bullet chars, U+2022) so a client that echoes it back can't
            // overwrite the real key with dots.
            var apiKeyProvided = !string.IsNullOrEmpty(settingsDto.ApiKey)
                && !settingsDto.ApiKey.Contains('•');

            // Update only the app settings fields exposed by the UI
            settings.RemoteAccess = remoteAccess;
            if (apiKeyProvided)
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
            // Same stale-client guard, with one extra wrinkle: null here means "key absent, leave
            // the stored selection alone", but an EMPTY list is a real choice (every stage off)
            // and must be written through untouched. Collapsing the two would make "turn
            // everything off" silently save as "never configured" — which resolves back to the
            // catalog defaults on the next request. Ids are persisted as sent rather than
            // filtered against the catalog: unknown ids are already ignored at Resolve time, so
            // dropping them here would only destroy a newer build's selection.
            if (settingsDto.TokenSaverStages is not null)
                settings.TokenSaverStages = [.. settingsDto.TokenSaverStages];
            if (settingsDto.TokenSaverCaptureEnabled.HasValue)
                settings.TokenSaverCaptureEnabled = settingsDto.TokenSaverCaptureEnabled.Value;

            // Save back to settings.json
            Config.Save(settings);

            // Update static Configs so runtime reflects the change immediately
            ParserConfigs.SetRemoteAccess(remoteAccess);
            if (apiKeyProvided)
                ParserConfigs.SetApiKey(settingsDto.ApiKey!);
            ParserConfigs.SetUseVsCodeTheme(settingsDto.UseVsCodeTheme);
            ParserConfigs.SetMcpEnabled(true);

            return Results.Ok(BuildAppSettingsDto(settings));
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
            return Results.Ok(BuildAppSettingsDto(settings));
        }).WithName("UpdateComputerName");
    }

    private static AppSettingsDto BuildAppSettingsDto(Settings settings)
    {
        var maskedKey = string.IsNullOrEmpty(settings.ApiKey)
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
            // Passed through as-is, null included: null means "never configured", which the client
            // resolves to the catalog's defaultSelection exactly as CompressionCatalog.Resolve
            // does server-side. Substituting the defaults here would make the two states
            // indistinguishable to the UI.
            settings.TokenSaverStages,
            settings.TokenSaverCaptureEnabled,
            GetMachineName()
        );
    }

    private static string NormalizeComputerName(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length <= MaxComputerNameLength)
            return trimmed;

        // Truncate by UTF-16 code units, but don't leave a dangling high surrogate
        // (which would render as a replacement char) if the cut splits a pair.
        var cut = char.IsHighSurrogate(trimmed[MaxComputerNameLength - 1])
            ? MaxComputerNameLength - 1
            : MaxComputerNameLength;
        return trimmed[..cut];
    }

    private static string GetMachineName()
    {
        try
        {
            return NormalizeComputerName(Environment.MachineName);
        }
        catch
        {
            return string.Empty;
        }
    }
}
