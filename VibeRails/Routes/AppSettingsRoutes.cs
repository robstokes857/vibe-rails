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
            try
            {
                var configPath = Path.Combine(AppContext.BaseDirectory, "app_config.json");
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize(json,
                        AppJsonSerializerContext.Default.AppConfiguration)
                        ?? new AppConfiguration();

                    return Results.Ok(new AppSettingsDto(
                        config.RemoteAccess,
                        config.ApiKey
                    ));
                }

                return Results.Ok(new AppSettingsDto(false, ""));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to read settings: {ex.Message}"));
            }
        }).WithName("GetAppSettings");

        // POST /api/v1/settings - Update app settings
        app.MapPost("/api/v1/settings", (AppSettingsDto settings) =>
        {
            try
            {
                var configPath = Path.Combine(AppContext.BaseDirectory, "app_config.json");

                // Read existing config to preserve all other fields
                AppConfiguration config;
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    config = JsonSerializer.Deserialize(json,
                        AppJsonSerializerContext.Default.AppConfiguration)
                        ?? new AppConfiguration();
                }
                else
                {
                    config = new AppConfiguration();
                }

                // Update only the settings fields
                config.RemoteAccess = settings.RemoteAccess;
                config.ApiKey = settings.ApiKey;

                // Write back to file
                var updatedJson = JsonSerializer.Serialize(config,
                    AppJsonSerializerContext.Default.AppConfiguration);
                File.WriteAllText(configPath, updatedJson);

                // Update static Configs so runtime reflects the change immediately
                Configs.SetRemoteAccess(settings.RemoteAccess);
                Configs.SetApiKey(settings.ApiKey);

                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to save settings: {ex.Message}"));
            }
        }).WithName("UpdateAppSettings");
    }
}
