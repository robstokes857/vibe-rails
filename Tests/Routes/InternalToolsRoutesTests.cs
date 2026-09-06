using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Routes;

public sealed class InternalToolsRoutesTests
{
    // One client for the whole class: a per-test HttpClient leaves sockets in TIME_WAIT.
    private static readonly HttpClient SharedClient = new();

    [Fact]
    public async Task ReadRoutesBindFilters_ReturnAotJson_AndExposeNoCrudMutations()
    {
        var reader = new RecordingReader();
        var diagnostics = new RecordingDiagnosticReader();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IFeatureLogReader>(reader);
        builder.Services.AddSingleton<IDiagnosticLogReader>(diagnostics);
        var auth = new Mock<IAuthService>();
        auth.Setup(service => service.ValidateToken(It.IsAny<string?>()))
            .Returns((string? token) => token == "test-session");
        auth.Setup(service => service.ValidateTabToken(It.IsAny<string?>()))
            .Returns((string? token) => token == "test-tab");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        InternalToolsRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var baseUri = new Uri(app.Urls.First());
            using var unauthenticated = await SendAsync(baseUri, "/api/v1/internal/logs?source=application");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            Assert.Null(diagnostics.Source);
            using var missingTab = await SendAsync(baseUri, "/api/v1/internal/logs?source=application", session: "test-session");
            Assert.Equal(HttpStatusCode.Unauthorized, missingTab.StatusCode);
            Assert.Null(diagnostics.Source);

            var json = await ReadAuthenticatedAsync(baseUri,
                "/api/v1/internal/logs?feature=data-upload&level=Error&status=failed&search=snapshot&operationId=op-1&offset=3&limit=25");
            using var document = JsonDocument.Parse(json);
            Assert.Equal("entry-1", document.RootElement.GetProperty("entries")[0].GetProperty("id").GetString());
            Assert.Equal("data-upload", document.RootElement.GetProperty("features")[0].GetString());
            Assert.Equal(2, document.RootElement.GetProperty("droppedCount").GetInt64());
            Assert.Equal(1, document.RootElement.GetProperty("writeFailures").GetInt64());
            Assert.Equal(3, document.RootElement.GetProperty("readFailures").GetInt64());
            Assert.False(reader.UploadsOnly);
            Assert.Equal(new FeatureLogQuery("data-upload", "Error", "failed", "snapshot", "op-1", 3, 25), reader.Query);
            Assert.Equal("features", document.RootElement.GetProperty("entries")[0].GetProperty("source").GetString());

            var diagnosticJson = await ReadAuthenticatedAsync(baseUri,
                "/api/v1/internal/logs?source=application&feature=Jobs&level=Warning&search=older&offset=2&limit=10");
            using var diagnosticDocument = JsonDocument.Parse(diagnosticJson);
            Assert.Equal("application", diagnostics.Source);
            Assert.Equal(new FeatureLogQuery(Feature: "Jobs", Level: "Warning", Search: "older", Offset: 2, Limit: 10), diagnostics.Query);
            Assert.Equal("vb-20260901.log", diagnosticDocument.RootElement.GetProperty("entries")[0].GetProperty("sourceFile").GetString());
            Assert.True(diagnosticDocument.RootElement.GetProperty("truncated").GetBoolean());

            using var demonLogs = await SendAuthenticatedAsync(baseUri, "/api/v1/internal/logs?source=daemon");
            Assert.Equal(HttpStatusCode.OK, demonLogs.StatusCode);
            Assert.Equal("daemon", diagnostics.Source);
            Assert.Equal(new FeatureLogQuery(), diagnostics.Query);

            using var invalidSource = await SendAuthenticatedAsync(baseUri, "/api/v1/internal/logs?source=..%2F..%2Fstate.db");
            Assert.Equal(HttpStatusCode.BadRequest, invalidSource.StatusCode);
            Assert.Equal("daemon", diagnostics.Source); // Never forwarded to a filesystem reader.

            using var uploads = await SendAuthenticatedAsync(baseUri, "/api/v1/internal/uploads?status=uploaded&search=op-1");
            Assert.Equal(HttpStatusCode.OK, uploads.StatusCode);
            Assert.True(reader.UploadsOnly);
            Assert.Equal(new FeatureLogQuery(Status: "uploaded", Search: "op-1"), reader.Query);

            using var mutation = await SendAuthenticatedAsync(baseUri, "/api/v1/internal/uploads", HttpMethod.Post);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, mutation.StatusCode);
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    private static Task<HttpResponseMessage> SendAuthenticatedAsync(Uri baseUri, string path, HttpMethod? method = null) =>
        SendAsync(baseUri, path, "test-session", "test-tab", method);

    private static async Task<string> ReadAuthenticatedAsync(Uri baseUri, string path)
    {
        using var response = await SendAuthenticatedAsync(baseUri, path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAsync(Uri baseUri, string path, string? session = null,
        string? tab = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(baseUri, path));
        if (session != null)
            request.Headers.Add("viberails_session", session);
        if (tab != null)
            request.Headers.Add("viberails_tab", tab);
        return await SharedClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingReader : IFeatureLogReader
    {
        public FeatureLogQuery? Query { get; private set; }
        public bool UploadsOnly { get; private set; }

        public Task<InternalLogResponse> ReadAsync(FeatureLogQuery query, bool uploadsOnly = false,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            UploadsOnly = uploadsOnly;
            return Task.FromResult(new InternalLogResponse(
                [new InternalLogEntry("entry-1", DateTimeOffset.UtcNow, "data-upload", "Error", "failed",
                    "Upload failed", "op-1", "Database snapshot", "failed")],
                ["data-upload"], false, 2, 1, 3));
        }
    }

    private sealed class RecordingDiagnosticReader : IDiagnosticLogReader
    {
        public string? Source { get; private set; }
        public FeatureLogQuery? Query { get; private set; }

        public Task<InternalLogResponse> ReadAsync(string source, FeatureLogQuery query,
            CancellationToken cancellationToken = default)
        {
            Source = source;
            Query = query;
            return Task.FromResult(new InternalLogResponse(
                [new InternalLogEntry("diagnostic-1", DateTimeOffset.UtcNow, "Jobs", "Warning", "diagnostic",
                    "An older message\nWith exception details", Source: source, SourceFile: "vb-20260901.log")],
                ["Jobs"], false, 0, 0, Truncated: true));
        }
    }
}
