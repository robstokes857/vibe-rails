using System.Text.Json;
using VibeRails.Services.Board.Sharing;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

public static partial class BoardRoutes
{
    internal static void MapCardSharing(WebApplication app)
    {
        // Inherits BoardRoutes' active-root registration and both local credentials in middleware.
        var group = app.MapGroup("/api/v1/board/cards/{card}/sharing-links");
        group.AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
            return next(context);
        });
        // Domain errors stay HTTP 200: a remote 401 must not trigger the SPA bootstrap/retry POST.
        group.MapGet("", async (string card, int? before, [Microsoft.AspNetCore.Mvc.FromServices] CardSharePublisher shares, CancellationToken ct) =>
            Reply(await shares.ListAsync(Project(), card, before, ct)));
        group.MapPost("", async (string card, HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] CardSharePublisher shares, CancellationToken ct) =>
            await WithName(context, name => shares.CreateAsync(Project(), card, name, ct), ct));
        group.MapPatch("/{id:int}", async (string card, int id, HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] CardSharePublisher shares, CancellationToken ct) =>
            await WithName(context, name => shares.RenameAsync(Project(), card, id, name, ct), ct));
        group.MapDelete("/{id:int}", async (string card, int id, [Microsoft.AspNetCore.Mvc.FromServices] CardSharePublisher shares, CancellationToken ct) =>
            Reply(await shares.RevokeAsync(Project(), card, id, ct)));
        group.MapPost("/refresh", async (string card, [Microsoft.AspNetCore.Mvc.FromServices] CardSharePublisher shares, CancellationToken ct) =>
            Reply(await shares.RefreshAsync(Project(), card, ct)));
    }
    private static IResult Reply(CardShareResult result) => Results.Json(result, CardSharingJsonContext.Default.CardShareResult);
    private static async Task<IResult> WithName(HttpContext context, Func<string?, Task<CardShareResult>> action, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
        if (context.Request.ContentLength > 4096) return Results.StatusCode(413);
        try
        {
            var bytes = await SessionSharingService.ReadBoundedAsync(context.Request.Body, 4096, ct);
            var name = JsonSerializer.Deserialize(bytes, CardSharingJsonContext.Default.CardShareNameRequest);
            return Reply(await action(name?.DisplayName));
        }
        catch (InvalidDataException) { return Results.StatusCode(413); }
        catch (JsonException) { return Results.StatusCode(400); }
    }
}
