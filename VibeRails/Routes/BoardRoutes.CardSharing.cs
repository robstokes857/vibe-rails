using System.Text.Json;
using VibeRails.Services.Board.Sharing;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

public static partial class BoardRoutes
{
    // A name plus up to ten addresses of 320 characters fits well inside this bound.
    internal const int CardShareMaxBodyBytes = 8192;

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
        group.MapPost("", async Task<IResult> (string card, HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] CardSharePublisher shares, CancellationToken ct) =>
            await WithJson(context, bytes =>
            {
                var body = JsonSerializer.Deserialize(bytes, CardSharingJsonContext.Default.CardShareCreateRequest);
                return shares.CreateAsync(Project(), card, body?.DisplayName, body?.Access, body?.Emails, ct);
            }, ct));
        group.MapPatch("/{id:int}", async Task<IResult> (string card, int id, HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] CardSharePublisher shares, CancellationToken ct) =>
            await WithJson(context, bytes =>
            {
                var body = JsonSerializer.Deserialize(bytes, CardSharingJsonContext.Default.CardShareNameRequest);
                return shares.RenameAsync(Project(), card, id, body?.DisplayName, ct);
            }, ct));
        group.MapPut("/{id:int}/access", async Task<IResult> (string card, int id, HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] CardSharePublisher shares, CancellationToken ct) =>
            await WithJson(context, bytes =>
            {
                var body = JsonSerializer.Deserialize(bytes, CardSharingJsonContext.Default.CardShareAccessRequest);
                return shares.SetAccessAsync(Project(), card, id, body?.Access, body?.Emails, ct);
            }, ct));
        group.MapDelete("/{id:int}", async (string card, int id, [Microsoft.AspNetCore.Mvc.FromServices] CardSharePublisher shares, CancellationToken ct) =>
            Reply(await shares.RevokeAsync(Project(), card, id, ct)));
        group.MapPost("/refresh", async (string card, [Microsoft.AspNetCore.Mvc.FromServices] CardSharePublisher shares, CancellationToken ct) =>
            Reply(await shares.RefreshAsync(Project(), card, ct)));
    }
    private static IResult Reply(CardShareResult result) => Results.Json(result, CardSharingJsonContext.Default.CardShareResult);
    /// <summary>Bounded JSON body (including chunked bodies); the action deserializes with a source-generated type.</summary>
    private static async Task<IResult> WithJson(HttpContext context, Func<byte[], Task<CardShareResult>> action, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
        if (context.Request.ContentLength > CardShareMaxBodyBytes) return Results.StatusCode(413);
        try
        {
            var bytes = await SessionSharingService.ReadBoundedAsync(context.Request.Body, CardShareMaxBodyBytes, ct);
            return Reply(await action(bytes));
        }
        catch (InvalidDataException) { return Results.StatusCode(413); }
        catch (JsonException) { return Results.StatusCode(400); }
    }
}
