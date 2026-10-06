using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VibeRails.Data.Abstractions;
using VibeRails.Services.Diagnostics;
using VibeRails.Utils;

namespace VibeRails.Services.Backups;

/// <summary>Account-scoped, durable complete-backup queue shared by root backends on this computer.</summary>
public sealed class CompleteBackupService
{
    private readonly IDatabaseSnapshotStore snapshots;
    private readonly BackupTransport transport;
    private readonly Func<CancellationToken, Task<List<BackupFileSource>>> files;
    private readonly Func<string> root;
    private readonly Func<string> key;
    private readonly Func<DateTime> now;
    private readonly IFeatureLog log;
    private readonly Dictionary<string, DateTime> checkpointRetryAfter = [];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal const int PartsPerTick = 16;

    public CompleteBackupService(IDatabaseSnapshotStore snapshots, BackupTransport transport, BackupFiles files, IFeatureLog log)
        : this(snapshots, transport, ct => files.EnumerateAsync(Path.GetDirectoryName(ParserConfigs.GetStatePath())!, ct),
            () => Path.GetDirectoryName(ParserConfigs.GetStatePath())!, ParserConfigs.GetApiKey, () => DateTime.UtcNow, log) { }

    internal CompleteBackupService(IDatabaseSnapshotStore snapshots, BackupTransport transport,
        Func<CancellationToken, Task<List<BackupFileSource>>> files, Func<string> root, Func<string> key,
        Func<DateTime> now, IFeatureLog? log = null)
    { this.snapshots = snapshots; this.transport = transport; this.files = files; this.root = root; this.key = key; this.now = now; this.log = log ?? NullFeatureLog.Instance; }

    private string Spool => Path.Combine(root(), "complete-backups");
    private string AccountDirectory(string credential) => Path.Combine(Spool, BackupFormat.Hash(Encoding.UTF8.GetBytes(credential)));

    /// <summary>Reads small atomic checkpoint files only. No payload or credential is returned.</summary>
    public BackupCoverage GetCoverage()
    {
        var credential = key();
        if (string.IsNullOrWhiteSpace(credential)) return new(false, [], BackupFiles.Exclusions);
        var account = AccountDirectory(credential);
        return new(true, BackupFormat.Datasets.Select(dataset =>
        {
            BackupCheckpoint state;
            try { state = ReadState(account, dataset); }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { state = new() { Status = "failed", Error = "Backup checkpoint could not be read." }; }
            if (dataset != "configuration" && state.Status == "current" && state.SourceFingerprint != DatabaseFingerprint(dataset))
                state = state with { Status = "pending" };
            return new BackupDatasetCoverage(dataset, state);
        }).ToList(), BackupFiles.Exclusions);
    }

