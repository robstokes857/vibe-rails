using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Routes;

public static class SessionRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/sessions/{sessionId}/logs", async (
            IDbService dbService,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            var result = await dbService.GetSessionWithLogsAsync(sessionId, cancellationToken);
            if (result == null)
            {
                return Results.NotFound(new ErrorResponse($"Session not found: {sessionId}"));
            }
            return Results.Ok(result);
        }).WithName("GetSessionLogs");

        app.MapGet("/api/v1/sessions/recent", async (
            IDbService dbService,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var sessions = await dbService.GetRecentSessionsAsync(limit ?? 10, cancellationToken);
            return Results.Ok(sessions);
        }).WithName("GetRecentSessions");
    }
}
