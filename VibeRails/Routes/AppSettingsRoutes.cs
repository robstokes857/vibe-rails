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
                settings.ApiKey
            ));
        }).WithName("GetAppSettings");

        // POST /api/v1/settings - Update app settings
        app.MapPost("/api/v1/settings", (AppSettingsDto settingsDto) =>
        {
            // Load existing settings from settings.json
            var settings = Config.Load();

            // Update only the RemoteAccess and ApiKey fields
            settings.RemoteAccess = settingsDto.RemoteAccess;
            settings.ApiKey = settingsDto.ApiKey;

            // Save back to settings.json
            Config.Save(settings);

            // Update static Configs so runtime reflects the change immediately
            ParserConfigs.SetRemoteAccess(settingsDto.RemoteAccess);
            ParserConfigs.SetApiKey(settingsDto.ApiKey);

            return Results.Ok(settingsDto);
        }).WithName("UpdateAppSettings");
    }
}
