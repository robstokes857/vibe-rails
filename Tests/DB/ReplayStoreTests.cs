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

    [Fact]
    public void TailReadsOnlyTheNewestFramesOfOneSession_WithoutWritingStorage()
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
                // Ids deliberately disagree with Sequence: the recording order is Sequence.
                command.CommandText = """
                    CREATE TABLE SessionLogs(Id INTEGER PRIMARY KEY, SessionId TEXT, Timestamp TEXT, Content BLOB);
                    CREATE TABLE TerminalSessionLogs(Id INTEGER PRIMARY KEY, SessionId TEXT, Sequence INTEGER, IsAlternateScreen INTEGER, Data BLOB, Cols INTEGER, Rows INTEGER, Timestamp TEXT);
                    INSERT INTO TerminalSessionLogs VALUES
                        (10,'tui',1,0,X'4141',80,24,'2026-10-12T00:00:00Z'),
                        (13,'tui',2,1,X'4242',80,24,'2026-10-12T00:00:01Z'),
                        (11,'tui',3,1,X'4343',100,30,'2026-10-12T00:00:02Z'),
                        (12,'other',1,0,X'5858',80,24,'2026-10-12T00:00:00Z');
                    INSERT INTO SessionLogs VALUES(1,'legacy','2026-10-12T00:00:00Z',X'4C31'),(2,'legacy','2026-10-12T00:00:01Z',X'4C32');
                    """;
                command.ExecuteNonQuery();
            }
            var before = File.ReadAllBytes(path);
            var store = new ReplayStore(new SqliteStoragePaths(path));

            var tail = store.Tail("tui", maxBytes: 4);
            Assert.Equal(["BB", "CC"], tail.Frames.Select(frame => Encoding.UTF8.GetString(frame.Data)));
            Assert.True(tail.StartsOnAlternateScreen);
            Assert.Equal((100, 30), (tail.Frames[^1].Cols, tail.Frames[^1].Rows));

            var whole = store.Tail("tui", maxBytes: 1024);
            Assert.Equal(["AA", "BB", "CC"], whole.Frames.Select(frame => Encoding.UTF8.GetString(frame.Data)));
            Assert.False(whole.StartsOnAlternateScreen);

            // Recordings older than the buffered table replay their raw chunks on the default grid.
            var legacy = store.Tail("legacy", maxBytes: 1024);
            Assert.Equal(["L1", "L2"], legacy.Frames.Select(frame => Encoding.UTF8.GetString(frame.Data)));
            Assert.All(legacy.Frames, frame => Assert.Equal((120, 30), (frame.Cols, frame.Rows)));

            Assert.Empty(store.Tail("missing", maxBytes: 1024).Frames);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
