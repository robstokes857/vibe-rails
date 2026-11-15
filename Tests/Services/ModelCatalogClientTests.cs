using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests.Services;

public sealed class ModelCatalogClientTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public string Body = "{\"version\":1,\"revision\":1,\"models\":[{\"cli\":\"codex\",\"value\":\"gpt-67\",\"label\":\"Codex 67\"}]}";
        public TaskCompletionSource? Pause;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal(ModelCatalogClient.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Empty(request.Headers);
            if (Pause is not null) await Pause.Task.WaitAsync(ct);
            return new(Status) { Content = new StringContent(Body) };
        }
    }

    [Fact]
    public async Task Memory_and_persistent_cache_limit_reads_across_restarts_and_retain_good_data_on_failure()
    {
        var stored = (string?)null;
        var global = new Mock<IGlobalCache>();
        global.Setup(g => g.GetAsync(ModelCatalogClient.CacheKey)).ReturnsAsync(() => stored);
        global.Setup(g => g.SetAsync(ModelCatalogClient.CacheKey, It.IsAny<string>()))
            .Callback<string, string>((_, json) => stored = json).Returns(Task.CompletedTask);
        using var services = new ServiceCollection().AddSingleton(global.Object).BuildServiceProvider();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var handler = new Handler();
        var clock = new Clock();
        var service = new ModelCatalogClient(handler, services.GetRequiredService<IServiceScopeFactory>(), memory, clock);
        Assert.Equal("gpt-67", Assert.Single((await service.GetAsync())!.Models).Value);
        await service.GetAsync();
        global.Verify(g => g.GetAsync(ModelCatalogClient.CacheKey), Times.Once);
        Assert.Equal(1, handler.Calls);
        memory.Remove(ModelCatalogClient.CacheKey);
        var restarted = new ModelCatalogClient(handler, services.GetRequiredService<IServiceScopeFactory>(), memory, clock);
        await restarted.GetAsync();
        Assert.Equal(1, handler.Calls);
        clock.Now += TimeSpan.FromHours(1);
        handler.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(1, (await restarted.GetAsync())!.Revision);
        await restarted.GetAsync();
        Assert.Equal(2, handler.Calls);
        clock.Now += TimeSpan.FromMinutes(5);
        handler.Status = HttpStatusCode.OK; handler.Body = "{\"version\":999,\"revision\":2,\"models\":[]}";
        Assert.Equal(1, (await restarted.GetAsync())!.Revision);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_request_and_bad_initial_data_returns_fallback()
    {
        using var handler = new Handler { Pause = new(TaskCreationOptions.RunContinuationsAsynchronously), Body = "{}" };
        var global = new Mock<IGlobalCache>();
        global.Setup(g => g.SetAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        using var services = new ServiceCollection().AddSingleton(global.Object).BuildServiceProvider();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = new ModelCatalogClient(handler, services.GetRequiredService<IServiceScopeFactory>(), memory, new Clock());
        using var cts = new CancellationTokenSource();
        var cancelled = service.GetAsync(cts.Token);
        var waiting = service.GetAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        handler.Pause.SetResult();
        Assert.Null(await waiting);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void New_model_names_do_not_require_local_catalog_membership_to_launch()
    {
        var args = BaseLlmOptionsBuilder.BuildArguments(LLM.Codex, new("gpt-67"));
        Assert.Equal(new[] { "--model", "gpt-67" }, args);
        Assert.False(ModelCatalogClient.Valid(new(1, 1, [new("codex", "--help", "bad")])));
    }
}
