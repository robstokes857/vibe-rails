using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using VibeRails;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services;

[Collection("ProcessEnvIsolation")]
public sealed class DataExportServiceTests : IDisposable
{
    private const string ValidExportUrl = "https://exports.example.test/upload";
    private const string ConfiguredApiKey = "configured-export-api-key";

    private readonly string _originalApiKey = ParserConfigs.GetApiKey();
    private readonly string _originalStatePath = ParserConfigs.GetStatePath();
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"viberails-data-export-tests-{Guid.NewGuid():N}");
    private readonly string _exportTempRoot;
    private int _temporaryDirectoryCount;

    public DataExportServiceTests()
    {
        _exportTempRoot = Path.Combine(_testRoot, "export-temp");
        Directory.CreateDirectory(_exportTempRoot);
    }

    [Fact]
    public void NoRedirectHttpHandler_DisablesAutomaticRedirects()
    {
        // Shared by the data-export and token-savings clients: both send the API key in a custom
        // X-Api-Key header, which is NOT stripped on a cross-host redirect the way Authorization is.
        using var handler = MapRegisterServices.CreateNoRedirectHttpMessageHandler();
        var httpHandler = Assert.IsType<HttpClientHandler>(handler);

        Assert.False(httpHandler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExportAsync_MissingOrWhitespaceApiKey_DoesNoWork(string apiKey)
    {
        ParserConfigs.SetApiKey(apiKey);
        ParserConfigs.SetStatePath(Path.Combine(_testRoot, "missing-state.db"));
        var handler = new CapturingHandler(HttpStatusCode.OK, _exportTempRoot);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.NoApiKey, result.Status);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, Volatile.Read(ref _temporaryDirectoryCount));
        AssertExportTempRootIsEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/export")]
    [InlineData("http://exports.example.test/upload")]
    // Chunk endpoints are built by appending path segments, and appending to a URL that already
    // has a query or fragment buries the new path inside the query — every chunk request would
    // silently target the base path instead.
    [InlineData("https://exports.example.test/upload?tenant=x")]
    [InlineData("https://exports.example.test/upload#fragment")]
    public async Task ExportAsync_InvalidExportUrl_DoesNoWork(string? exportUrl)
    {
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(Path.Combine(_testRoot, "missing-state.db"));
        var handler = new CapturingHandler(HttpStatusCode.OK, _exportTempRoot);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, exportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.NotConfigured, result.Status);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, Volatile.Read(ref _temporaryDirectoryCount));
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_MissingStateDatabase_DoesNoWork()
    {
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(Path.Combine(_testRoot, "missing-state.db"));
        var handler = new CapturingHandler(HttpStatusCode.OK, _exportTempRoot);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.Failed, result.Status);
        Assert.Contains("not found", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, Volatile.Read(ref _temporaryDirectoryCount));
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_LiveWalSnapshot_UploadsValidBrotliDatabaseWithHashAndHeaders()
    {
        var statePath = NewStatePath();
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);

        await using var writer = await CreateLiveWalDatabaseAsync(statePath);
        var walPath = statePath + "-wal";
        Assert.True(File.Exists(walPath));
        Assert.True(new FileInfo(walPath).Length > 0);

        var handler = new CapturingHandler(HttpStatusCode.OK, _exportTempRoot);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.Success, result.Status);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(ValidExportUrl, handler.RequestUri?.AbsoluteUri);
        Assert.Equal(ConfiguredApiKey, handler.ApiKey);
        Assert.Equal("test-computer", Uri.UnescapeDataString(handler.ComputerName ?? ""));
        Assert.Equal("application/octet-stream", handler.ContentType);
        Assert.Equal("attachment", handler.ContentDispositionType);
        Assert.Equal(DataExportService.CompressedFileName, handler.FileName?.Trim('"'));
        Assert.Empty(handler.ContentEncodings);

        var compressedBody = Assert.IsType<byte[]>(handler.Body);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(compressedBody));
        Assert.Equal(expectedHash, handler.ContentSha256);
        Assert.Equal(expectedHash, result.Sha256);
        Assert.Equal(
            [DataExportService.CompressedFileName],
            handler.FileNamesObservedDuringSend);

        var exportedDatabasePath = Path.Combine(_testRoot, "exported-state.db");
        await DecompressToFileAsync(
            compressedBody,
            exportedDatabasePath,
            TestContext.Current.CancellationToken);

        var exportedConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = exportedDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var exported = new SqliteConnection(exportedConnectionString);
        await exported.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "ok",
            await ExecuteScalarAsync<string>(
                exported,
                "PRAGMA integrity_check;",
                TestContext.Current.CancellationToken));
        Assert.Equal(
            "committed only in the live WAL",
            await ExecuteScalarAsync<string>(
                exported,
                "SELECT Value FROM ExportProbe WHERE Id = 1;",
                TestContext.Current.CancellationToken));

        // Export must be read-only with respect to the live database. The uploaded bytes came
        // from BackupDatabase's snapshot, and the source remains healthy and unchanged.
        Assert.Equal(
            "ok",
            await ExecuteScalarAsync<string>(
                writer,
                "PRAGMA integrity_check;",
                TestContext.Current.CancellationToken));
        Assert.Equal(
            "committed only in the live WAL",
            await ExecuteScalarAsync<string>(
                writer,
                "SELECT Value FROM ExportProbe WHERE Id = 1;",
                TestContext.Current.CancellationToken));

        AssertExportTempRootIsEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DataExportStatus.InvalidApiKey)]
    [InlineData(HttpStatusCode.Forbidden, DataExportStatus.InvalidApiKey)]
    [InlineData(HttpStatusCode.BadGateway, DataExportStatus.UploadFailed)]
    [InlineData(HttpStatusCode.TemporaryRedirect, DataExportStatus.UploadFailed)]
    public async Task ExportAsync_MapsUpstreamFailureAndCleansTemporaryArtifacts(
        HttpStatusCode upstreamStatus,
        DataExportStatus expectedStatus)
    {
        var statePath = await CreateSimpleStateDatabaseAsync();
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);
        var handler = new CapturingHandler(upstreamStatus, _exportTempRoot);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(string.IsNullOrWhiteSpace(result.Sha256));
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_ProblemDetailsFailure_PreservesDetailStatusAndReference()
    {
        var statePath = await CreateSimpleStateDatabaseAsync();
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);
        var handler = new CapturingHandler(
            (HttpStatusCode)422,
            _exportTempRoot,
            """
            {
              "title": "Unprocessable Entity",
              "status": 422,
              "detail": "The account does not have enough storage for this export.",
              "traceId": "00-safe-trace-00"
            }
            """);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.UploadFailed, result.Status);
        Assert.Equal(
            "The account does not have enough storage for this export "
            + "(HTTP 422 Unprocessable Entity). Reference: 00-safe-trace-00",
            result.Detail);
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_GenericProblemTitle_UsesActionableStatusMessage()
    {
        var statePath = await CreateSimpleStateDatabaseAsync();
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);
        var handler = new CapturingHandler(
            HttpStatusCode.ServiceUnavailable,
            _exportTempRoot,
            """
            { "title": "Service Unavailable", "status": 503, "traceId": "retry-reference" }
            """);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.UploadFailed, result.Status);
        Assert.Equal(
            "The export service is temporarily unavailable; retry in a few minutes "
            + "(HTTP 503 Service Unavailable). Reference: retry-reference",
            result.Detail);
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_PlainTextFailure_PreservesServerMessageAndStatus()
    {
        var statePath = await CreateSimpleStateDatabaseAsync();
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);
        var handler = new CapturingHandler(
            HttpStatusCode.BadRequest,
            _exportTempRoot,
            "Computer name is required.",
            "text/plain");
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.UploadFailed, result.Status);
        Assert.Equal(
            "Computer name is required (HTTP 400 Bad Request).",
            result.Detail);
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_HtmlErrorPage_PreservesServerRequestId()
    {
        var statePath = await CreateSimpleStateDatabaseAsync();
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);
        var handler = new CapturingHandler(
            HttpStatusCode.InternalServerError,
            _exportTempRoot,
            """
            <!DOCTYPE html>
            <html><body>
              <h1>Error.</h1>
              <p><strong>Request ID:</strong>
                 <code>00-server-trace-parent-span-00</code></p>
            </body></html>
            """,
            "text/html");
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.UploadFailed, result.Status);
        Assert.Equal(
            "The export service is temporarily unavailable; retry in a few minutes "
            + "(HTTP 500 Internal Server Error). Reference: 00-server-trace-parent-span-00",
            result.Detail);
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_TransportFailure_ReturnsUploadFailedAndCleansTemporaryArtifacts()
    {
        var statePath = await CreateSimpleStateDatabaseAsync();
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);
        var handler = new ThrowingHandler(new HttpRequestException("Simulated transport failure."));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.UploadFailed, result.Status);
        Assert.Equal(1, handler.RequestCount);
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_CallerCancellation_PropagatesAndCleansTemporaryArtifacts()
    {
        var statePath = await CreateSimpleStateDatabaseAsync();
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);
        var handler = new BlockingHandler();
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var exportTask = service.ExportAsync(cancellationSource.Token);
        await handler.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await exportTask);
        Assert.Equal(1, handler.RequestCount);
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_CorruptStateDatabase_ReturnsFailedAndCleansSnapshotArtifacts()
    {
        var statePath = NewStatePath();
        await File.WriteAllTextAsync(
            statePath,
            "this is not a SQLite database",
            TestContext.Current.CancellationToken);
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);
        var handler = new CapturingHandler(HttpStatusCode.OK, _exportTempRoot);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.Failed, result.Status);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(1, Volatile.Read(ref _temporaryDirectoryCount));
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_ConcurrentRequest_ReturnsBusyWithoutStartingSecondExport()
    {
        var statePath = await CreateSimpleStateDatabaseAsync();
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);
        var firstHandler = new BlockingHandler();
        var secondHandler = new CapturingHandler(HttpStatusCode.OK, _exportTempRoot);
        using var firstClient = new HttpClient(firstHandler);
        using var secondClient = new HttpClient(secondHandler);
        var firstService = CreateService(firstClient, ValidExportUrl);
        var secondService = CreateService(secondClient, ValidExportUrl);

        Task<DataExportResult>? firstExport = null;
        DataExportResult? firstResult = null;
        try
        {
            firstExport = firstService.ExportAsync(TestContext.Current.CancellationToken);
            await firstHandler.Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            var secondResult = await secondService.ExportAsync(
                TestContext.Current.CancellationToken);

            Assert.Equal(DataExportStatus.Busy, secondResult.Status);
            Assert.Equal(0, secondHandler.RequestCount);
            Assert.Equal(1, Volatile.Read(ref _temporaryDirectoryCount));
        }
        finally
        {
            firstHandler.Release.TrySetResult();
            if (firstExport is not null)
            {
                firstResult = await firstExport.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None);
            }
        }

        Assert.Equal(DataExportStatus.Success, firstResult?.Status);
        AssertExportTempRootIsEmpty();
    }

    [Fact]
    public async Task ExportAsync_CrossProcessLockHeld_ReturnsBusyWithoutCreatingSnapshot()
    {
        var statePath = await CreateSimpleStateDatabaseAsync();
        ParserConfigs.SetApiKey(ConfiguredApiKey);
        ParserConfigs.SetStatePath(statePath);
        var handler = new CapturingHandler(HttpStatusCode.OK, _exportTempRoot);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, ValidExportUrl);

        using var heldLock = CrossProcessFileLock.TryAcquire(
            CrossProcessFileLock.BesideStateDatabase(
                statePath,
                DataExportService.LockFileName));
        Assert.NotNull(heldLock);

        var result = await service.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DataExportStatus.Busy, result.Status);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, Volatile.Read(ref _temporaryDirectoryCount));
        AssertExportTempRootIsEmpty();
    }

    public void Dispose()
    {
        ParserConfigs.SetApiKey(_originalApiKey);
        ParserConfigs.SetStatePath(_originalStatePath);

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private DataExportService CreateService(HttpClient httpClient, string? exportUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VibeRails:ExportUrl"] = exportUrl
            })
            .Build();

        return new DataExportService(
            httpClient,
            configuration,
            CreateTemporaryDirectoryPath,
            static () => "test-computer");
    }

    private string CreateTemporaryDirectoryPath()
    {
        var sequence = Interlocked.Increment(ref _temporaryDirectoryCount);
        return Path.Combine(_exportTempRoot, $"export-{sequence}");
    }

    private string NewStatePath() =>
        Path.Combine(_testRoot, $"state-{Guid.NewGuid():N}.db");

    private async Task<string> CreateSimpleStateDatabaseAsync()
    {
        var statePath = NewStatePath();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = statePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            "CREATE TABLE ExportProbe (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);"
            + "INSERT INTO ExportProbe (Id, Value) VALUES (1, 'simple row');",
            TestContext.Current.CancellationToken);
        return statePath;
    }

    private static async Task<SqliteConnection> CreateLiveWalDatabaseAsync(string statePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = statePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        var writer = new SqliteConnection(connectionString);
        try
        {
            await writer.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                "wal",
                await ExecuteScalarAsync<string>(
                    writer,
                    "PRAGMA journal_mode = WAL;",
                    TestContext.Current.CancellationToken));
            await ExecuteNonQueryAsync(
                writer,
                "PRAGMA wal_autocheckpoint = 0;"
                + "CREATE TABLE ExportProbe (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);",
                TestContext.Current.CancellationToken);
            await ExecuteNonQueryAsync(
                writer,
                "PRAGMA wal_checkpoint(TRUNCATE);",
                TestContext.Current.CancellationToken);
            await ExecuteNonQueryAsync(
                writer,
                """
                INSERT INTO ExportProbe (Id, Value)
                VALUES (1, 'committed only in the live WAL');
                """,
                TestContext.Current.CancellationToken);
            return writer;
        }
        catch
        {
            await writer.DisposeAsync();
            throw;
        }
    }

    private static async Task DecompressToFileAsync(
        byte[] compressedBody,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(compressedBody);
        await using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        await decompressor.CopyToAsync(output, cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T?> ExecuteScalarAsync<T>(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return (T?)value;
    }

    private void AssertExportTempRootIsEmpty()
    {
        Assert.Empty(Directory.EnumerateFileSystemEntries(_exportTempRoot));
    }

    private sealed class CapturingHandler(
        HttpStatusCode statusCode,
        string exportTempRoot,
        string? responseBody = null,
        string responseMediaType = "application/problem+json") : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? ComputerName { get; private set; }
        public string? ContentSha256 { get; private set; }
        public string? ContentType { get; private set; }
        public string? ContentDispositionType { get; private set; }
        public string? FileName { get; private set; }
        public string[] ContentEncodings { get; private set; } = [];
        public string[] FileNamesObservedDuringSend { get; private set; } = [];
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            ApiKey = GetSingleHeader(request, "X-Api-Key");
            ComputerName = GetSingleHeader(request, "X-Computer-Name");
            ContentSha256 = GetSingleHeader(request, "X-Content-SHA256");
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            ContentDispositionType = request.Content?.Headers.ContentDisposition?.DispositionType;
            FileName = request.Content?.Headers.ContentDisposition?.FileName;
            ContentEncodings = request.Content?.Headers.ContentEncoding.ToArray() ?? [];
            FileNamesObservedDuringSend = Directory
                .EnumerateFiles(exportTempRoot, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray()!;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            var response = new HttpResponseMessage(statusCode);
            if (responseBody is not null)
            {
                response.Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    responseMediaType);
            }
            return response;
        }

        private static string? GetSingleHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values)
                ? values.SingleOrDefault()
                : null;
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
