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
                new ChatHistorySettingsDto(LLMParser.Normalize(settings.ChatHistorySettings?.ProcessingLlm))
            ));
        }).WithName("GetAppSettings");

        // POST /api/v1/settings - Update app settings
        app.MapPost("/api/v1/settings", (AppSettingsDto settingsDto) =>
        {
            // Remote access requires a PIN — if none is set, force it off
            var remoteAccess = settingsDto.RemoteAccess && RemoteConfig.IsPinConfigured;

            // Load existing settings from settings.json
            var settings = Config.Load();

            // Update only the app settings fields exposed by the UI
            settings.RemoteAccess = remoteAccess;
            // Empty apiKey means "unchanged" (masked value was not edited)
            if (!string.IsNullOrEmpty(settingsDto.ApiKey))
                settings.ApiKey = settingsDto.ApiKey;
            settings.EnablePrerelease = settingsDto.EnablePrerelease;
            settings.DeveloperOptions = settingsDto.DeveloperOptions;
            settings.ChatHistorySettings ??= new ChatHistorySettings();
            if (settingsDto.ChatHistorySettings is not null)
                settings.ChatHistorySettings.ProcessingLlm = LLMParser.Normalize(settingsDto.ChatHistorySettings.ProcessingLlm);

            // Save back to settings.json
            Config.Save(settings);

            // Update static Configs so runtime reflects the change immediately
            ParserConfigs.SetRemoteAccess(remoteAccess);
            if (!string.IsNullOrEmpty(settingsDto.ApiKey))
                ParserConfigs.SetApiKey(settingsDto.ApiKey);
            ParserConfigs.SetEnablePrerelease(settingsDto.EnablePrerelease);
            ParserConfigs.SetDeveloperOptions(settingsDto.DeveloperOptions);
            return Results.Ok(settingsDto with
            {
                RemoteAccess = remoteAccess,
                ChatHistorySettings = new ChatHistorySettingsDto(settings.ChatHistorySettings.ProcessingLlm)
            });
        }).WithName("UpdateAppSettings");
    }
}
