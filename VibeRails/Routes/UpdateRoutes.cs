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

    }
}
