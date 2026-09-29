using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Diagnostics;
using VibeRails_Front.Controllers;
using VibeRails_Front.Data;
using VibeRails_Front.Data.Entities;
using VibeRails_Front.Services;
using VibeRails_Front.Services.Boards;
using Xunit;
using DesktopSync = VibeRails.Services.Board.Sync.BoardSyncService;
using HostedSync = VibeRails_Front.Services.Boards.BoardSyncService;

namespace BoardSync.Integration;

public sealed class BoardSyncRoundTripTests : IDisposable
{
    private const int Owner = 23;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), "vb51-coupled-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryDatabaseRoot remoteRoot = new();
    private readonly string remoteName = "vb51-coupled-" + Guid.NewGuid().ToString("N");
    private readonly BoardStore store;
    private readonly DesktopSync sync;
    private readonly BoardSyncHttpClient client;
    private readonly ControllerTransport transport;
    private readonly string connectionString;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardSyncRoundTripTests()
    {
        Directory.CreateDirectory(root);
        connectionString = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new BoardStore(connectionString, connectionString);
        transport = new ControllerTransport(this);
        client = new BoardSyncHttpClient(new ClientFactory(transport),
            new Uri("https://board-sync.invalid/api/v1/boards"), () => "fixture-owner-key");
        sync = new DesktopSync(store, client, new BoardSyncLock(Path.Combine(root, "sync.lock")), NullFeatureLog.Instance);
        using var db = Db();
        db.Users.Add(new User { Id = Owner, Auth0Id = "auth0|fixture-owner" });
        db.SaveChanges();
    }

    [Fact]
    public async Task PublishAndExchangePreserveFieldsCommentsNotesAndLocalBoundaries()
    {
        var card = await LocalCard();
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Desktop comment", Ct);
        await store.AddNoteAsync(root, card.Id, BoardAuthor.Agent("Codex", "codex", "private-session-id"), "Desktop note", Ct);
        var published = await Publish(card);

        await using (var db = Db())
        {
            var hosted = new HostedSync(db, TimeProvider.System);
            var remote = (await hosted.GetCardAsync(Owner, published, card.Id, Ct))!;
            Assert.Equal(card.Key, remote.Card.Key);
            Assert.Equal("base:codex", remote.Card.Assignee);
            Assert.Equal("Read @src/file.cs", remote.Card.Description);
            Assert.Contains(remote.Log, entry => entry.Kind == "comment" && entry.Body == "Desktop comment");
            Assert.Contains(remote.Log, entry => entry.Kind == "note" && entry.Body == "Desktop note");
            await hosted.EditCardAsync(Owner, published, card.Id, "Owner", Json("""{"title":"Hosted title","priority":"high","points":3,"tags":["web"],"blocked":true,"flagged":true}"""), Ct);
            await hosted.CommentAsync(Owner, published, card.Id, "Owner", "Web comment", Ct);
        }

        // Agent notes are authored by desktop sessions. A second owner-side client supplies a
        // new note through the same API so this desktop must apply a genuinely unseen note.
        await client.PushAsync(published.ToString(), new(null, null, null,
        [
            new BoardSyncEntryWire("note_" + Guid.NewGuid().ToString("N"), card.Id, card.Key, "note",
                new("agent", "Codex", "codex"), "Remote owner note", DateTime.UtcNow, null)
        ]), Ct, client.DestinationKey);

        var status = await Sync(card);
        Assert.Equal(0, status.Unsent);
        var local = (await store.GetCardDetailAsync(root, card.Id, Ct))!;
        Assert.Equal("Hosted title", local.Card.Title);
        Assert.Equal("high", local.Card.Priority);
        Assert.Equal(3, local.Card.Points);
        Assert.Equal(["web"], local.Card.Tags);
        Assert.True(local.Card.Blocked);
        Assert.True(local.Card.Flagged);
        Assert.Equal("env:7:codex", local.Card.Assignee);
        Assert.Equal(2, local.Comments.Count);
        Assert.Equal(2, local.Notes.Count);
        Assert.Contains(local.Notes, entry => entry.Body == "Remote owner note");
        Assert.Contains(local.Comments, entry => entry.Body == "Web comment");

        var payload = string.Join("\n", transport.Bodies);
        Assert.Contains("@src/file.cs", payload);
        Assert.DoesNotContain("env:7", payload);
        Assert.DoesNotContain("private-session-id", payload);
        Assert.DoesNotContain("fixture-owner-key", payload);
        Assert.DoesNotContain("projectPath", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attachments", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("automations", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("terminalSessions", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfflineConflictsUseServerArrivalAndKeepUnrelatedFieldsAndHistory()
    {
        var card = await LocalCard();
        var remoteId = await Publish(card);
        // The desktop edits first while offline; its entry reaches the server after the web edit.
        await store.UpdateCardAsync(root, card.Id, new(Title: "Offline desktop title"), Ct);
        await using (var db = Db())
        {
            var hosted = new HostedSync(db, TimeProvider.System);
            await hosted.EditCardAsync(Owner, remoteId, card.Id, "Owner", Json("""{"title":"Web title","description":"Web description"}"""), Ct);
        }
        await Sync(card);
        var local = (await store.FindCardAsync(root, card.Id, Ct))!;
        Assert.Equal("Offline desktop title", local.Title);
        Assert.Equal("Web description", local.Description);
        await using var verify = Db();
        var remote = await verify.SyncedCards.SingleAsync(Ct);
        Assert.Equal(local.Title, remote.Title);
        Assert.Equal(local.Description, remote.Description);
        var history = await store.GetHistoryAsync(root, card.BoardId, card.Id, 0, Ct);
        Assert.Contains(history!, entry => entry.Changes?.Contains("Web title") == true);
        Assert.Contains(history!, entry => entry.Changes?.Contains("Offline desktop title") == true);
    }

    [Fact]
    public async Task LostPushAcknowledgementRetriesTheSameJsonWithoutDuplicateEntries()
    {
        var card = await LocalCard();
        transport.LoseNextPushAcknowledgement = true;
        var first = (await sync.SetPublishedAsync(root, card.BoardId, true, Ct))!;
        Assert.NotNull(first.LastError);
        Assert.Equal(1, first.Unsent);
        var after = await Sync(card);
        Assert.Equal(0, after.Unsent);
        Assert.Equal(1, after.Cursor);
        Assert.Equal(2, transport.PushBodies.Count);
        Assert.Equal(transport.PushBodies[0], transport.PushBodies[1]);
        await using var db = Db();
        Assert.Single(await db.SyncedCardLogEntries.ToListAsync(Ct));
        Assert.Single(await db.SyncedCards.ToListAsync(Ct));
    }

    [Fact]
    public async Task WebCardIdentitySurvivesPullAndLaneAutomationsQueueOnce()
    {
        var local = await LocalCard();
        var remoteId = await Publish(local);
        var lanes = await store.GetColumnsAsync(root, Ct);
        // Only the job-reference columns queried by BoardStore are needed in this disposable DB.
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Jobs (Id INTEGER PRIMARY KEY, ProjectPath TEXT, Enabled INTEGER, DeletedUTC TEXT);
                INSERT INTO Jobs SELECT 10, ProjectPath, 1, NULL FROM Boards LIMIT 1;
                INSERT INTO Jobs SELECT 20, ProjectPath, 1, NULL FROM Boards LIMIT 1;
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }
        await store.SaveLaneAutomationAsync(root, lanes[0].Id, [10], 0, Ct);
        await store.SaveLaneAutomationAsync(root, lanes[1].Id, [20], 0, Ct);
        WebWriteResult created;
        await using (var db = Db())
            created = await new HostedSync(db, TimeProvider.System).CreateCardAsync(Owner, remoteId, "Owner", "Web created", lanes[0].Id, Ct);
        await Sync(local);
        var pulled = (await store.FindCardAsync(root, created.CardId, Ct))!;
        Assert.Equal(created.Key, pulled.Key);
        Assert.NotEqual(local.Key, pulled.Key);
        Assert.Equal(10, Assert.Single(await store.GetPendingLaneAutomationsAsync(root, pulled.Id, Ct)).JobId);
        await Sync(local);
        Assert.Single(await store.GetPendingLaneAutomationsAsync(root, pulled.Id, Ct));
        await using (var db = Db())
            await new HostedSync(db, TimeProvider.System).MoveCardAsync(Owner, remoteId, pulled.Id, "Owner", lanes[1].Id, Ct);
        await Sync(local);
        Assert.Equal(lanes[1].Id, (await store.FindCardAsync(root, pulled.Id, Ct))!.ColumnId);
        var due = Assert.Single(await store.GetDueLaneAutomationsAsync(DateTime.UtcNow.AddMinutes(2), Ct));
        Assert.Equal(20, due.JobId);
        await Sync(local);
        Assert.Equal(due.EventKey, Assert.Single(await store.GetDueLaneAutomationsAsync(DateTime.UtcNow.AddMinutes(2), Ct)).EventKey);
        Assert.Equal(0, await store.CountUnsentLogEntriesAsync(local.BoardId, Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisplayIdCollisionsConvergeInEitherArrivalOrder(bool remoteFirst)
    {
        var baseline = await LocalCard();
        var board = (await store.GetBoardAsync(root, baseline.BoardId, Ct))!;
        await store.RenameBoardAsync(root, board.Id, board.Name, "VIBE", Ct);
        var remoteId = await Publish(baseline);
        var local = await store.CreateCardAsync(root, new(null, "Offline", "", null, "medium", null, [], false, DisplayId: "VIBE-1"), Ct);
        WebWriteResult web;
        if (!remoteFirst) await Sync(baseline);
        await using (var db = Db())
            web = await new HostedSync(db, TimeProvider.System).CreateCardAsync(Owner, remoteId, "Owner", "Web", baseline.ColumnId, Ct);
        await Sync(baseline);
        await Sync(baseline); // Delivers a local conflict correction, if one was needed.
        var localAfter = (await store.FindCardAsync(root, local.Id, Ct))!;
        var webAfter = (await store.FindCardAsync(root, web.CardId, Ct))!;
        Assert.Equal(local.Key, localAfter.Key);
        Assert.Equal(web.Key, webAfter.Key);
        Assert.NotEqual(localAfter.DisplayId, webAfter.DisplayId);
        Assert.Equal("VIBE-1", remoteFirst ? webAfter.DisplayId : localAfter.DisplayId);
        await using (var db = Db())
        {
            var hosted = new HostedSync(db, TimeProvider.System);
            Assert.Equal(localAfter.DisplayId, (await hosted.GetCardAsync(Owner, remoteId, local.Id, Ct))!.Card.DisplayId);
            Assert.Equal(webAfter.DisplayId, (await hosted.GetCardAsync(Owner, remoteId, web.CardId, Ct))!.Card.DisplayId);
            await hosted.EditCardAsync(Owner, remoteId, web.CardId, "Owner", Json("""{"displayId":"PRETTY-55"}"""), Ct);
        }
        await Sync(baseline);
        Assert.Equal(webAfter.Id, (await store.FindCardAsync(root, "pretty-55", Ct))!.Id);
        Assert.Equal(0, await store.CountUnsentLogEntriesAsync(board.Id, Ct));
    }

    private async Task<BoardCardRecord> LocalCard()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        return await store.CreateCardAsync(root,
            new(null, "Desktop title", "Read @src/file.cs", "env:7:codex", "medium", null, [], false), Ct);
    }

    [Fact]
    public async Task ActivitySnapshotCrossesRealWireWithSavedCodeAndAttachmentBytes_ThenReplacesRemovedLinks()
    {
        var card = await LocalCard();
        var related = await LocalCard();
        var session = Guid.NewGuid().ToString("D");
        var agentSession = Guid.NewGuid().ToString("D");
        var sha = new string('a', 40);
        await store.LinkSessionAsync(root, card.Id, session, "private-tab", "env:7:codex", "codex", "Implementation", "launch", Ct);
        await store.LinkSessionAsync(root, card.Id, agentSession, "private-agent-tab", "env:7:codex", "codex", "Code review", BoardSessionRecord.AutomationOrigin, Ct);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Sessions(Id TEXT PRIMARY KEY, EndedUTC TEXT, ExitCode INTEGER);
                CREATE TABLE ChatSummary(SessionId TEXT PRIMARY KEY, SummaryText TEXT);
                INSERT INTO Sessions VALUES($session, '2026-09-29T12:00:00Z', 0);
                INSERT INTO ChatSummary VALUES($session, 'Reviewed the saved code.');
                """;
            command.Parameters.AddWithValue("$session", agentSession);
            await command.ExecuteNonQueryAsync(Ct);
        }
        await store.AddCommitAsync(root, card.Id, sha, "Fixture author", "Fixture change", DateTime.UtcNow,
            new([new("src/file.cs", "csharp", "original", "saved replacement")], 1), Ct);
        var attachment = (await store.AddAttachmentContentAsync(root, card.Id, "result.txt", "text/plain", "saved attachment"u8.ToArray(), Ct))!;
        await store.LinkCardAsync(root, card.Id, related.Id, Ct);
        await sync.SyncDueAsync(Ct);
        var status = (await sync.GetStatusAsync(root, card.BoardId, Ct))!;
        Assert.Null(status.LastError);
        var remoteId = Guid.Parse(status.RemoteBoardId!);
        await using (var db = Db())
        {
            var row = await db.SyncedCardActivities.SingleAsync(a => a.BoardId == remoteId && a.CardId == card.Id, Ct);
            var snapshot = BoardActivityContract.Parse(Encoding.UTF8.GetBytes(row.SnapshotJson));
            Assert.Equal(2, snapshot.Sessions.Count);
            Assert.Contains(snapshot.Sessions, s => s.Id == session && !s.IsAutomation);
            var agent = Assert.Single(snapshot.Sessions, s => s.Id == agentSession);
            Assert.True(agent.IsAutomation);
            Assert.Equal(0, agent.ExitCode);
            Assert.NotNull(agent.EndedUtc);
            Assert.Equal("Reviewed the saved code.", agent.Summary);
            Assert.Equal("saved replacement", Assert.Single(Assert.Single(snapshot.Commits).Files).After);
            Assert.Equal("saved attachment", Encoding.UTF8.GetString(Convert.FromBase64String(Assert.Single(snapshot.Attachments).ContentBase64!)));
            Assert.Equal(related.Key, Assert.Single(snapshot.LinkedCards).Key);
        }
        Assert.DoesNotContain("private-tab", string.Join("\n", transport.Bodies));
        Assert.DoesNotContain("env:7", string.Join("\n", transport.Bodies));
        await store.UnlinkSessionAsync(root, card.Id, session, Ct);
        await store.UnlinkSessionAsync(root, card.Id, agentSession, Ct);
        await store.RemoveCommitAsync(root, card.Id, sha, Ct);
        await store.DeleteAttachmentAsync(root, card.Id, attachment.Id, Ct);
        await store.UnlinkCardAsync(root, card.Id, related.Id, Ct);
        await Sync(card);
        await using (var db = Db())
        {
            var row = await db.SyncedCardActivities.SingleAsync(a => a.BoardId == remoteId && a.CardId == card.Id, Ct);
            var snapshot = BoardActivityContract.Parse(Encoding.UTF8.GetBytes(row.SnapshotJson));
            Assert.Empty(snapshot.Sessions);
            Assert.Empty(snapshot.Commits);
            Assert.Empty(snapshot.Attachments);
            Assert.Empty(snapshot.LinkedCards);
        }
    }

    private async Task<Guid> Publish(BoardCardRecord card)
    {
        var status = (await sync.SetPublishedAsync(root, card.BoardId, true, Ct, includeActivity: true))!;
        Assert.Null(status.LastError);
        Assert.Equal(0, status.Unsent);
        return Guid.Parse(status.RemoteBoardId!);
    }

    private async Task<BoardSyncStatus> Sync(BoardCardRecord card)
    {
        var status = (await sync.SyncNowAsync(root, card.BoardId, Ct))!;
        Assert.Null(status.LastError);
        return status;
    }

    private VibeRailsDbContext Db()
    {
        var db = new VibeRailsDbContext(new DbContextOptionsBuilder<VibeRailsDbContext>()
            .UseInMemoryDatabase(remoteName, remoteRoot).Options);
        db.SetCurrentUser(Owner);
        return db;
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ControllerTransport(BoardSyncRoundTripTests fixture) : HttpMessageHandler
    {
        public bool LoseNextPushAcknowledgement { get; set; }
        public List<string> Bodies { get; } = [];
        public List<string> PushBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("board-sync.invalid", request.RequestUri!.Host);
            Assert.Equal("fixture-owner-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (body.Length > 0) Bodies.Add(body);
            var http = new DefaultHttpContext();
            http.Items[CurrentUser.ResolvedApiKeyItemKey] = new ApiKey
            {
                Id = 1, UserId = Owner, KeyHash = "fixture", KeyPrefix = "fixt", KeySuffix = "-key"
            };
            http.Request.ContentType = "application/json";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            await using var db = fixture.Db();
            var controller = new BoardsApiController(new HostedSync(db, TimeProvider.System))
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };
            var path = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            IActionResult result;
            if (path[^1] == "publish") result = await controller.Publish(ct);
            else if (path[^1] == "push")
            {
                PushBodies.Add(body);
                result = await controller.Push(path[^2], ct);
                if (LoseNextPushAcknowledgement && result is OkObjectResult)
                {
                    LoseNextPushAcknowledgement = false;
                    throw new HttpRequestException("Fixture drops an acknowledgement after the server commit.");
                }
            }
            else if (path[^1] == "activity")
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                var activity = new BoardActivityApiController(db)
                {
                    ControllerContext = new ControllerContext { HttpContext = http }
                };
                result = await activity.Replace(Guid.Parse(path[^4]), path[^2], ct);
            }
            else
            {
                Assert.Equal("entries", path[^1]);
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                result = await controller.Entries(path[^2], query["after"], query["limit"], ct);
            }
            var response = Assert.IsAssignableFrom<ObjectResult>(result);
            return new HttpResponseMessage((HttpStatusCode)(response.StatusCode ?? 200))
            {
                Content = new StringContent(JsonSerializer.Serialize(response.Value, response.Value!.GetType(), WebJson), Encoding.UTF8, "application/json")
            };
        }
    }

    public void Dispose()
    {
        transport.Dispose();
        // root is the unique directory allocated by this fixture, never an application directory.
        Directory.Delete(root, recursive: true);
    }
}
