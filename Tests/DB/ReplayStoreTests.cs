using System.Text;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.Data.Sqlite.Replay;
using Xunit;

namespace Tests.DB;

public sealed class ReplayStoreTests
{
    [Fact]
    public void PagesAndDetailsStayWithinSessionAndSnapshot_WithoutWritingStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "viberails-replay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        try
        {
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE SessionLogs(Id INTEGER PRIMARY KEY, SessionId TEXT, Timestamp TEXT, Content BLOB);
                    CREATE TABLE UserInputs(Id INTEGER PRIMARY KEY, SessionId TEXT);
                    CREATE TABLE InputFileChanges(Id INTEGER PRIMARY KEY, UserInputId INTEGER, FilePath TEXT, DiffContent TEXT);
                    INSERT INTO SessionLogs VALUES(1,'one','2026-09-30T00:00:00Z',X'414243'),(2,'two','2026-09-30T00:00:00Z',X'444546'),(3,'one','2026-09-30T00:00:01Z',X'474849');
                    INSERT INTO UserInputs VALUES(1,'one'),(2,'two');
                    INSERT INTO InputFileChanges VALUES(1,1,'one.js','+one'),(2,2,'two.js','+two');
                    """;
                command.ExecuteNonQuery();
            }
            var before = File.ReadAllBytes(path);
            var store = new ReplayStore(new SqliteStoragePaths(path));
            var first = store.Frames("one", 0, 1, "raw");
            Assert.Equal("ABC", Encoding.UTF8.GetString(Assert.Single(first.Items).Data));
            Assert.True(first.Done);
            var next = store.Frames("one", 1, 3, "raw");
            Assert.Equal(3, Assert.Single(next.Items).Id);
            Assert.Null(store.Diff("one", 2));
            Assert.Equal("+one", store.Diff("one", 1)!.Diff);
            Assert.Null(store.ExchangeDetail("one", "missing"));
            Assert.Empty(store.Exchanges("one", 0, 100).Items);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(File.Exists(Path.Combine(directory, "proxy_exchanges.db")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
