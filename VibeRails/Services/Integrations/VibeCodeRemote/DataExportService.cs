using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Serilog;
using VibeRails.Services.Diagnostics;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Creates a consistent snapshot of state.db, Brotli-compresses and hashes the snapshot,
/// and streams the compressed bytes to the configured remote endpoint.
/// </summary>
public sealed class DataExportService : IDataExportService
{
    internal const string ExportUrlSettingKey = "VibeRails:ExportUrl";
    internal const string SnapshotFileName = "copy_state.db";
    internal const string CompressedFileName = "copy_state.db.br";
    internal const string LockFileName = ".data-export.lock";

    // Anything larger than one block goes through the resumable chunked protocol: a shared host
    // can cap request size and time out a long request, so a multi-GB single POST is at the mercy
    // of one uninterrupted connection. Smaller payloads are not worth the extra round trips. This
    // only decides *whether* to chunk — the server reports the block size actually used.
    internal const int ChunkedUploadThresholdBytes = 4 * 1024 * 1024;
    private const int DefaultUploadBlockSizeBytes = 4 * 1024 * 1024;
    private const int MaxUploadAttemptsPerBlock = 3;
    private const int MaxCommitPasses = 2;

    // The block size arrives from the server, so it is bounded before it is used to size a pooled
    // buffer or to divide the payload. Left unchecked, a huge value is a multi-gigabyte rent and a
    // tiny one is a request per byte. These are rejected rather than clamped on purpose: the server
    // validates the block count against its *own* block size, so quietly substituting a different
    // one would fail every commit instead of failing here with a readable reason.
    private const int MinUploadBlockSizeBytes = 1024 * 1024;
    private const int MaxUploadBlockSizeBytes = 64 * 1024 * 1024;

    // Azure's ceiling on blocks per block blob. More than this could never be committed.
    private const int MaxUploadBlockCount = 50_000;

    // Metadata responses (the probe, and any error body) are small by contract. Bounding the read
    // stops a stalled or endlessly-streaming server from growing the heap while this export holds
    // the process gate and the cross-process lock.
    private const int MaxMetadataBodyBytes = 64 * 1024;
    private const int MaxErrorBodyBytes = 4 * 1024;
    private const int MaxDisplayedErrorCharacters = 600;

    // ResponseHeadersRead hands back the response once headers land, so the body is read outside
    // whatever the send itself was bounded by. These calls exchange a few hundred bytes; without
    // a deadline of their own a silent server could hold both locks indefinitely.
    private static readonly TimeSpan MetadataResponseTimeout = TimeSpan.FromSeconds(30);

    private const int SnapshotPollIntervalMs = 250;

    // Fast in-process rejection avoids touching the lock file for duplicate requests in one host.
    // CrossProcessFileLock below supplies the actual machine-wide guarantee across browser, VS
    // Code, and Git Guard root backends.
    private static readonly SemaphoreSlim ExportGate = new(initialCount: 1, maxCount: 1);

    // Brotli quality is a speed/size tradeoff, and SQLite pages are largely incompressible.
    // Quality 11 is the slowest level by a wide margin and CopyToAsync runs the compressor
    // *synchronously* on whichever thread-pool thread services each chunk — on a large state.db
    // that pins a pool thread for minutes inside an HTTP request. 5 is the usual one-shot choice.
    private const int BrotliQuality = 5;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly Func<string> _temporaryDirectoryFactory;
    private readonly Func<string> _computerNameFactory;
    private readonly IDataExportProgress _progress;
    private readonly IFeatureLog _featureLog;

    public DataExportService(
        HttpClient httpClient,
        IConfiguration configuration,
        IDataExportProgress progress,
        IFeatureLog? featureLog = null)
        : this(
            httpClient,
            configuration,
            static () => Path.Combine(
                Path.GetTempPath(),
                $"viberails-data-export-{Guid.NewGuid():N}"),
            ResolveComputerName,
            progress,
            featureLog)
    {
    }

    internal DataExportService(
        HttpClient httpClient,
        IConfiguration configuration,
        Func<string> temporaryDirectoryFactory,
        Func<string> computerNameFactory,
        IDataExportProgress? progress = null,
        IFeatureLog? featureLog = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _temporaryDirectoryFactory = temporaryDirectoryFactory;
        _computerNameFactory = computerNameFactory;
        _progress = progress ?? NullDataExportProgress.Instance;
        _featureLog = featureLog ?? NullFeatureLog.Instance;
    }

    /// <summary>
    /// Absolute HTTPS only. The API key travels in a request header, so a relative or cleartext
    /// URL must never be used — and the shipped placeholder must not look configured.
    /// </summary>
    internal bool TryGetExportUri(out Uri exportUri)
        => TryParseExportUri(_configuration[ExportUrlSettingKey], out exportUri);

