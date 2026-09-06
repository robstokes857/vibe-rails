using System.Globalization;
using System.Text;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Services.Diagnostics;

public sealed class DiagnosticLogReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "viberails-diagnostic-log-tests", Guid.NewGuid().ToString("N"));
    private string LogDirectory => Path.Combine(_root, "logs");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task HistoricalFilesAndMultilineExceptionsAreVisibleWithoutRunningAWriter()
    {
        var oldFile = await WriteAsync("vb-20260801.log", Line("2026-08-01 10:00:00.123", "INF", "[Startup] Previous installation started"));
        var error = "[DataExport] Upload failed\nSystem.IO.IOException: Synthetic test failure\n   at Example.Upload()";
        await WriteAsync("vb-20260905.log", Line("2026-09-05 12:00:00.456", "ERR", error));
        var originalBytes = await File.ReadAllBytesAsync(oldFile, Token);
        var reader = new DiagnosticLogReader(Options());

        var response = await reader.ReadAsync("application", new FeatureLogQuery(), Token);

        Assert.Equal(2, response.Entries.Count);
        var latest = response.Entries[0];
        Assert.Equal("DataExport", latest.Feature);
        Assert.Equal("Error", latest.Level);
        Assert.Equal(error, latest.Message);
        Assert.Equal("diagnostic", latest.EventName);
        Assert.Equal("application", latest.Source);
        Assert.Equal("vb-20260905.log", latest.SourceFile);
        Assert.Equal(latest.SourceFile, latest.Subject);
        Assert.Null(latest.Status);
        Assert.Null(latest.OperationId);
        var expected = DateTime.ParseExact("2026-09-05 12:00:00.456", "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
        Assert.Equal(new DateTimeOffset(expected).ToUniversalTime(), latest.TimestampUtc);
        Assert.Equal(TimeSpan.Zero, latest.TimestampUtc.Offset);
        Assert.Equal(["DataExport", "Startup"], response.Features);
        Assert.Equal(0, response.ReadFailures);
        Assert.False(response.Truncated);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(oldFile, Token));
        Assert.Equal(2, Directory.GetFiles(LogDirectory).Length);
    }

    [Fact]
    public async Task SourceSelectionOnlyReadsItsOwnFilesAndIgnoresUnrelatedNames()
    {
        await WriteAsync("vb-20260905.log", Line("2026-09-05 10:00:00.000", "INF", "[Startup] Application"));
        await WriteAsync("vbd-20260905.log", Line("2026-09-05 11:00:00.000", "WRN", "[Jobs] Daemon"));
        await WriteAsync("vb-secrets.log", Line("2026-09-05 12:00:00.000", "INF", "Do not read"));
        await WriteAsync("vb-20269999.log", Line("2026-09-05 12:00:00.000", "INF", "Invalid date"));
        await WriteAsync("vb-20260905.log.backup", Line("2026-09-05 12:00:00.000", "INF", "Backup"));
        var nested = Path.Combine(LogDirectory, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "vb-20260906.log"),
            Line("2026-09-06 12:00:00.000", "INF", "Nested"), Token);
        var reader = new DiagnosticLogReader(Options());

        Assert.Equal("Startup", Assert.Single((await reader.ReadAsync("application", new FeatureLogQuery(), Token)).Entries).Feature);
        var daemon = Assert.Single((await reader.ReadAsync("daemon", new FeatureLogQuery(), Token)).Entries);
        Assert.Equal("Jobs", daemon.Feature);
        Assert.Equal("daemon", daemon.Source);
        Assert.Equal("Warning", daemon.Level);
    }

    [Theory]
    [InlineData("../vb-20260905.log")]
    [InlineData("C:\\arbitrary.log")]
    [InlineData("features")]
    [InlineData("")]
    public async Task InvalidSourceCannotSelectAnArbitraryPath(string source)
    {
        var reader = new DiagnosticLogReader(Options());
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(source, new FeatureLogQuery(), Token));
        Assert.False(Directory.Exists(LogDirectory));
    }

    [Fact]
    public async Task FiltersAreCaseInsensitiveAndAppliedBeforePagination()
    {
        await WriteAsync("vb-20260905.log",
            Line("2026-09-05 10:00:00.000", "ERR", "[DataExport] Snapshot old failure") +
            Line("2026-09-05 10:01:00.000", "INF", "[Startup] Normal startup") +
            Line("2026-09-05 10:02:00.000", "ERR", "[DataExport] Snapshot new failure"));
        var reader = new DiagnosticLogReader(Options());
        var query = new FeatureLogQuery(Feature: "dataexport", Level: "ERROR", Search: "SNAPSHOT", Limit: 1);

        var first = await reader.ReadAsync("application", query, Token);
        Assert.Contains("new failure", Assert.Single(first.Entries).Message);
        Assert.True(first.HasMore);
        var second = await reader.ReadAsync("application", query with { Offset = 1 }, Token);
        Assert.Contains("old failure", Assert.Single(second.Entries).Message);
        Assert.False(second.HasMore);
        Assert.Equal(first.Features, second.Features);
        Assert.Empty((await reader.ReadAsync("application", query with { Status = "uploaded" }, Token)).Entries);
        Assert.Empty((await reader.ReadAsync("application", query with { OperationId = "invented" }, Token)).Entries);
        Assert.Equal(3, (await reader.ReadAsync("application", new FeatureLogQuery(Search: "VB-20260905.LOG"), Token)).Entries.Count);
    }

    [Fact]
    public async Task LevelsAreMappedAndOnlySafeLeadingTagsBecomeFeatures()
    {
        var levels = new[] { "VRB", "DBG", "INF", "WRN", "ERR", "FTL" };
        await WriteAsync("vb-20260905.log", string.Concat(levels.Select((level, i) =>
            Line($"2026-09-05 10:00:0{i}.000", level, i == 0 ? "[Jobs.Worker-1] Tagged" : "Untagged [NotAFeature]"))) +
            Line("2026-09-05 11:00:00.000", "INF", "[not a tag] General"));

        var response = await new DiagnosticLogReader(Options()).ReadAsync("application", new FeatureLogQuery(), Token);

        Assert.Equal(["Information", "Critical", "Error", "Warning", "Information", "Debug", "Trace"], response.Entries.Select(e => e.Level));
        Assert.Equal(["general", "Jobs.Worker-1"], response.Features);
        Assert.All(response.Entries.Take(6), e => Assert.Equal("general", e.Feature));
    }

    [Fact]
    public async Task ReaderSkipsCorruptHeadersAndIncompleteFinalLinesAndKeepsStableIds()
    {
        var path = await WriteAsync("vb-20260905.log", "orphan corrupt data\n" +
            Line("2026-09-05 10:00:00.000", "INF", "[Startup] Good entry") +
            "2026-99-99 10:00:00.000 [ERR] Corrupt timestamp\n" +
            Line("2026-09-05 11:00:00.000", "ERR", "[Jobs] Another good entry") +
            "2026-09-05 12:00:00.000 [INF] Not flushed");
        var reader = new DiagnosticLogReader(Options());

        var first = await reader.ReadAsync("application", new FeatureLogQuery(), Token);
        Assert.Equal(2, first.Entries.Count);
        Assert.Equal(2, first.ReadFailures);
        Assert.Equal("[Startup] Good entry", first.Entries[1].Message);
        await File.AppendAllTextAsync(path, "\n", Token);
        var refreshed = await reader.ReadAsync("application", new FeatureLogQuery(), Token);
        Assert.Equal(3, refreshed.Entries.Count);
        Assert.Equal(first.Entries.Select(e => e.Id), refreshed.Entries.Skip(1).Select(e => e.Id));
        // The same two damaged lines are still there; a refresh reports them again, not four.
        Assert.Equal(2, refreshed.ReadFailures);
    }

    [Fact]
    public async Task TailAndMessageBoundsKeepRecentCompleteEventsAndExposeTruncation()
    {
        await WriteAsync("vb-20260905.log", new string('x', 2048) + "\n" +
            Line("2026-09-05 10:00:00.000", "ERR", "[Jobs] " + new string('m', 350)) +
            Line("2026-09-05 11:00:00.000", "INF", "[Startup] Recent"));

        var response = await new DiagnosticLogReader(Options(bytes: 512, messageChars: 128))
            .ReadAsync("application", new FeatureLogQuery(), Token);

        Assert.Equal(2, response.Entries.Count);
        Assert.Equal("[Startup] Recent", response.Entries[0].Message);
        Assert.Equal(128, response.Entries[1].Message.Length);
        Assert.True(response.Truncated);
        Assert.Equal(0, response.ReadFailures);
    }

    [Fact]
    public async Task FileLimitUsesLastWriteTimeIncludingEarlierActiveRollingFiles()
    {
        for (var day = 1; day <= 8; day++)
            await WriteAsync($"vb-202609{day:D2}.log", Line($"2026-09-{day:D2} 10:00:00.000", "INF", $"Day {day}"));
        await WriteAsync("vb-20260908_001.log", Line("2026-09-08 11:00:00.000", "INF", "Rolled once"));
        await WriteAsync("vb-20260908_002.log", Line("2026-09-08 12:00:00.000", "INF", "Rolled twice"));
        // Concurrent processes may continue writing an earlier suffix/date after newer files exist.
        File.SetLastWriteTimeUtc(Path.Combine(LogDirectory, "vb-20260908_001.log"), DateTime.UtcNow.AddDays(4));
        File.SetLastWriteTimeUtc(Path.Combine(LogDirectory, "vb-20260901.log"), DateTime.UtcNow.AddDays(5));
        var reader = new DiagnosticLogReader(Options(maxFiles: 2));

        var response = await reader.ReadAsync("application", new FeatureLogQuery(), Token);

        Assert.Equal(["Rolled once", "Day 1"], response.Entries.Select(e => e.Message));
        Assert.True(response.Truncated);
    }

    [Fact]
    public async Task FileLimitRanksAFileStillBeingWrittenByItsLiveWriteTime()
    {
        // Directory enumeration may report a stale write time for a file another process still
        // holds open; the reader must rank it by the handle-accurate value instead.
        var closed = await WriteAsync("vb-20260905.log", Line("2026-09-05 10:00:00.000", "INF", "Closed earlier"));
        var open = await WriteAsync("vb-20260906.log", Line("2026-09-06 08:00:00.000", "INF", "Opened at start"));
        File.SetLastWriteTimeUtc(closed, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(open, DateTime.UtcNow.AddDays(-1));
        await using (var writer = new FileStream(open, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes(Line("2026-09-06 09:00:00.000", "INF", "Still writing")), Token);
            writer.Flush(flushToDisk: true);

            var response = await new DiagnosticLogReader(Options(maxFiles: 1))
                .ReadAsync("application", new FeatureLogQuery(), Token);

            Assert.Equal(["Still writing", "Opened at start"], response.Entries.Select(e => e.Message));
            Assert.True(response.Truncated);
        }
    }

    [Fact]
    public async Task EntryLimitKeepsNewestTimestampsEvenWhenFileEntriesAreOutOfOrder()
    {
        await WriteAsync("vb-20260905.log",
            Line("2026-09-05 12:00:00.000", "INF", "Newest") +
            Line("2026-09-05 10:00:00.000", "INF", "Oldest") +
            Line("2026-09-05 11:00:00.000", "INF", "Middle"));

        var response = await new DiagnosticLogReader(Options(entries: 2))
            .ReadAsync("application", new FeatureLogQuery(), Token);

        Assert.Equal(["Newest", "Middle"], response.Entries.Select(e => e.Message));
        Assert.True(response.Truncated);
    }

    [Fact]
    public async Task PaginationIsCappedAndClampsInvalidBounds()
    {
        await WriteAsync("vb-20260905.log", string.Concat(Enumerable.Range(0, 205).Select(i =>
            Line("2026-09-05 10:00:00.000", "INF", $"Event {i}"))));
        var reader = new DiagnosticLogReader(Options());

        Assert.Equal(100, (await reader.ReadAsync("application", new FeatureLogQuery(), Token)).Entries.Count);
        var maximum = await reader.ReadAsync("application", new FeatureLogQuery(Limit: int.MaxValue), Token);
        Assert.Equal(200, maximum.Entries.Count);
        Assert.True(maximum.HasMore);
        Assert.Equal("Event 204", maximum.Entries[0].Message);
        Assert.Single((await reader.ReadAsync("application", new FeatureLogQuery(Offset: -10, Limit: 0), Token)).Entries);
        Assert.Empty((await reader.ReadAsync("application", new FeatureLogQuery(Offset: int.MaxValue), Token)).Entries);
    }

    [Fact]
    public async Task ConcurrentQueriesShareOneSnapshotAndSourcesHaveIndependentCaches()
    {
        var path = await WriteAsync("vb-20260905.log", "corrupt line\n" +
            Line("2026-09-05 10:00:00.000", "INF", "Before cache"));
        var reader = new DiagnosticLogReader(Options(cache: TimeSpan.FromMinutes(1)));
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            reader.ReadAsync("application", new FeatureLogQuery(), Token)));
        Assert.All(responses, r => Assert.Equal(1, r.ReadFailures));
        await File.AppendAllTextAsync(path, Line("2026-09-05 11:00:00.000", "INF", "After cache"), Token);
        await WriteAsync("vbd-20260905.log", Line("2026-09-05 11:00:00.000", "INF", "Daemon new file"));

        Assert.Equal("Before cache", Assert.Single((await reader.ReadAsync("application", new FeatureLogQuery(), Token)).Entries).Message);
        Assert.Equal("Daemon new file", Assert.Single((await reader.ReadAsync("daemon", new FeatureLogQuery(), Token)).Entries).Message);
        var refreshed = await new DiagnosticLogReader(Options()).ReadAsync("application", new FeatureLogQuery(), Token);
        Assert.Equal(2, refreshed.Entries.Count);
    }

    [Fact]
    public async Task MissingDirectoryIsEmptyWithoutCreatingIt()
    {
        var reader = new DiagnosticLogReader(Options());
        Assert.False(Directory.Exists(LogDirectory));
        var absent = await reader.ReadAsync("application", new FeatureLogQuery(), Token);
        Assert.Empty(absent.Entries);
        Assert.Equal(0, absent.ReadFailures);
        Assert.False(Directory.Exists(LogDirectory));
    }

    [Fact]
    public async Task LockedFilesReportFailureAndCanBeReadAfterUnlocking()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires mandatory Windows file-sharing locks.");
        var reader = new DiagnosticLogReader(Options());
        var path = await WriteAsync("vb-20260905.log", Line("2026-09-05 10:00:00.000", "INF", "Locked file"));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unavailable = await reader.ReadAsync("application", new FeatureLogQuery(), Token);
            Assert.Empty(unavailable.Entries);
            Assert.Equal(1, unavailable.ReadFailures);
        }
        Assert.Single((await reader.ReadAsync("application", new FeatureLogQuery(), Token)).Entries);
    }

    [Fact]
    public async Task DirectoryEnumerationIsBounded()
    {
        for (var i = 0; i < 5; i++)
            await WriteAsync($"unrelated-{i}.txt", "Unrelated data");

        var response = await new DiagnosticLogReader(Options(directoryEntries: 2))
            .ReadAsync("application", new FeatureLogQuery(), Token);

        Assert.Empty(response.Entries);
        Assert.True(response.Truncated);
    }

    [Fact]
    public async Task LinkedLogFilesAreNotRead()
    {
        Directory.CreateDirectory(LogDirectory);
        var target = Path.Combine(_root, "outside.log");
        await File.WriteAllTextAsync(target, Line("2026-09-05 10:00:00.000", "INF", "Do not expose linked data"), Token);
        var link = Path.Combine(LogDirectory, "vb-20260905.log");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Skip("Creating symbolic links is unavailable for this test user.");
        }

        var response = await new DiagnosticLogReader(Options()).ReadAsync("application", new FeatureLogQuery(), Token);

        Assert.Empty(response.Entries);
        Assert.Equal(1, response.ReadFailures);
    }

    [Fact]
    public async Task LinkedLogDirectoryIsNotRead()
    {
        var target = Path.Combine(_root, "linked-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "vb-20260905.log"),
            Line("2026-09-05 10:00:00.000", "INF", "Do not expose linked directory"), Token);
        try { Directory.CreateSymbolicLink(LogDirectory, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Skip("Creating symbolic links is unavailable for this test user.");
        }

        var response = await new DiagnosticLogReader(Options()).ReadAsync("application", new FeatureLogQuery(), Token);

        Assert.Empty(response.Entries);
        Assert.Equal(1, response.ReadFailures);
    }

    private async Task<string> WriteAsync(string name, string text)
    {
        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, name);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), Token);
        return path;
    }

    private static string Line(string timestamp, string level, string message) => $"{timestamp} [{level}] {message}\n";

    private DiagnosticLogOptions Options(int maxFiles = 7, int bytes = 2 * 1024 * 1024, int entries = 10_000,
        int messageChars = 16 * 1024, int directoryEntries = 1024, TimeSpan? cache = null) => new()
    {
        DirectoryPath = LogDirectory,
        MaxFiles = maxFiles,
        MaxBytesPerFile = bytes,
        MaxReadEntries = entries,
        MaxMessageChars = messageChars,
        MaxDirectoryEntries = directoryEntries,
        ReadCacheDuration = cache ?? TimeSpan.Zero
    };
}
