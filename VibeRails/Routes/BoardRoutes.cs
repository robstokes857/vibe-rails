using Microsoft.AspNetCore.Http.Features;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Jira;
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

        var reviewRoutes = app.MapGroup("/api/v1/board/cards/{card}/reviews");
        reviewRoutes.AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return next(context);
        });
        reviewRoutes.MapGet("", ([Microsoft.AspNetCore.Mvc.FromServices] BoardReviewService reader, string card, int? offset, CancellationToken ct) =>
            RunAsync(async () => OkOrNotFound(await reader.ReadAsync(Project(), card, offset ?? 0, ct), "Card")));
        reviewRoutes.MapGet("/{reviewId}", ([Microsoft.AspNetCore.Mvc.FromServices] BoardReviewService reader, string card, string reviewId, bool? verify, CancellationToken ct) =>
            RunAsync(async () => OkOrNotFound(await reader.ReportAsync(Project(), card, reviewId, verify == true, Project(), ct), "Review")));

        var checks = app.MapGroup("/api/v1/board/cards/{card}/checks");
        checks.AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return next(context);
        });
        checks.MapGet("", ([Microsoft.AspNetCore.Mvc.FromServices] BoardChecksReader reader, string card, int? offset, CancellationToken ct) =>
            RunAsync(async () => OkOrNotFound(await reader.ReadAsync(Project(), card, offset ?? 0, ct), "Card")));
        checks.MapGet("/{checkId}", ([Microsoft.AspNetCore.Mvc.FromServices] BoardChecksReader reader, string card, string checkId, bool? verify, CancellationToken ct) =>
            RunAsync(async () => OkOrNotFound(await reader.ReportAsync(Project(), card, checkId, ct, verify == true), "Check")));

        // ---------------------------------------------------------------- boards
        var sharingRoutes = app.MapGroup("/api/v1/board");
        sharingRoutes.AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return next(context);
        });
        sharingRoutes.MapGet("/shared", ([Microsoft.AspNetCore.Mvc.FromServices] BoardSharingService sharing, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await sharing.DiscoverAsync(ct))));
        sharingRoutes.MapPost("/shared/{remoteId}/import", ([Microsoft.AspNetCore.Mvc.FromServices] BoardSharingService sharing, string remoteId, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await sharing.ImportAsync(Project(), remoteId, ct))));
        sharingRoutes.MapGet("/boards/{boardId}/sharing", ([Microsoft.AspNetCore.Mvc.FromServices] BoardSharingService sharing, string boardId, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await sharing.GetAsync(Project(), boardId, ct))));
        sharingRoutes.MapPost("/boards/{boardId}/sharing", ([Microsoft.AspNetCore.Mvc.FromServices] BoardSharingService sharing, string boardId, BoardSharingEmailRequest body, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await sharing.SaveAsync(Project(), boardId, null, body, ct))));
        sharingRoutes.MapPut("/boards/{boardId}/sharing/{inviteId}", ([Microsoft.AspNetCore.Mvc.FromServices] BoardSharingService sharing, string boardId, string inviteId, BoardSharingEmailRequest body, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await sharing.SaveAsync(Project(), boardId, inviteId, body, ct))));
        sharingRoutes.MapDelete("/boards/{boardId}/sharing/{inviteId}", ([Microsoft.AspNetCore.Mvc.FromServices] BoardSharingService sharing, string boardId, string inviteId, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await sharing.RemoveAsync(Project(), boardId, inviteId, ct))));
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

        // Human inspection only. History is not part of card responses or MCP tools.
        app.MapGet("/api/v1/board/boards/{boardId}/history", (IBoardService board, string boardId, string? card, int? offset, CancellationToken cancellationToken) =>
            RunAsync(async () =>
            {
                if (offset is < 0 or > 1_000_000) throw new BoardValidationException("Invalid history offset.");
                var rows = await board.GetHistoryAsync(Project(), boardId, card, offset ?? 0, cancellationToken);
                return rows is null ? Results.NotFound(new ErrorResponse("Board or card not found."))
                    : Results.Ok(new BoardHistoryResponse(rows.Take(100).ToList(), rows.Count > 100, (offset ?? 0) + Math.Min(rows.Count, 100)));
            })).WithName("GetBoardHistory");

        // ---------------------------------------------------------------- columns

        app.MapGet("/api/v1/board/boards/{boardId}/context", (IBoardService board, string boardId, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.GetContextSettingsAsync(Project(), boardId, cancellationToken), "Board")))
            .WithName("GetBoardContext");

        app.MapPut("/api/v1/board/boards/{boardId}/context", (IBoardService board, string boardId, UpdateBoardContextRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.SaveContextSettingsAsync(Project(), boardId, request, cancellationToken), "Board")))
            .WithName("SaveBoardContext");

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

        app.MapGet("/api/v1/board/columns/{columnId}/automation", (BoardAutomationService automation, string columnId, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await automation.GetAsync(Project(), columnId, cancellationToken), "Lane")))
            .WithName("GetBoardLaneAutomation");

        app.MapPut("/api/v1/board/columns/{columnId}/automation", (BoardAutomationService automation, string columnId, UpdateBoardLaneAutomationRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await automation.SaveAsync(Project(), columnId, request, cancellationToken), "Lane")))
            .WithName("SaveBoardLaneAutomation");

        app.MapGet("/api/v1/board/cards/{card}/automations", (BoardCardAutomationService automation, string card, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await automation.GetAsync(Project(), card, cancellationToken), "Card")))
            .WithName("GetBoardCardAutomations");

        app.MapPost("/api/v1/board/cards/{card}/automations", (BoardCardAutomationService automation, string card, RunBoardCardAutomationRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await automation.RunAsync(Project(), card, request.JobId, cancellationToken), "Card")))
            .WithName("RunBoardCardAutomation");

        app.MapGet("/api/v1/board/cards", (IBoardService board, string? boardId, int? pageSize, string? columnId,
            int? offset, string? continuationToken, string? q, string? assignee, string? type, string? priority, string? tag,
            string? origin, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(pageSize is int size
                ? await board.GetCardsPageAsync(Project(), new BoardCardPageQuery(size, columnId, offset ?? 0,
                    q, assignee, type, priority, tag, continuationToken, origin), cancellationToken, boardId)
                : await board.GetCardsAsync(Project(), cancellationToken, boardId))))
            .WithName("GetBoardCards");

        app.MapPost("/api/v1/board/cards", (IBoardService board, CreateBoardCardRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await board.CreateCardAsync(Project(), request, cancellationToken))))
            .WithName("CreateBoardCard");

        app.MapPost("/api/v1/board/cards/activity", (IBoardService board, BoardCardActivityRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await board.GetCardActivityAsync(Project(), request, cancellationToken))))
            .WithName("GetBoardCardActivity");

        app.MapGet("/api/v1/board/cards/{card}", (IBoardService board, string card, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.GetCardAsync(Project(), card, cancellationToken), "Card")))
            .WithName("GetBoardCard");

        app.MapPut("/api/v1/board/cards/{card}", (IBoardService board, string card, UpdateBoardCardRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.UpdateCardAsync(Project(), card, request, cancellationToken), "Card")))
            .WithName("UpdateBoardCard");

        app.MapDelete("/api/v1/board/cards/{card}", (IBoardService board, string card, CancellationToken cancellationToken) =>
            RunAsync(async () => await board.DeleteCardAsync(Project(), card, cancellationToken)
                ? Results.Ok(new OK("Card deleted"))
                : NotFound("Card", card)))
            .WithName("DeleteBoardCard");

        app.MapPost("/api/v1/board/cards/{card}/move", (IBoardService board, string card, MoveBoardCardRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound((await board.MoveCardAsync(Project(), card,
                new BoardCardMoveRequest(request.ColumnId ?? string.Empty, request.Position, request.SkipAutomations, BoardAuthor.User()), cancellationToken))?.Card, "Card")))
            .WithName("MoveBoardCard");

        app.MapPost("/api/v1/board/cards/{card}/merge", (IBoardService board, string card, MergeBoardCardsRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await board.MergeCardsAsync(Project(), card, request.TargetCard ?? string.Empty, cancellationToken), "Card")))
            .WithName("MergeBoardCards");

        app.MapDelete("/api/v1/board/cards/{card}/comments/{commentId}", (IBoardService board, string card, string commentId, CancellationToken cancellationToken) =>
            RunAsync(async () => await board.DeleteCommentAsync(Project(), card, commentId, BoardAuthor.User(), cancellationToken)
                ? Results.Ok(new OK("Comment deleted")) : NotFound("Comment", commentId)))
            .WithName("DeleteBoardComment");

        app.MapPost("/api/v1/board/cards/{card}/launch", (IBoardLaunchService launcher, string card, LaunchBoardCardRequest? request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await launcher.LaunchAsync(Project(), card, request?.Selection, cancellationToken, request?.Intent ?? "work"), "Card")))
            .WithName("LaunchBoardCard");

        // The context an agent launched on this card right now would receive (VB-63): the launch
        // prompt plus the first Board tool reads, as characters and estimated tokens, with the
        // latest recorded launch sample. Read-only; nothing is stored by asking.
        app.MapGet("/api/v1/board/cards/{card}/context", (IBoardContextEstimator context, string card, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(await context.EstimateAsync(Project(), card, cancellationToken), "Card")))
            .WithName("GetBoardCardContext");

        // ---------------------------------------------------------------- files
        //
        // Repo-wide file names for the composer's `@path` typeahead (VB-35): names only, never
        // contents, and only under the dashboard's root path. The text a user writes is the
        // reference; nothing is stored here.

        app.MapGet("/api/v1/board/files", (IBoardFileIndexService files, string? q, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await files.SearchAsync(Project(), q, cancellationToken))))
            .WithName("SearchBoardFiles");

        // ---------------------------------------------------------------- rails

        app.MapGet("/api/v1/board/cards/link-candidates", (IBoardService board, string? q, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(await board.GetCardLinkCandidatesAsync(Project(), null, q, cancellationToken))))
            .WithName("GetNewBoardCardLinkCandidates");

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

        // Jira Cloud, one saved JQL filter per board. The token is accepted on save and never
        // returned. Pull is one-way: Jira wins on the mapped fields.
        app.MapGet("/api/v1/board/boards/{boardId}/jira", (IJiraPullService jira, string boardId, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(ToResponse(await jira.GetAsync(Project(), boardId, cancellationToken), boardId))))
            .WithName("GetJiraConnection");

        app.MapPut("/api/v1/board/boards/{boardId}/jira", (IJiraPullService jira, string boardId, SaveJiraConnectionRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => Results.Ok(ToResponse(await jira.SaveAsync(Project(), boardId, new BoardJiraConnectionSave(
                request.SiteUrl ?? string.Empty, request.Email ?? string.Empty, request.StoryPointsFieldId,
                request.Jql ?? string.Empty, request.Enabled), request.ApiToken, cancellationToken), boardId))))
            .WithName("SaveJiraConnection");

        app.MapPost("/api/v1/board/boards/{boardId}/jira/test", (IJiraPullService jira, string boardId, CancellationToken cancellationToken) =>
            RunAsync(async () =>
            {
                var result = await jira.TestAsync(Project(), boardId, cancellationToken);
                return Results.Ok(new JiraTestResponse(result.Outcome == JiraCallOutcome.Ok, result.Value, result.Detail));
            }))
            .WithName("TestJiraConnection");

        app.MapPost("/api/v1/board/boards/{boardId}/jira/pull", (IJiraPullService jira, string boardId, bool? dryRun, CancellationToken cancellationToken) =>
            RunAsync(async () =>
            {
                var report = await jira.PullAsync(Project(), boardId, dryRun == true, cancellationToken);
                return Results.Ok(new JiraPullResponse(report.DryRun, report.Outcome, report.Created, report.Updated, report.Skipped, report.Failed, report.Message));
            }))
            .WithName("PullJiraFilter");

        // ---------------------------------------------------------------- viberails.ai sync (VB-51)
        //
        // Status, the per-board Publish switch, and a manual sync. Outbound only: the desktop talks
        // to viberails.ai with the saved API key, which never appears in these responses.
        app.MapGet("/api/v1/board/boards/{boardId}/sync", (IBoardSyncService sync, string boardId, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(ToResponse(await sync.GetStatusAsync(Project(), boardId, cancellationToken)), "Board")))
            .WithName("GetBoardSyncStatus");

        app.MapPut("/api/v1/board/boards/{boardId}/sync", (IBoardSyncService sync, string boardId, SetBoardSyncRequest request, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(ToResponse(await sync.SetPublishedAsync(Project(), boardId, request.Enabled, cancellationToken, request.IncludeActivity)), "Board")))
            .WithName("SetBoardSync");

        app.MapPost("/api/v1/board/boards/{boardId}/sync/now", (IBoardSyncService sync, string boardId, CancellationToken cancellationToken) =>
            RunAsync(async () => OkOrNotFound(ToResponse(await sync.SyncNowAsync(Project(), boardId, cancellationToken)), "Board")))
            .WithName("SyncBoardNow");
    }

    private static BoardSyncStatusResponse? ToResponse(BoardSyncStatus? status) =>
        status is null
            ? null
            : new BoardSyncStatusResponse(status.BoardId, status.Published, status.Enabled, status.RemoteBoardId, status.RemoteUrl,
                status.Cursor, status.Unsent, status.LastSyncUtc, status.LastError, status.Configured, status.Rejected, status.RejectedEntries,
                status.Skipped, status.SkippedEntries, status.ActivityEnabled);

    private static JiraConnectionResponse ToResponse(BoardJiraConnectionRecord? connection, string boardId) =>
        connection is null
            ? new JiraConnectionResponse(boardId, null, null, false, BoardJiraAuthStatus.None, null, null, false, null, null, null, null, true)
            : new JiraConnectionResponse(connection.BoardId, connection.SiteUrl, connection.Email, connection.HasToken,
                connection.AuthStatus, connection.StoryPointsFieldId, connection.Jql, connection.Enabled,
                connection.DisabledReason, connection.LastTestedUtc, connection.LastPullUtc, connection.LastReport, true);

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
        catch (JiraConfigException ex)
        {
            // Bad site/email/JQL/field, no token, or an expired connection: the message says which.
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (BoardConflictException ex)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
        catch (BoardSyncClientException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.Status is >= 400 and <= 599 ? ex.Status : 502);
        }
        catch (Services.Jobs.JobServiceException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.StatusCode);
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
