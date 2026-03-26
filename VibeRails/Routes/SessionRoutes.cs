using VibeRails.DTOs;
using VibeRails.DB;

namespace VibeRails.Routes;

public static class SessionRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/sessions/{sessionId}/logs", async (
            IRepository repository,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            var result = await repository.GetSessionWithLogsAsync(sessionId, cancellationToken);
            if (result == null)
            {
                return Results.NotFound(new ErrorResponse($"Session not found: {sessionId}"));
            }
            return Results.Ok(result);
        }).WithName("GetSessionLogs");

        app.MapGet("/api/v1/sessions/recent", async (
            IRepository repository,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var sessions = await repository.GetRecentSessionsAsync(limit ?? 10, cancellationToken);
            return Results.Ok(sessions);
        }).WithName("GetRecentSessions");

        app.MapGet("/api/v1/sessions/{sessionId}/output", async (
            IRepository repository,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            var result = await repository.GetSessionOutputAsync(sessionId, cancellationToken);
            if (result == null)
            {
                return Results.NotFound(new ErrorResponse($"Session not found: {sessionId}"));
            }

            return Results.Ok(result);
        }).WithName("GetSessionOutput");
    }
}
