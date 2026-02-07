using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.Routes;

public static class UpdateRoutes
{
    public static void Map(WebApplication app)
    {
        // GET /api/v1/update/check - Check for available updates
        app.MapGet("/api/v1/update/check", async (
            UpdateService updateService,
            CancellationToken cancellationToken) =>
        {
            var updateInfo = await updateService.CheckForUpdateAsync(cancellationToken);
            return Results.Ok(updateInfo);
        }).WithName("CheckForUpdate");

        // GET /api/v1/update/version - Get current version
        app.MapGet("/api/v1/update/version", () =>
        {
            return Results.Ok(new VersionResponse(VersionInfo.Version));
        }).WithName("GetVersion");

        // GET /api/v1/version - API version endpoint
        app.MapGet("/api/v1/version", () =>
        {
            return Results.Ok(new ApiVersionResponse("v1", VersionInfo.Version));
        }).WithName("GetApiVersion");

        // POST /api/v1/update/install - Trigger update installation
        app.MapPost("/api/v1/update/install", async (
            UpdateInstaller updateInstaller,
            IHostApplicationLifetime lifetime,
            CancellationToken cancellationToken) =>
        {
            var success = await updateInstaller.InstallUpdateAsync(cancellationToken);
            if (success)
            {
                // Trigger graceful shutdown
                lifetime.StopApplication();
                return Results.Ok(new MessageResponse("Update installation started. Application shutting down..."));
            }
            return Results.BadRequest(new MessageResponse("Failed to start update installation"));
        }).WithName("InstallUpdate");
    }
}
