using System.Text.Json;
using VibeRails.DB;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.ReviewRequests;
using VibeRails.Utils;

namespace VibeRails.Routes;

/// <summary>
/// Review requests before a commit (VB-50). Active root only; CookieAuthMiddleware requires both
/// process and tab credentials. Everything here stays on this computer: no route contacts
/// viberails.ai except the decision route, which verifies a submitted approval signature with the
/// same public endpoint the commit hook uses.
/// </summary>
public static class ReviewRequestRoutes
{
    /// <summary>Reviewers, session ids, a card key and a note; the bundle itself is captured server-side.</summary>
    internal const int MaxCreateBodyBytes = 16 * 1024;

    /// <summary>A trailer (under 1 KiB), a comment (4,000 characters) and a reviewer address.</summary>
    internal const int MaxDecisionBodyBytes = 32 * 1024;

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v1/review-requests");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(context);
            }
            catch (ReviewRequestException ex)
            {
                return Results.Json(new ReviewRequestError(ex.Message, ex.Code),
                    ReviewRequestJsonContext.Default.ReviewRequestError, statusCode: ex.Status);
            }
        });

        group.MapGet("", async (ReviewRequestService reviews, CancellationToken ct) =>
            Results.Json(await reviews.ListAsync(Project(), ct), ReviewRequestJsonContext.Default.ReviewRequestList));

        group.MapPost("", async Task<IResult> (HttpContext context, ReviewRequestService reviews, ISessionStore sessions, CancellationToken ct) =>
            await WithJson(context, MaxCreateBodyBytes, async bytes =>
            {
                var body = JsonSerializer.Deserialize(bytes, ReviewRequestJsonContext.Default.CreateReviewRequest);
                var created = await reviews.CreateAsync(Project(), body, sessions, ct);
                return Results.Json(created, ReviewRequestJsonContext.Default.ReviewRequestSummary, statusCode: StatusCodes.Status201Created);
            }, ct));

        group.MapGet("/{id:guid}", async (Guid id, ReviewRequestService reviews, CancellationToken ct) =>
            Results.Json(await reviews.GetAsync(Project(), id.ToString("D"), ct), ReviewRequestJsonContext.Default.ReviewRequestDetail));

        group.MapPost("/{id:guid}/decisions", async Task<IResult> (Guid id, HttpContext context, ReviewRequestService reviews, CancellationToken ct) =>
            await WithJson(context, MaxDecisionBodyBytes, async bytes =>
            {
                var body = JsonSerializer.Deserialize(bytes, ReviewRequestJsonContext.Default.RecordReviewDecisionRequest);
                var updated = await reviews.RecordDecisionAsync(Project(), id.ToString("D"), body, ct);
                return Results.Json(updated, ReviewRequestJsonContext.Default.ReviewRequestSummary);
            }, ct));

        group.MapPost("/{id:guid}/cancel", async (Guid id, ReviewRequestService reviews, CancellationToken ct) =>
            Results.Json(await reviews.CancelAsync(Project(), id.ToString("D"), ct), ReviewRequestJsonContext.Default.ReviewRequestSummary));

        group.MapDelete("/{id:guid}", async (Guid id, ReviewRequestService reviews, CancellationToken ct) =>
        {
            await reviews.DeleteAsync(Project(), id.ToString("D"), ct);
            return Results.NoContent();
        });
    }

    private static string Project() => ParserConfigs.GetRootPath();

    /// <summary>Bounded JSON body (including chunked bodies); the action deserializes with a source-generated type.</summary>
    private static async Task<IResult> WithJson(HttpContext context, int maxBytes, Func<byte[], Task<IResult>> action, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > maxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        try
        {
            var bytes = await SessionSharingService.ReadBoundedAsync(context.Request.Body, maxBytes, ct);
            return await action(bytes);
        }
        catch (InvalidDataException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        catch (JsonException)
        {
            return Results.Json(new ReviewRequestError("The request body is not valid JSON for this route.", "invalid_request"),
                ReviewRequestJsonContext.Default.ReviewRequestError, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
