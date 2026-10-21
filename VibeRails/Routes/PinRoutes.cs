using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Auth;
using VibeRails.Utils;

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
        app.MapPost("/api/v1/settings/pin", (SetPinRequest request, SettingsPresenceService presence) =>
        {
            if (request.Pin is not { Length: >= 4 and <= 6 } || !request.Pin.All(char.IsAsciiDigit))
                return Results.BadRequest(new ErrorResponse("PIN must be 4 to 6 digits."));

            using var lease = Config.Store.AcquireWriteLock();
            if (!presence.Verify(request.PresenceProof, Config.LoadFresh())) return Results.StatusCode(403);
            RemoteConfig.SetPin(request.Pin);
            return Results.Ok(new PinStatusResponse(true));
        }).WithName("SetPin");

        // DELETE /api/v1/settings/pin - clear the PIN
        app.MapDelete("/api/v1/settings/pin", ([Microsoft.AspNetCore.Mvc.FromBody] SetPinRequest request, SettingsPresenceService presence) =>
        {
            using var lease = Config.Store.AcquireWriteLock();
            if (!presence.Verify(request.PresenceProof, Config.LoadFresh())) return Results.StatusCode(403);
            RemoteConfig.ClearPin();
            return Results.Ok(new PinStatusResponse(false));
        }).WithName("ClearPin");
    }
}
