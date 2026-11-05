using System.Text.Json;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.Services;

public sealed class LlmLaunchDefaultsTests
{
    [Fact]
    public async Task DefaultsPersistPerLlmIncludingSpeedAndYolo()
    {
        var values = new Dictionary<string, string>();
        var repository = new Mock<IRepository>();
        repository.Setup(x => x.GetGlobalCacheValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => values.GetValueOrDefault(key));
        repository.Setup(x => x.SetGlobalCacheValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((key, json, _) => values[key] = json)
            .Returns(Task.CompletedTask);
        var service = new LlmPickerPreferenceService(repository.Object);
        var codex = new BaseLlmOptions("gpt-6-astra", "xhigh", null, true, "ultrafast");
        var saved = await service.SaveLaunchDefaultsAsync("Codex", codex, TestContext.Current.CancellationToken);
        await service.SaveLaunchDefaultsAsync("claude", new("claude-fable-5-1[1m]", "high", "plan"), TestContext.Current.CancellationToken);

        var reopened = new LlmPickerPreferenceService(repository.Object);
        Assert.Equal(saved, await reopened.GetLaunchDefaultsAsync("codex", TestContext.Current.CancellationToken));
        Assert.Equal("ultrafast", saved.Speed);
        Assert.True(saved.Yolo);
        Assert.Equal("plan", (await reopened.GetLaunchDefaultsAsync("claude", TestContext.Current.CancellationToken)).Mode);
        Assert.Equal(2, values.Count);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("env:7:codex")]
    [InlineData("unknown")]
    [InlineData("999")]
    public async Task InvalidLlmCannotReadOrWriteDefaults(string cli)
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var service = new LlmPickerPreferenceService(repository.Object);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetLaunchDefaultsAsync(cli, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveLaunchDefaultsAsync(cli, new(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsupportedSpeedIsRejectedWithoutWriting()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var service = new LlmPickerPreferenceService(repository.Object);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveLaunchDefaultsAsync("claude",
            new(Speed: "ultrafast"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StaleOrMalformedStoredDefaultsFallBackWithoutChangingData()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository.SetupSequence(x => x.GetGlobalCacheValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("not json")
            .ReturnsAsync(JsonSerializer.Serialize(new BaseLlmOptions(Speed: "invalid")))
            .ReturnsAsync((string?)null);
        var service = new LlmPickerPreferenceService(repository.Object);
        for (var i = 0; i < 3; i++)
            Assert.Equal(new BaseLlmOptions(), await service.GetLaunchDefaultsAsync("codex", TestContext.Current.CancellationToken));
    }
}
