using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using VibeRails.Utils;

namespace VibeRails.Routes;

public static class AppSettingsRoutes
{
    public static void Map(WebApplication app)
    {
        // GET /api/v1/settings - Read current app settings
        app.MapGet("/api/v1/settings", () =>
        {
            var settings = Config.Load();
            var maskedKey = string.IsNullOrEmpty(settings.ApiKey)
                ? ""
                : settings.ApiKey.Length <= 4
                    ? new string('•', settings.ApiKey.Length)
                    : new string('•', settings.ApiKey.Length - 4) + settings.ApiKey[^4..];
            return Results.Ok(new AppSettingsDto(
                settings.RemoteAccess,
                maskedKey,
                settings.EnablePrerelease,
                settings.DeveloperOptions,
                settings.UseVsCodeTheme,
                settings.McpEnabled
            ));
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

            return Results.Ok(settingsDto with
            {
                RemoteAccess = remoteAccess
            });
        }).WithName("UpdateAppSettings");
    }
}
