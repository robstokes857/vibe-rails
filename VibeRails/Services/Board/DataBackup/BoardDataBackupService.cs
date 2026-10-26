using System.Text;
using System.Text.Json;
using VibeRails.Services.Diagnostics;
using VibeRails.Utils;

namespace VibeRails.Services.Board.Backup;

/// <summary>Automatic, complete Board snapshots in PostgreSQL, independent of hosted Board publication.</summary>
public sealed class BoardDataBackupService
{
    private readonly IBoardStore store;
    private readonly BoardBackupTransport transport;
    private readonly Func<string> root;
    private readonly Func<string> key;
    private readonly Func<DateTime> now;
    private readonly IFeatureLog log;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static readonly TimeSpan CaptureInterval = TimeSpan.FromMinutes(15);
    private string Spool => Path.Combine(root(), "board-data-backups");

    public BoardDataBackupService(IBoardStore store, BoardBackupTransport transport, IFeatureLog log)
        : this(store, transport, () => Path.GetDirectoryName(ParserConfigs.GetStatePath())!, ParserConfigs.GetApiKey,
            () => DateTime.UtcNow, log) { }

    internal BoardDataBackupService(IBoardStore store, BoardBackupTransport transport, Func<string> root,
        Func<string> key, Func<DateTime> now, IFeatureLog? log = null)
    { this.store = store; this.transport = transport; this.root = root; this.key = key; this.now = now; this.log = log ?? NullFeatureLog.Instance; }

