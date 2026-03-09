using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services;
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
            return Results.Ok(new AppSettingsDto(
                settings.RemoteAccess,
                settings.ApiKey,
                settings.EnablePrerelease
            ));
        }).WithName("GetAppSettings");

        // POST /api/v1/settings - Update app settings
        app.MapPost("/api/v1/settings", (AppSettingsDto settingsDto) =>
        {
            // Remote access requires a PIN — if none is set, force it off
            var remoteAccess = settingsDto.RemoteAccess && RemoteConfig.IsPinConfigured;

            // Load existing settings from settings.json
            var settings = Config.Load();

            // Update only the RemoteAccess, ApiKey, and EnablePrerelease fields
            settings.RemoteAccess = remoteAccess;
            settings.ApiKey = settingsDto.ApiKey;
            settings.EnablePrerelease = settingsDto.EnablePrerelease;

            // Save back to settings.json
            Config.Save(settings);

            // Update static Configs so runtime reflects the change immediately
            ParserConfigs.SetRemoteAccess(remoteAccess);
            ParserConfigs.SetApiKey(settingsDto.ApiKey);
            ParserConfigs.SetEnablePrerelease(settingsDto.EnablePrerelease);
            return Results.Ok(settingsDto with { RemoteAccess = remoteAccess });
        }).WithName("UpdateAppSettings");
    }
}
