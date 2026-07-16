using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.AgentTools;
using VibeRails.Services.LlmProxy;

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
                totals.TokensSaved,
                totals.SessionTokensSaved,
                totals.MonthTokensSaved));
        }).WithName("GetTokenSavings");

        // This endpoint is consumed by the root process's terminal-tab proxy routes. It only
        // exists meaningfully inside a tab child, where CurrentTabId is set and the singleton
        // ITabTokenSaverState belongs to that one child process.
        app.MapGet("/api/v1/token-savings/tab", (
            ILocalToolApiContext toolApiContext,
            ITabTokenSaverState state) =>
        {
            if (string.IsNullOrWhiteSpace(toolApiContext.CurrentTabId))
                return Results.NotFound(new ErrorResponse("Per-tab token savings are available only in terminal tabs."));

            return Results.Ok(new TabTokenSaverStateResponse(state.Enabled));
        }).WithName("GetTabTokenSaverState");

        app.MapPut("/api/v1/token-savings/tab", (
            TabTokenSaverStateRequest request,
            ILocalToolApiContext toolApiContext,
            ITabTokenSaverState state) =>
        {
            if (string.IsNullOrWhiteSpace(toolApiContext.CurrentTabId))
                return Results.NotFound(new ErrorResponse("Per-tab token savings are available only in terminal tabs."));

            state.Enabled = request.Enabled;
            return Results.Ok(new TabTokenSaverStateResponse(state.Enabled));
        }).WithName("SetTabTokenSaverState");
    }
}
