using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.Services.Backups;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services;

public sealed class CompleteBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vb-complete-backup-" + Guid.NewGuid().ToString("N"));
    private readonly Cloud cloud = new();
    private DateTime now = DateTime.UtcNow;
    private string key = "account-a";
    private readonly List<BackupFileSource> files = [];
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    public CompleteBackupTests() => Directory.CreateDirectory(root);
    private CompleteBackupService Service() => new(new SqliteDatabaseSnapshotStore(), new BackupTransport(new HttpClient(cloud)),
        _ => Task.FromResult(files), () => root, () => key, () => now);

    [Fact]
    public async Task BoardRestoresAllBoardsHistoryRelationshipsAndUnboundedOriginalContent()
    {
        SqliteStorage.EnsureAllSchemas(new(Path.Combine(root, "state.db")));
        var jobs = SqliteStorage.CreateJobStore(Path.Combine(root, "state.db"));
        var job1 = await jobs.CreateJobAsync(new("Review", root, VibeRails.Services.LLM.NotSet, null, "", null, true, []), Ct);
        var job2 = await jobs.CreateJobAsync(new("Checks", root, VibeRails.Services.LLM.NotSet, null, "", null, true, []), Ct);
        var store = SqliteStorage.CreateBoardStore(Path.Combine(root, "state.db"));
        var first = await store.CreateBoardAsync(root, "First", Ct);
        var second = await store.CreateBoardAsync(root, "Second", Ct);
        var lane = (await store.GetColumnsAsync(root, Ct, first.Id))[0];
        var otherLane = (await store.GetColumnsAsync(root, Ct, second.Id))[0];
        var card = await store.CreateCardAsync(root, new(lane.Id, "Originals", "Full text", null, "medium", null, [], false), Ct);
        var linked = await store.CreateCardAsync(root, new(otherLane.Id, "Other board", "", null, "medium", null, [], false), Ct);
        await store.LinkCardAsync(root, card.Id, linked.Id, Ct);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.Agent("Codex", "codex", null), "Retained discussion", Ct);
        await store.SaveContextSettingsAsync(root, first.Id, new("User instructions", []), 0, Ct);
        await store.SaveLaneAutomationAsync(root, lane.Id, [job1.Id, job2.Id], 0, Ct);
        var originals = new Dictionary<string, byte[]>();
        foreach (var (name, count) in new[] { ("broken.png", 96773), ("board.png", 149318), ("stop.png", 39897), ("VIBE-12-desktop-companion-0f6a7279.md", 85388) })
        {
            var real = Environment.GetEnvironmentVariable("VIBE_BACKUP_ORIGINAL_FIXTURES");
            var path = real is null ? null : Path.Combine(real, name);
            var bytes = path is not null && File.Exists(path) ? await File.ReadAllBytesAsync(path, Ct) : RandomNumberGenerator.GetBytes(count);
            var attachment = (await store.AddAttachmentContentAsync(root, card.Id, name, "application/octet-stream", bytes, Ct))!;
            originals.Add(attachment.Id, bytes);
        }
        var large = new string('λ', 700_000);
        var sha = new string('a', 40);
        await store.AddCommitAsync(root, card.Id, sha, "Author", "Snapshot", now, new([new("large.cs", "csharp", large, large + "later")], 1), Ct);
        // An attachment larger than the entire hosted activity budget forces multiple transport parts.
        await store.AddAttachmentContentAsync(root, linked.Id, "large.bin", "application/octet-stream", RandomNumberGenerator.GetBytes(9 * 1024 * 1024), Ct);
        await Service().TickAsync(Ct);
        var archive = Assert.Single(cloud.Manifests.Values.Where(m => m.Dataset == "board"));
        Assert.True(archive.Parts.Count > 1);
        var restored = await RestoreDatabase(archive);
        var recovered = new BoardStore($"Data Source={restored};Pooling=False", $"Data Source={Path.Combine(root, "state.db")};Pooling=False");
        Assert.Equal(2, (await recovered.GetBoardsAsync(root, Ct)).Count);
        var detail = (await recovered.GetCardDetailAsync(root, card.Id, Ct))!;
        Assert.Contains(detail.Comments, c => c.Body == "Retained discussion");
        Assert.Equal(linked.Id, Assert.Single(detail.LinkedCards).Id);
        Assert.Equal("User instructions", (await recovered.GetContextSettingsAsync(root, first.Id, Ct))!.Context.DefaultMessage);
        foreach (var (id, bytes) in originals)
            Assert.Equal(BackupFormat.Hash(bytes), BackupFormat.Hash((await recovered.GetAttachmentContentAsync(root, card.Id, id, Ct))!.Content));
        var snapshot = (await recovered.GetCommitSnapshotAsync(root, card.Id, sha, Ct))!;
        Assert.Equal(large, snapshot.Files[0].OriginalContent);
        Assert.Equal(large + "later", snapshot.Files[0].ModifiedContent);
        using var restoredDb = Open(restored);
        using var liveDb = Open(Path.Combine(root, "board.db"));
        foreach (var table in new[] { "Boards", "BoardComments", "BoardCardLinks", "BoardLaneAutomations", "BoardLaneAdditionalAutomations", "BoardCardOptions", "BoardContextSettings" })
            Assert.Equal(Scalar(liveDb, $"SELECT COUNT(*) FROM {table}"), Scalar(restoredDb, $"SELECT COUNT(*) FROM {table}"));
        var output = Environment.GetEnvironmentVariable("VIBE_BACKUP_RECOVERY_OUTPUT");
        if (!string.IsNullOrEmpty(output)) await SaveBundle(archive, output);
    }

    [Fact]
    public async Task RetrySurvivesRestartAndLostReceipt_LaterEditsGetIndependentVersions()
    {
        CreateDatabase("board.db", "CREATE TABLE Durable(Id INTEGER PRIMARY KEY, Value BLOB); INSERT INTO Durable VALUES(1,randomblob(9000000));");
        cloud.FailPartOnce = 2;
        var service = Service();
        await service.TickAsync(Ct);
        var failed = service.GetCoverage().Datasets.Single(d => d.Dataset == "board").State;
        Assert.Equal("failed", failed.Status);
        Assert.Equal(1, failed.NextPart);
        Assert.Null(failed.Receipt);
        var pendingVersion = failed.PendingVersion;
        cloud.LoseCommitOnce = true;
        service = Service();
        await Drain(service, 20);
        var receipt = service.GetCoverage().Datasets.Single(d => d.Dataset == "board").State.Receipt;
        Assert.Equal(pendingVersion, receipt!.Version);
        Assert.Single(cloud.Manifests.Values.Where(m => m.Dataset == "board"));
        using (var writer = Open(Path.Combine(root, "board.db"))) Execute(writer, "INSERT INTO Durable VALUES(2,'later edit');");
        Assert.Equal("pending", service.GetCoverage().Datasets.Single(d => d.Dataset == "board").State.Status);
        await Drain(service, 20);
        Assert.Equal(2, cloud.Manifests.Values.Count(m => m.Dataset == "board"));
        var latest = cloud.Manifests.Values.Where(m => m.Dataset == "board").MaxBy(m => m.SourceStartedUtc)!;
        using var db = Open(await RestoreDatabase(latest));
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM Durable"));
    }

    [Fact]
    public async Task WrongAccountReceiptNeverAcknowledges_ChangingAccountRequiresNewCoverage()
    {
        CreateDatabase("board.db", "CREATE TABLE Durable(Value TEXT); INSERT INTO Durable VALUES('saved');");
        cloud.WrongReceipt = true;
        var service = Service();
        await service.TickAsync(Ct);
        Assert.Null(service.GetCoverage().Datasets.Single(d => d.Dataset == "board").State.Receipt);
        cloud.WrongReceipt = false;
        await Drain(service, 8);
        Assert.NotNull(service.GetCoverage().Datasets.Single(d => d.Dataset == "board").State.Receipt);
        key = "account-b";
        Assert.All(service.GetCoverage().Datasets, d => Assert.Null(d.State.Receipt));
        await service.TickAsync(Ct);
        Assert.Equal(2, service.GetCoverage().Datasets.Single(d => d.Dataset == "board").State.Receipt!.AccountId);
    }

    [Fact]
    public async Task CorruptSpoolIsPreservedAndRebuilt_NeverAcknowledged()
    {
        CreateDatabase("board.db", "CREATE TABLE Durable(Value TEXT); INSERT INTO Durable VALUES('original');");
        cloud.Offline = true;
        var service = Service();
        await service.TickAsync(Ct);
        var part = Assert.Single(Directory.GetFiles(Path.Combine(root, "complete-backups"), "*.part", SearchOption.AllDirectories));
        await File.WriteAllBytesAsync(part, "bad"u8.ToArray(), Ct);
        cloud.Offline = false;
        await Drain(service, 12);
        Assert.True(File.Exists(part));
        var receipt = service.GetCoverage().Datasets.Single(d => d.Dataset == "board").State.Receipt;
        Assert.NotNull(receipt);
        Assert.NotEqual(Path.GetFileName(Path.GetDirectoryName(part)), receipt.Version);
    }

    [Theory]
    [InlineData("missing-manifest")]
    [InlineData("malformed-manifest")]
    [InlineData("missing-part")]
    public async Task MissingOrMalformedStagingIsRecapturedAfterRestart(string damage)
    {
        CreateDatabase("board.db", "CREATE TABLE Durable(Value TEXT); INSERT INTO Durable VALUES('preserved');");
        cloud.Offline = true;
        await Service().TickAsync(Ct);
        var manifest = Assert.Single(Directory.GetFiles(Path.Combine(root, "complete-backups"), "manifest.json", SearchOption.AllDirectories));
        var version = Path.GetFileName(Path.GetDirectoryName(manifest));
        if (damage == "missing-manifest") File.Delete(manifest);
        else if (damage == "malformed-manifest") await File.WriteAllTextAsync(manifest, "{broken", Ct);
        else File.Delete(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(manifest)!, "*.part")));
        cloud.Offline = false;
        var restarted = Service();
        await Drain(restarted, 16);
        var receipt = restarted.GetCoverage().Datasets.Single(d => d.Dataset == "board").State.Receipt;
        Assert.NotNull(receipt);
        Assert.NotEqual(version, receipt.Version);
        using var db = Open(await RestoreDatabase(cloud.Manifests[receipt.Version]));
        Assert.Equal("preserved", Scalar(db, "SELECT Value FROM Durable"));
        if (damage == "malformed-manifest") Assert.Equal("{broken", await File.ReadAllTextAsync(manifest, Ct));
    }

    [Fact]
    public async Task StateProxyAndConfigurationRecoverRecordsWithoutSessionReceipts()
    {
        CreateDatabase("state.db", "CREATE TABLE Environments(Name TEXT); INSERT INTO Environments VALUES('worker'); CREATE TABLE JobRunActions(Output TEXT); INSERT INTO JobRunActions VALUES('result');");
        CreateDatabase("proxy_exchanges.db", "CREATE TABLE ProxyExchanges(Id INTEGER PRIMARY KEY, SessionId TEXT, RequestBody TEXT); INSERT INTO ProxyExchanges VALUES(1,NULL,'unattributed');");
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, "{\"ApiKey\":\"secret\",\"PinHash\":\"hash\",\"ComputerName\":\"test\",\"RemoveCoAuthorTrailers\":false,\"ClaudeTokenSaverEnabled\":false,\"provider\":{\"access_token\":\"nested-secret\",\"theme\":\"dark\"}}", Ct);
        var script = Path.Combine(root, "script.py"); await File.WriteAllTextAsync(script, "print('preserved')", Ct);
        files.Add(new(settings, "installation/settings.json")); files.Add(new(script, "automation/1/check.py"));
        await Drain(Service(), 8);
        var state = cloud.Manifests.Values.Single(m => m.Dataset == "state");
        using (var db = Open(await RestoreDatabase(state)))
        { Assert.Equal("worker", Scalar(db, "SELECT Name FROM Environments")); Assert.Equal("result", Scalar(db, "SELECT Output FROM JobRunActions")); }
        var proxy = cloud.Manifests.Values.Single(m => m.Dataset == "proxy");
        using (var db = Open(await RestoreDatabase(proxy))) Assert.Equal("unattributed", Scalar(db, "SELECT RequestBody FROM ProxyExchanges WHERE SessionId IS NULL"));
        var config = cloud.Manifests.Values.Single(m => m.Dataset == "configuration");
        using var zip = new ZipArchive(new MemoryStream(Payload(config)));
        using var reader = new StreamReader(zip.GetEntry("installation/settings.json")!.Open());
        var text = await reader.ReadToEndAsync(Ct);
        Assert.DoesNotContain("secret", text); Assert.DoesNotContain("PinHash", text); Assert.Contains("RemoveCoAuthorTrailers", text);
        Assert.Contains("ClaudeTokenSaverEnabled", text); Assert.Contains("theme", text);
        Assert.NotNull(zip.GetEntry("automation/1/check.py")); Assert.NotNull(zip.GetEntry("files.json"));
    }

    [Fact]
    public async Task IncompleteFilesAndCorruptCheckpointNeverMasqueradeAsCompleteCoverage()
    {
        CreateDatabase("board.db", "CREATE TABLE Durable(Value TEXT); INSERT INTO Durable VALUES('saved');");
        files.Add(new(Path.Combine(root, "missing.py"), "automation/missing.py"));
        var service = Service();
        await Drain(service, 4);
        var config = service.GetCoverage().Datasets.Single(d => d.Dataset == "configuration").State;
        Assert.NotNull(config.Receipt); Assert.NotEmpty(config.CoverageIssues); Assert.NotEqual("current", config.Status);
        var checkpoint = Directory.GetFiles(Path.Combine(root, "complete-backups"), "checkpoint.json", SearchOption.AllDirectories)
            .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "board");
        await File.WriteAllTextAsync(checkpoint, "broken JSON", Ct);
        Assert.Equal("failed", service.GetCoverage().Datasets.Single(d => d.Dataset == "board").State.Status);
        await Drain(service, 4);
        Assert.NotNull(service.GetCoverage().Datasets.Single(d => d.Dataset == "board").State.Receipt);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(checkpoint)!, "checkpoint.json.corrupt-*"));
    }

    [Fact]
    public async Task SnapshotIncludesCommittedWal_ConcurrentWritersRemainResponsive_AndCancellationWorks()
    {
        var path = Path.Combine(root, "source.db");
        using var source = Open(path);
        Execute(source, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Payload(Id INTEGER PRIMARY KEY, Value BLOB); INSERT INTO Payload VALUES(1,randomblob(16000000));");
        var snapshotPath = Path.Combine(root, "snapshot.db");
        var task = new SqliteDatabaseSnapshotStore().CreateSnapshotAsync(path, snapshotPath, Ct);
        var writes = 0;
        while (!task.IsCompleted)
        {
            Execute(source, "INSERT INTO Payload(Value) VALUES('concurrent');"); writes++;
            await Task.Delay(2, Ct);
        }
        await task;
        Assert.True(writes > 0);
        using (var snapshot = Open(snapshotPath)) { Assert.Equal("ok", Scalar(snapshot, "PRAGMA integrity_check")); Assert.Equal(16000000L, Scalar(snapshot, "SELECT length(Value) FROM Payload WHERE Id=1")); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqliteDatabaseSnapshotStore().CreateSnapshotAsync(path, Path.Combine(root, "cancelled.db"), cancelled.Token));
    }

    private async Task Drain(CompleteBackupService service, int ticks)
    { for (var i = 0; i < ticks; i++) { now = now.AddMinutes(1); await service.TickAsync(Ct); } }
    private void CreateDatabase(string name, string sql) { using var db = Open(Path.Combine(root, name)); Execute(db, sql); }
    private static SqliteConnection Open(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }
    private static void Execute(SqliteConnection db, string sql) { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql) { using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
    private byte[] Payload(BackupManifest m) => m.Parts.SelectMany(p =>
    {
        var bytes = cloud.Parts[(1, p.Sha256)]; Assert.Equal(p.Bytes, bytes.Length); Assert.Equal(p.Sha256, BackupFormat.Hash(bytes)); return bytes;
    }).ToArray();
    private async Task<string> RestoreDatabase(BackupManifest m)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
        using var input = new BrotliStream(new MemoryStream(Payload(m)), CompressionMode.Decompress);
        await using var output = File.Create(path); await input.CopyToAsync(output, Ct); return path;
    }
    private async Task SaveBundle(BackupManifest m, string directory)
    {
        Directory.CreateDirectory(directory);
        var bytes = cloud.ManifestBytes[m.Version];
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.sha256"), BackupFormat.Hash(bytes), Ct);
        using var zip = ZipFile.Open(Path.Combine(directory, "board.backup.zip"), ZipArchiveMode.Create);
        await using (var s = zip.CreateEntry("manifest.json").Open()) await s.WriteAsync(bytes, Ct);
        await using (var s = zip.CreateEntry("manifest.sha256").Open()) await s.WriteAsync(Encoding.ASCII.GetBytes(BackupFormat.Hash(bytes)), Ct);
        foreach (var part in m.Parts.DistinctBy(p => p.Sha256))
        { await using var s = zip.CreateEntry(part.Sha256 + ".part", CompressionLevel.NoCompression).Open(); await s.WriteAsync(cloud.Parts[(1, part.Sha256)], Ct); }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    private sealed class Cloud : HttpMessageHandler
    {
        internal readonly Dictionary<(int Account, string Hash), byte[]> Parts = [];
        internal readonly Dictionary<string, BackupManifest> Manifests = [];
        internal readonly Dictionary<string, byte[]> ManifestBytes = [];
        public bool Offline, WrongReceipt, LoseCommitOnce;
        public int FailPartOnce, PartRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            var account = request.Headers.GetValues("X-Api-Key").Single() == "account-a" ? 1 : 2;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/account")) return Json(new BackupAccount(account), BackupJson.Default.BackupAccount);
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            var hash = BackupFormat.Hash(bytes);
            if (request.Method == HttpMethod.Put)
            {
                if (++PartRequests == FailPartOnce) throw new HttpRequestException("interrupted");
                Assert.EndsWith(hash, path); Parts[(account, hash)] = bytes;
                return Json(new BackupPartReceipt(hash, bytes.Length, true), BackupJson.Default.BackupPartReceipt);
            }
            var m = JsonSerializer.Deserialize(bytes, BackupJson.Default.BackupManifest)!;
            Assert.True(BackupFormat.IsValid(m)); Assert.All(m.Parts, p => Assert.True(Parts.ContainsKey((account, p.Sha256))));
            if (ManifestBytes.TryGetValue(m.Version, out var previous)) Assert.Equal(previous, bytes);
            Manifests[m.Version] = m; ManifestBytes[m.Version] = bytes;
            if (LoseCommitOnce) { LoseCommitOnce = false; throw new HttpRequestException("lost reply"); }
            return Json(new BackupReceipt(WrongReceipt ? 999 : account, m.ComputerId, m.Dataset, m.Version, hash,
                m.PayloadBytes, m.SourceStartedUtc, m.SourceCompletedUtc, m.SourceCompletedUtc.AddSeconds(1), true), BackupJson.Default.BackupReceipt);
        }
        private static HttpResponseMessage Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value, type), Encoding.UTF8, "application/json") };
    }
}
