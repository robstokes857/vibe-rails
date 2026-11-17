using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Auth;
using VibeRails.Utils;

namespace VibeRails.Routes;

public static class PinRoutes
{
    public static void Map(WebApplication app) => Map(app, Config.Store);

    internal static void Map(WebApplication app, SettingsFile store)
    {
        // Keep the legacy PIN routes/JSON names for older clients.
        // GET /api/v1/settings/pin/status - check whether a credential is currently set
        app.MapGet("/api/v1/settings/pin/status", () =>
        {
            return Results.Ok(new PinStatusResponse(RemoteConfig.HasCredential(store.LoadFresh())));
        }).WithName("GetPinStatus");

        // POST /api/v1/settings/pin - set (or replace) the password
        app.MapPost("/api/v1/settings/pin", (SetPinRequest request, SettingsPresenceService presence) =>
        {
            if (!RemoteConfig.IsSettableCredential(request.Pin))
                return Results.BadRequest(new ErrorResponse(
                    "Password must be between 1 and 128 characters and not only spaces."));

            using var lease = store.AcquireWriteLock();
            if (!presence.Verify(request.PresenceProof, store.LoadFresh())) return Results.StatusCode(403);
            RemoteConfig.SetPin(request.Pin, store);
            return Results.Ok(new PinStatusResponse(true));
        }).WithName("SetPin");

        // DELETE /api/v1/settings/pin - clear the credential
        app.MapDelete("/api/v1/settings/pin", ([Microsoft.AspNetCore.Mvc.FromBody] SetPinRequest request, SettingsPresenceService presence) =>
        {
            using var lease = store.AcquireWriteLock();
            if (!presence.Verify(request.PresenceProof, store.LoadFresh())) return Results.StatusCode(403);
            RemoteConfig.ClearPin(store);
            return Results.Ok(new PinStatusResponse(false));
        }).WithName("ClearPin");
    }
}
