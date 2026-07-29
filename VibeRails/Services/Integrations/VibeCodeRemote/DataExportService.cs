using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Creates a consistent snapshot of state.db, Brotli-compresses and hashes the snapshot,
/// and streams the compressed bytes to the configured remote endpoint.
/// </summary>
public sealed class DataExportService : IDataExportService
{
    internal const string ExportUrl = "https://viberails.ai/api/v1/data-exports";
    private static readonly Uri ExportUri = new(ExportUrl);
    internal const string SnapshotFileName = "copy_state.db";
    internal const string CompressedFileName = "copy_state.db.br";
    internal const string LockFileName = ".data-export.lock";

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

    public DataExportService(
        HttpClient httpClient,
        IConfiguration configuration)
        : this(
            httpClient,
            configuration,
            static () => Path.Combine(
                Path.GetTempPath(),
                $"viberails-data-export-{Guid.NewGuid():N}"),
            ResolveComputerName)
    {
    }

    internal DataExportService(
        HttpClient httpClient,
        IConfiguration configuration,
        Func<string> temporaryDirectoryFactory,
        Func<string> computerNameFactory)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _temporaryDirectoryFactory = temporaryDirectoryFactory;
        _computerNameFactory = computerNameFactory;
    }

    public async Task<DataExportResult> ExportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var apiKey = ParserConfigs.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new DataExportResult(
                DataExportStatus.NoApiKey,
                Detail: "No API key is configured.");
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

            // Microsoft.Data.Sqlite exposes BackupDatabase synchronously, so cancellation is
            // observed immediately before and after this one transactionally consistent call.
            CreateSnapshot(statePath, snapshotPath);
            cancellationToken.ThrowIfCancellationRequested();
            await CompressSnapshotAsync(snapshotPath, compressedPath, cancellationToken);
            var sha256 = await ComputeSha256Async(compressedPath, cancellationToken);
            var computerName = ComputerNameFormatter.Normalize(_computerNameFactory());
            if (string.IsNullOrWhiteSpace(computerName))
                computerName = "unknown-computer";

            DeleteSnapshotFilesBestEffort(snapshotPath);

            return await UploadAsync(
                ExportUri,
                apiKey,
                computerName,
                sha256,
                compressedPath,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    private static async Task CompressSnapshotAsync(
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
            await source.CopyToAsync(compressor, cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(
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
        var hash = await SHA256.HashDataAsync(compressedStream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private async Task<DataExportResult> UploadAsync(
        Uri exportUri,
        string apiKey,
        string computerName,
        string sha256,
        string compressedPath,
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
            using var content = new StreamContent(uploadStream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = $"\"{CompressedFileName}\""
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, exportUri);
            request.Headers.Add("X-Api-Key", apiKey);
            request.Headers.Add("X-Computer-Name", Uri.EscapeDataString(computerName));
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
                    "The export server rejected the API key.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DataExportResult(
                    DataExportStatus.UploadFailed,
                    sha256,
                    $"The export server returned HTTP {(int)response.StatusCode}.");
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