    /// <summary>
    /// Static so the settings endpoint can gate the Export button on exactly the rule the export
    /// itself enforces, instead of the two drifting apart.
    /// </summary>
    internal static bool TryParseExportUri(string? configured, out Uri exportUri)
    {
        exportUri = null!;

        if (string.IsNullOrWhiteSpace(configured))
            return false;
        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        // Chunk endpoints are built by appending path segments. Appending to a URL that already
        // carries a query or fragment silently lands the new path inside the query, leaving every
        // chunk request pointed at the base path. Refuse it here so the misconfiguration is
        // visible rather than mysterious.
        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
            return false;

        exportUri = parsed;
        return true;
    }

    public async Task<DataExportResult> ExportAsync(CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("D");
        WriteUploadLog(operationId, "started", "Database snapshot upload requested.");
        try
        {
            var result = await ExportCoreAsync(cancellationToken);
            var (status, message) = result.Status switch
            {
                DataExportStatus.Success => ("succeeded", "The server acknowledged the database snapshot upload."),
                DataExportStatus.NoApiKey => ("skipped", "Upload skipped because no API key is configured."),
                DataExportStatus.NotConfigured => ("skipped", "Upload skipped because no HTTPS export endpoint is configured."),
                DataExportStatus.Busy => ("skipped", "Upload skipped because another database export is running."),
                DataExportStatus.InvalidApiKey => ("failed", "The server rejected the configured API key."),
                DataExportStatus.UploadFailed => ("failed", "The database snapshot upload was not acknowledged by the server."),
                _ => ("failed", "The database snapshot could not be prepared for upload.")
            };
            WriteUploadLog(operationId, status, message);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteUploadLog(operationId, "cancelled", "The database snapshot upload was cancelled before an acknowledgement was received.");
            throw;
        }
        catch
        {
            WriteUploadLog(operationId, "failed", "The database snapshot export did not complete.");
            throw;
        }
    }

    private void WriteUploadLog(string operationId, string status, string message) =>
        _featureLog.Write(
            "data-upload", status, message, operationId, "Database snapshot", status,
            status == "failed" ? LogLevel.Warning : LogLevel.Information);

