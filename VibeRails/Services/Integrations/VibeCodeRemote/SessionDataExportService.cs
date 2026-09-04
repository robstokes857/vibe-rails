using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Writes one completed session as deterministic JSON directly through Brotli to a private temp
/// file, then uploads that file using the session envelope protocol. SQLite is acknowledged only
/// after an identity-matching 200/201 response from the final POST/commit request.
/// </summary>
public sealed class SessionDataExportService : ISessionDataExportService
{
    internal const int EnvelopeSchemaVersion = 1;
    internal const int ChunkedUploadThresholdBytes = 4 * 1024 * 1024;
    internal const string LockFileName = ".session-data-drain.lock";
    internal const string SpoolDirectoryName = ".session-data-spool";
    internal const string CompressedFileSuffix = ".json.br";
    internal const string InProgressSuffix = ".tmp";

    private const int DefaultBlockSize = 4 * 1024 * 1024;
    private const int MinBlockSize = 1024 * 1024;
    private const int MaxBlockSize = 64 * 1024 * 1024;
    private const int MaxBlockCount = 50_000;
    private const int MaxBlockAttempts = 3;
    private const int MaxCommitPasses = 2;
    private const int MaxMetadataBytes = 64 * 1024;
    private const int BrotliQuality = 5;

    private static readonly SemaphoreSlim ExportGate = new(1, 1);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);

    // Data-carrying sends need their own budget: without one their only bound is the 30-minute
    // HttpClient.Timeout, and both run while ExportGate and the cross-process drain lock are held,
    // so a black-holed socket stalls every root backend on the machine for that whole window.
    // Both paths carry a bounded payload - one block, or an envelope at or below the chunking
    // threshold - so a fixed deadline cannot be tripped by a merely large upload.
    private static readonly TimeSpan PayloadTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionDataExportService> _logger;
    private readonly Func<string> _computerNameFactory;

    public SessionDataExportService(
        HttpClient httpClient,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<SessionDataExportService> logger)
        : this(
            httpClient,
            configuration,
            scopeFactory,
            logger,
            ResolveComputerName)
    {
    }

    internal SessionDataExportService(
        HttpClient httpClient,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<SessionDataExportService> logger,
        Func<string> computerNameFactory)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _computerNameFactory = computerNameFactory;
    }

    public bool IsConfigured => TryResolveConfiguration(out _, out _);

    public async Task<SessionDataExportResult> ExportSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(sessionId, out var sourceId) || sourceId == Guid.Empty)
        {
            return Failure(
                SessionDataExportStatus.NotFound,
                sessionId,
                detail: "The session id is not a non-empty GUID.");
        }

        var apiKey = ParserConfigs.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Failure(SessionDataExportStatus.NoApiKey, sessionId, detail: "No API key is configured.");
        if (!DataExportEndpointConfiguration.TryParseExportUri(
                _configuration[DataExportEndpointConfiguration.ExportUrlSettingKey],
                out var baseUri))
        {
            return Failure(
                SessionDataExportStatus.NotConfigured,
                sessionId,
                detail: "No absolute HTTPS export URL is configured.");
        }

        if (!ExportGate.Wait(0))
            return Failure(SessionDataExportStatus.Busy, sessionId, detail: "Another data export is running.");

        CrossProcessFileLock? fileLock = null;
        string? inProgressPath = null;
        try
        {
            var statePath = ParserConfigs.GetStatePath();
            fileLock = CrossProcessFileLock.TryAcquire(
                CrossProcessFileLock.BesideStateDatabase(statePath, LockFileName));
            if (fileLock is null)
                return Failure(SessionDataExportStatus.Busy, sessionId, detail: "Another data export is running.");

            var spoolDirectory = TryResolveSpoolDirectory(statePath)
                ?? throw new InvalidOperationException("The state database directory could not be resolved.");
            PrivateFilePermissions.EnsureDirectory(spoolDirectory);
            var compressedPath = SpoolPathFor(spoolDirectory, sourceId);

            if (!File.Exists(compressedPath))
            {
                // Fixed name means a crash cannot leave unbounded random temp fragments. The
                // cross-process lock makes one writer authoritative for this path.
                inProgressPath = compressedPath + InProgressSuffix;
                DeleteSpoolBestEffort(inProgressPath);
                var descriptor = await PrepareSpoolAsync(
                    sessionId,
                    inProgressPath,
                    cancellationToken) ?? throw new SessionNotFoundException();
                if (descriptor.SourceId != sourceId
                    || descriptor.SchemaVersion != EnvelopeSchemaVersion
                    || !string.Equals(descriptor.Kind, "session", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The local session envelope identity is invalid.");
                }

                File.Move(inProgressPath, compressedPath, overwrite: false);
                inProgressPath = null;
                PrivateFilePermissions.EnsureFile(compressedPath);
            }

            var compressedLength = new FileInfo(compressedPath).Length;
            if (compressedLength <= 0)
                throw new InvalidDataException("The prepared session envelope is empty.");
            var sha256 = await ComputeSha256Async(compressedPath, cancellationToken);

            var computerName = ComputerNameFormatter.Normalize(_computerNameFactory());
            if (string.IsNullOrWhiteSpace(computerName))
                computerName = "unknown-computer";

            var sessionUri = new Uri(
                $"{baseUri.AbsoluteUri.TrimEnd('/')}/sessions/{sourceId:D}",
                UriKind.Absolute);
            var upload = await UploadAsync(
                sessionUri,
                sourceId,
                apiKey,
                computerName,
                sha256,
                compressedPath,
                compressedLength,
                cancellationToken);
            if (upload.Status != SessionDataExportStatus.Success)
                return upload;

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
                if (!await repository.MarkSessionExportedAsync(
                        sessionId,
                        DateTime.UtcNow,
                        cancellationToken))
                {
                    // The row may have been deleted or concurrently acknowledged and therefore
                    // may never be selected again. Do not strand sensitive spool data forever.
                    DeleteSpoolBestEffort(compressedPath);
                    return Failure(
                        SessionDataExportStatus.Failed,
                        sessionId,
                        sha256,
                        "The remote upload succeeded, but the local acknowledgement could not be saved.");
                }
            }

            DeleteSpoolBestEffort(compressedPath);
            return upload;
        }
        catch (SessionNotFoundException)
        {
            return Failure(
                SessionDataExportStatus.NotFound,
                sessionId,
                detail: "The ended, unexported session was not found.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Session data export failed while preparing session {SessionId}.",
                sessionId);
            return Failure(
                SessionDataExportStatus.Failed,
                sessionId,
                detail: "Failed to prepare the session data export.");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(inProgressPath))
                DeleteSpoolBestEffort(inProgressPath);
            fileLock?.Dispose();
            ExportGate.Release();
        }
    }

    private bool TryResolveConfiguration(out string apiKey, out Uri exportUri)
    {
        exportUri = null!;
        apiKey = ParserConfigs.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        return DataExportEndpointConfiguration.TryParseExportUri(
            _configuration[DataExportEndpointConfiguration.ExportUrlSettingKey],
            out exportUri);
    }

    private async Task<SessionDataExportDescriptor?> PrepareSpoolAsync(
        string sessionId,
        string path,
        CancellationToken cancellationToken)
    {
        using (File.Create(path))
        {
        }
        PrivateFilePermissions.EnsureFile(path);

        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        SessionDataExportDescriptor? descriptor;
        await using (var brotli = new BrotliStream(
            file,
            new BrotliCompressionOptions { Quality = BrotliQuality },
            leaveOpen: true))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            descriptor = await scope.ServiceProvider.GetRequiredService<IRepository>()
                .WriteSessionExportAsync(sessionId, brotli, cancellationToken);
        }
        await file.FlushAsync(cancellationToken);
        file.Flush(flushToDisk: true);
        return descriptor;
    }

    private async Task<SessionDataExportResult> UploadAsync(
        Uri sessionUri,
        Guid sourceId,
        string apiKey,
        string computerName,
        string sha256,
        string path,
        long length,
        CancellationToken cancellationToken)
    {
        // No fallback between the two: under the current route layout both the probe and the
        // single POST live on one controller behind one route base, so a probe 404 means the
        // base is absent and re-POSTing the whole envelope to it would 404 identically.
        return length > ChunkedUploadThresholdBytes
            ? await UploadInBlocksAsync(
                sessionUri, sourceId, apiKey, computerName, sha256, path, length, cancellationToken)
            : await UploadSingleAsync(
                sessionUri, sourceId, apiKey, computerName, sha256, path, length, cancellationToken);
    }

    private async Task<SessionDataExportResult> UploadSingleAsync(
        Uri uri,
        Guid sourceId,
        string apiKey,
        string computerName,
        string sha256,
        string path,
        long length,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = OpenRead(path);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = length;
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
            AddHeaders(request, apiKey, computerName);
            request.Headers.Add("X-Content-SHA256", sha256);
            using var deadline = CreateDeadline(cancellationToken, PayloadTimeout);
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return await FinalResponseAsync(response, sourceId, sha256, length, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Session envelope upload failed ({ExceptionType}).",
                exception.GetType().Name);
            return Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, "Failed to upload the session envelope.");
        }
    }

    private async Task<SessionDataExportResult> UploadInBlocksAsync(
        Uri sessionUri,
        Guid sourceId,
        string apiKey,
        string computerName,
        string sha256,
        string path,
        long length,
        CancellationToken cancellationToken)
    {
        for (var pass = 1; pass <= MaxCommitPasses; pass++)
        {
            var probe = await ProbeAsync(sessionUri, sourceId, apiKey, computerName, sha256, length, cancellationToken);
            if (probe.Failure is not null) return probe.Failure;

            var blockSize = probe.BlockSizeBytes == 0 ? DefaultBlockSize : probe.BlockSizeBytes;
            if (blockSize is < MinBlockSize or > MaxBlockSize)
                return Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, "The server returned an invalid block size.");
            var countLong = (length + blockSize - 1) / blockSize;
            if (countLong <= 0 || countLong > MaxBlockCount)
                return Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, "The session envelope needs too many blocks.");
            var count = (int)countLong;

            if (!probe.AlreadyStored)
            {
                var staged = probe.UploadedIndices.ToHashSet();
                var buffer = ArrayPool<byte>.Shared.Rent(blockSize);
                try
                {
                    await using var stream = OpenRead(path);
                    for (var index = 0; index < count; index++)
                    {
                        if (staged.Contains(index)) continue;
                        var blockLength = (int)BlockLength(index, blockSize, length);
                        stream.Position = (long)index * blockSize;
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, blockLength), cancellationToken);
                        var failure = await StageBlockAsync(
                            sessionUri, sourceId, apiKey, computerName, sha256,
                            index, buffer, blockLength, cancellationToken);
                        if (failure is not null) return failure;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            var commit = await CommitAsync(
                sessionUri, sourceId, apiKey, computerName, sha256, length, count, cancellationToken);
            if (!commit.BlocksMissing || pass == MaxCommitPasses)
                return commit.Result;
        }

        throw new UnreachableException();
    }

    private sealed record ProbeResult(
        SessionDataExportResult? Failure,
        int BlockSizeBytes,
        IReadOnlyList<int> UploadedIndices,
        bool AlreadyStored);

    private async Task<ProbeResult> ProbeAsync(
        Uri sessionUri,
        Guid sourceId,
        string apiKey,
        string computerName,
        string sha256,
        long length,
        CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                ChunkUri(sessionUri, sha256, $"?length={length}"));
            AddHeaders(request, apiKey, computerName);
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(Failure(SessionDataExportStatus.InvalidApiKey, sourceId.ToString("D"), sha256, "The server rejected the API key."), 0, Array.Empty<int>(), false);
            if (response.StatusCode != HttpStatusCode.OK)
                return new(Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, $"The upload probe returned HTTP {(int)response.StatusCode}."), 0, Array.Empty<int>(), false);

            var body = await ReadBoundedAsync(response.Content, deadline.Token);
            if (body is null)
                return new(Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, "The upload probe returned an unusable response."), 0, Array.Empty<int>(), false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var uploaded = new List<int>();
            if (root.TryGetProperty("uploadedIndices", out var indices) && indices.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in indices.EnumerateArray())
                    if (item.TryGetInt32(out var index) && index >= 0) uploaded.Add(index);
            }
            var blockSize = root.TryGetProperty("blockSizeBytes", out var size) && size.TryGetInt32(out var parsedSize) ? parsedSize : 0;
            var alreadyStored = root.TryGetProperty("alreadyStored", out var stored) && stored.ValueKind == JsonValueKind.True;
            return new(null, blockSize, uploaded, alreadyStored);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Session envelope chunk probe failed ({ExceptionType}).",
                exception.GetType().Name);
            return new(Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, "Could not reach the upload endpoint."), 0, Array.Empty<int>(), false);
        }
    }

    private async Task<SessionDataExportResult?> StageBlockAsync(
        Uri sessionUri,
        Guid sourceId,
        string apiKey,
        string computerName,
        string sha256,
        int index,
        byte[] buffer,
        int length,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxBlockAttempts; attempt++)
        {
            try
            {
                using var content = new ByteArrayContent(buffer, 0, length);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    ChunkUri(sessionUri, sha256, $"/{index}")) { Content = content };
                AddHeaders(request, apiKey, computerName);
                // Per attempt, so a block that times out is retried; only the caller's own
                // cancellation escapes the retry loop.
                using var deadline = CreateDeadline(cancellationToken, PayloadTimeout);
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode == HttpStatusCode.OK) return null;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return Failure(SessionDataExportStatus.InvalidApiKey, sourceId.ToString("D"), sha256, "The server rejected the API key.");
                if (!IsTransient(response.StatusCode) || attempt == MaxBlockAttempts)
                    return Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, $"Block {index} returned HTTP {(int)response.StatusCode}.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < MaxBlockAttempts)
            {
                _logger.LogWarning(
                    "Session envelope block {BlockIndex} attempt {Attempt} failed ({ExceptionType}).",
                    index,
                    attempt,
                    exception.GetType().Name);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Session envelope block {BlockIndex} failed ({ExceptionType}).",
                    index,
                    exception.GetType().Name);
                return Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, $"Block {index} could not be uploaded.");
            }
            await Task.Delay(500 * (1 << (attempt - 1)), cancellationToken);
        }
        throw new UnreachableException();
    }

    private sealed record CommitResult(SessionDataExportResult Result, bool BlocksMissing);

    private async Task<CommitResult> CommitAsync(
        Uri sessionUri,
        Guid sourceId,
        string apiKey,
        string computerName,
        string sha256,
        long length,
        int blockCount,
        CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken);
        try
        {
            using var content = new StringContent(
                $"{{\"length\":{length},\"blockCount\":{blockCount}}}",
                Encoding.UTF8,
                "application/json");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                ChunkUri(sessionUri, sha256, "/commit")) { Content = content };
            AddHeaders(request, apiKey, computerName);
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return new(
                    Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, "The server could not commit the uploaded blocks."),
                    BlocksMissing: true);
            }
            return new(
                await FinalResponseAsync(response, sourceId, sha256, length, deadline.Token),
                BlocksMissing: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Session envelope commit failed ({ExceptionType}).",
                exception.GetType().Name);
            return new(
                Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, "The uploaded blocks could not be committed."),
                BlocksMissing: false);
        }
    }

    private static async Task<SessionDataExportResult> FinalResponseAsync(
        HttpResponseMessage response,
        Guid sourceId,
        string sha256,
        long compressedBytes,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return Failure(SessionDataExportStatus.InvalidApiKey, sourceId.ToString("D"), sha256, "The server rejected the API key.");
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
            return Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, $"The upload returned HTTP {(int)response.StatusCode}.");

        var body = await ReadBoundedAsync(response.Content, cancellationToken);
        if (body is null)
            return Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, "The upload acknowledgement was empty or too large.");
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var kindMatches = root.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && string.Equals(kind.GetString(), "session", StringComparison.Ordinal);
            var idMatches = root.TryGetProperty("sourceId", out var id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out var returnedId)
                && returnedId == sourceId;
            var versionMatches = root.TryGetProperty("schemaVersion", out var version)
                && version.TryGetInt32(out var returnedVersion)
                && returnedVersion == EnvelopeSchemaVersion;
            var hashMatches = root.TryGetProperty("sha256", out var hash)
                && hash.ValueKind == JsonValueKind.String
                && string.Equals(hash.GetString(), sha256, StringComparison.OrdinalIgnoreCase);
            var lengthMatches = root.TryGetProperty("compressedBytes", out var length)
                && length.TryGetInt64(out var returnedLength)
                && returnedLength == compressedBytes;
            var statusMatches = root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() is "stored" or "already_exists";
            if (!kindMatches || !idMatches || !versionMatches || !hashMatches
                || !lengthMatches || !statusMatches)
                return Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, "The upload acknowledgement did not match this session.");
        }
        catch (JsonException)
        {
            return Failure(SessionDataExportStatus.UploadFailed, sourceId.ToString("D"), sha256, "The upload acknowledgement was not valid JSON.");
        }

        return new SessionDataExportResult(SessionDataExportStatus.Success, sourceId.ToString("D"), sha256);
    }

    private static void AddHeaders(HttpRequestMessage request, string apiKey, string computerName)
    {
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Add("X-Computer-Name", Uri.EscapeDataString(computerName));
        request.Headers.Add("X-Envelope-Schema-Version", EnvelopeSchemaVersion.ToString(CultureInfo.InvariantCulture));
    }

    private static Uri ChunkUri(Uri sessionUri, string sha256, string suffix)
        => new($"{sessionUri.AbsoluteUri.TrimEnd('/')}/chunks/{sha256}{suffix}", UriKind.Absolute);

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxMetadataBytes + 1];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken);
        return read is > 0 and <= MaxMetadataBytes ? buffer[..read] : null;
    }

    private static CancellationTokenSource CreateDeadline(
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? MetadataTimeout);
        return deadline;
    }

    private static long BlockLength(int index, int blockSize, long totalLength)
        => Math.Min(blockSize, Math.Max(0, totalLength - ((long)index * blockSize)));

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static SessionDataExportResult Failure(
        SessionDataExportStatus status,
        string sessionId,
        string? sha256 = null,
        string? detail = null)
        => new(status, sessionId, sha256, detail);

    private static string ResolveComputerName()
    {
        try
        {
            var configured = ComputerNameFormatter.Normalize(Config.LoadFresh().ComputerName);
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
        }
        catch
        {
        }
        return ComputerNameFormatter.Machine();
    }

    private static string? TryResolveSpoolDirectory(string? statePath = null)
    {
        var stateDirectory = Path.GetDirectoryName(statePath ?? ParserConfigs.GetStatePath());
        return string.IsNullOrWhiteSpace(stateDirectory)
            ? null
            : Path.Combine(stateDirectory, SpoolDirectoryName, $"v{EnvelopeSchemaVersion}");
    }

    private static string SpoolPathFor(string spoolDirectory, Guid sourceId)
        => Path.Combine(spoolDirectory, $"{sourceId:D}{CompressedFileSuffix}");

    public void DeleteSpool(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var sourceId) || sourceId == Guid.Empty)
            return;

        var spoolDirectory = TryResolveSpoolDirectory();
        if (spoolDirectory is null || !Directory.Exists(spoolDirectory))
            return;

        var path = SpoolPathFor(spoolDirectory, sourceId);
        DeleteSpoolBestEffort(path);
        DeleteSpoolBestEffort(path + InProgressSuffix);
    }

    public async Task<int> SweepOrphanedSpoolAsync(CancellationToken cancellationToken)
    {
        var statePath = ParserConfigs.GetStatePath();
        var spoolDirectory = TryResolveSpoolDirectory(statePath);
        if (spoolDirectory is null || !Directory.Exists(spoolDirectory))
            return 0;

        // Hold the same two gates an export holds. A spool being uploaded right now is opened
        // with FileShare.Read and no FILE_SHARE_DELETE, so deleting it under a live upload
        // throws on Windows; and the cross-process lock keeps a second root backend from
        // sweeping a file this machine's other backend is still sending.
        if (!ExportGate.Wait(0))
            return 0;

        CrossProcessFileLock? fileLock = null;
        try
        {
            fileLock = CrossProcessFileLock.TryAcquire(
                CrossProcessFileLock.BesideStateDatabase(statePath, LockFileName));
            if (fileLock is null)
                return 0;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
            var removed = 0;

            foreach (var path in Directory.EnumerateFiles(spoolDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadSpoolSessionId(path, out var sourceId))
                    continue;
                if (await repository.SessionAwaitsExportAsync(sourceId.ToString("D"), cancellationToken))
                    continue;

                DeleteSpoolBestEffort(path);
                removed++;
            }

            if (removed > 0)
                _logger.LogInformation("Removed {Count} orphaned session export spool file(s).", removed);
            return removed;
        }
        finally
        {
            fileLock?.Dispose();
            ExportGate.Release();
        }
    }

    /// <summary>
    /// Only files this service names are candidates. Anything else in the directory is left
    /// alone rather than assumed to be litter.
    /// </summary>
    private static bool TryReadSpoolSessionId(string path, out Guid sourceId)
    {
        sourceId = Guid.Empty;
        var name = Path.GetFileName(path);
        if (name.EndsWith(InProgressSuffix, StringComparison.Ordinal))
            name = name[..^InProgressSuffix.Length];
        if (!name.EndsWith(CompressedFileSuffix, StringComparison.Ordinal))
            return false;

        return Guid.TryParseExact(name[..^CompressedFileSuffix.Length], "D", out sourceId)
            && sourceId != Guid.Empty;
    }

    private void DeleteSpoolBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to remove prepared session export data.");
        }
    }

    private sealed class SessionNotFoundException : Exception;
}
