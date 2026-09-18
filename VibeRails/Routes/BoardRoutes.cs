using Microsoft.AspNetCore.Http.Features;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Utils;

namespace VibeRails.Routes;

/// <summary>
/// Kanban board API. Every path contains <c>/api/</c>, so CookieAuthMiddleware already requires
/// both the session and tab credentials — nothing to register here. Mapped on the active root
/// backend only (terminal-tab children have no board UI). The project is always the dashboard's
/// root path; it is never read from the request.
/// </summary>
public static class BoardRoutes
{
    public static void Map(WebApplication app)
    {
        // Attachment uploads are not size-limited. This has to run as middleware, before model
        // binding reads the body: a RequestSizeLimitAttribute on the endpoint does nothing here,
        // because only the MVC filter pipeline honours it and a MapPost lambda never runs those —
        // Kestrel's 30 MB default applied instead. The board is a single local user's own files;
        // what they can fill is their own disk.
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method)
                && context.Request.Path.StartsWithSegments("/api/v1/board/cards")
                && context.Request.Path.Value?.EndsWith("/attachments", StringComparison.Ordinal) == true)
            {
                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = null;
            }
            await next(context);
        });

        // ---------------------------------------------------------------- boards
        //
        // Lane and card lists take ?board=<id>; omitted means the project's first board, which is
        // what every client before boards existed was reading.

        app.MapGet("/api/v1/board/boards", (IBoardService board, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await board.GetBoardsAsync(Project(), cancellationToken))))
            .WithName("GetBoards");

        app.MapPost("/api/v1/board/boards", (IBoardService board, CreateBoardRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await board.CreateBoardAsync(Project(), request, cancellationToken))))
            .WithName("CreateBoard");

        app.MapPut("/api/v1/board/boards/{boardId}", (IBoardService board, string boardId, UpdateBoardRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.UpdateBoardAsync(Project(), boardId, request, cancellationToken), "Board")))
            .WithName("UpdateBoard");

        app.MapDelete("/api/v1/board/boards/{boardId}", (IBoardService board, string boardId, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.DeleteBoardAsync(Project(), boardId, cancellationToken), "Board")))
            .WithName("DeleteBoard");

        // ---------------------------------------------------------------- columns

        app.MapGet("/api/v1/board/columns", (IBoardService board, string? boardId, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await board.GetColumnsAsync(Project(), cancellationToken, boardId))))
            .WithName("GetBoardColumns");

        app.MapPost("/api/v1/board/columns", (IBoardService board, CreateBoardColumnRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await board.CreateColumnAsync(Project(), request, cancellationToken))))
            .WithName("CreateBoardColumn");

        // Mapped before /{id} so "order" is never taken for a lane id.
        app.MapPut("/api/v1/board/columns/order", (IBoardService board, ReorderBoardColumnsRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await board.ReorderColumnsAsync(Project(), request.OrderedIds ?? [], cancellationToken, request.BoardId))))
            .WithName("ReorderBoardColumns");

        app.MapPut("/api/v1/board/columns/{columnId}", (IBoardService board, string columnId, UpdateBoardColumnRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.UpdateColumnAsync(Project(), columnId, request, cancellationToken), "Lane")))
            .WithName("UpdateBoardColumn");

        app.MapDelete("/api/v1/board/columns/{columnId}", (IBoardService board, string columnId, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.DeleteColumnAsync(Project(), columnId, cancellationToken), "Lane")))
            .WithName("DeleteBoardColumn");

        // ---------------------------------------------------------------- cards

        app.MapGet("/api/v1/board/cards", (IBoardService board, string? boardId, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await board.GetCardsAsync(Project(), cancellationToken, boardId))))
            .WithName("GetBoardCards");

        app.MapPost("/api/v1/board/cards", (IBoardService board, CreateBoardCardRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await board.CreateCardAsync(Project(), request, cancellationToken))))
            .WithName("CreateBoardCard");

        app.MapGet("/api/v1/board/cards/{card}", (IBoardService board, string card, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.GetCardAsync(Project(), card, cancellationToken), "Card")))
            .WithName("GetBoardCard");

        app.MapPut("/api/v1/board/cards/{card}", (IBoardService board, string card, UpdateBoardCardRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.UpdateCardAsync(Project(), card, request, cancellationToken), "Card")))
            .WithName("UpdateBoardCard");

        app.MapGet("/api/v1/board/cards/{card}/history", (IBoardService board, string card, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.GetDescriptionHistoryAsync(Project(), card, cancellationToken), "Card")))
            .WithName("GetBoardCardDescriptionHistory");

        app.MapDelete("/api/v1/board/cards/{card}", (IBoardService board, string card, CancellationToken cancellationToken) =>
            RunAsync(async () => await board.DeleteCardAsync(Project(), card, cancellationToken)
                ? Results.Ok(new OK("Card deleted"))
                : NotFound("Card", card)))
            .WithName("DeleteBoardCard");

        app.MapPost("/api/v1/board/cards/{card}/move", (IBoardService board, string card, MoveBoardCardRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.MoveCardAsync(Project(), card, request.ColumnId ?? string.Empty, request.Position, cancellationToken), "Card")))
            .WithName("MoveBoardCard");

        app.MapPost("/api/v1/board/cards/{card}/launch", (IBoardLaunchService launcher, string card, LaunchBoardCardRequest? request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await launcher.LaunchAsync(Project(), card, request?.Selection, cancellationToken), "Card")))
            .WithName("LaunchBoardCard");

        // ---------------------------------------------------------------- rails

        app.MapGet("/api/v1/board/cards/{card}/links/candidates", (IBoardService board, string card, string? q, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.GetCardLinkCandidatesAsync(Project(), card, q, cancellationToken), "Card")))
            .WithName("GetBoardCardLinkCandidates");

        app.MapPost("/api/v1/board/cards/{card}/links", (IBoardService board, string card, LinkBoardCardRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.LinkCardAsync(Project(), card, request.Card, cancellationToken), "Card")))
            .WithName("LinkBoardCard");

        app.MapDelete("/api/v1/board/cards/{card}/links/{linkedCard}", (IBoardService board, string card, string linkedCard, CancellationToken cancellationToken) =>
            RunAsync(async () => await board.UnlinkCardAsync(Project(), card, linkedCard, cancellationToken)
                ? Results.Ok(new OK("Card unlinked"))
                : NotFound("Card link", linkedCard)))
            .WithName("UnlinkBoardCard");

        app.MapPost("/api/v1/board/cards/{card}/comments", (IBoardService board, string card, AddBoardCommentRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.AddCommentAsync(Project(), card, BoardAuthor.User(), request.Body ?? string.Empty, cancellationToken), "Card")))
            .WithName("AddBoardComment");

        // Agent notes: the scratchpad agents append over MCP. The dashboard reads it and may add
        // a user note; it is never part of the comment stream.
        app.MapGet("/api/v1/board/cards/{card}/notes", (IBoardService board, string card, CancellationToken cancellationToken) =>
            RunAsync(async () =>
            {
                var notes = await board.GetNotesAsync(Project(), card, cancellationToken);
                return notes is null ? NotFound("Card", card) : Results.Ok(new BoardNoteListResponse(notes));
            }))
            .WithName("GetBoardCardNotes");

        app.MapPost("/api/v1/board/cards/{card}/notes", (IBoardService board, string card, AddBoardNoteRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.AddNoteAsync(Project(), card, BoardAuthor.User(), request.Body ?? string.Empty, cancellationToken), "Card")))
            .WithName("AddBoardCardNote");

        app.MapPost("/api/v1/board/cards/{card}/attachments", (IBoardService board, string card, AddBoardAttachmentRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.AddAttachmentAsync(Project(), card, request, cancellationToken), "Card")))
            .WithName("AddBoardAttachment");

        app.MapGet("/api/v1/board/cards/{card}/attachments/{attachmentId}/content", (IBoardService board, HttpContext context, string card, string attachmentId, CancellationToken cancellationToken) =>
            RunAsync(async () =>
            {
                var content = await board.GetAttachmentContentAsync(Project(), card, attachmentId, cancellationToken);
                if (content is null) return NotFound("Attachment", attachmentId);
                context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(content.Content, "application/octet-stream", content.Attachment.Name);
            }))
            .WithName("GetBoardAttachmentContent");

        app.MapDelete("/api/v1/board/cards/{card}/attachments/{attachmentId}", (IBoardService board, string card, string attachmentId, CancellationToken cancellationToken) =>
            RunAsync(async () => await board.DeleteAttachmentAsync(Project(), card, attachmentId, cancellationToken)
                ? Results.Ok(new OK("Attachment removed"))
                : NotFound("Attachment", attachmentId)))
            .WithName("DeleteBoardAttachment");

        app.MapGet("/api/v1/board/cards/{card}/commits", (IBoardService board, string card, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.GetCommitsAsync(Project(), card, cancellationToken), "Card")))
            .WithName("GetBoardCardCommits");

        app.MapPost("/api/v1/board/cards/{card}/commits", (IBoardService board, string card, LinkBoardCommitRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.LinkCommitAsync(Project(), card, request.Sha ?? string.Empty, cancellationToken), "Card")))
            .WithName("LinkBoardCardCommit");

        app.MapDelete("/api/v1/board/cards/{card}/commits/{sha}", (IBoardService board, string card, string sha, CancellationToken cancellationToken) =>
            RunAsync(async () => await board.UnlinkCommitAsync(Project(), card, sha, cancellationToken)
                ? Results.Ok(new OK("Commit unlinked"))
                : NotFound("Commit", sha)))
            .WithName("UnlinkBoardCardCommit");

        app.MapGet("/api/v1/board/cards/{card}/commits/{sha}/diff", (IBoardService board, string card, string sha, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.GetCommitDiffAsync(Project(), card, sha, cancellationToken), "Card")))
            .WithName("GetBoardCardCommitDiff");

        app.MapGet("/api/v1/board/cards/{card}/sessions", (IBoardService board, string card, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.GetSessionsAsync(Project(), card, cancellationToken), "Card")))
            .WithName("GetBoardCardSessions");

        app.MapPost("/api/v1/board/cards/{card}/sessions", (IBoardService board, string card, AddBoardSessionRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.LinkSessionAsync(Project(), card,
                string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString() : request.Id!,
                tabId: null, selection: string.Empty, cli: string.Empty,
                request.DisplayName ?? string.Empty, BoardSessionRecord.ManualOrigin, cancellationToken), "Card")))
            .WithName("LinkBoardCardSession");

        app.MapPut("/api/v1/board/cards/{card}/sessions/{sessionId}", (IBoardService board, string card, string sessionId, UpdateBoardSessionRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.RenameSessionAsync(Project(), card, sessionId, request.DisplayName ?? string.Empty, cancellationToken), "Session")))
            .WithName("RenameBoardCardSession");

        app.MapDelete("/api/v1/board/cards/{card}/sessions/{sessionId}", (IBoardService board, string card, string sessionId, CancellationToken cancellationToken) =>
            RunAsync(async () => await board.UnlinkSessionAsync(Project(), card, sessionId, cancellationToken)
                ? Results.Ok(new OK("Session unlinked"))
                : NotFound("Session", sessionId)))
            .WithName("UnlinkBoardCardSession");
    }

    private static string Project() => ParserConfigs.GetRootPath();

    private static IResult OkOrNotFound<T>(T? value, string kind) where T : class =>
        value is null ? Results.NotFound(new ErrorResponse($"{kind} not found.")) : Results.Ok(value);

    private static IResult NotFound(string kind, string id) =>
        Results.NotFound(new ErrorResponse($"{kind} not found: {id}"));

    /// <summary>Board rule violations become readable 400/409 bodies; everything else propagates as a 500.</summary>
    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (BoardValidationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (BoardConflictException ex)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
        catch (Services.Environments.PromptTooLongException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }
}
