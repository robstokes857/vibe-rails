using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.FileSystem;
using Xunit;

namespace Tests.Routes;

public sealed class FileSystemRoutesTests : IDisposable
{
    private static readonly HttpClient SharedClient = new();
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"viberails-filesystem-route-{Guid.NewGuid():N}");

    public FileSystemRoutesTests()
    {
        Directory.CreateDirectory(_testRoot);
    }

    [Fact]
    public async Task GetWithoutPath_UsesTheResolvedDefaultAndReturnsTheBrowsePayload()
    {
        var browser = new RecordingBrowserService();

        await WithHostAsync(browser, async baseUri =>
        {
            using var response = await SharedClient.GetAsync(
                new Uri(baseUri, "/api/v1/filesystem/entries"),
                TestContext.Current.CancellationToken);
            var body = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.FileSystemBrowseResponse,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Null(browser.RequestedPath);
            Assert.False(browser.IncludeHidden);
            Assert.Equal(browser.DefaultPath, body.CurrentPath);
            Assert.True(Path.IsPathFullyQualified(browser.DefaultPath));
            Assert.True(Directory.Exists(browser.DefaultPath));
        });
    }

    [Theory]
    [InlineData((int)FileSystemBrowseError.InvalidPath, HttpStatusCode.BadRequest)]
    [InlineData((int)FileSystemBrowseError.NotFound, HttpStatusCode.NotFound)]
    [InlineData((int)FileSystemBrowseError.AccessDenied, HttpStatusCode.Forbidden)]
    public async Task BrowseErrors_ReturnCleanHttpErrors(
        int errorValue,
        HttpStatusCode expectedStatus)
    {
        const string message = "The requested directory is unavailable.";
        var error = (FileSystemBrowseError)errorValue;
        var browser = new ThrowingBrowserService(error, message);

        await WithHostAsync(browser, async baseUri =>
        {
            using var response = await SharedClient.GetAsync(
                new Uri(baseUri, "/api/v1/filesystem/entries?path=ignored"),
                TestContext.Current.CancellationToken);
            var body = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.ErrorResponse,
                TestContext.Current.CancellationToken);

            Assert.Equal(expectedStatus, response.StatusCode);
            Assert.NotNull(body);
            Assert.Equal(message, body.Error);
            Assert.DoesNotContain(nameof(FileSystemBrowseException), body.Error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task BrowseQuery_ForwardsSearchCursorAndPageSize()
    {
        var browser = new RecordingBrowserService();

        await WithHostAsync(browser, async baseUri =>
        {
            var query = new Uri(
                baseUri,
                "/api/v1/filesystem/entries?path=ignored&includeHidden=true"
                + "&search=space%20%23%20%2B%20%E9%9B%AA&cursor=cursor%2B%2Fvalue&pageSize=27");
            using var response = await SharedClient.GetAsync(
                query,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("ignored", browser.RequestedPath);
            Assert.True(browser.IncludeHidden);
            Assert.Equal("space # + 雪", browser.Search);
            Assert.Equal("cursor+/value", browser.Cursor);
            Assert.Equal(27, browser.PageSize);
        });
    }

    private async Task WithHostAsync(
        IFileSystemBrowserService browser,
        Func<Uri, Task> test)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(browser);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(
                0,
                AppJsonSerializerContext.Default);
        });

        await using var app = builder.Build();
        FileSystemRoutes.Map(app, _testRoot);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await test(new Uri(app.Urls.First()));
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingBrowserService : IFileSystemBrowserService
    {
        public string? RequestedPath { get; private set; }
        public string DefaultPath { get; private set; } = string.Empty;
        public bool IncludeHidden { get; private set; }
        public string? Search { get; private set; }
        public string? Cursor { get; private set; }
        public int? PageSize { get; private set; }

        public FileSystemBrowseResponse Browse(
            string? path,
            string defaultPath,
            bool includeHidden,
            string? search = null,
            string? cursor = null,
            int? pageSize = null,
            CancellationToken cancellationToken = default)
        {
            RequestedPath = path;
            DefaultPath = defaultPath;
            IncludeHidden = includeHidden;
            Search = search;
            Cursor = cursor;
            PageSize = pageSize;
            return new FileSystemBrowseResponse(
                defaultPath,
                defaultPath,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(defaultPath)),
                Path.GetDirectoryName(defaultPath),
                [],
                [],
                [],
                Truncated: false);
        }
    }

    private sealed class ThrowingBrowserService(
        FileSystemBrowseError error,
        string message) : IFileSystemBrowserService
    {
        public FileSystemBrowseResponse Browse(
            string? path,
            string defaultPath,
            bool includeHidden,
            string? search = null,
            string? cursor = null,
            int? pageSize = null,
            CancellationToken cancellationToken = default) =>
            throw new FileSystemBrowseException(error, message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
