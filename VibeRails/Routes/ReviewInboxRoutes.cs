using System.Text.Json;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.ReviewRequests;
using VibeRails.Utils;

namespace VibeRails.Routes;

/// <summary>
/// "Reviews requested of you" (VB-50 slice D). Active root only; CookieAuthMiddleware requires both
/// process and tab credentials. The list is the cached hosted inbox; refresh and reject call
/// viberails.ai with the account key; approve opens the signing popup in its own console window and
/// never signs here. No route reads a key file or a password.
/// </summary>
public static class ReviewInboxRoutes
{
    /// <summary>A comment of up to 4,000 characters.</summary>
    internal const int MaxRejectBodyBytes = 32 * 1024;

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v1/review-inbox");
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
                    ReviewInboxJsonContext.Default.ReviewRequestError, statusCode: ex.Status);
            }
            catch (ReviewRequestTransportException ex)
            {
                // The host's own refusal of a decision (not a reviewer, not open, a repeated or bad
                // trailer) keeps its status; an unreachable or broken host is a bad gateway. The
                // wording is always the transport's fixed local text.
                var status = ex.HttpStatus is 400 or 404 or 409 or 429 or 503 ? ex.HttpStatus.Value : StatusCodes.Status502BadGateway;
                return Results.Json(new ReviewRequestError(ex.Message, ex.Code),
                    ReviewInboxJsonContext.Default.ReviewRequestError, statusCode: status);
            }
        });

        group.MapGet("", (ReviewInboxService inbox) =>
            Results.Json(inbox.View(Project()), ReviewInboxJsonContext.Default.ReviewInboxView));

        group.MapPost("/refresh", async (ReviewInboxService inbox, CancellationToken ct) =>
            Results.Json(await inbox.RefreshNowAsync(Project(), ct), ReviewInboxJsonContext.Default.ReviewInboxView));

        group.MapPost("/{reference}/reject", async Task<IResult> (string reference, HttpContext context, ReviewInboxService inbox, CancellationToken ct) =>
        {
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if (context.Request.ContentLength > MaxRejectBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            ReviewInboxRejectRequest? body;
            try
            {
                var bytes = await SessionSharingService.ReadBoundedAsync(context.Request.Body, MaxRejectBodyBytes, ct);
                body = JsonSerializer.Deserialize(bytes, ReviewInboxJsonContext.Default.ReviewInboxRejectRequest);
            }
            catch (InvalidDataException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (JsonException)
            {
                return Results.Json(new ReviewRequestError("The request body is not valid JSON for this route.", "invalid_request"),
                    ReviewInboxJsonContext.Default.ReviewRequestError, statusCode: StatusCodes.Status400BadRequest);
            }
            return Results.Json(await inbox.RejectAsync(reference, body, ct), ReviewInboxJsonContext.Default.ReviewInboxActionResult);
        });

        // The popup runs in its own window and outlives this request; 202 says it was opened, the list says how it ended.
        group.MapPost("/{reference}/approve", (string reference, ReviewInboxService inbox) =>
            Results.Json(inbox.BeginApprove(reference, Project()), ReviewInboxJsonContext.Default.ReviewInboxActionResult,
                statusCode: StatusCodes.Status202Accepted));
    }

    private static string Project() => ParserConfigs.GetRootPath();
}
