using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsRoutesDatabaseSizeTests : IDisposable
{
    private readonly string _originalStatePath = ParserConfigs.GetStatePath();
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"viberails-db-size-route-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetDbSize_ReturnsConfiguredStateDatabaseLength_AndZeroWhenMissing()
    {
        const long expectedBytes = 12_345_678;
        Directory.CreateDirectory(_testRoot);
        var statePath = Path.Combine(_testRoot, "state.db");
        await using (var stream = new FileStream(
            statePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read))
        {
            stream.SetLength(expectedBytes);
        }
        ParserConfigs.SetStatePath(statePath);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(
                0,
                AppJsonSerializerContext.Default);
        });

        await using var app = builder.Build();
        AppSettingsRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            using var response = await client.GetAsync(
                "/api/v1/settings/db-size",
                TestContext.Current.CancellationToken);
            var body = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.StateDatabaseSizeResponse,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Equal(expectedBytes, body.Bytes);

            File.Delete(statePath);
            using var missingResponse = await client.GetAsync(
                "/api/v1/settings/db-size",
                TestContext.Current.CancellationToken);
            var missingBody = await missingResponse.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.StateDatabaseSizeResponse,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, missingResponse.StatusCode);
            Assert.NotNull(missingBody);
            Assert.Equal(0, missingBody.Bytes);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    public void Dispose()
    {
        ParserConfigs.SetStatePath(_originalStatePath);
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
