using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services;

[Collection("ProcessEnvIsolation")]
public sealed class SessionDataExportServiceTests : IDisposable
{
    private const string ExportUrl = "https://exports.example.test/v1/data";
    private const string ApiKey = "session-export-api-key";
    private const string ComputerName = "test computer/α";
    private readonly UploadFeatureLogRecorder _featureLog = new();

    private readonly string _originalApiKey = ParserConfigs.GetApiKey();
    private readonly string _originalStatePath = ParserConfigs.GetStatePath();
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"viberails-session-export-service-tests-{Guid.NewGuid():N}");

    public SessionDataExportServiceTests()
    {
        Directory.CreateDirectory(_testRoot);
        ParserConfigs.SetApiKey(ApiKey);
        ParserConfigs.SetStatePath(Path.Combine(_testRoot, "state.db"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "already_exists")]
    [InlineData(HttpStatusCode.Created, "stored")]
    public async Task ExportSessionAsync_ExactAcknowledgement_MarksAndDeletesSpool(
        HttpStatusCode responseStatus,
        string acknowledgementStatus)
    {
        var sourceId = Guid.NewGuid();
        var sessionId = sourceId.ToString("D");
        var envelope = Encoding.UTF8.GetBytes(
            "{\"kind\":\"session\",\"value\":\"small\"}");
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupEnvelopeWrite(
            repository,
            sessionId,
            sourceId,
            () => envelope);
        repository
            .Setup(repo => repo.MarkSessionExportedAsync(
                sessionId,
                It.Is<DateTime>(value => value.Kind == DateTimeKind.Utc),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        using var services = BuildServices(repository.Object);
        var handler = new SingleUploadHandler(
            responseStatus,
            sourceId,
            acknowledgementStatus);
        using var client = new HttpClient(handler);
        var service = CreateService(client, services);

        var result = await service.ExportSessionAsync(
            sessionId,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionDataExportStatus.Success, result.Status);
        _featureLog.AssertAttempt(sessionId, "succeeded");
        var request = Assert.IsType<RequestSnapshot>(handler.Request);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/v1/data/sessions/{sessionId}", request.Uri.AbsolutePath);
        Assert.Equal(ApiKey, request.ApiKey);
        Assert.Equal(Uri.EscapeDataString(ComputerName), request.ComputerName);
        Assert.Equal("1", request.SchemaVersion);
        Assert.Equal("application/octet-stream", request.ContentType);
        Assert.Equal(request.Body.LongLength, request.ContentLength);
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(request.Body));
        Assert.Equal(expectedSha, request.ContentSha256);
        Assert.Equal(expectedSha, result.Sha256);
        Assert.Equal(envelope, await DecompressAsync(request.Body));
        Assert.False(File.Exists(SpoolPath(sourceId)));
        repository.Verify(repo => repo.WriteSessionExportAsync(
            sessionId, It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(repo => repo.MarkSessionExportedAsync(
            sessionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExportSessionAsync_AckButLocalMarkFails_DeletesSpool()
    {
        var sourceId = Guid.NewGuid();
        var sessionId = sourceId.ToString("D");
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupEnvelopeWrite(
            repository,
            sessionId,
            sourceId,
            () => Encoding.UTF8.GetBytes("{\"kind\":\"session\",\"value\":\"row-gone\"}"));
        repository
            .Setup(repo => repo.MarkSessionExportedAsync(
                sessionId,
                It.Is<DateTime>(value => value.Kind == DateTimeKind.Utc),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        using var services = BuildServices(repository.Object);
        var handler = new SingleUploadHandler(
            HttpStatusCode.Created,
            sourceId,
            "stored");
        using var client = new HttpClient(handler);
        var service = CreateService(client, services);

        var result = await service.ExportSessionAsync(
            sessionId,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionDataExportStatus.Failed, result.Status);
        var log = _featureLog.AssertAttempt(sessionId, "uploaded");
        Assert.Equal("local-acknowledgement-failed", log.Event);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Contains("local acknowledgement", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(SpoolPath(sourceId)));
        repository.Verify(repo => repo.WriteSessionExportAsync(
            sessionId, It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(repo => repo.MarkSessionExportedAsync(
            sessionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExportSessionAsync_MismatchedAcknowledgement_NeverMarksAndRetainsSpool()
    {
        var sourceId = Guid.NewGuid();
        var sessionId = sourceId.ToString("D");
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupEnvelopeWrite(
            repository,
            sessionId,
            sourceId,
            () => Encoding.UTF8.GetBytes("{\"kind\":\"session\",\"value\":\"mismatch\"}"));
        using var services = BuildServices(repository.Object);
        var handler = new SingleUploadHandler(
            HttpStatusCode.OK,
            sourceId,
            "already_exists",
            mismatchAcknowledgement: true);
        using var client = new HttpClient(handler);
        var service = CreateService(client, services);

        var result = await service.ExportSessionAsync(
            sessionId,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionDataExportStatus.UploadFailed, result.Status);
        Assert.Contains("did not match", result.Detail, StringComparison.OrdinalIgnoreCase);
        _featureLog.AssertAttempt(sessionId, "failed");
        Assert.True(File.Exists(SpoolPath(sourceId)));
        repository.Verify(repo => repo.WriteSessionExportAsync(
            sessionId, It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(repo => repo.MarkSessionExportedAsync(
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportSessionAsync_LocalAcknowledgementThrows_HistoryStillShowsUploaded(bool cancelled)
    {
        var sourceId = Guid.NewGuid();
        var sessionId = sourceId.ToString("D").ToUpperInvariant();
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupEnvelopeWrite(repository, sessionId, sourceId,
            () => Encoding.UTF8.GetBytes("private transcript content"));
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        repository
            .Setup(repo => repo.MarkSessionExportedAsync(
                sessionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((string _, DateTime _, CancellationToken token) =>
            {
                if (cancelled)
                {
                    cancellationSource.Cancel();
                    return Task.FromCanceled<bool>(token);
                }
                return Task.FromException<bool>(new InvalidOperationException(
                    $"Sensitive local failure: {ApiKey}; private transcript content"));
            });
        using var services = BuildServices(repository.Object);
        using var client = new HttpClient(new SingleUploadHandler(HttpStatusCode.Created, sourceId, "stored"));
        var service = CreateService(client, services);

        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.ExportSessionAsync(sessionId, cancellationSource.Token));
        }
        else
        {
            var result = await service.ExportSessionAsync(sessionId, cancellationSource.Token);
            Assert.Equal(SessionDataExportStatus.Failed, result.Status);
        }

        var log = _featureLog.AssertAttempt(sourceId.ToString("D"), "uploaded");
        Assert.Equal("local-acknowledgement-failed", log.Event);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.All(_featureLog.Entries, entry =>
        {
            Assert.DoesNotContain(ApiKey, entry.Message);
            Assert.DoesNotContain("private transcript", entry.Message);
            Assert.DoesNotContain("Sensitive local failure", entry.Message);
        });
    }

    [Theory]
    [InlineData("missing-key", SessionDataExportStatus.NoApiKey)]
    [InlineData("not-configured", SessionDataExportStatus.NotConfigured)]
    [InlineData("busy", SessionDataExportStatus.Busy)]
    [InlineData("not-found", SessionDataExportStatus.NotFound)]
    [InlineData("invalid-id", SessionDataExportStatus.NotFound)]
    public async Task ExportSessionAsync_EarlyExit_RecordsSkippedWithoutNetworkWork(
        string reason,
        SessionDataExportStatus expectedStatus)
    {
        var sessionId = reason == "invalid-id" ? ApiKey + " invalid id" : Guid.NewGuid().ToString("D");
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        if (reason == "not-found")
        {
            repository.Setup(repo => repo.WriteSessionExportAsync(
                    sessionId, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SessionDataExportDescriptor?)null);
        }
        if (reason == "missing-key")
            ParserConfigs.SetApiKey(string.Empty);
        using var heldLock = reason == "busy"
            ? CrossProcessFileLock.TryAcquire(CrossProcessFileLock.BesideStateDatabase(
                ParserConfigs.GetStatePath(), SessionDataExportService.LockFileName))
            : null;
        if (reason == "busy")
            Assert.NotNull(heldLock);
        using var services = BuildServices(repository.Object);
        using var client = new HttpClient(new UnreachableHandler());
        var service = CreateService(client, services, reason == "not-configured" ? null : ExportUrl);

        var result = await service.ExportSessionAsync(sessionId, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, result.Status);
        _featureLog.AssertAttempt(reason == "invalid-id" ? "Unknown session" : sessionId, "skipped");
        Assert.All(_featureLog.Entries, entry => Assert.DoesNotContain(ApiKey, entry.ToString()));
    }

    [Fact]
    public async Task ExportSessionAsync_AlreadyCancelled_RecordsCancellationAndPropagates()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        using var services = BuildServices(new Mock<IRepository>(MockBehavior.Strict).Object);
        using var client = new HttpClient(new UnreachableHandler());
        var service = CreateService(client, services);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ExportSessionAsync(sessionId, cancellationSource.Token));

        _featureLog.AssertAttempt(sessionId, "cancelled");
    }

    [Fact]
    public async Task ExportSessionAsync_Http409_NeverMarksAndRetainsSpool()
    {
        var sourceId = Guid.NewGuid();
        var sessionId = sourceId.ToString("D");
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupEnvelopeWrite(
            repository,
            sessionId,
            sourceId,
            () => Encoding.UTF8.GetBytes("{\"kind\":\"session\",\"value\":\"conflict\"}"));
        using var services = BuildServices(repository.Object);
        var handler = new SingleUploadHandler(
            HttpStatusCode.Conflict,
            sourceId,
            acknowledgementStatus: null);
        using var client = new HttpClient(handler);
        var service = CreateService(client, services);

        var result = await service.ExportSessionAsync(
            sessionId,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionDataExportStatus.UploadFailed, result.Status);
        Assert.Contains("409", result.Detail, StringComparison.Ordinal);
        _featureLog.AssertAttempt(sessionId, "failed");
        Assert.True(File.Exists(SpoolPath(sourceId)));
        repository.Verify(repo => repo.WriteSessionExportAsync(
            sessionId, It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(repo => repo.MarkSessionExportedAsync(
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExportSessionAsync_RetryAfterLostAcknowledgement_ReusesFrozenSpool()
    {
        var sourceId = Guid.NewGuid();
        var sessionId = sourceId.ToString("D");
        var currentEnvelope = Encoding.UTF8.GetBytes(
            "{\"kind\":\"session\",\"transcript\":\"before mutation\"}");
        var writeCount = 0;
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupEnvelopeWrite(
            repository,
            sessionId,
            sourceId,
            () => currentEnvelope,
            () => writeCount++);
        repository
            .Setup(repo => repo.MarkSessionExportedAsync(
                sessionId,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        using var services = BuildServices(repository.Object);

        var lostAckHandler = new SingleUploadHandler(
            HttpStatusCode.Created,
            sourceId,
            "stored",
            throwAfterReadingRequest: true);
        using (var firstClient = new HttpClient(lostAckHandler))
        {
            var firstService = CreateService(firstClient, services);
            var firstResult = await firstService.ExportSessionAsync(
                sessionId,
                TestContext.Current.CancellationToken);

            Assert.Equal(SessionDataExportStatus.UploadFailed, firstResult.Status);
            Assert.True(File.Exists(SpoolPath(sourceId)));
            Assert.Equal(1, writeCount);
        }

        currentEnvelope = Encoding.UTF8.GetBytes(
            "{\"kind\":\"session\",\"transcript\":\"after database mutation\"}");
        var retryHandler = new SingleUploadHandler(
            HttpStatusCode.OK,
            sourceId,
            "already_exists");
        using var retryClient = new HttpClient(retryHandler);
        var retryService = CreateService(retryClient, services);

        var retryResult = await retryService.ExportSessionAsync(
            sessionId,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionDataExportStatus.Success, retryResult.Status);
        var operationIds = _featureLog.Entries.Select(entry => entry.OperationId).Distinct().ToArray();
        Assert.Equal(2, operationIds.Length);
        _featureLog.AssertAttempt(sessionId, "failed", operationIds[0]);
        _featureLog.AssertAttempt(sessionId, "succeeded", operationIds[1]);
        Assert.Equal(1, writeCount);
        Assert.Equal(
            Assert.IsType<RequestSnapshot>(lostAckHandler.Request).Body,
            Assert.IsType<RequestSnapshot>(retryHandler.Request).Body);
        Assert.Equal(lostAckHandler.Request.ContentSha256, retryHandler.Request.ContentSha256);
        Assert.False(File.Exists(SpoolPath(sourceId)));
        repository.Verify(repo => repo.WriteSessionExportAsync(
            sessionId, It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(repo => repo.MarkSessionExportedAsync(
            sessionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExportSessionAsync_LargeEnvelope_UsesChunkRoutesHeadersAndFinalAck()
    {
        var sourceId = Guid.NewGuid();
        var sessionId = sourceId.ToString("D");
        var envelope = new byte[5 * 1024 * 1024];
        new Random(0x51A7).NextBytes(envelope);
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupEnvelopeWrite(repository, sessionId, sourceId, () => envelope);
        repository
            .Setup(repo => repo.MarkSessionExportedAsync(
                sessionId,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        using var services = BuildServices(repository.Object);
        var handler = new ChunkUploadHandler(sourceId);
        using var client = new HttpClient(handler);
        var service = CreateService(client, services);

        var result = await service.ExportSessionAsync(
            sessionId,
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionDataExportStatus.Success, result.Status);
        Assert.True(handler.CompressedLength > SessionDataExportService.ChunkedUploadThresholdBytes);
        Assert.Equal(handler.Sha256, result.Sha256);
        var sessionPath = $"/v1/data/sessions/{sessionId}";
        var probe = Assert.IsType<RequestSnapshot>(handler.Probe);
        Assert.Equal(HttpMethod.Get, probe.Method);
        Assert.Equal(
            $"{sessionPath}/chunks/{handler.Sha256}?length={handler.CompressedLength}",
            probe.Uri.PathAndQuery);
        AssertCommonHeaders(probe);

        var expectedBlockCount = (int)(
            (handler.CompressedLength + ChunkUploadHandler.BlockSizeBytes - 1)
            / ChunkUploadHandler.BlockSizeBytes);
        Assert.Equal(expectedBlockCount, handler.Blocks.Count);
        Assert.Equal(
            Enumerable.Range(0, expectedBlockCount),
            handler.Blocks.Select(block => block.Index));
        foreach (var block in handler.Blocks)
        {
            Assert.Equal(HttpMethod.Put, block.Request.Method);
            Assert.Equal(
                $"{sessionPath}/chunks/{handler.Sha256}/{block.Index}",
                block.Request.Uri.AbsolutePath);
            Assert.Equal("application/octet-stream", block.Request.ContentType);
            AssertCommonHeaders(block.Request);
        }

        var reconstructed = handler.Blocks
            .OrderBy(block => block.Index)
            .SelectMany(block => block.Request.Body)
            .ToArray();
        Assert.Equal(handler.CompressedLength, reconstructed.LongLength);
        Assert.Equal(
            handler.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(reconstructed)));

        var commit = Assert.IsType<RequestSnapshot>(handler.Commit);
        Assert.Equal(HttpMethod.Post, commit.Method);
        Assert.Equal(
            $"{sessionPath}/chunks/{handler.Sha256}/commit",
            commit.Uri.AbsolutePath);
        Assert.Equal("application/json", commit.ContentType);
        AssertCommonHeaders(commit);
        using (var commitDocument = JsonDocument.Parse(commit.Body))
        {
            Assert.Equal(
                handler.CompressedLength,
                commitDocument.RootElement.GetProperty("length").GetInt64());
            Assert.Equal(
                expectedBlockCount,
                commitDocument.RootElement.GetProperty("blockCount").GetInt32());
        }

        Assert.DoesNotContain(
            handler.Requests,
            request => request.Method == HttpMethod.Post
                && request.Uri.AbsolutePath == sessionPath);
        Assert.False(File.Exists(SpoolPath(sourceId)));
        repository.Verify(repo => repo.WriteSessionExportAsync(
            sessionId, It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(repo => repo.MarkSessionExportedAsync(
            sessionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    public void Dispose()
    {
        ParserConfigs.SetApiKey(_originalApiKey);
        ParserConfigs.SetStatePath(_originalStatePath);
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [Fact]
    public async Task SweepOrphanedSpoolAsync_RemovesSpoolsWhoseSessionNoLongerAwaitsExport()
    {
        var retained = Guid.NewGuid();
        var exported = Guid.NewGuid();
        var deleted = Guid.NewGuid();

        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(repo => repo.SessionAwaitsExportAsync(
                retained.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        foreach (var gone in new[] { exported, deleted })
        {
            repository
                .Setup(repo => repo.SessionAwaitsExportAsync(
                    gone.ToString("D"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        }

        var retainedPath = WriteSpool(retained, ".json.br");
        var exportedPath = WriteSpool(exported, ".json.br");
        var deletedPath = WriteSpool(deleted, ".json.br");
        var deletedTempPath = WriteSpool(deleted, ".json.br.tmp");
        // Not written by this service; a sweep must not treat the directory as a free-for-all.
        var foreignPath = Path.Combine(Path.GetDirectoryName(retainedPath)!, "notes.txt");
        File.WriteAllText(foreignPath, "unrelated");

        using var services = BuildServices(repository.Object);
        using var client = new HttpClient(new UnreachableHandler());
        var service = CreateService(client, services);

        var removed = await service.SweepOrphanedSpoolAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, removed);
        Assert.True(File.Exists(retainedPath), "a session still awaiting export keeps its spool");
        Assert.False(File.Exists(exportedPath));
        Assert.False(File.Exists(deletedPath));
        Assert.False(File.Exists(deletedTempPath));
        Assert.True(File.Exists(foreignPath), "unrecognised files are left alone");
    }

    [Fact]
    public void DeleteSpool_RemovesBothTheEnvelopeAndItsInProgressTemp()
    {
        var sourceId = Guid.NewGuid();
        var path = WriteSpool(sourceId, ".json.br");
        var tempPath = WriteSpool(sourceId, ".json.br.tmp");

        var repository = new Mock<IRepository>(MockBehavior.Strict);
        using var services = BuildServices(repository.Object);
        using var client = new HttpClient(new UnreachableHandler());
        var service = CreateService(client, services);

        service.DeleteSpool(sourceId.ToString("D"));

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(tempPath));
        // A deletion for a session that never spooled anything must be silent, not an exception.
        service.DeleteSpool(Guid.NewGuid().ToString("D"));
        service.DeleteSpool("not-a-guid");
    }

    private string WriteSpool(Guid sourceId, string suffix)
    {
        var directory = Path.Combine(
            _testRoot,
            SessionDataExportService.SpoolDirectoryName,
            $"v{SessionDataExportService.EnvelopeSchemaVersion}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{sourceId:D}{suffix}");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The sweep must not perform any network I/O.");
    }

    private SessionDataExportService CreateService(
        HttpClient client,
        ServiceProvider services,
        string? exportUrl = ExportUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VibeRails:ExportUrl"] = exportUrl
            })
            .Build();
        return new SessionDataExportService(
            client,
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SessionDataExportService>.Instance,
            () => ComputerName,
            _featureLog);
    }

    private static ServiceProvider BuildServices(IRepository repository) =>
        new ServiceCollection()
            .AddSingleton(repository)
            .BuildServiceProvider();

    private static void SetupEnvelopeWrite(
        Mock<IRepository> repository,
        string sessionId,
        Guid sourceId,
        Func<byte[]> envelopeFactory,
        Action? onWrite = null)
    {
        repository
            .Setup(repo => repo.WriteSessionExportAsync(
                sessionId,
                It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, Stream destination, CancellationToken cancellationToken) =>
                WriteEnvelopeAsync(
                    destination,
                    envelopeFactory(),
                    sourceId,
                    onWrite,
                    cancellationToken));
    }

    private static async Task<SessionDataExportDescriptor?> WriteEnvelopeAsync(
        Stream destination,
        byte[] envelope,
        Guid sourceId,
        Action? onWrite,
        CancellationToken cancellationToken)
    {
        onWrite?.Invoke();
        await destination.WriteAsync(envelope, cancellationToken);
        return new SessionDataExportDescriptor(1, "session", sourceId);
    }

    private static async Task<byte[]> DecompressAsync(byte[] compressed)
    {
        await using var input = new MemoryStream(compressed);
        await using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        await using var output = new MemoryStream();
        await brotli.CopyToAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private string SpoolPath(Guid sourceId) => Path.Combine(
        _testRoot,
        SessionDataExportService.SpoolDirectoryName,
        $"v{SessionDataExportService.EnvelopeSchemaVersion}",
        $"{sourceId:D}.json.br");

    private static void AssertCommonHeaders(RequestSnapshot request)
    {
        Assert.Equal(ApiKey, request.ApiKey);
        Assert.Equal(Uri.EscapeDataString(ComputerName), request.ComputerName);
        Assert.Equal("1", request.SchemaVersion);
    }

    private static HttpResponseMessage Ack(
        HttpStatusCode statusCode,
        Guid sourceId,
        string sha256,
        long compressedLength,
        string serverStatus)
    {
        var json = JsonSerializer.Serialize(new
        {
            status = serverStatus,
            kind = "session",
            sourceId = sourceId.ToString("D"),
            schemaVersion = 1,
            sha256,
            compressedBytes = compressedLength
        });
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        string? ApiKey,
        string? ComputerName,
        string? SchemaVersion,
        string? ContentSha256,
        string? ContentType,
        long? ContentLength,
        byte[] Body)
    {
        public static async Task<RequestSnapshot> CaptureAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new RequestSnapshot(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Request URI was absent."),
                Header(request, "X-Api-Key"),
                Header(request, "X-Computer-Name"),
                Header(request, "X-Envelope-Schema-Version"),
                Header(request, "X-Content-SHA256"),
                request.Content?.Headers.ContentType?.MediaType,
                request.Content?.Headers.ContentLength,
                body);
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values)
                ? values.SingleOrDefault()
                : null;
    }

    private sealed class SingleUploadHandler(
        HttpStatusCode responseStatus,
        Guid sourceId,
        string? acknowledgementStatus,
        bool mismatchAcknowledgement = false,
        bool throwAfterReadingRequest = false) : HttpMessageHandler
    {
        public RequestSnapshot? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = await RequestSnapshot.CaptureAsync(request, cancellationToken);
            if (throwAfterReadingRequest)
                throw new HttpRequestException("The response was lost after the request was sent.");
            if (responseStatus == HttpStatusCode.Conflict)
                return new HttpResponseMessage(responseStatus);

            var sha = Request.ContentSha256
                ?? throw new InvalidOperationException("Single upload did not send a SHA-256 header.");
            return Ack(
                responseStatus,
                mismatchAcknowledgement ? Guid.NewGuid() : sourceId,
                sha,
                Request.Body.LongLength,
                acknowledgementStatus
                    ?? throw new InvalidOperationException("Acknowledgement status was absent."));
        }
    }

    private sealed class ChunkUploadHandler(Guid sourceId) : HttpMessageHandler
    {
        public const int BlockSizeBytes = 1024 * 1024;

        public List<RequestSnapshot> Requests { get; } = [];
        public List<(int Index, RequestSnapshot Request)> Blocks { get; } = [];
        public RequestSnapshot? Probe { get; private set; }
        public RequestSnapshot? Commit { get; private set; }
        public string Sha256 { get; private set; } = string.Empty;
        public long CompressedLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var snapshot = await RequestSnapshot.CaptureAsync(request, cancellationToken);
            Requests.Add(snapshot);

            if (request.Method == HttpMethod.Get)
            {
                Probe = snapshot;
                var chunksMarker = "/chunks/";
                var markerIndex = snapshot.Uri.AbsolutePath.LastIndexOf(
                    chunksMarker,
                    StringComparison.Ordinal);
                Sha256 = snapshot.Uri.AbsolutePath[(markerIndex + chunksMarker.Length)..];
                CompressedLength = long.Parse(
                    snapshot.Uri.Query["?length=".Length..],
                    System.Globalization.CultureInfo.InvariantCulture);
                return JsonResponse(HttpStatusCode.OK, new
                {
                    blockSizeBytes = BlockSizeBytes,
                    uploadedIndices = Array.Empty<int>(),
                    alreadyStored = false
                });
            }

            if (request.Method == HttpMethod.Put)
            {
                var index = int.Parse(
                    snapshot.Uri.Segments[^1].Trim('/'),
                    System.Globalization.CultureInfo.InvariantCulture);
                Blocks.Add((index, snapshot));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post
                && snapshot.Uri.AbsolutePath.EndsWith("/commit", StringComparison.Ordinal))
            {
                Commit = snapshot;
                return Ack(
                    HttpStatusCode.Created,
                    sourceId,
                    Sha256,
                    CompressedLength,
                    "stored");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(
            HttpStatusCode statusCode,
            object value) =>
            new(statusCode)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value),
                    Encoding.UTF8,
                    "application/json")
            };
    }
}