    private async Task<DataExportResult> ExportCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var apiKey = ParserConfigs.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new DataExportResult(
                DataExportStatus.NoApiKey,
                Detail: "No API key is configured.");
        }

        if (!TryGetExportUri(out var exportUri))
        {
            return new DataExportResult(
                DataExportStatus.NotConfigured,
                Detail: "No absolute HTTPS export URL is configured.");
        }

        var statePath = ParserConfigs.GetStatePath();
        if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath))
        {
            return new DataExportResult(
                DataExportStatus.Failed,
                Detail: "The state database was not found.");
        }

        if (!ExportGate.Wait(0))
        {
            return new DataExportResult(
                DataExportStatus.Busy,
                Detail: "Another data export is already running.");
        }

        string? temporaryDirectory = null;
        CrossProcessFileLock? crossProcessLock = null;
        var progressStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            crossProcessLock = CrossProcessFileLock.TryAcquire(
                CrossProcessFileLock.BesideStateDatabase(statePath, LockFileName));
            if (crossProcessLock is null)
            {
                return new DataExportResult(
                    DataExportStatus.Busy,
                    Detail: "Another data export is already running.");
            }

            // Only after both locks are held: an export that loses the race must not reset the
            // progress the winning run is publishing.
            _progress.Begin();
            progressStarted = true;

            temporaryDirectory = _temporaryDirectoryFactory();
            PrivateFilePermissions.EnsureDirectory(temporaryDirectory);

            var insufficientSpace = DescribeInsufficientSpace(statePath, temporaryDirectory);
            if (insufficientSpace is not null)
            {
                Log.Warning("[DataExport] {Detail}", insufficientSpace);
                return new DataExportResult(DataExportStatus.Failed, Detail: insufficientSpace);
            }

            var snapshotPath = Path.Combine(temporaryDirectory, SnapshotFileName);
            var compressedPath = Path.Combine(temporaryDirectory, CompressedFileName);

            _progress.SetStage(DataExportStage.Snapshot, TryGetFileLength(statePath));
            await CreateSnapshotWithProgressAsync(statePath, snapshotPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _progress.SetStage(DataExportStage.Compressing, TryGetFileLength(snapshotPath));
            await CompressSnapshotAsync(snapshotPath, compressedPath, cancellationToken);

            var compressedLength = TryGetFileLength(compressedPath);
            _progress.SetStage(DataExportStage.Hashing, compressedLength);
            var sha256 = await ComputeSha256Async(compressedPath, cancellationToken);
            var computerName = ComputerNameFormatter.Normalize(_computerNameFactory());
            if (string.IsNullOrWhiteSpace(computerName))
                computerName = "unknown-computer";

            DeleteSnapshotFilesBestEffort(snapshotPath);

            _progress.SetStage(DataExportStage.Uploading, compressedLength);
            return await SendCompressedExportAsync(
                exportUri,
                apiKey,
                computerName,
                sha256,
                compressedPath,
                compressedLength,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            // Overwhelmingly the "something else has the database open" case, which has its own
            // obvious fix. Reporting it as a generic preparation failure sends people diffing code.
            Log.Error(
                exception,
                "[DataExport] SQLite refused to snapshot the state database (code {ErrorCode}).",
                exception.SqliteErrorCode);
            return new DataExportResult(
                DataExportStatus.Failed,
                Detail: exception.SqliteErrorCode == 5
                    ? "The state database is locked by another program. Close any SQLite browser "
                      + "or leftover vb process and try again."
                    : "SQLite could not read the state database.");
        }
        catch (Exception exception)
        {
            Log.Error(
                exception,
                "[DataExport] Failed while creating or preparing the state database export.");
            return new DataExportResult(
                DataExportStatus.Failed,
                Detail: "Failed to prepare the data export.");
        }
        finally
        {
            try
            {
                if (progressStarted)
                    _progress.End();
            }
            finally
            {
                try
                {
                    CleanupTemporaryArtifacts(temporaryDirectory);
                }
                finally
                {
                    try
                    {
                        crossProcessLock?.Dispose();
                    }
                    finally
                    {
                        ExportGate.Release();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Runs the one blocking backup call while reporting how far it has got. SQLite exposes no
    /// incremental backup progress through Microsoft.Data.Sqlite, so the signal is the destination
    /// file's own growth — a real measurement rather than a synthetic ramp. Only the temp copy is
    /// ever inspected; nothing here opens the live database.
    /// </summary>
    private async Task CreateSnapshotWithProgressAsync(
        string statePath,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        // Deliberately not cancellable, exactly as before: BackupDatabase cannot be interrupted,
        // and abandoning it while cleanup deletes the file it is writing would be worse than
        // waiting. Cancellation is observed by the caller as soon as this returns.
        var backup = Task.Run(() => CreateSnapshot(statePath, snapshotPath), CancellationToken.None);

        while (true)
        {
            var finished = await Task.WhenAny(backup, Task.Delay(SnapshotPollIntervalMs));
            if (ReferenceEquals(finished, backup))
                break;

            _progress.SetProcessed(TryGetFileLength(snapshotPath));
        }

        await backup;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static long TryGetFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            // Progress display only — an unreadable length must never fail an export.
            return 0;
        }
    }

    /// <summary>
    /// The snapshot and its compressed copy both sit in the temp directory before the upload, so
    /// a large state.db needs roughly twice its size free. Checking up front turns a failure that
    /// would otherwise land after minutes of backup and compression into an immediate, explicable
    /// one. Returns null when there is room — or when free space can't be determined (UNC paths
    /// and some mounts throw), because an unreadable volume must not block a viable export.
    /// </summary>
    private static string? DescribeInsufficientSpace(string statePath, string temporaryDirectory)
    {
        try
        {
            var required = new FileInfo(statePath).Length * 2L;
            var root = Path.GetPathRoot(Path.GetFullPath(temporaryDirectory));
            if (string.IsNullOrWhiteSpace(root))
                return null;

            var available = new DriveInfo(root).AvailableFreeSpace;
            if (available >= required)
                return null;

            const long Mb = 1024 * 1024;
            return $"The data export needs about {required / Mb} MB free on the temporary volume, "
                   + $"which has {available / Mb} MB.";
        }
        catch
        {
            return null;
        }
    }

    private static void CreateSnapshot(string statePath, string snapshotPath)
    {
        // The only line in this file that opens a database for writing is the destination
        // connection below. If a refactor ever pointed it at the live path, File.Create would
        // truncate state.db before SQLite even got involved. The export is read-only with respect
        // to the live database by design, and this keeps it that way.
        if (string.Equals(
                Path.GetFullPath(statePath),
                Path.GetFullPath(snapshotPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The export snapshot destination must never be the live state database.");
        }

        // Pre-create and restrict the destination before SQLite writes any sensitive pages.
        using (File.Create(snapshotPath))
        {
        }
        PrivateFilePermissions.EnsureFile(snapshotPath);

        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = statePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var sourceConnection = new SqliteConnection(sourceConnectionString);
        using var destinationConnection = new SqliteConnection(destinationConnectionString);
        sourceConnection.Open();
        destinationConnection.Open();
        sourceConnection.BackupDatabase(destinationConnection);
    }

    private async Task CompressSnapshotAsync(
        string snapshotPath,
        string compressedPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            snapshotPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Pre-create and restrict before any compressed page is written, matching CreateSnapshot.
        // EnsureDirectory has already put 0700 on the parent so the window isn't reachable today;
        // ordering it correctly here keeps a future copy-paste of this block safe on its own.
        using (File.Create(compressedPath))
        {
        }
        PrivateFilePermissions.EnsureFile(compressedPath);

        await using var destination = new FileStream(
            compressedPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using (var compressor = new BrotliStream(
            destination,
            new BrotliCompressionOptions { Quality = BrotliQuality },
            leaveOpen: true))
        {
            // Progress is measured on the read side: the snapshot's size is known, the compressed
            // size is not until it has been written.
            await new ProgressReadStream(source, _progress)
                .CopyToAsync(compressor, cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
    }

    private async Task<string> ComputeSha256Async(
        string compressedPath,
        CancellationToken cancellationToken)
    {
        await using var compressedStream = new FileStream(
            compressedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(
            new ProgressReadStream(compressedStream, _progress),
            cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Chooses how to get the compressed copy to the server. Anything over a single block goes
    /// through the resumable chunked protocol; a server that does not offer it falls back to the
    /// original single request, so the client works either side of a server deploy.
    /// </summary>
    private async Task<DataExportResult> SendCompressedExportAsync(
        Uri exportUri,
        string apiKey,
        string computerName,
        string sha256,
        string compressedPath,
        long compressedLength,
        CancellationToken cancellationToken)
    {
        if (compressedLength > ChunkedUploadThresholdBytes)
        {
            var chunked = await UploadInBlocksAsync(
                exportUri,
                apiKey,
                computerName,
                sha256,
                compressedPath,
                compressedLength,
                cancellationToken);
            if (chunked is not null)
                return chunked;

            Log.Information(
                "[DataExport] The server has no chunked upload endpoint; using a single request.");
        }

        return await UploadAsync(
            exportUri,
            apiKey,
            computerName,
            sha256,
            compressedPath,
            compressedLength,
            cancellationToken);
    }

    private async Task<DataExportResult> UploadAsync(
        Uri exportUri,
        string apiKey,
        string computerName,
        string sha256,
        string compressedPath,
        long compressedLength,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var uploadStream = new FileStream(
                compressedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var content = new StreamContent(
                new ProgressReadStream(uploadStream, _progress));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = $"\"{CompressedFileName}\""
            };

            // Must be set by hand. StreamContent only derives Content-Length from a seekable
            // stream, the progress wrapper is not seekable, and the server answers a request
            // without Content-Length with 411 rather than storing anything.
            content.Headers.ContentLength = compressedLength;

            using var request = new HttpRequestMessage(HttpMethod.Post, exportUri);
            AddExportHeaders(request, apiKey, computerName);
            request.Headers.Add("X-Content-SHA256", sha256);
            request.Content = content;

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new DataExportResult(
                    DataExportStatus.InvalidApiKey,
                    sha256,
                    await DescribeFailureAsync(
                        response,
                        "The export server rejected the API key.",
                        cancellationToken));
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DataExportResult(
                    DataExportStatus.UploadFailed,
                    sha256,
                    await DescribeFailureAsync(response, null, cancellationToken));
            }

            return new DataExportResult(DataExportStatus.Success, sha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            Log.Warning(
                "[DataExport] Upload timed out ({ExceptionType}).",
                exception.GetType().Name);
            return new DataExportResult(
                DataExportStatus.UploadFailed,
                sha256,
                "The data upload timed out.");
        }
        catch (Exception exception)
        {
            // Log only the exception type: a malformed user-supplied API key can be the cause of
            // a header-format exception, and the key must never be written to logs.
            Log.Warning(
                "[DataExport] Upload failed ({ExceptionType}).",
                exception.GetType().Name);
            return new DataExportResult(
                DataExportStatus.UploadFailed,
                sha256,
                "Failed to upload the data export.");
        }
    }

    // ── Resumable chunked upload ─────────────────────────────────────────────────────────────
    //
    // Reads exclusively from the compressed temp copy. A retry or a resumed upload never re-opens
    // the live state database — the snapshot happens once per export, and only once.

    /// <summary>
    /// Returns null when the server has no chunked endpoints, which tells the caller to fall back
    /// to a single request. Any other outcome is a real result.
    /// </summary>
    private async Task<DataExportResult?> UploadInBlocksAsync(
        Uri exportUri,
        string apiKey,
        string computerName,
        string sha256,
        string compressedPath,
        long compressedLength,
        CancellationToken cancellationToken)
    {
        // The server answers a commit whose blocks are incomplete with 409 and expects the client
        // to re-probe and re-send what is missing. One automatic pass closes that loop instead of
        // making the user press Retry to do exactly the same thing.
        for (var pass = 1; ; pass++)
        {
            var probe = await ProbeChunksAsync(
                exportUri,
                apiKey,
                computerName,
                sha256,
                compressedLength,
                cancellationToken);
            if (probe.NotSupported)
                return null;
            if (probe.Failure is not null)
                return probe.Failure;

            var blockSize = probe.BlockSizeBytes > 0
                ? probe.BlockSizeBytes
                : DefaultUploadBlockSizeBytes;
            if (blockSize is < MinUploadBlockSizeBytes or > MaxUploadBlockSizeBytes)
            {
                Log.Warning(
                    "[DataExport] The server asked for an unusable {BlockSize}-byte block size.",
                    blockSize);
                return new DataExportResult(
                    DataExportStatus.UploadFailed,
                    sha256,
                    "The export server asked for an unusable upload block size.");
            }

            // Long arithmetic throughout: at a small block size the count for a large export
            // overflows an int and would silently produce a negative or truncated block count.
            var totalBlocks = (compressedLength + blockSize - 1) / blockSize;
            if (totalBlocks > MaxUploadBlockCount)
            {
                return new DataExportResult(
                    DataExportStatus.UploadFailed,
                    sha256,
                    $"This export needs {totalBlocks} upload blocks, more than the "
                        + $"{MaxUploadBlockCount} the server can commit.");
            }

            var blockCount = (int)totalBlocks;

            if (probe.AlreadyStored)
            {
                _progress.SetProcessed(compressedLength);
            }
            else
            {
                var failure = await StageMissingBlocksAsync(
                    exportUri,
                    apiKey,
                    computerName,
                    sha256,
                    compressedPath,
                    compressedLength,
                    blockSize,
                    blockCount,
                    probe.UploadedIndices,
                    cancellationToken);
                if (failure is not null)
                    return failure;
            }

            var commit = await CommitChunksAsync(
                exportUri,
                apiKey,
                computerName,
                sha256,
                compressedLength,
                blockCount,
                cancellationToken);

            if (!commit.BlocksMissing || pass >= MaxCommitPasses)
                return commit.Result;

            Log.Warning(
                "[DataExport] The server reported incomplete blocks at commit; re-sending them.");
        }
    }

    private async Task<DataExportResult?> StageMissingBlocksAsync(
        Uri exportUri,
        string apiKey,
        string computerName,
        string sha256,
        string compressedPath,
        long compressedLength,
        int blockSize,
        int blockCount,
        IReadOnlyList<int> uploadedIndices,
        CancellationToken cancellationToken)
    {
        var alreadyStaged = uploadedIndices.ToHashSet();
        var sentBytes = 0L;
        foreach (var index in alreadyStaged)
            sentBytes += BlockLength(index, blockSize, compressedLength);
        _progress.SetProcessed(sentBytes);

        if (alreadyStaged.Count > 0)
        {
            Log.Information(
                "[DataExport] Resuming upload: {Staged} of {Total} blocks already stored.",
                alreadyStaged.Count,
                blockCount);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            await using var source = new FileStream(
                compressedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous);

            for (var index = 0; index < blockCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (alreadyStaged.Contains(index))
                    continue;

                var length = (int)BlockLength(index, blockSize, compressedLength);
                source.Position = (long)index * blockSize;
                await source.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);

                var failure = await StageBlockAsync(
                    exportUri,
                    apiKey,
                    computerName,
                    sha256,
                    index,
                    buffer,
                    length,
                    cancellationToken);
                if (failure is not null)
                    return failure;

                sentBytes += length;
                _progress.SetProcessed(sentBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return null;
    }

    /// <summary>
    /// <see cref="CommitOutcome.BlocksMissing"/> distinguishes the server's "re-send and commit
    /// again" answer from a failure that retrying cannot fix.
    /// </summary>
    private sealed record CommitOutcome(DataExportResult Result, bool BlocksMissing);

    private sealed record ChunkProbe(
        bool NotSupported,
        DataExportResult? Failure,
        int BlockSizeBytes,
        IReadOnlyList<int> UploadedIndices,
        bool AlreadyStored);

    private async Task<ChunkProbe> ProbeChunksAsync(
        Uri exportUri,
        string apiKey,
        string computerName,
        string sha256,
        long compressedLength,
        CancellationToken cancellationToken)
    {
        using var deadline = CreateMetadataDeadline(cancellationToken);
        var token = deadline.Token;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                ChunkUri(exportUri, sha256, $"?length={compressedLength}"));
            AddExportHeaders(request, apiKey, computerName);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new ChunkProbe(true, null, 0, Array.Empty<int>(), false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ChunkProbe(
                    false,
                    new DataExportResult(
                        DataExportStatus.InvalidApiKey,
                        sha256,
                        await DescribeFailureAsync(
                            response,
                            "The export server rejected the API key.",
                            token)),
                    0,
                    Array.Empty<int>(),
                    false);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new ChunkProbe(
                    false,
                    new DataExportResult(
                        DataExportStatus.UploadFailed,
                        sha256,
                        await DescribeFailureAsync(response, null, token)),
                    0,
                    Array.Empty<int>(),
                    false);
            }

            var body = await ReadBoundedBodyAsync(response.Content, MaxMetadataBodyBytes, token);
            if (body is null)
            {
                return new ChunkProbe(
                    false,
                    new DataExportResult(
                        DataExportStatus.UploadFailed,
                        sha256,
                        "The export server sent an unusable response to the upload probe."),
                    0,
                    Array.Empty<int>(),
                    false);
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var uploaded = new List<int>();
            if (root.TryGetProperty("uploadedIndices", out var indices)
                && indices.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in indices.EnumerateArray())
                {
                    if (element.TryGetInt32(out var index) && index >= 0)
                        uploaded.Add(index);
                }
            }

            return new ChunkProbe(
                false,
                null,
                root.TryGetProperty("blockSizeBytes", out var size) && size.TryGetInt32(out var blockSize)
                    ? blockSize
                    : 0,
                uploaded,
                root.TryGetProperty("alreadyStored", out var present)
                    && present.ValueKind == JsonValueKind.True);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            // A server that answers the probe with something unparseable is not one that can be
            // chunk-uploaded to. Fall back rather than fail the whole export.
            return new ChunkProbe(true, null, 0, Array.Empty<int>(), false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "[DataExport] Chunk probe failed ({ExceptionType}).",
                exception.GetType().Name);
            return new ChunkProbe(
                false,
                new DataExportResult(
                    DataExportStatus.UploadFailed,
                    sha256,
                    "Could not reach the export server."),
                0,
                Array.Empty<int>(),
                false);
        }
    }

    /// <summary>Returns null on success, or the failure that should end the export.</summary>
    private async Task<DataExportResult?> StageBlockAsync(
        Uri exportUri,
        string apiKey,
        string computerName,
        string sha256,
        int index,
        byte[] buffer,
        int length,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var content = new ByteArrayContent(buffer, 0, length);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                using var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    ChunkUri(exportUri, sha256, $"/{index}"))
                {
                    Content = content
                };
                AddExportHeaders(request, apiKey, computerName);

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                    return null;

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return new DataExportResult(
                        DataExportStatus.InvalidApiKey,
                        sha256,
                        await DescribeFailureAsync(
                            response,
                            "The export server rejected the API key.",
                            cancellationToken));
                }

                if (!IsTransient(response.StatusCode) || attempt >= MaxUploadAttemptsPerBlock)
                {
                    return new DataExportResult(
                        DataExportStatus.UploadFailed,
                        sha256,
                        await DescribeFailureAsync(response, null, cancellationToken));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < MaxUploadAttemptsPerBlock)
            {
                Log.Warning(
                    "[DataExport] Block {Index} attempt {Attempt} failed ({ExceptionType}); retrying.",
                    index,
                    attempt,
                    exception.GetType().Name);
            }
            catch (Exception exception)
            {
                Log.Warning(
                    "[DataExport] Block {Index} failed ({ExceptionType}).",
                    index,
                    exception.GetType().Name);
                return new DataExportResult(
                    DataExportStatus.UploadFailed,
                    sha256,
                    "Failed to upload part of the data export.");
            }

            await Task.Delay(RetryDelayMs(attempt), cancellationToken);
        }
    }

    private async Task<CommitOutcome> CommitChunksAsync(
        Uri exportUri,
        string apiKey,
        string computerName,
        string sha256,
        long compressedLength,
        int blockCount,
        CancellationToken cancellationToken)
    {
        using var deadline = CreateMetadataDeadline(cancellationToken);
        var token = deadline.Token;
        try
        {
            using var content = new StringContent(
                $"{{\"length\":{compressedLength},\"blockCount\":{blockCount}}}",
                Encoding.UTF8,
                "application/json");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                ChunkUri(exportUri, sha256, "/commit"))
            {
                Content = content
            };
            AddExportHeaders(request, apiKey, computerName);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new CommitOutcome(
                    new DataExportResult(
                        DataExportStatus.InvalidApiKey,
                        sha256,
                        await DescribeFailureAsync(
                            response,
                            "The export server rejected the API key.",
                            token)),
                    BlocksMissing: false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new CommitOutcome(
                    new DataExportResult(
                        DataExportStatus.UploadFailed,
                        sha256,
                        await DescribeFailureAsync(response, null, token)),
                    // 409 is the server asking for the missing blocks, not a dead end.
                    response.StatusCode == HttpStatusCode.Conflict);
            }

            return new CommitOutcome(
                new DataExportResult(DataExportStatus.Success, sha256),
                BlocksMissing: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warning(
                "[DataExport] Commit failed ({ExceptionType}).",
                exception.GetType().Name);
            return new CommitOutcome(
                new DataExportResult(
                    DataExportStatus.UploadFailed,
                    sha256,
                    "The upload finished but the export could not be completed."),
                BlocksMissing: false);
        }
    }

    private static long BlockLength(int index, int blockSize, long totalLength)
    {
        var remaining = totalLength - ((long)index * blockSize);
        return remaining >= blockSize ? blockSize : Math.Max(0, remaining);
    }

    private static Uri ChunkUri(Uri exportUri, string sha256, string suffix)
        => new($"{exportUri.AbsoluteUri.TrimEnd('/')}/chunks/{sha256}{suffix}");

    private static void AddExportHeaders(
        HttpRequestMessage request,
        string apiKey,
        string computerName)
    {
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Add("X-Computer-Name", Uri.EscapeDataString(computerName));
    }

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static int RetryDelayMs(int attempt) => 500 * (1 << (attempt - 1));

    /// <summary>
    /// Prefers the server's own explanation and keeps the HTTP status/reference alongside it.
    /// The production endpoint uses RFC Problem Details, while older endpoints returned
    /// { "error": "..." }; accepting both keeps a useful failure from collapsing to a generic
    /// "upload failed" message.
    /// </summary>
    private static async Task<string> DescribeFailureAsync(
        HttpResponseMessage response,
        string? fallback,
        CancellationToken cancellationToken)
    {
        var serverFailure = await TryReadErrorMessageAsync(response, cancellationToken);
        var httpStatus = FormatHttpStatus(response);
        var message = serverFailure?.Message;
        if (string.IsNullOrWhiteSpace(message) || IsGenericHttpTitle(message, response))
            message = fallback ?? DescribeStatus(response.StatusCode);

        var detail = $"{message.Trim().TrimEnd('.', '!', '?')} ({httpStatus}).";
        if (!string.IsNullOrWhiteSpace(serverFailure?.Reference))
            detail += $" Reference: {serverFailure.Reference}";

        // Status and request reference are safe to retain and make a remote failure diagnosable.
        // Do not log the response message: an upstream must not be able to reflect secrets into
        // the local log file.
        Log.Warning(
            "[DataExport] Export server returned {HttpStatus}. Reference={Reference}.",
            httpStatus,
            serverFailure?.Reference ?? "none");
        return detail;
    }

    private static string FormatHttpStatus(HttpResponseMessage response)
    {
        var reason = CleanServerText(response.ReasonPhrase, maxCharacters: 80);
        return string.IsNullOrWhiteSpace(reason)
            ? $"HTTP {(int)response.StatusCode}"
            : $"HTTP {(int)response.StatusCode} {reason}";
    }

    private static bool IsGenericHttpTitle(string message, HttpResponseMessage response)
    {
        var normalized = message.Trim().TrimEnd('.', '!', '?');
        var reason = response.ReasonPhrase?.Trim().TrimEnd('.', '!', '?');
        return string.Equals(normalized, reason, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                normalized,
                "An error occurred while processing your request",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeStatus(HttpStatusCode statusCode) => (int)statusCode switch
    {
        404 => "The configured export endpoint was not found",
        408 or 504 => "The export server timed out while receiving the upload",
        409 => "The export server could not assemble all uploaded blocks",
        413 => "The compressed export is larger than the server accepts",
        429 => "The export server is handling too many requests; wait a few minutes and retry",
        500 or 502 or 503 => "The export service is temporarily unavailable; retry in a few minutes",
        507 => "The export server does not have enough storage for this upload",
        _ => "The export server rejected the upload"
    };

    /// <summary>
    /// Bounds a metadata response body. Returns null when it is empty or larger than the caller
    /// will accept, so a server that streams without end cannot grow the heap while this export
    /// still holds the process gate and the cross-process lock.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedBodyAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);

        // One byte of headroom: reading the full buffer means the body reached the cap, and a
        // truncated document is worse to parse than none at all.
        var buffer = new byte[maxBytes + 1];
        var read = await stream.ReadAtLeastAsync(
            buffer,
            buffer.Length,
            throwOnEndOfStream: false,
            cancellationToken);

        return read == 0 || read > maxBytes ? null : buffer[..read];
    }

    private static CancellationTokenSource CreateMetadataDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MetadataResponseTimeout);
        return deadline;
    }

    private sealed record RemoteFailure(string? Message, string? Reference);

    private static async Task<RemoteFailure?> TryReadErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadBoundedBodyAsync(response.Content, MaxErrorBodyBytes, cancellationToken);
            if (body is null)
                return null;

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    return new RemoteFailure(
                        CleanServerText(root.GetString()),
                        ReadResponseReference(response));
                }
                if (root.ValueKind != JsonValueKind.Object)
                    return null;

                var message = ReadJsonMessage(root);
                var reference = ReadJsonString(root, "traceId")
                    ?? ReadJsonString(root, "requestId")
                    ?? ReadJsonString(root, "correlationId")
                    ?? ReadResponseReference(response);
                return string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(reference)
                    ? null
                    : new RemoteFailure(message, CleanServerText(reference, maxCharacters: 128));
            }
            catch (JsonException)
            {
                var rawText = Encoding.UTF8.GetString(body);
                var reference = ReadHtmlRequestReference(rawText)
                    ?? ReadResponseReference(response);
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) != true)
                {
                    return string.IsNullOrWhiteSpace(reference)
                        ? null
                        : new RemoteFailure(null, reference);
                }

                var text = CleanServerText(rawText);
                return string.IsNullOrWhiteSpace(text) || text.StartsWith('<')
                    ? string.IsNullOrWhiteSpace(reference)
                        ? null
                        : new RemoteFailure(null, reference)
                    : new RemoteFailure(text, reference);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A truncated body, an HTML error page from an edge proxy, anything at all: the
            // caller still has a status-code message to fall back on.
            return null;
        }
    }

    private static string? ReadJsonMessage(JsonElement root)
    {
        var detail = ReadJsonString(root, "detail");
        if (!string.IsNullOrWhiteSpace(detail))
            return detail;

        if (TryGetJsonProperty(root, "error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
                return CleanServerText(error.GetString());
            if (error.ValueKind == JsonValueKind.Object)
            {
                var nested = ReadJsonString(error, "message")
                    ?? ReadJsonString(error, "detail");
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        var message = ReadJsonString(root, "message");
        if (!string.IsNullOrWhiteSpace(message))
            return message;

        var validation = ReadValidationErrors(root);
        return !string.IsNullOrWhiteSpace(validation)
            ? validation
            : ReadJsonString(root, "title");
    }

    private static string? ReadValidationErrors(JsonElement root)
    {
        if (!TryGetJsonProperty(root, "errors", out var errors)
            || errors.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var messages = new List<string>(capacity: 3);
        foreach (var property in errors.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                AddValidationMessage(property.Name, property.Value.GetString(), messages);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        AddValidationMessage(property.Name, item.GetString(), messages);
                    if (messages.Count == 3)
                        break;
                }
            }

            if (messages.Count == 3)
                break;
        }

        return messages.Count == 0 ? null : string.Join(" ", messages);
    }

    private static void AddValidationMessage(
        string field,
        string? rawMessage,
        ICollection<string> messages)
    {
        var message = CleanServerText(rawMessage, maxCharacters: 180);
        if (string.IsNullOrWhiteSpace(message))
            return;

        var label = CleanServerText(field, maxCharacters: 60);
        messages.Add(string.IsNullOrWhiteSpace(label) ? message : $"{label}: {message}");
    }

    private static string? ReadJsonString(JsonElement root, string name)
        => TryGetJsonProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? CleanServerText(value.GetString())
            : null;

    private static bool TryGetJsonProperty(
        JsonElement root,
        string name,
        out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadHtmlRequestReference(string html)
    {
        var marker = html.IndexOf("Request ID:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return null;

        var codeStart = html.IndexOf("<code>", marker, StringComparison.OrdinalIgnoreCase);
        if (codeStart < 0)
            return null;
        codeStart += "<code>".Length;

        var codeEnd = html.IndexOf("</code>", codeStart, StringComparison.OrdinalIgnoreCase);
        if (codeEnd < 0)
            return null;

        return CleanServerText(
            WebUtility.HtmlDecode(html[codeStart..codeEnd]),
            maxCharacters: 128);
    }

    private static string? ReadResponseReference(HttpResponseMessage response)
    {
        foreach (var header in new[] { "x-request-id", "x-correlation-id" })
        {
            if (response.Headers.TryGetValues(header, out var values))
                return CleanServerText(values.FirstOrDefault(), maxCharacters: 128);
        }

        return null;
    }

    private static string? CleanServerText(
        string? value,
        int maxCharacters = MaxDisplayedErrorCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(Math.Min(value.Length, maxCharacters));
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace && builder.Length < maxCharacters)
                builder.Append(' ');
            pendingSpace = false;
            if (builder.Length >= maxCharacters)
                break;
            builder.Append(character);
        }

        if (builder.Length == 0)
            return null;
        if (builder.Length == maxCharacters && value.Length > maxCharacters)
            builder.Append('…');
        return builder.ToString();
    }

    private static string ResolveComputerName()
    {
        try
        {
            var configured = ComputerNameFormatter.Normalize(
                Config.LoadFresh().ComputerName);
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
        }
        catch
        {
            // A missing or temporarily unreadable settings file should not prevent
            // exporting a database when the live machine name is available.
        }

        return ComputerNameFormatter.Machine();
    }

    private void DeleteSnapshotFilesBestEffort(string snapshotPath)
    {
        foreach (var (path, fileName) in new[]
                 {
                     (snapshotPath, SnapshotFileName),
                     (snapshotPath + "-wal", SnapshotFileName + "-wal"),
                     (snapshotPath + "-shm", SnapshotFileName + "-shm")
                 })
        {
            try
            {
                // File.Delete already no-ops on a missing file; no Exists guard needed.
                File.Delete(path);
            }
            catch (Exception exception)
            {
                // The final cleanup retries these files. A cleanup warning must not replace
                // the primary upload result.
                Log.Warning(
                    exception,
                    "[DataExport] Failed to remove temporary export file {FileName} before upload.",
                    fileName);
            }
        }
    }

    private void CleanupTemporaryArtifacts(string? temporaryDirectory)
    {
        if (string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            return;
        }

        foreach (var fileName in new[]
                 {
                     SnapshotFileName,
                     SnapshotFileName + "-wal",
                     SnapshotFileName + "-shm",
                     CompressedFileName
                 })
        {
            try
            {
                File.Delete(Path.Combine(temporaryDirectory, fileName));
            }
            catch (Exception exception)
            {
                Log.Warning(
                    exception,
                    "[DataExport] Failed to remove temporary export file {FileName}.",
                    fileName);
            }
        }

        try
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "[DataExport] Failed to remove the temporary export directory.");
        }
    }
}
