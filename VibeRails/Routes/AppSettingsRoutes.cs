using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
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
        app.MapPost("/api/v1/settings", async (AppSettingsDto settingsDto, IGlobalCache globalCache) =>
        {
            // Remote access requires a PIN — if none is set, force it off
            var remoteAccess = settingsDto.RemoteAccess && RemoteConfig.IsPinConfigured;

            // Load existing settings from settings.json
            var settings = Config.Load();
            var mcpWasEnabled = settings.McpEnabled;

            // Update only the app settings fields exposed by the UI
            settings.RemoteAccess = remoteAccess;
            // Empty apiKey means "unchanged" (masked value was not edited)
            if (!string.IsNullOrEmpty(settingsDto.ApiKey))
                settings.ApiKey = settingsDto.ApiKey;
            settings.EnablePrerelease = settingsDto.EnablePrerelease;
            settings.DeveloperOptions = settingsDto.DeveloperOptions;
            settings.UseVsCodeTheme = settingsDto.UseVsCodeTheme;
            settings.McpEnabled = settingsDto.McpEnabled;
            // Store the raw name (blank allowed). The machine-name default is resolved
            // at use (push notification) — never persisted — so a blank value keeps
            // tracking the live machine name and the field stays clearable.
            settings.ComputerName = NormalizeComputerName(settingsDto.ComputerName ?? settings.ComputerName);

            // Save back to settings.json
            Config.Save(settings);

            // Update static Configs so runtime reflects the change immediately
            ParserConfigs.SetRemoteAccess(remoteAccess);
            if (!string.IsNullOrEmpty(settingsDto.ApiKey))
                ParserConfigs.SetApiKey(settingsDto.ApiKey);
            ParserConfigs.SetEnablePrerelease(settingsDto.EnablePrerelease);
            ParserConfigs.SetDeveloperOptions(settingsDto.DeveloperOptions);
            ParserConfigs.SetUseVsCodeTheme(settingsDto.UseVsCodeTheme);
            ParserConfigs.SetMcpEnabled(settingsDto.McpEnabled);

            // Opting IN to MCP: clear the per-CLI "already removed" record so that the next
            // time the user opts out, each CLI's one-shot `mcp remove` fires again. The add
            // itself happens at session launch (see CommandService); we only reset bookkeeping.
            if (settingsDto.McpEnabled && !mcpWasEnabled)
            {
                foreach (var cli in CommandService.McpClis)
                    await globalCache.SetAsync(GlobalCacheKeys.McpRemovedFromCli(cli), "false");
            }

            return Results.Ok(BuildAppSettingsDto(settings));
        }).WithName("UpdateAppSettings");

        // POST /api/v1/settings/computer-name - Update ONLY the notification computer
        // name. Loads the current settings server-side and touches a single field, so
        // a save from the terminal panel can never overwrite unrelated settings with a
        // stale client-side copy (remoteAccess, mcpEnabled, theme, …).
        app.MapPost("/api/v1/settings/computer-name", (UpdateComputerNameDto dto) =>
        {
            var settings = Config.Load();
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
            settings.EnablePrerelease,
            settings.DeveloperOptions,
            settings.UseVsCodeTheme,
            settings.McpEnabled,
            NormalizeComputerName(settings.ComputerName),
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