    /// <summary>One dataset per tick, at most 64 MiB of delivery. Independent of session/Board sync gates.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var credential = key();
        if (string.IsNullOrWhiteSpace(credential) || !await Gate.WaitAsync(0, ct)) return;
        try
        {
            PrivateFilePermissions.EnsureDirectory(Spool);
            using var machineLock = CrossProcessFileLock.TryAcquire(Path.Combine(Spool, ".backup.lock"));
            if (machineLock is null) return;
            var account = AccountDirectory(credential);
            PrivateFilePermissions.EnsureDirectory(account);
            var candidates = new List<(string Dataset, BackupCheckpoint State)>();
            foreach (var candidate in BackupFormat.Datasets)
                if (TryRecoverState(account, candidate) is { } recovered) candidates.Add((candidate, recovered));
            var selected = candidates
                .Where(x => x.State.NextAttemptUtc <= now() || x.State.NextAttemptUtc > now().AddHours(6))
                .OrderBy(x => x.State.LastCheckedUtc ?? DateTime.MinValue).FirstOrDefault();
            if (selected.Dataset is null) return;
            var dataset = selected.Dataset;
            var state = selected.State with { LastCheckedUtc = now() };
            var datasetPath = Path.Combine(account, dataset);
            PrivateFilePermissions.EnsureDirectory(datasetPath);
            var preparing = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (state.Receipt is { AccountId: > 0, ChecksumsVerified: true } acknowledged)
                    await ReclaimAcknowledgedStagingAsync(datasetPath, dataset, acknowledged.AccountId, ct);
                // A shutdown or crash during preparation leaves the recorded version without a manifest. That is an
                // interrupted capture, not corrupt staging: forget it, and the next preparation reclaims its directory.
                if (state is { Status: "preparing", PendingVersion: { } interrupted }
                    && !File.Exists(Path.Combine(VersionPath(datasetPath, interrupted), "manifest.json")))
                    state = state with { PendingVersion = null, NextPart = 0, Status = "pending" };
                // The configuration walk is lazy: at most once per tick, and only when a capture or receipt needs it.
                List<BackupFileSource>? sources = null;
                string? fingerprint = null;
                async Task<string> FingerprintAsync() => fingerprint ??= dataset == "configuration"
                    ? BackupFiles.Fingerprint(sources ??= await files(ct)) : DatabaseFingerprint(dataset);
                int? accountId = null;
                if (state.PendingVersion is null)
                {
                    if (dataset != "configuration" && !File.Exists(DatabasePath(dataset)))
                    {
                        Save(state with { Status = "absent", Error = "No local database exists; checked again automatically." });
                        return;
                    }
                    var age = state.Receipt is null ? TimeSpan.MaxValue : now() - state.Receipt.SourceStartedUtc;
                    var interval = dataset == "board" ? TimeSpan.FromMinutes(15) : dataset == "configuration" ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24);
                    if (state.Receipt is not null && age >= TimeSpan.Zero && age < interval && state.Status != "failed")
                    {
                        // Configuration is not walked again until its interval elapses; its status is as of the last walk.
                        if (dataset == "configuration") { Save(state with { Status = state.Status == "preparing" ? "pending" : state.Status, Error = null }); return; }
                        Save(state with { Status = await FingerprintAsync() == state.SourceFingerprint && state.CoverageIssues.Count == 0 ? "current" : "pending", Error = null });
                        return;
                    }
                    // A forced daily version also catches edits by tools which preserve file timestamps.
                    if (await FingerprintAsync() == state.SourceFingerprint && age >= TimeSpan.Zero && age < TimeSpan.FromHours(24) && state.CoverageIssues.Count == 0 && state.Status != "failed")
                    { Save(state with { Status = "current", Error = null }); return; }
                    // The account service is contacted before anything is staged: a rejected key or a server without
                    // complete backups must not leave multi-gigabyte archives behind that nothing will accept.
                    accountId = await transport.AccountAsync(credential, ct);
                    if (key() != credential) return;
                    // The version is recorded before staging, so a crash after its manifest is written resumes delivery
                    // instead of orphaning a complete archive that neither reclaim path would touch.
                    var version = Guid.NewGuid().ToString("N");
                    Save(state with { Status = "preparing", PendingVersion = version, NextPart = 0, Error = null });
                    preparing = true;
                    using var captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    captureTimeout.CancelAfter(TimeSpan.FromMinutes(30));
                    var manifest = await PrepareAsync(datasetPath, dataset, version, fingerprint!, sources, captureTimeout.Token);
                    state = state with { Status = "uploading", Error = null, SourceFingerprint = manifest.SourceFingerprint, CoverageIssues = manifest.CoverageIssues };
                    Save(state);
                    preparing = false;
                }
                if (key() != credential) return;
                var versionPath = VersionPath(datasetPath, state.PendingVersion!);
                var (manifestBytes, pending) = await ReadPendingManifestAsync(versionPath, ct);
                if (pending is null || !BackupFormat.IsValid(pending) || pending.Dataset != dataset || pending.Version != state.PendingVersion || state.NextPart > pending.Parts.Count)
                    throw new InvalidDataException("Backup staging manifest is invalid; it has been preserved.");
                accountId ??= await transport.AccountAsync(credential, ct);
                var end = Math.Min(pending.Parts.Count, state.NextPart + PartsPerTick);
                var deliveryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                for (var i = state.NextPart; i < end; i++)
                {
                    if (key() != credential) return;
                    var part = pending.Parts[i];
                    await transport.UploadPartAsync(credential, part, Path.Combine(versionPath, part.Sha256 + ".part"), ct);
                    state = state with { NextPart = i + 1, Status = "uploading", Attempts = 0, Error = null };
                    Save(state);
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(deliveryStarted) > TimeSpan.FromSeconds(45)) break;
                }
                if (state.NextPart < pending.Parts.Count || key() != credential) return;
                var latest = await FingerprintAsync();
                var receipt = await transport.CommitAsync(credential, accountId.Value, pending, manifestBytes, ct);
                // Persist the validated receipt before removing any transport staging file.
                AtomicFile.WriteAllText(Path.Combine(versionPath, "receipt.json"), JsonSerializer.Serialize(receipt, BackupJson.Default.BackupReceipt));
                // Fingerprint and coverage come from the delivered manifest: a resumed version may not share them with the checkpoint.
                state = state with { Receipt = receipt, PendingVersion = null, NextPart = 0, Attempts = 0, NextAttemptUtc = default,
                    SourceFingerprint = pending.SourceFingerprint, CoverageIssues = pending.CoverageIssues,
                    Status = pending.CoverageIssues.Count == 0 && latest == pending.SourceFingerprint ? "current" : "pending", Error = null };
                Save(state);
                foreach (var part in pending.Parts.DistinctBy(p => p.Sha256)) File.Delete(Path.Combine(versionPath, part.Sha256 + ".part"));
                log.Write("data-upload", "succeeded", $"Complete backup receipt verified for {dataset}.", pending.Version, "Complete backup", "succeeded");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                // Every failure is recorded with backoff, including SQLite busy/locked errors from the stores the
                // configuration walk reads and invalid stored paths. An escaped exception would skip this Save, so the
                // same dataset would be selected again on every tick and the others would starve.
                var corrupt = e is InvalidDataException && e.Message.StartsWith("Backup staging", StringComparison.Ordinal);
                var shown = e is HttpRequestException or InvalidDataException || e.Message.StartsWith("Not enough disk space", StringComparison.Ordinal);
                state = state with { Status = "failed", Error = shown ? e.Message : "Backup preparation or delivery failed; it will retry automatically.",
                    Attempts = state.Attempts + 1, NextAttemptUtc = now().AddMinutes(Math.Min(360, Math.Pow(2, Math.Min(state.Attempts + 1, 9)))),
                    // A version that failed during preparation was never published; its directory has no manifest and is reclaimed.
                    PendingVersion = corrupt || preparing ? null : state.PendingVersion,
                    NextPart = corrupt || preparing || e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Conflict } ? 0 : state.NextPart };
                Save(state);
                log.Write("data-upload", "failed", $"Complete backup for {dataset} will retry ({e.GetType().Name}).", state.PendingVersion, "Complete backup", "failed", LogLevel.Warning);
            }

            void Save(BackupCheckpoint current) { state = current; WriteState(account, dataset, current); }
        }
        finally { Gate.Release(); }
    }

    private async Task<BackupManifest> PrepareAsync(string directory, string dataset, string version, string fingerprint,
        List<BackupFileSource>? sources, CancellationToken ct)
    {
        ReclaimUnpublishedStaging(directory);
        var work = VersionPath(directory, version);
        PrivateFilePermissions.EnsureDirectory(work);
        var started = now();
        List<string> issues = [];
        var snapshot = Path.Combine(work, "snapshot.db");
        // The parent spool is private before SQLite or compression writes any user content.
        using var parts = new BackupPartWriter(work);
        if (sources is null)
        {
            var source = DatabasePath(dataset);
            if (AvailableFreeSpace(work) < new FileInfo(source).Length * 2 + BackupFormat.PartBytes)
                throw new IOException("Not enough disk space to prepare a complete backup.");
            await snapshots.CreateSnapshotAsync(source, snapshot, ct);
            try
            {
                await using var input = File.OpenRead(snapshot);
                await using var compression = new BrotliStream(parts, new BrotliCompressionOptions { Quality = 4 }, leaveOpen: true);
                await input.CopyToAsync(compression, 128 * 1024, ct);
            }
            finally { File.Delete(snapshot); }
        }
        else issues = await BackupFiles.WriteZipAsync(parts, sources, snapshots, work, ct);
        parts.Finish();
        var machineFile = Path.Combine(Spool, "computer-id");
        var machine = File.Exists(machineFile) ? AtomicFile.ReadAllText(machineFile) : Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(machine, "N", out _)) throw new IOException("Backup computer identity is invalid.");
        if (!File.Exists(machineFile)) AtomicFile.WriteAllText(machineFile, machine);
        var name = ComputerNameFormatter.Normalize(Environment.MachineName);
        var manifest = new BackupManifest(1, machine, name, dataset, version, started, now(), fingerprint,
            sources is null ? "sqlite.br" : "zip", parts.Length, parts.Parts, issues);
        if (!BackupFormat.IsValid(manifest)) throw new IOException("The complete backup manifest is invalid.");
        AtomicFile.WriteAllText(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(manifest, BackupJson.Default.BackupManifest));
        return manifest;
    }

    private static void ReclaimUnpublishedStaging(string directory)
    {
        // Only interrupted, unpublished transport artifacts. A manifest/receipt (even corrupt)
        // protects its directory. The machine lock is held; no other root is preparing here.
        foreach (var candidate in Directory.EnumerateDirectories(directory))
        {
            if (!Guid.TryParseExact(Path.GetFileName(candidate), "N", out _) || BackupFiles.HasLink(candidate)
                || File.Exists(Path.Combine(candidate, "manifest.json")) || File.Exists(Path.Combine(candidate, "receipt.json"))) continue;
            var artifacts = Directory.GetFiles(candidate);
            if (Directory.EnumerateDirectories(candidate).Any() || artifacts.Any(p => BackupFiles.HasLink(p) || !IsStagingName(Path.GetFileName(p)))) continue;
            foreach (var artifact in artifacts) File.Delete(artifact);
            Directory.Delete(candidate); // Nonrecursive: an unexpected file is never removed.
        }

    }

    /// <summary>
    /// Transport and capture artifacts a preparation creates: parts, the database snapshot, native database
    /// snapshots and large-file copies, including the SQLite sidecars a crash mid-snapshot leaves beside them.
    /// </summary>
    internal static bool IsStagingName(string name)
    {
        if (name.EndsWith(".part", StringComparison.Ordinal)) return BackupFormat.IsHash(name[..^5]);
        if (name.StartsWith("copy-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal)) return Guid.TryParseExact(name[5..^4], "N", out _);
        var database = name.EndsWith("-journal", StringComparison.Ordinal) ? name[..^8]
            : name.EndsWith("-wal", StringComparison.Ordinal) || name.EndsWith("-shm", StringComparison.Ordinal) ? name[..^4] : name;
        return database == "snapshot.db" || (database.StartsWith("native-", StringComparison.Ordinal) && database.EndsWith(".db", StringComparison.Ordinal)
            && Guid.TryParseExact(database[7..^3], "N", out _));
    }

    /// <summary>Free space on the staging volume, or unlimited when it is not a drive (a UNC profile); the write itself then reports a full disk.</summary>
    private static long AvailableFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch (ArgumentException) { return long.MaxValue; }
    }

    private static async Task ReclaimAcknowledgedStagingAsync(string directory, string dataset, int accountId, CancellationToken ct)
    {
        // A crash can occur after the durable receipt/checkpoint but before part deletion.
        // Revisit old receipts too, with bounded work, without deleting source or diagnostic data.
        var inspected = 0;
        foreach (var candidate in Directory.EnumerateDirectories(directory))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(candidate), "N", out _) || BackupFiles.HasLink(candidate)) continue;
            try
            {
                var receiptPath = Path.Combine(candidate, "receipt.json");
                if (!File.Exists(receiptPath) || !Directory.EnumerateFiles(candidate, "*.part").Any()
                    || new FileInfo(receiptPath).Length > 32 * 1024) continue;
                var (bytes, manifest) = await ReadPendingManifestAsync(candidate, ct);
                var receipt = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(receiptPath, ct), BackupJson.Default.BackupReceipt);
                if (manifest is null || !BackupFormat.IsValid(manifest) || manifest.Dataset != dataset
                    || manifest.Version != Path.GetFileName(candidate)
                    || !BackupTransport.MatchesReceipt(receipt, accountId, manifest, BackupFormat.Hash(bytes))) continue;
                foreach (var part in manifest.Parts.DistinctBy(p => p.Sha256))
                {
                    var path = Path.Combine(candidate, part.Sha256 + ".part");
                    if (!File.Exists(path) || BackupFiles.HasLink(path)) continue;
                    if (inspected++ >= PartsPerTick) return;
                    if (new FileInfo(path).Length != part.Bytes) continue;
                    var content = await File.ReadAllBytesAsync(path, ct);
                    if (BackupFormat.Hash(content) == part.Sha256) File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { /* Preserve unreadable or corrupt evidence and retry on a later tick. */ }
        }
    }

    private string DatabasePath(string dataset) => Path.Combine(root(), dataset switch { "board" => "board.db", "state" => "state.db", "proxy" => "proxy_exchanges.db", _ => throw new ArgumentException("Unknown database.") });
    private static async Task<(byte[] Bytes, BackupManifest? Manifest)> ReadPendingManifestAsync(string directory, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(directory, "manifest.json");
            if (new FileInfo(path).Length > BackupFormat.MaxManifestBytes)
                throw new InvalidDataException("Backup staging manifest exceeds its limit.");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return (bytes, JsonSerializer.Deserialize(bytes, BackupJson.Default.BackupManifest));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        { throw new InvalidDataException("Backup staging manifest is missing or malformed; remaining files have been preserved.", e); }
    }

    private string DatabaseFingerprint(string dataset) => BackupFiles.Fingerprint([new(DatabasePath(dataset), dataset)]);
    private static string VersionPath(string root, string version) => Guid.TryParseExact(version, "N", out _) ? Path.Combine(root, version) : throw new InvalidDataException("Backup staging version is invalid.");
    private static BackupCheckpoint ReadState(string account, string dataset)
    {
        var path = Path.Combine(account, dataset, "checkpoint.json");
        long length;
        try { length = new FileInfo(path).Length; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(); }
        if (length > 2 * 1024 * 1024) throw new InvalidDataException("Backup checkpoint exceeds its size limit.");
        var state = JsonSerializer.Deserialize(AtomicFile.ReadAllText(path), BackupJson.Default.BackupCheckpoint)
            ?? throw new InvalidDataException("Backup checkpoint is invalid.");
        if (state.NextPart < 0 || state.NextPart > BackupFormat.MaxParts || state.Attempts < 0
            || state.CoverageIssues is null || (state.PendingVersion is not null && !Guid.TryParseExact(state.PendingVersion, "N", out _)))
            throw new InvalidDataException("Backup checkpoint is invalid.");
        return state;
    }
    private static BackupCheckpoint RecoverState(string account, string dataset)
    {
        try { return ReadState(account, dataset); }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            // Keep the corrupt receipt/checkpoint for inspection; it cannot acknowledge any archive.
            var path = Path.Combine(account, dataset, "checkpoint.json");
            File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
            return new() { Status = "failed", Error = "A corrupt checkpoint was preserved; a new backup will be created." };
        }
    }
    private BackupCheckpoint? TryRecoverState(string account, string dataset)
    {
        var id = Path.Combine(account, dataset);
        if (checkpointRetryAfter.TryGetValue(id, out var retry) && retry > now() && retry <= now().AddHours(6)) return null;
        try
        {
            var state = RecoverState(account, dataset);
            checkpointRetryAfter.Remove(id);
            return state;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never replace a checkpoint we could not read. Other datasets remain eligible.
            checkpointRetryAfter[id] = now().AddMinutes(2);
            log.Write("data-upload", "failed", $"Complete backup checkpoint for {dataset} is unreadable; other datasets will continue.",
                null, "Complete backup", "failed", LogLevel.Warning);
            return null;
        }
    }
    private static void WriteState(string account, string dataset, BackupCheckpoint state) => AtomicFile.WriteAllText(
        Path.Combine(account, dataset, "checkpoint.json"), JsonSerializer.Serialize(state, BackupJson.Default.BackupCheckpoint));
}
