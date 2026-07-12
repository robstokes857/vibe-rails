using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Routes;

/// <summary>
/// Serves the token-saver tally for the UI's initial paint; live updates after that ride the
/// <c>proxy_activity</c> WebSocket pings. Deliberately not part of the settings DTO — savings is
/// telemetry, not a setting, and must never round-trip through the settings POST.
/// </summary>
public static class TokenSavingsRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/token-savings", (ITokenSavingsStore store) =>
        {
            var totals = store.GetTotals();
            return Results.Ok(new TokenSavingsDto(
                totals.BytesBefore,
                totals.BytesAfter,
                totals.BytesSaved,
                totals.TokensSaved));
        }).WithName("GetTokenSavings");
    }
}
