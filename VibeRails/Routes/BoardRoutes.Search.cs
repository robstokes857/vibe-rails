using Microsoft.AspNetCore.Mvc;
using VibeRails.DTOs;
using VibeRails.Services.Board;

namespace VibeRails.Routes;

public static partial class BoardRoutes
{
    private static void MapLocalSearch(WebApplication app)
    {
        app.MapGet("/api/v1/board/cards/search", ([FromServices] BoardSearchService search,
            HttpContext context, string? q, int? count, CancellationToken ct) => RunAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new BoardSearchResponse(await search.SearchAsync(Project(), q ?? "", count ?? 50, ct: ct)));
        })).WithName("SearchLocalBoardCards");

        // Explicit local-user navigation, separate from the existing repository-scoped routes.
        // The request supplies only an immutable card identity; ownership is read from the store.
        var local = app.MapGroup("/api/v1/board/local-cards/{card}");
        local.AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return next(context);
        });
        local.MapGet("", ([FromServices] IBoardStore store, [FromServices] IBoardService board, string card, CancellationToken ct) =>
            WithLocalCardAsync(store, card, async target =>
            {
                var detail = await board.GetCardAsync(target.ProjectPath, target.Id, ct);
                if (detail is null) return NotFound("Card", card);
                var owner = await board.FindBoardAsync(target.ProjectPath, detail.BoardId, ct);
                var columns = await board.GetColumnsAsync(target.ProjectPath, ct, detail.BoardId);
                var isJiraBoard = await store.GetJiraConnectionAsync(target.ProjectPath, detail.BoardId, ct) is not null;
                return Results.Ok(new LocalBoardCardResponse(ForCurrentProject(detail)!, target.ProjectPath,
                    owner?.Name ?? detail.BoardId, columns.Columns, IsCurrentProject(target.ProjectPath), isJiraBoard));
            }, ct)).WithName("GetLocalBoardCard");
        local.MapPut("", ([FromServices] IBoardStore store, [FromServices] IBoardService board,
            string card, UpdateBoardCardRequest request, CancellationToken ct) =>
            WithLocalCardAsync(store, card, async target =>
                OkOrNotFound(ForCurrentProject(await board.UpdateCardAsync(target.ProjectPath, target.Id, request, ct)), "Card"), ct))
            .WithName("UpdateLocalBoardCard");
        local.MapPost("/move", ([FromServices] IBoardStore store, [FromServices] IBoardService board,
            string card, MoveBoardCardRequest request, CancellationToken ct) =>
            WithLocalCardAsync(store, card, async target =>
                OkOrNotFound(ForCurrentProject((await board.MoveCardAsync(target.ProjectPath, target.Id,
                    new BoardCardMoveRequest(request.ColumnId ?? "", request.Position, request.SkipAutomations, BoardAuthor.User()), ct))?.Card), "Card"), ct))
            .WithName("MoveLocalBoardCard");
        local.MapPost("/comments", ([FromServices] IBoardStore store, [FromServices] IBoardService board,
            string card, AddBoardCommentRequest request, CancellationToken ct) =>
            WithLocalCardAsync(store, card, async target =>
                OkOrNotFound(await board.AddCommentAsync(target.ProjectPath, target.Id, BoardAuthor.User(), request.Body ?? "", ct, request.SyncToJira), "Card"), ct))
            .WithName("CommentLocalBoardCard");
        local.MapGet("/links/candidates", ([FromServices] IBoardStore store, [FromServices] IBoardService board,
            string card, string? q, CancellationToken ct) =>
            WithLocalCardAsync(store, card, async target =>
            {
                var candidates = await board.GetCardLinkCandidatesAsync(target.ProjectPath, target.Id, q, ct, preferredProjectPath: Project());
                return OkOrNotFound(candidates is null ? null : candidates with
                { Cards = candidates.Cards.Select(ForCurrentProject).ToList() }, "Card");
            }, ct)).WithName("GetLocalBoardCardLinkCandidates");
        local.MapPost("/links", ([FromServices] IBoardStore store, [FromServices] IBoardService board,
            string card, LinkBoardCardRequest request, CancellationToken ct) =>
            WithLocalCardAsync(store, card, async target =>
            {
                var linked = await board.LinkCardAsync(target.ProjectPath, target.Id, request.Card, ct);
                return OkOrNotFound(linked is null ? null : ForCurrentProject(linked), "Card");
            }, ct))
            .WithName("LinkLocalBoardCard");
        local.MapDelete("/links/{linkedCard}", ([FromServices] IBoardStore store, [FromServices] IBoardService board,
            string card, string linkedCard, CancellationToken ct) =>
            WithLocalCardAsync(store, card, async target =>
                await board.UnlinkCardAsync(target.ProjectPath, target.Id, linkedCard, ct)
                    ? Results.Ok(new OK("Card unlinked")) : NotFound("Card link", linkedCard), ct))
            .WithName("UnlinkLocalBoardCard");
    }

    private static Task<IResult> WithLocalCardAsync(IBoardStore store, string identity,
        Func<BoardCardRecord, Task<IResult>> action, CancellationToken ct) => RunAsync(async () =>
    {
        var target = await store.FindLocalCardAsync(identity, ct);
        return target is null ? NotFound("Card", identity) : await action(target);
    });

    private static bool IsCurrentProject(string project) =>
        string.Equals(BoardPaths.NormalizeProjectPath(project), BoardPaths.NormalizeProjectPath(Project()), BoardPaths.ProjectPathComparison);

    private static BoardLinkedCardDto ForCurrentProject(BoardLinkedCardDto card) =>
        card with { IsCurrentProject = card.ProjectPath is not null && IsCurrentProject(card.ProjectPath) };

    private static BoardCardResponse? ForCurrentProject(BoardCardResponse? card) =>
        card is null ? null : card with { LinkedCards = card.LinkedCards.Select(ForCurrentProject).ToList() };
}
