using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sharing;
using VibeRails.Services.Board.Sync;
using VibeRails.Utils;
using VibeRails_Front.Data;
using VibeRails_Front.Data.Entities;
using VibeRails_Front.Data.Ownership;
using VibeRails_Front.Services;
using VibeRails_Front.Services.FileStorage;
using Hosted = VibeRails_Front.Services.CardSharing;
using Xunit;

namespace BoardSync.Integration;

public sealed class CardSharingRoundTripTests
{
    [Fact]
    public async Task SavedCardToPublicDocumentsAndReplay_RefreshAndRevoke_RoundTripAcrossBothProducts()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "card-sharing-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousKey = ParserConfigs.GetApiKey();
        const string key = "synthetic-card-account";
        ParserConfigs.SetApiKey(key);
        try
        {
            var sessions = new Repository($"Data Source={Path.Combine(root, "state.db")};Pooling=False");
            var boards = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", $"Data Source={Path.Combine(root, "state.db")};Pooling=False");
            await boards.EnsureDefaultColumnsAsync(root, ct);
            var columns = await boards.GetColumnsAsync(root, ct);
            var card = (await boards.CreateCardAsync(root, new(columns[0].Id, "Shared work", "Design @src/app.cs", null, "medium", null, [], false), ct))!;
            var fullDescription = new string('d', 100000);
            await boards.UpdateCardAsync(root, card.Id, new(Description: fullDescription), ct);
            await boards.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Review notes", ct);
            var document = Encoding.UTF8.GetBytes("# Design\n" + new string('d', 2 * 1024 * 1024));
            await boards.AddAttachmentContentAsync(root, card.Id, "design.md", "text/markdown", document, ct);
            await boards.AddCommitAsync(root, card.Id, new string('a', 40), "Developer", "Saved change", DateTime.UtcNow,
                new SandboxDiffResponse([new("app.cs", "csharp", "before", "after")], 1), ct);
            var sessionId = Guid.NewGuid().ToString("D");
            await sessions.CreateSessionAsync(sessionId, "codex", null, root, Environment.ProcessId);
            await sessions.LogSessionOutputAsync(sessionId, Encoding.UTF8.GetBytes("Round trip replay\r\n"));
            await sessions.CompleteSessionAsync(sessionId, 0);
            await boards.LinkSessionAsync(root, card.Id, sessionId, null, "base:codex", "codex", "Implementation", "launch", ct);
            await sessions.SaveChatSummaryAsync(new ChatSummary { SessionId = sessionId, SummaryText = "Full session summary", Date = DateTime.UtcNow }, ct);

            var remoteRoot = new InMemoryDatabaseRoot();
            var remoteName = "card-roundtrip-" + Guid.NewGuid().ToString("N");
            VibeRailsDbContext Db()
            {
                var db = new VibeRailsDbContext(new DbContextOptionsBuilder<VibeRailsDbContext>().UseInMemoryDatabase(remoteName, remoteRoot).Options);
                db.SetCurrentUser(23); return db;
            }
            await using (var db = Db())
            {
                db.Users.Add(new User { Id = 23, Auth0Id = "auth0|synthetic-card-owner" });
                using (db.BeginPrivilegedWrite(PrivilegedWrites.IdentityBootstrap)) await db.SaveChangesAsync(ct);
                db.ApiKeys.Add(new ApiKey { Id = 1, UserId = 23, KeyHash = "fixture", Capabilities = ApiKeyCapability.Boards | ApiKeyCapability.Sessions,
                    CreatedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddMonths(2) });
                await db.SaveChangesAsync(ct);
            }
            using var transport = new Transport(Db, key);
            using var http = new HttpClient(transport);
            var publisher = new CardSharePublisher(boards, new(boards, sessions, sessions), new(http), sessions,
                new BoardSyncLock(Path.Combine(root, "sync.lock")), new());
            var result = await publisher.CreateAsync(root, card.Id, "Release review", ct);
            Assert.True(result.Success, result.Message);
            Assert.Equal(card.Id, result.Link!.LocalCardId);
            var publicKey = result.Link.SharePath.Split("#key=")[1];
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            Assert.Equal(sessionId, (await sessions.GetNextSharedSessionAsync(fingerprint, DateTime.UtcNow, ct))!.SessionId);

            await using var readDb = Db();
            var access = new Hosted.CardSharingAccess(readDb, TimeProvider.System, new VibeRails_Front.Services.Sharing.ShareViewerResolver());
            var json = JsonSerializer.SerializeToElement((await access.ReadAsync(publicKey, ct)).Value, Hosted.CardShareContract.Json);
            Assert.Equal("Shared work", json.GetProperty("title").GetString());
            Assert.Equal("Review notes", json.GetProperty("discussion")[0].GetProperty("body").GetString());
            Assert.Equal("after", json.GetProperty("commits")[0].GetProperty("files")[0].GetProperty("after").GetString());
            Assert.Equal("pending_upload", json.GetProperty("sessions")[0].GetProperty("status").GetString());
            Assert.Equal("Full session summary", json.GetProperty("sessions")[0].GetProperty("summary").GetString());
            Assert.Contains(json.GetProperty("history").EnumerateArray(), h => h.GetProperty("body").GetString()!.Contains(fullDescription));
            var revision = json.GetProperty("revision").GetString();
            var attachment = (await access.AttachmentAsync(publicKey, 1, revision, ct)).Value!;
            Assert.Equal(document, Convert.FromBase64String(attachment.ContentBase64));
            Assert.Null((await access.AttachmentAsync(publicKey, 2, revision, ct)).Value);

            // Transfer the actual local archive into synthetic private storage. Upload protocol
            // auth/retries are covered separately; this proves the current encoder/decoder pair.
            using var exported = new MemoryStream();
            Assert.NotNull(await sessions.WriteSessionExportAsync(sessionId, exported, ct));
            using var compressed = new MemoryStream();
            await using (var brotli = new BrotliStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                await brotli.WriteAsync(exported.ToArray(), ct);
            var blob = compressed.ToArray();
            await using (var db = Db())
            {
                db.DataEnvelopes.Add(new DataEnvelope { UserId = 23, Kind = "session", SourceId = Guid.Parse(sessionId), SchemaVersion = 2,
                    ComputerName = "fixture", BlobName = "fixture-recording", Sha256 = Convert.ToHexStringLower(SHA256.HashData(blob)), CompressedBytes = blob.Length });
                await db.SaveChangesAsync(ct);
                Assert.Empty(await db.SessionSharingLinks.ToListAsync(ct));
            }
            var replayReader = new SessionEnvelopeReadService(readDb, new Storage(blob), NullLogger<SessionEnvelopeReadService>.Instance);
            var replay = await replayReader.OpenCardSharedReplayPayloadAsync(publicKey, 1, revision, ct);
            Assert.Equal(SessionEnvelopeReadStatus.Ok, replay.Status);
            using (replay.Content)
            {
                var payload = await JsonDocument.ParseAsync(replay.Content!, cancellationToken: ct);
                Assert.Contains("Um91bmQgdHJpcCByZXBsYXk", payload.RootElement.GetRawText());
            }
            Assert.Equal(SessionEnvelopeReadStatus.NotFound, (await replayReader.OpenCardSharedReplayPayloadAsync(publicKey, 2, revision, ct)).Status);

            await boards.UpdateCardAsync(root, card.Id, new(Description: "Updated saved description"), ct);
            await publisher.RefreshDueAsync(ct);
            await publisher.RefreshDueAsync(ct);
            Assert.Equal(1, transport.Refreshes); // Includes desktop/hosted serialization and hash compatibility.
            json = JsonSerializer.SerializeToElement((await access.ReadAsync(publicKey, ct)).Value, Hosted.CardShareContract.Json);
            Assert.Equal("Updated saved description", json.GetProperty("description").GetString());
            Assert.Contains(json.GetProperty("history").EnumerateArray(), h => h.GetProperty("body").GetString()!.Contains(fullDescription));
            Assert.Contains(json.GetProperty("history").EnumerateArray(), h => h.GetProperty("body").GetString()!.Contains("to: Updated saved description"));
            Assert.Equal("ready", json.GetProperty("sessions")[0].GetProperty("status").GetString());
            Assert.Null((await access.AttachmentAsync(publicKey, 1, revision, ct)).Value);
            Assert.True((await publisher.RenameAsync(root, card.Id, result.Link.Id, "Renamed review", ct)).Success);
            Assert.Equal("Renamed review", Assert.Single((await publisher.ListAsync(root, card.Id, null, ct)).Links!).DisplayName);
            Assert.True((await publisher.RevokeAsync(root, card.Id, result.Link.Id, ct)).Success);
            Assert.Null((await access.ReadAsync(publicKey, ct)).Value);
            Assert.Null((await access.AttachmentAsync(publicKey, 1, json.GetProperty("revision").GetString(), ct)).Value);
            Assert.Equal(SessionEnvelopeReadStatus.NotFound, (await replayReader.OpenCardSharedReplayPayloadAsync(publicKey, 1, revision, ct)).Status);
        }
        finally
        {
            ParserConfigs.SetApiKey(previousKey);
            Directory.Delete(root, recursive: true); // Only the unique disposable fixture allocated above.
        }
    }

