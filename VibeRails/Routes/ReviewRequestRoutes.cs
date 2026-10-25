using System.Text.Json;
using VibeRails.Auth;
using VibeRails.DB;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.ReviewRequests;
using VibeRails.Utils;

namespace VibeRails.Routes;

/// <summary>
/// Review requests before a commit (VB-50). Active root only; CookieAuthMiddleware requires both
/// process and tab credentials. Two routes contact viberails.ai: the decision route verifies a
/// submitted approval signature with the same public endpoint the commit hook uses, and the send
/// route transfers the bundle after the caller confirmed the disclosure (slice C). Cancel also
/// asks the host to cancel a sent request's copy.
/// </summary>
public static class ReviewRequestRoutes
{
    /// <summary>Reviewers, session ids, a card key and a note; the bundle itself is captured server-side.</summary>
    internal const int MaxCreateBodyBytes = 16 * 1024;

    /// <summary>A trailer (under 1 KiB), a comment (4,000 characters) and a reviewer address.</summary>
    internal const int MaxDecisionBodyBytes = 32 * 1024;

    /// <summary><c>{ "confirmed": true }</c> and nothing else.</summary>
    internal const int MaxSendBodyBytes = 1024;

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
                // A terminal's tool contact (request_review, slice E) may attach only its own session,
                // and always does: the middleware admits the contact, this decides what it may say.
                if (ToolContact(context) is { } contact)
                {
                    var own = contact.SessionId;
                    if (own is null || (body?.Sessions ?? []).Any(id => !string.Equals(id?.Trim(), own, StringComparison.OrdinalIgnoreCase)))
                        return Forbidden("A terminal's tool credentials can attach only that terminal's own session to a review request.");
                    body = (body ?? new CreateReviewRequest()) with { Sessions = [own] };
                }
                var created = await reviews.CreateAsync(Project(), body, sessions, ct);
                return Results.Json(created, ReviewRequestJsonContext.Default.ReviewRequestSummary, statusCode: StatusCodes.Status201Created);
            }, ct));

        // Slice E: what a request made now would capture (read-only, nothing stored or sent), and which
        // linked commits already carry a tree a reviewer approved. Both are literal segments, so they
        // never collide with the {id:guid} routes below.
        group.MapGet("/staged", async (ReviewRequestService reviews, CancellationToken ct) =>
            Results.Json(await reviews.PreviewAsync(Project(), ct), ReviewRequestJsonContext.Default.ReviewStagedPreview));

        group.MapGet("/commit-approvals", async (HttpContext context, ReviewRequestService reviews, CancellationToken ct) =>
        {
            var shas = new List<string>();
            foreach (var value in context.Request.Query["sha"])
                shas.AddRange((value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return Results.Json(await reviews.CommitApprovalsAsync(Project(), shas, ct), ReviewRequestJsonContext.Default.ReviewCommitApprovals);
        });

        group.MapGet("/{id:guid}", async (Guid id, ReviewRequestService reviews, CancellationToken ct) =>
            Results.Json(await reviews.GetAsync(Project(), id.ToString("D"), ct), ReviewRequestJsonContext.Default.ReviewRequestDetail));

        group.MapPost("/{id:guid}/decisions", async Task<IResult> (Guid id, HttpContext context, ReviewRequestService reviews, CancellationToken ct) =>
            await WithJson(context, MaxDecisionBodyBytes, async bytes =>
            {
                var body = JsonSerializer.Deserialize(bytes, ReviewRequestJsonContext.Default.RecordReviewDecisionRequest);
                var updated = await reviews.RecordDecisionAsync(Project(), id.ToString("D"), body, ct);
                return Results.Json(updated, ReviewRequestJsonContext.Default.ReviewRequestSummary);
            }, ct));

        group.MapPost("/{id:guid}/cancel", async (Guid id, ReviewRequestSender sender, CancellationToken ct) =>
            Results.Json(await sender.CancelAsync(Project(), id.ToString("D"), ct), ReviewRequestJsonContext.Default.ReviewRequestSummary));

        // Slice C: what a send would transfer, and the send itself. The disclosure is read-only and
        // contacts nothing; the send refuses without "confirmed": true so nothing leaves unseen.
        group.MapGet("/{id:guid}/disclosure", async Task<IResult> (Guid id, HttpContext context, ReviewRequestSender sender, ReviewRequestService reviews, ISessionStore sessions, CancellationToken ct) =>
        {
            // A tool contact reads only a request that attaches its own session (the one request_review made).
            if (ToolContact(context) is { } contact)
            {
                var detail = await reviews.GetAsync(Project(), id.ToString("D"), ct);
                if (contact.SessionId is null || !detail.Bundle.Sessions.Any(session => string.Equals(session.Id, contact.SessionId, StringComparison.OrdinalIgnoreCase)))
                    return Forbidden("A terminal's tool credentials can read only a review request that attaches that terminal's session.");
            }
            return Results.Json(await sender.DisclosureAsync(Project(), id.ToString("D"), sessions, ct), ReviewRequestJsonContext.Default.ReviewRequestDisclosure);
        });

        group.MapPost("/{id:guid}/send", async Task<IResult> (Guid id, HttpContext context, ReviewRequestSender sender, CancellationToken ct) =>
            await WithJson(context, MaxSendBodyBytes, async bytes =>
            {
                var body = JsonSerializer.Deserialize(bytes, ReviewRequestJsonContext.Default.SendReviewRequest);
                var sent = await sender.SendAsync(Project(), id.ToString("D"), body, ct);
                return Results.Json(sent, ReviewRequestJsonContext.Default.ReviewRequestSent);
            }, ct));

        // The sender deletes: a sent request's hosted copy is cancelled first, or the delete is refused
        // (409 remote_pending), so reviewers never keep access to a request that is gone locally.
        group.MapDelete("/{id:guid}", async (Guid id, ReviewRequestSender sender, CancellationToken ct) =>
        {
            await sender.DeleteAsync(Project(), id.ToString("D"), ct);
            return Results.NoContent();
        });
    }

    private static string Project() => ParserConfigs.GetRootPath();

    /// <summary>The scoped tool contact the middleware admitted (a CLI's <c>VIBERAILS_TOOL_*</c> pair), or null for the dashboard credentials.</summary>
    private static AgentCredentialService.Contact? ToolContact(HttpContext context) =>
        context.Items[typeof(AgentCredentialService.Contact)] as AgentCredentialService.Contact;

    private static IResult Forbidden(string message) =>
        Results.Json(new ReviewRequestError(message, "forbidden"), ReviewRequestJsonContext.Default.ReviewRequestError, statusCode: StatusCodes.Status403Forbidden);

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
