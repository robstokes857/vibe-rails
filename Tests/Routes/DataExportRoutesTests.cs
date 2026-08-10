using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Routes;

public sealed class DataExportRoutesTests
{
    // One client for the whole class: a per-test HttpClient leaves sockets in TIME_WAIT, and
    // this is a [Theory] with seven cases.
    private static readonly HttpClient SharedClient = new();

    [Theory]
    [InlineData(
        DataExportStatus.Success,
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        null,
        true,
        "ok",
        "Data exported successfully.")]
    [InlineData(
        DataExportStatus.NoApiKey,
        null,
        "Service detail must not replace the route message.",
        false,
        "no_api_key",
        "Save a valid API key before exporting data.")]
    [InlineData(
        DataExportStatus.NotConfigured,
        null,
        "Service detail must not replace the route message.",
        false,
        "not_configured",
        "Data export is not configured.")]
    [InlineData(
        DataExportStatus.Busy,
        null,
        "Service detail must not replace the route message.",
        false,
        "busy",
        "A data export is already in progress.")]
    [InlineData(
        DataExportStatus.InvalidApiKey,
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "The export server rejected the API key (HTTP 401 Unauthorized). Reference: auth-trace",
        false,
        "invalid_api_key",
        "The export server rejected the API key (HTTP 401 Unauthorized). Reference: auth-trace")]
    [InlineData(
        DataExportStatus.UploadFailed,
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "The export endpoint was unavailable.",
        false,
        "upload_failed",
        "The export endpoint was unavailable.")]
    [InlineData(
        DataExportStatus.Failed,
        null,
        "The snapshot could not be created.",
        false,
        "failed",
        "The snapshot could not be created.")]
    public async Task ExportData_MapsEveryDomainStatusToAnHttp200Response(
        DataExportStatus serviceStatus,
        string? sha256,
        string? detail,
        bool expectedSuccess,
        string expectedStatus,
        string expectedMessage)
    {
        var service = new StubDataExportService(
            new DataExportResult(serviceStatus, sha256, detail));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDataExportService>(service);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(
                0,
                AppJsonSerializerContext.Default);
        });

        await using var app = builder.Build();
        DataExportRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(
                    new Uri(app.Urls.First()),
                    "/api/v1/settings/export-data"));
            using var response = await SharedClient.SendAsync(
                request,
                TestContext.Current.CancellationToken);
            var body = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.DataExportResponse,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Equal(expectedSuccess, body.Success);
            Assert.Equal(expectedStatus, body.Status);
            Assert.Equal(expectedMessage, body.Message);
            Assert.Equal(sha256, body.Sha256);
            Assert.Equal(1, service.CallCount);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private sealed class StubDataExportService(DataExportResult result) : IDataExportService
    {
        public int CallCount { get; private set; }

        public Task<DataExportResult> ExportAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