    private sealed class Transport(Func<VibeRailsDbContext> dbFactory, string key) : HttpMessageHandler
    {
        public int Refreshes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://viberails.ai", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal(key, request.Headers.GetValues("X-Api-Key").Single());
            await using var db = dbFactory(); var service = new Hosted.CardSharingService(db, TimeProvider.System);
            var path = request.RequestUri.AbsolutePath; var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            object? result;
            var status = HttpStatusCode.OK;
            if (path.EndsWith("/sources")) result = await service.SourcesAsync(1, int.Parse(query["after"].ToString()), ct);
            else if (path.Contains("/sources/"))
            {
                Refreshes++;
                result = await service.RefreshAsync(int.Parse(path.Split('/')[^1]), 1, JsonSerializer.Deserialize<Hosted.CardShareRefreshRequest>(body, Hosted.CardShareContract.Json)!, ct);
            }
            else if (request.Method == HttpMethod.Post)
            {
                result = await service.PublishAsync(1, JsonSerializer.Deserialize<Hosted.CardSharePublishRequest>(body, Hosted.CardShareContract.Json)!, ct);
                status = HttpStatusCode.Created;
            }
            else if (request.Method == HttpMethod.Get) result = await service.ListAsync(query["sourceKey"], int.TryParse(query.GetValueOrDefault("before"), out var before) ? before : null, ct, query["localCardId"]);
            else
            {
                var id = int.Parse(path.Split('/')[^1]);
                Assert.True(request.Method == HttpMethod.Delete ? await service.RevokeAsync(id, ct)
                    : await service.RenameAsync(id, JsonSerializer.Deserialize<Hosted.CardShareNameRequest>(body, Hosted.CardShareContract.Json)!.DisplayName, ct));
                return new(HttpStatusCode.NoContent);
            }
            return new(status) { Content = new StringContent(JsonSerializer.Serialize(result, Hosted.CardShareContract.Json), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Storage(byte[] blob) : IFileStorageService
    {
        public Task<FileDownloadResult> OpenReadAsync(string name, CancellationToken ct) => Task.FromResult(new FileDownloadResult(FileDownloadStatus.Ok, name, new MemoryStream(blob, false), blob.Length));
        public Task<FileUploadResult> UploadAsync(FileUploadRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StagedBlocksResult> GetStagedBlocksAsync(StagedBlocksRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StageBlockResult> StageBlockAsync(StageBlockRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<FileUploadResult> CommitBlocksAsync(CommitBlocksRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
