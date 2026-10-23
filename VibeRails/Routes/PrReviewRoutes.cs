using Microsoft.AspNetCore.Mvc;
using VibeRails.DTOs;
using VibeRails.Services.PrReviews;
using VibeRails.Utils;

namespace VibeRails.Routes;

/// <summary>
/// Lead-review records for the current project's GitHub pull requests. Root dashboard only. The
/// watch list is shared; single-PR actions select a validated watched repository or origin fallback.
/// GitHub reviews use the user's own authenticated <c>gh</c>.
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

        routes.MapGet("/settings", ([FromServices] PrReviewService service, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.GetSettingsAsync(ct))));
        routes.MapPut("/settings", ([FromServices] PrReviewService service, VibeRails.DB.PrReviewerSettings request, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.SetSettingsAsync(request.Enabled, ct, request.Repositories))));
        routes.MapPost("/{number:int}/reviews/{id:long}/publish", ([FromServices] PrReviewService service, int number, long id, [FromQuery] string? repository, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.PublishAsync(Project(), number, id, ct, repository))));

        routes.MapGet("", ([FromServices] PrReviewService service, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.ListAsync(Project(), ct))))
            .WithName("ListPrReviews");

        routes.MapPost("", ([FromServices] PrReviewService service, PrReviewRequest request, [FromQuery] string? repository, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var result = await service.UpsertAsync(Project(), request, ct, repository);
                return result.Created
                    ? Results.Created($"/api/v1/pr-reviews/{result.PullRequest.Number}?repository={Uri.EscapeDataString(result.PullRequest.Repository)}", result)
                    : Results.Ok(result);
            }))
            .WithName("UpsertPrReview");

        routes.MapPost("/{number:int}/legacy-conflict/resolve", ([FromServices] PrReviewService service, int number,
            PrReviewResolveLegacyRequest request, [FromQuery] string? repository, CancellationToken ct) => RunAsync(async () =>
            {
                await service.ResolveLegacyConflictAsync(Project(), number, request, ct, repository);
                return Results.Ok(new PrReviewDeleteResponse(number, null));
            }))
            .WithName("ResolvePrReviewLegacyConflict");

        // The one call an unattended run needs: which PR next, and why.
        routes.MapGet("/queue", ([FromServices] PrReviewService service, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.QueueAsync(Project(), ct))))
            .WithName("GetPrReviewQueue");

        routes.MapPost("/github/sync", ([FromServices] PrReviewService service, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.SyncAsync(Project(), ct))))
            .WithName("SyncPrReviewGitHubStates");

        routes.MapGet("/{number:int}", ([FromServices] PrReviewService service, int number, [FromQuery] string? repository, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.GetAsync(Project(), number, ct, repository))))
            .WithName("GetPrReview");

        routes.MapPut("/{number:int}", ([FromServices] PrReviewService service, int number, PrReviewRequest request,
            [FromQuery] string? repository, CancellationToken ct) => RunAsync(async () => Results.Ok(await service.UpdateAsync(Project(), number, request, ct, repository))))
            .WithName("UpdatePrReview");

        routes.MapDelete("/{number:int}", ([FromServices] PrReviewService service, int number, [FromQuery] string? repository, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await service.DeleteAsync(Project(), number, ct, repository);
                return Results.Ok(new PrReviewDeleteResponse(number, null));
            }))
            .WithName("DeletePrReview");

        routes.MapPost("/{number:int}/reviews/start", ([FromServices] PrReviewService service, int number,
            PrReviewRequest? request, [FromQuery] string? repository, CancellationToken ct) => RunAsync(async () =>
            {
                var result = await service.StartAsync(Project(), number, request, ct, repository);
                return result.Created
                    ? Results.Created($"/api/v1/pr-reviews/{number}?repository={Uri.EscapeDataString(result.PullRequest.Repository)}", result)
                    : Results.Ok(result);
            }))
            .WithName("StartPrReview");

        routes.MapPost("/{number:int}/reviews/draft", ([FromServices] PrReviewService service, int number,
            PrReviewRequest? request, [FromQuery] string? repository, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.DraftAsync(Project(), number, request, ct, repository))))
            .WithName("SavePrReviewDraft");

        routes.MapPost("/{number:int}/reviews/complete", ([FromServices] PrReviewService service, int number,
            PrReviewRequest? request, [FromQuery] string? repository, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await service.CompleteAsync(Project(), number, request, ct, repository))))
            .WithName("CompletePrReview");

        routes.MapPut("/reviews/{id:long}", ([FromServices] PrReviewService service, long id, PrReviewRequest request,
            [FromQuery] string? repository, CancellationToken ct) => RunAsync(async () => Results.Ok(await service.UpdateReviewAsync(Project(), id, request, ct, repository))))
            .WithName("UpdatePrReviewPass");

        routes.MapDelete("/reviews/{id:long}", ([FromServices] PrReviewService service, long id, [FromQuery] string? repository, CancellationToken ct) =>
            RunAsync(async () =>
                Results.Ok(new PrReviewDeleteResponse(await service.DeleteReviewAsync(Project(), id, ct, repository), id))))
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
        catch (VibeRails.DB.PrReviewAdoptionConflictException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: StatusCodes.Status409Conflict);
        }
    }
}
