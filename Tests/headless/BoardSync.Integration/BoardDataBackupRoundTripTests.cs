using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeRails.Services.Board;
using VibeRails_Front.Data;
using VibeRails_Front.Data.Entities;
using VibeRails_Front.Data.Ownership;
using Hosted = VibeRails_Front.Services.BoardBackups;
using Xunit;

namespace BoardSync.Integration;

public sealed class BoardDataBackupRoundTripTests
{
    [Fact]
    public async Task CompleteLocalCapturePersistsQueryableRowsAndOriginalContentWithoutUiPublication()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "board-backup-coupled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
            var store = new BoardStore(cs, cs);
            var board = await store.CreateBoardAsync(root, "Unpublished", ct);
            var card = await store.CreateCardAsync(root, new(null, "Full card", "Complete description", null, "medium", null, [], false, BoardId: board.Id), ct);
            await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "unsent discussion", ct);
            var original = new byte[5 * 1024 * 1024 + 31]; new Random(97).NextBytes(original);
            var code = string.Concat(Enumerable.Repeat("full original code 🦉\r\n", 30000));
            await using (var local = new SqliteConnection(cs))
            {
                await local.OpenAsync(ct); using var command = local.CreateCommand();
                command.CommandText = "CREATE TABLE FutureContent(Id TEXT PRIMARY KEY, Original BLOB, Code TEXT); INSERT INTO FutureContent VALUES('content',$bytes,$code); UPDATE BoardCards SET DeletedUTC='2026-10-25T00:00:00Z' WHERE Id=$card";
                command.Parameters.AddWithValue("$bytes", original); command.Parameters.AddWithValue("$code", code); command.Parameters.AddWithValue("$card", card.Id);
                await command.ExecuteNonQueryAsync(ct);
            }
            await using var db = new VibeRailsDbContext(new DbContextOptionsBuilder<VibeRailsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            db.Users.Add(new User { Id = 1, Auth0Id = "test|backup" });
            using (db.BeginPrivilegedWrite(PrivilegedWrites.IdentityBootstrap)) await db.SaveChangesAsync(ct);
            db.SetCurrentUser(1);
            var backup = new Hosted.BoardDataBackupService(db);
            var captured = new List<BoardDataBackupItem>();
            await store.ReadDataBackupAsync((item, _) => { captured.Add(item); return Task.CompletedTask; }, ct);
            foreach (var item in captured.Where(x => x.Content is not null).DistinctBy(x => x.Sha256))
                await backup.PutContentAsync(item.Sha256, item.Content!, ct);
            foreach (var batch in captured.Where(x => x.Json is not null).DistinctBy(x => x.Sha256).Chunk(64))
            {
                using var json = JsonDocument.Parse("[" + string.Join(',', batch.Select(x => x.Json)) + "]");
                var receipt = await backup.PutRowsAsync(json.RootElement, ct);
                Assert.Equal(batch.Select(x => x.Sha256), receipt.Hashes);
            }
            var manifest = new Hosted.BoardBackupManifest(1, Hosted.BoardBackupProtocol.NewComputerId(), "fixture", Hosted.BoardBackupProtocol.NewVersion(),
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, captured.Where(x => x.Json is not null).Select(x => x.Sha256).Distinct().ToList(),
                captured.Where(x => x.Content is not null).Select(x => x.Sha256).Distinct().ToList());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await backup.CommitAsync(manifest.Version, Hosted.BoardBackupProtocol.Hash(bytes), bytes, ct);
            var cards = await db.BoardDataBackups.Where(x => x.TableName == "BoardCards").ToListAsync(ct);
            using (var json = JsonDocument.Parse(Assert.Single(cards).PayloadJson!))
            {
                Assert.Equal("Full card", json.RootElement.GetProperty("values").GetProperty("Title").GetString());
                Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("values").GetProperty("DeletedUTC").ValueKind);
            }
            using var contentRow = JsonDocument.Parse((await db.BoardDataBackups.SingleAsync(x => x.TableName == "FutureContent", ct)).PayloadJson!);
            var parts = await db.BoardDataBackups.Where(x => x.Kind == "content").ToDictionaryAsync(x => x.Sha256, x => x.Content!, ct);
            byte[] Restore(string column) => contentRow.RootElement.GetProperty("values").GetProperty(column).GetProperty("parts")
                .EnumerateArray().SelectMany(x => parts[x.GetString()!]).ToArray();
            Assert.Equal(original, Restore("Original")); Assert.Equal(code, Encoding.UTF8.GetString(Restore("Code")));
            Assert.Empty(await db.SyncedCards.ToListAsync(ct)); Assert.Empty(await db.BackupParts.ToListAsync(ct));
            Assert.Single(await db.BoardDataBackupSnapshots.ToListAsync(ct));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
