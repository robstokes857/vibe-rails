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
        app.MapGet("/api/v1/token-savings", async (ITokenSavingsStore store) =>
        {
            // Read the shared table before answering. This process serves the dashboard but does
            // not proxy anything — every tab's savings are recorded by that tab's own child
            // process — so its unrefreshed totals would report a session that never moves.
            await store.RefreshAsync();
            var totals = store.GetTotals();
            return Results.Ok(new TokenSavingsDto(
                totals.BytesBefore,
                totals.BytesAfter,
                totals.BytesSaved,
                totals.TokensSaved,
                totals.SessionTokensSaved,
                totals.MonthTokensSaved));
        }).WithName("GetTokenSavings");
    }
}