    /// <summary>Reads durable status without starting an upload.</summary>
    public BoardBackupState GetStatus()
    {
        var credential = key();
        if (string.IsNullOrWhiteSpace(credential)) return new() { Error = "No account key is configured." };
        try { return ReadState(AccountPath(credential)); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { return new() { Error = "Board backup checkpoint cannot be read; it remains preserved." }; }
    }

    /// <summary>Captures at most every fifteen minutes and delivers bounded batches with resumable receipts.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var credential = key();
        if (string.IsNullOrWhiteSpace(credential) || !await Gate.WaitAsync(0, ct)) return;
        try
        {
            PrivateFilePermissions.EnsureDirectory(Spool);
            using var machineLock = CrossProcessFileLock.TryAcquire(Path.Combine(Spool, ".lock"));
            if (machineLock is null) return;
            var account = AccountPath(credential); PrivateFilePermissions.EnsureDirectory(account);
            BoardBackupState state;
            try { state = ReadState(account); }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            {
                var checkpoint = Path.Combine(account, "checkpoint.json");
                File.Move(checkpoint, checkpoint + ".corrupt-" + Guid.NewGuid().ToString("N"));
                state = new() { Error = "A corrupt checkpoint was preserved; creating a new complete Board backup." };
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                log.Write("data-upload", "failed", "Board backup checkpoint is unreadable; preserved for recovery.", null, "Board PostgreSQL backup", "failed", LogLevel.Warning);
                return;
            }
            if (state.NextAttemptUtc > now() && state.NextAttemptUtc <= now().AddHours(6)) return;
            try
            {
                var owner = await transport.AccountAsync(credential, ct);
                if (key() != credential) return;
                if (state.PendingVersion is null)
                {
                    var version = BoardBackupProtocol.NewVersion();
                    state = state with { PendingVersion = version, NextContent = 0, NextRecord = 0 };
                    Save();
                }
                var directory = VersionPath(account, state.PendingVersion);
                var manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    // A capture interrupted before its atomic manifest has no remotely acknowledged snapshot.
                    // Its known files may be overwritten when the same version is recaptured.
                    PrivateFilePermissions.EnsureDirectory(directory);
                    state = state with { NextContent = 0, NextRecord = 0 }; Save();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(10));
                    var manifest = await CaptureAsync(directory, state.PendingVersion, deadline.Token);
                    var fingerprint = Fingerprint(manifest);
                    if (state.Fingerprint == fingerprint && state.Receipt is { } previous && previous.AccountId == owner)
                    {
                        state = state with { PendingVersion = null, NextContent = 0, NextRecord = 0, Attempts = 0,
                            NextAttemptUtc = now() + CaptureInterval, Error = null };
                        Save(); Cleanup(directory); return;
                    }
                    var serialized = JsonSerializer.Serialize(manifest, BoardBackupJson.Default.BoardBackupManifest);
                    if (Encoding.UTF8.GetByteCount(serialized) > BoardBackupProtocol.ManifestBytes)
                        throw new InvalidDataException("Complete Board backup inventory exceeds the supported size; no snapshot acknowledged.");
                    AtomicFile.WriteAllText(manifestPath, serialized);
                }
                var bytes = await ReadBoundedAsync(manifestPath, BoardBackupProtocol.ManifestBytes, ct);
                var pending = JsonSerializer.Deserialize(bytes, BoardBackupJson.Default.BoardBackupManifest);
                if (pending is null || !BoardBackupProtocol.IsValid(pending) || pending.Version != state.PendingVersion
                    || state.NextContent > pending.Contents.Count || state.NextRecord > pending.Records.Count)
                    throw new InvalidDataException("Complete Board backup staging is invalid; preserved for recovery.");
                var started = System.Diagnostics.Stopwatch.GetTimestamp(); var requests = 0;
                while (state.NextContent < pending.Contents.Count && requests < 16)
                {
                    if (key() != credential) return;
                    var hashes = pending.Contents.Skip(state.NextContent).Take(BoardBackupProtocol.BatchRows).ToArray();
                    var missing = await transport.MissingAsync(credential, "content", hashes, ct); requests++;
                    foreach (var hash in hashes)
                    {
                        if (key() != credential || requests >= 16) return;
                        if (missing.Contains(hash))
                        {
                            var content = await ReadBoundedAsync(Path.Combine(directory, hash + ".bin"), BoardBackupProtocol.ContentBytes, ct);
                            await transport.ContentAsync(credential, hash, content, ct); requests++;
                        }
                        state = state with { NextContent = state.NextContent + 1 }; Save();
                        if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(40)) return;
                    }
                }
                while (state.NextContent == pending.Contents.Count && state.NextRecord < pending.Records.Count && requests < 16)
                {
                    if (key() != credential) return;
                    var hashes = pending.Records.Skip(state.NextRecord).Take(BoardBackupProtocol.BatchRows).ToArray();
                    var missing = await transport.MissingAsync(credential, "row", hashes, ct); requests++;
                    var rows = new List<byte[]>(); var size = 2; var processed = 0;
                    foreach (var hash in hashes)
                    {
                        if (!missing.Contains(hash)) { processed++; continue; }
                        var row = await ReadBoundedAsync(Path.Combine(directory, hash + ".json"), BoardBackupProtocol.RequestBytes - 2, ct);
                        if (BoardBackupProtocol.Hash(row) != hash) throw new InvalidDataException("Board backup row checksum failed.");
                        if (size + row.Length + 1 > BoardBackupProtocol.RequestBytes) break;
                        rows.Add(row); size += row.Length + 1; processed++;
                    }
                    if (processed == 0) throw new InvalidDataException("A Board backup row exceeds the transfer bound.");
                    if (rows.Count > 0) { await transport.RowsAsync(credential, rows, ct); requests++; }
                    state = state with { NextRecord = state.NextRecord + processed }; Save();
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(40)) return;
                }
                if (state.NextContent < pending.Contents.Count || state.NextRecord < pending.Records.Count || key() != credential) return;
                var receipt = await transport.CommitAsync(credential, owner, pending, bytes, ct);
                AtomicFile.WriteAllText(Path.Combine(directory, "receipt.json"), JsonSerializer.Serialize(receipt, BoardBackupJson.Default.BoardBackupReceipt));
                state = state with { Receipt = receipt, Fingerprint = Fingerprint(pending), PendingVersion = null,
                    NextContent = 0, NextRecord = 0, Attempts = 0, NextAttemptUtc = now() + CaptureInterval, Error = null };
                Save(); Cleanup(directory);
                log.Write("data-upload", "succeeded", $"PostgreSQL acknowledged the complete Board backup: {receipt.Records} rows, {receipt.Contents} content parts.",
                    pending.Version, "Board PostgreSQL backup", "succeeded");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                state = state with { Attempts = state.Attempts + 1,
                    NextAttemptUtc = now().AddMinutes(Math.Min(360, Math.Pow(2, Math.Min(state.Attempts + 1, 9)))),
                    PendingVersion = e is InvalidDataException or FileNotFoundException or DirectoryNotFoundException ? null : state.PendingVersion,
                    NextContent = e is InvalidDataException or FileNotFoundException or DirectoryNotFoundException ? 0 : state.NextContent,
                    NextRecord = e is InvalidDataException or FileNotFoundException or DirectoryNotFoundException ? 0 : state.NextRecord,
                    Error = e is HttpRequestException or InvalidDataException ? e.Message : "Board data backup failed; retrying automatically." };
                Save();
                log.Write("data-upload", "failed", state.Error, state.PendingVersion, "Board PostgreSQL backup", "failed", LogLevel.Warning);
            }
            void Save() => AtomicFile.WriteAllText(Path.Combine(account, "checkpoint.json"), JsonSerializer.Serialize(state, BoardBackupJson.Default.BoardBackupState));
        }
        finally { Gate.Release(); }
    }

    private async Task<BoardBackupManifest> CaptureAsync(string directory, string version, CancellationToken ct)
    {
        var records = new HashSet<string>(StringComparer.Ordinal); var contents = new HashSet<string>(StringComparer.Ordinal);
        var started = now();
        var machineFile = Path.Combine(Spool, "computer-id");
        if (!File.Exists(machineFile)) AtomicFile.WriteAllText(machineFile, BoardBackupProtocol.NewComputerId());
        var computer = AtomicFile.ReadAllText(machineFile).Trim();
        if (!BoardBackupProtocol.IsComputerId(computer)) throw new InvalidDataException("Board backup computer identity is invalid.");
        await store.ReadDataBackupAsync(async (item, token) =>
        {
            var bytes = item.Json is null ? item.Content ?? throw new InvalidDataException("Missing Board backup value.") : Encoding.UTF8.GetBytes(item.Json);
            if (BoardBackupProtocol.Hash(bytes) != item.Sha256) throw new InvalidDataException("Invalid Board backup content checksum.");
            var set = item.Json is null ? contents : records;
            if (!set.Add(item.Sha256)) return;
            if (set.Count > BoardBackupProtocol.MaxItems) throw new InvalidDataException("Complete Board backup exceeds the inventory bound; no snapshot acknowledged.");
            var path = Path.Combine(directory, item.Sha256 + (item.Json is null ? ".bin" : ".json"));
            await File.WriteAllBytesAsync(path, bytes, token); PrivateFilePermissions.EnsureFile(path);
        }, ct);
        var name = Environment.MachineName;
        return new(1, computer, name.Length > 80 ? name[..80] : name, version, started, now(),
            records.Order(StringComparer.Ordinal).ToList(), contents.Order(StringComparer.Ordinal).ToList());
    }

    private string AccountPath(string credential) => Path.Combine(Spool, BoardBackupProtocol.Hash(Encoding.UTF8.GetBytes(credential)));
    private static string VersionPath(string account, string? version) => BoardBackupProtocol.IsVersion(version)
        ? Path.Combine(account, version!) : throw new InvalidDataException("Invalid Board backup version.");
    private static string Fingerprint(BoardBackupManifest m) => BoardBackupProtocol.Hash(Encoding.UTF8.GetBytes(string.Join('\n', m.Records) + "\ncontents\n" + string.Join('\n', m.Contents)));
    private static BoardBackupState ReadState(string directory)
    {
        var path = Path.Combine(directory, "checkpoint.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Oversized Board backup checkpoint.");
        var state = JsonSerializer.Deserialize(AtomicFile.ReadAllText(path), BoardBackupJson.Default.BoardBackupState)
            ?? throw new InvalidDataException("Invalid Board backup checkpoint.");
        if (state.NextContent < 0 || state.NextRecord < 0 || state.Attempts < 0
            || (state.PendingVersion is not null && !BoardBackupProtocol.IsVersion(state.PendingVersion)))
            throw new InvalidDataException("Invalid Board backup checkpoint.");
        return state;
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length > limit) throw new InvalidDataException("Oversized Board backup staging file.");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, ct); return bytes;
    }
    private static void Cleanup(string directory)
    {
        // Only generated hash-named payloads are disposable. Manifests, receipts and unknown files remain.
        foreach (var file in Directory.EnumerateFiles(directory))
            if (Path.GetExtension(file) is ".bin" or ".json" && BoardBackupProtocol.IsHash(Path.GetFileNameWithoutExtension(file))) File.Delete(file);
    }
}
