using Microsoft.Data.Sqlite;
using VibeRails.Data.Replay;
using VibeRails.Services.Board;
using VibeRails.Services.SessionReplay;

namespace VibeRails.Routes;

/// <summary>Recording reads on the root host's existing session-plus-tab credential boundary.</summary>
public static class SessionReplayRoutes
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v1/session-replay");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (Exception error) when (error is SqliteException or IOException or InvalidDataException)
            {
                return Results.Json(new ApiError("The recording could not be read. Try reloading the snapshot."),
                    ReplayJson.Default.ApiError, statusCode: 503);
            }
        });
        group.MapGet("/status", (IReplayStore store) => Results.Json(store.Status(), ReplayJson.Default.AppStatus));
        group.MapGet("/sessions", (IReplayStore store, string? q, int? offset) =>
            Results.Json(store.Sessions(q, Math.Max(0, offset ?? 0)), ReplayJson.Default.SessionPage));
        group.MapGet("/sessions/{id}", async (IReplayStore store, IBoardStore board, string id, CancellationToken cancellationToken) =>
        {
            if (store.Manifest(id) is not { } manifest) return Results.NotFound();
            var cards = await board.GetSessionCardsAsync([id], cancellationToken);
            manifest = manifest with { Cards = cards.Select(card => new BoardLink(card.DisplayId ?? card.Key, card.Title, "")).ToList() };
            return Results.Json(manifest, ReplayJson.Default.Manifest);
        });
        group.MapGet("/sessions/{id}/frames", (IReplayStore store, string id, long after, long max, string source) =>
            after < 0 || max < 0 || source is not ("raw" or "enriched") ? Results.BadRequest() :
            Results.Json(store.Frames(id, after, max, source), ReplayJson.Default.FramePage));
        group.MapGet("/sessions/{id}/exchanges", (IReplayStore store, string id, long after, long max) =>
            after < 0 || max < 0 ? Results.BadRequest() :
            Results.Json(store.Exchanges(id, after, max), ReplayJson.Default.ExchangePage));
        group.MapGet("/sessions/{id}/changes/{changeId:long}", (IReplayStore store, string id, long changeId) =>
            store.Diff(id, changeId) is { } result ? Results.Json(result, ReplayJson.Default.DiffDetail) : Results.NotFound());
        group.MapGet("/sessions/{id}/exchanges/{exchangeId}", (IReplayStore store, string id, string exchangeId) =>
            store.ExchangeDetail(id, exchangeId) is { } result ? Results.Json(result, ReplayJson.Default.ExchangeDetail) : Results.NotFound());
    }
}
