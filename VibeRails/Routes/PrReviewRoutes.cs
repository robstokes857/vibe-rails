using Microsoft.AspNetCore.Mvc;
using VibeRails.DTOs;
using VibeRails.Services.PrReviews;
using VibeRails.Utils;

namespace VibeRails.Routes;

/// <summary>
/// Lead-review records for the current project's GitHub pull requests. Root dashboard only. The
/// project (and so the repository) is derived from the dashboard root, never from a request.
/// GitHub is only ever read, through the user's own <c>gh</c>.
/// </summary>
public static class PrReviewRoutes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/v1/pr-reviews");
        routes.AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return next(context);
        });

        routes.MapGet("", ([FromServices] PrReviewService service, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.ListAsync(Project(), ct))))
            .WithName("ListPrReviews");

        routes.MapPost("", ([FromServices] PrReviewService service, PrReviewRequest request, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var result = await service.UpsertAsync(Project(), request, ct);
                return result.Created
                    ? Results.Created($"/api/v1/pr-reviews/{result.PullRequest.Number}", result)
                    : Results.Ok(result);
            }))
            .WithName("UpsertPrReview");

        // The one call an unattended run needs: which PR next, and why.
        routes.MapGet("/queue", ([FromServices] PrReviewService service, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.QueueAsync(Project(), ct))))
            .WithName("GetPrReviewQueue");

        routes.MapPost("/github/sync", ([FromServices] PrReviewService service, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.SyncAsync(Project(), ct))))
            .WithName("SyncPrReviewGitHubStates");

        routes.MapGet("/{number:int}", ([FromServices] PrReviewService service, int number, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.GetAsync(Project(), number, ct))))
            .WithName("GetPrReview");

        routes.MapPut("/{number:int}", ([FromServices] PrReviewService service, int number, PrReviewRequest request,
            CancellationToken ct) => RunAsync(async () => Results.Ok(await service.UpdateAsync(Project(), number, request, ct))))
            .WithName("UpdatePrReview");

        routes.MapDelete("/{number:int}", ([FromServices] PrReviewService service, int number, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await service.DeleteAsync(Project(), number, ct);
                return Results.Ok(new PrReviewDeleteResponse(number, null));
            }))
            .WithName("DeletePrReview");

        routes.MapPost("/{number:int}/reviews/start", ([FromServices] PrReviewService service, int number,
            PrReviewRequest? request, CancellationToken ct) => RunAsync(async () =>
            {
                var result = await service.StartAsync(Project(), number, request, ct);
                return result.Created
                    ? Results.Created($"/api/v1/pr-reviews/{number}", result)
                    : Results.Ok(result);
            }))
            .WithName("StartPrReview");

        routes.MapPost("/{number:int}/reviews/draft", ([FromServices] PrReviewService service, int number,
            PrReviewRequest? request, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.DraftAsync(Project(), number, request, ct))))
            .WithName("SavePrReviewDraft");

        routes.MapPost("/{number:int}/reviews/complete", ([FromServices] PrReviewService service, int number,
            PrReviewRequest? request, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.CompleteAsync(Project(), number, request, ct))))
            .WithName("CompletePrReview");

        routes.MapPut("/reviews/{id:long}", ([FromServices] PrReviewService service, long id, PrReviewRequest request,
            CancellationToken ct) => RunAsync(async () => Results.Ok(await service.UpdateReviewAsync(Project(), id, request, ct))))
            .WithName("UpdatePrReviewPass");

        routes.MapDelete("/reviews/{id:long}", ([FromServices] PrReviewService service, long id, CancellationToken ct) =>
            RunAsync(async () =>
                Results.Ok(new PrReviewDeleteResponse(await service.DeleteReviewAsync(Project(), id, ct), id))))
            .WithName("DeletePrReviewPass");
    }

    private static string Project() => ParserConfigs.GetRootPath();

    /// <summary>Rule violations become readable bodies with their status; everything else is a 500.</summary>
    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (PrReviewException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.StatusCode);
        }
    }
}
