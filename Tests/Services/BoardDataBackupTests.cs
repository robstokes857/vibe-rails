using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Backup;
using Xunit;

namespace Tests.Services;

public sealed class BoardDataBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-data-backup-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly string cs;
    private DateTime now = DateTime.UtcNow;
    private string key = "first-account";
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardDataBackupTests()
    {
        Directory.CreateDirectory(root);
        cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new(cs, cs);
    }

    [Fact]
    public async Task CaptureIncludesUnpublishedAndDeletedCardsHistoryFutureTablesAndExactLargeValues()
    {
        var board = await store.CreateBoardAsync(root, "Private board", Ct);
        var other = await store.CreateBoardAsync(Path.Combine(root, "other"), "Other project", Ct);
        var card = await store.CreateCardAsync(root, new(null, "Retained", "original", null, "medium", null, [], false, BoardId: board.Id), Ct);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "never published", Ct);
        var binary = new byte[5 * 1024 * 1024 + 13]; new Random(7).NextBytes(binary);
        var text = string.Concat(Enumerable.Repeat("full text 🦉\r\n", 20000));
        await using (var db = new SqliteConnection(cs))
        {
            await db.OpenAsync(Ct); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE FutureBoardData (Id TEXT PRIMARY KEY, Value TEXT, Bytes BLOB); INSERT INTO FutureBoardData VALUES ('one',$text,$bytes); UPDATE BoardCards SET DeletedUTC='2026-10-25T00:00:00Z' WHERE Id=$card;";
            command.Parameters.AddWithValue("$text", text); command.Parameters.AddWithValue("$bytes", binary); command.Parameters.AddWithValue("$card", card.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }
        var items = new List<BoardDataBackupItem>();
        await store.ReadDataBackupAsync((item, _) => { items.Add(item); return Task.CompletedTask; }, Ct);
        var rows = items.Where(x => x.Json is not null).Select(x => JsonDocument.Parse(x.Json!)).ToArray();
        try
        {
            Assert.Contains(rows, x => x.RootElement.GetProperty("table").GetString() == "$schema");
            Assert.Contains(rows, x => Table(x) == "Boards" && Values(x).GetProperty("Id").GetString() == other.Id);
            Assert.Contains(rows, x => Table(x) == "BoardCards" && Values(x).GetProperty("Id").GetString() == card.Id
                && Values(x).GetProperty("DeletedUTC").ValueKind == JsonValueKind.String);
            Assert.Contains(rows, x => Table(x) == "BoardComments" && Values(x).GetProperty("Body").GetString() == "never published");
            var future = Assert.Single(rows.Where(x => Table(x) == "FutureBoardData"));
            var parts = items.Where(x => x.Content is not null).ToDictionary(x => x.Sha256, x => x.Content!);
            Assert.Equal(binary, Restore(Values(future).GetProperty("Bytes"), parts));
            Assert.Equal(text, Encoding.UTF8.GetString(Restore(Values(future).GetProperty("Value"), parts)));
            Assert.All(parts.Values, x => Assert.InRange(x.Length, 1, BoardBackupProtocol.ContentBytes));
        }
        finally { foreach (var row in rows) row.Dispose(); }
    }

    [Fact]
    public async Task SnapshotIsCoherentWhileAnotherConnectionWrites()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Before", "", null, "medium", null, [], false), Ct);
        var values = new List<BoardDataBackupItem>(); var changed = false;
        await store.ReadDataBackupAsync(async (item, ct) =>
        {
            values.Add(item);
            if (changed) return;
            changed = true;
            await store.UpdateCardAsync(root, card.Id, new(Title: "After"), ct);
        }, Ct);
        using var row = JsonDocument.Parse(Assert.Single(values.Where(x => x.Json is not null
            && JsonDocument.Parse(x.Json).RootElement.GetProperty("table").GetString() == "BoardCards")).Json!);
        Assert.Equal("Before", Values(row).GetProperty("Title").GetString());
        Assert.Equal("After", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
    }

    [Fact]
    public async Task LostCommitReplyResumesSameSnapshotAndOnlyAcknowledgesMatchingReceipt()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        await store.CreateCardAsync(root, new(null, "Backed up", "", null, "medium", null, [], false), Ct);
        using var cloud = new Cloud { FailCommitOnce = true };
        var service = Service(cloud);
        await service.TickAsync(Ct);
        Assert.Null(service.GetStatus().Receipt);
        var pending = service.GetStatus().PendingVersion;
        Assert.NotNull(pending);
        now = now.AddMinutes(3);
        await Service(cloud).TickAsync(Ct);
        var receipt = service.GetStatus().Receipt;
        Assert.NotNull(receipt); Assert.Equal(pending, receipt.Version);
        Assert.Equal(2, cloud.CommitVersions.Count); Assert.Single(cloud.CommitVersions.Distinct());
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "board-data-backups"), "*.bin", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AccountChangeDoesNotReuseAnotherAccountsReceipt()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        await store.CreateCardAsync(root, new(null, "Card", "", null, "medium", null, [], false), Ct);
        using var cloud = new Cloud(); var service = Service(cloud);
        await service.TickAsync(Ct); Assert.Equal(1, service.GetStatus().Receipt!.AccountId);
        now = now.AddMinutes(16);
        await service.TickAsync(Ct);
        Assert.Single(cloud.CommitVersions); // An unchanged complete capture needs no new version.
        key = "second-account";
        Assert.Null(service.GetStatus().Receipt);
        await service.TickAsync(Ct); Assert.Equal(2, service.GetStatus().Receipt!.AccountId);
        Assert.Equal(2, cloud.CommitVersions.Count);
    }

    [Fact]
    public async Task ChangedCardReusesPreviouslyAcknowledgedBinaryContent()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Before", "", null, "medium", null, [], false), Ct);
        await using (var db = new SqliteConnection(cs))
        {
            await db.OpenAsync(Ct); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE OriginalBytes(Id TEXT PRIMARY KEY, Value BLOB); INSERT INTO OriginalBytes VALUES('one',x'01020304');";
            await command.ExecuteNonQueryAsync(Ct);
        }
        using var cloud = new Cloud(); var service = Service(cloud);
        await service.TickAsync(Ct); Assert.Equal(1, cloud.ContentUploads);
        await store.UpdateCardAsync(root, card.Id, new(Title: "After"), Ct); now = now.AddMinutes(16);
        await service.TickAsync(Ct);
        Assert.Equal(2, cloud.CommitVersions.Count); Assert.Equal(1, cloud.ContentUploads);
    }

    [Fact]
    public async Task CorruptCheckpointIsPreservedAndAutomaticallyRecaptured()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        using var cloud = new Cloud(); var service = Service(cloud);
        await service.TickAsync(Ct);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root, "board-data-backups"), "checkpoint.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(path, "{broken", Ct);
        await service.TickAsync(Ct);
        Assert.NotNull(service.GetStatus().Receipt);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "checkpoint.json.corrupt-*"));
    }

    private BoardDataBackupService Service(Cloud cloud) => new(store, new(new HttpClient(cloud, disposeHandler: false)), () => root, () => key, () => now);
    private static string? Table(JsonDocument row) => row.RootElement.GetProperty("table").GetString();
    private static JsonElement Values(JsonDocument row) => row.RootElement.GetProperty("values");
    private static byte[] Restore(JsonElement value, Dictionary<string, byte[]> parts) => value.GetProperty("parts")
        .EnumerateArray().SelectMany(x => parts[x.GetString()!]).ToArray();

    private sealed class Cloud : HttpMessageHandler
    {
        public bool FailCommitOnce;
        public int ContentUploads;
        public List<string> CommitVersions { get; } = [];
        private readonly HashSet<string> known = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("viberails.ai", request.RequestUri!.Host);
            Assert.StartsWith("/api/v1/data-exports/board/", request.RequestUri.AbsolutePath);
            var owner = request.Headers.GetValues("X-Api-Key").Single() == "first-account" ? 1 : 2;
            var path = request.RequestUri.AbsolutePath;
            object reply;
            if (path.EndsWith("/account")) reply = new BoardBackupAccount(owner);
            else
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
                if (path.EndsWith("/inventory"))
                {
                    var inventory = JsonSerializer.Deserialize(bytes, BoardBackupJson.Default.BoardBackupInventoryRequest)!;
                    reply = new BoardBackupInventory(inventory.Hashes.Where(x => !known.Contains(owner + ":" + inventory.Kind + ":" + x)).ToList());
                }
                else if (path.Contains("/content/"))
                {
                    ContentUploads++;
                    var hash = BoardBackupProtocol.Hash(bytes); known.Add(owner + ":content:" + hash);
                    reply = new BoardBackupContentReceipt(hash, bytes.Length);
                }
                else if (path.EndsWith("/rows"))
                {
                    using var rows = JsonDocument.Parse(bytes);
                    var hashes = rows.RootElement.EnumerateArray().Select(x => BoardBackupProtocol.Hash(Encoding.UTF8.GetBytes(x.GetRawText()))).ToList();
                    foreach (var hash in hashes) known.Add(owner + ":row:" + hash);
                    reply = new BoardBackupRowsReceipt(hashes);
                }
                else
                {
                    var manifest = JsonSerializer.Deserialize(bytes, BoardBackupJson.Default.BoardBackupManifest)!;
                    CommitVersions.Add(manifest.Version);
                    if (FailCommitOnce) { FailCommitOnce = false; return new(HttpStatusCode.ServiceUnavailable); }
                    reply = new BoardBackupReceipt(owner, manifest.ComputerId, manifest.Version, BoardBackupProtocol.Hash(bytes), manifest.Records.Count, manifest.Contents.Count, DateTime.UtcNow);
                }
            }
            var json = reply switch
            {
                BoardBackupAccount value => JsonSerializer.Serialize(value, BoardBackupJson.Default.BoardBackupAccount),
                BoardBackupContentReceipt value => JsonSerializer.Serialize(value, BoardBackupJson.Default.BoardBackupContentReceipt),
                BoardBackupRowsReceipt value => JsonSerializer.Serialize(value, BoardBackupJson.Default.BoardBackupRowsReceipt),
                BoardBackupInventory value => JsonSerializer.Serialize(value, BoardBackupJson.Default.BoardBackupInventory),
                BoardBackupReceipt value => JsonSerializer.Serialize(value, BoardBackupJson.Default.BoardBackupReceipt),
                _ => throw new InvalidOperationException()
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
}
