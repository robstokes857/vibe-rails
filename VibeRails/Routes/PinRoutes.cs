using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.Routes;

public static class PinRoutes
{
    public static void Map(WebApplication app)
    {
        // GET /api/v1/settings/pin/status - check whether a PIN is currently set
        app.MapGet("/api/v1/settings/pin/status", () =>
        {
            return Results.Ok(new PinStatusResponse(RemoteConfig.IsPinConfigured));
        }).WithName("GetPinStatus");

        // POST /api/v1/settings/pin - set (or replace) the PIN
        app.MapPost("/api/v1/settings/pin", (SetPinRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Pin) || request.Pin.Length < 4)
                return Results.BadRequest(new ErrorResponse("PIN must be at least 4 characters."));

            RemoteConfig.SetPin(request.Pin);
            return Results.Ok(new PinStatusResponse(true));
        }).WithName("SetPin");

        // DELETE /api/v1/settings/pin - clear the PIN
        app.MapDelete("/api/v1/settings/pin", () =>
        {
            RemoteConfig.ClearPin();
            return Results.Ok(new PinStatusResponse(false));
        }).WithName("ClearPin");
    }
}
