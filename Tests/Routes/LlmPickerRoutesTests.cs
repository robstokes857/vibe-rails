using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services;
using Xunit;

namespace Tests.Routes;

public sealed class LlmPickerRoutesTests
{
    // One client for the whole class — per-test HttpClients leave TIME_WAIT sockets behind.
    private static readonly HttpClient SharedClient = new();

    [Fact]
    public async Task Get_ReturnsResolvedCatalog()
    {
        var expected = new LlmPickerPreferencesResponse([
            new("base:claude", "base", "Base CLIs", "Claude", "claude", null, true, 0)
        ]);
        var service = new Mock<ILlmPickerPreferenceService>();
        service.Setup(item => item.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        await WithHostAsync(service.Object, async baseUrl =>
        {
            using var response = await SharedClient.GetAsync(
                $"{baseUrl}/api/v1/llm-picker/preferences",
                TestContext.Current.CancellationToken);
            var body = await response.Content.ReadFromJsonAsync<LlmPickerPreferencesResponse>(
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("base:claude", Assert.Single(body!.Items).Key);
        });
    }

    [Fact]
    public async Task Put_MapsValidationFailuresToBadRequest()
    {
        var service = new Mock<ILlmPickerPreferenceService>();
        service.Setup(item => item.SaveAsync(
                It.IsAny<UpdateLlmPickerPreferencesRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmPickerPreferenceValidationException("Duplicate picker key."));

        await WithHostAsync(service.Object, async baseUrl =>
        {
            using var response = await SharedClient.PutAsJsonAsync(
                $"{baseUrl}/api/v1/llm-picker/preferences",
                new UpdateLlmPickerPreferencesRequest([]),
                TestContext.Current.CancellationToken);
            var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Duplicate picker key.", body!.Error);
        });
    }

    [Fact]
    public async Task LaunchDefaults_RoundTripSpeedAndMapValidationFailure()
    {
        var options = new BaseLlmOptions("gpt-6-astra", "high", null, true, "ultrafast");
        var service = new Mock<ILlmPickerPreferenceService>();
        service.Setup(x => x.GetLaunchDefaultsAsync("codex", It.IsAny<CancellationToken>())).ReturnsAsync(options);
        service.Setup(x => x.SaveLaunchDefaultsAsync("codex", options, It.IsAny<CancellationToken>())).ReturnsAsync(options);
        service.Setup(x => x.SaveLaunchDefaultsAsync("shell", It.IsAny<BaseLlmOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Choose a base LLM."));
        await WithHostAsync(service.Object, async baseUrl =>
        {
            var loaded = await SharedClient.GetFromJsonAsync<BaseLlmOptions>($"{baseUrl}/api/v1/llm-picker/defaults/codex", TestContext.Current.CancellationToken);
            Assert.Equal(options, loaded);
            using var saved = await SharedClient.PutAsJsonAsync($"{baseUrl}/api/v1/llm-picker/defaults/codex", options, TestContext.Current.CancellationToken);
            Assert.Equal(options, await saved.Content.ReadFromJsonAsync<BaseLlmOptions>(TestContext.Current.CancellationToken));
            using var invalid = await SharedClient.PutAsJsonAsync($"{baseUrl}/api/v1/llm-picker/defaults/shell", options, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        });
    }

    [Fact]
    public async Task LaunchDefaults_AreUnavailableOutsideRootBackend()
    {
        await WithHostAsync(new Mock<ILlmPickerPreferenceService>(MockBehavior.Strict).Object, async baseUrl =>
        {
            using var get = await SharedClient.GetAsync($"{baseUrl}/api/v1/llm-picker/defaults/codex", TestContext.Current.CancellationToken);
            using var put = await SharedClient.PutAsJsonAsync($"{baseUrl}/api/v1/llm-picker/defaults/codex", new BaseLlmOptions(), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        }, isActiveRootBackend: false);
    }

    private static async Task WithHostAsync(
        ILlmPickerPreferenceService service,
        Func<string, Task> test, bool isActiveRootBackend = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(service);
        await using var app = builder.Build();
        LlmPickerRoutes.Map(app, isActiveRootBackend);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await test(app.Urls.First());
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }
}
