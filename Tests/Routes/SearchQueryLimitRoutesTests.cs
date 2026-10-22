using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.BertBaseClasses;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Routes;

public sealed class SearchQueryLimitRoutesTests
{
    [Fact]
    public async Task OversizedQueriesReturn400WithoutCallingSearch()
    {
        var ct = TestContext.Current.CancellationToken;
        var unified = new Mock<IUnifiedSearchService>(MockBehavior.Strict);
        var bert = new Mock<IBertSearchServiceV2>(MockBehavior.Strict);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(unified.Object);
        builder.Services.AddSingleton(bert.Object);
        builder.Services.AddSingleton(Mock.Of<IBertSearchDbService>());
        builder.Services.AddSingleton(Mock.Of<IBertSettings>());
        builder.Services.AddSingleton(Mock.Of<IBertCaptureQueryService>());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        await using var app = builder.Build();
        SearchRoutes.Map(app);
        BertRoutes.Map(app);
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new(app.Urls.First()) };
        try
        {
            foreach (var path in new[] { "/api/v1/search", "/api/v1/bert/search" })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var response = await client.PostAsJsonAsync(path, new { query = new string(' ', 100_000), mode = "text", scope = "both", topK = 1 }, ct);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                Assert.Contains("1000", json.RootElement.GetProperty("error").GetString());
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
            }
            unified.VerifyNoOtherCalls();
            bert.VerifyNoOtherCalls();
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
}
