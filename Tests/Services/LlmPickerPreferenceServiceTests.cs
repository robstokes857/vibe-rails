using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services;
using Xunit;
namespace Tests.Services;

public sealed class LlmPickerPreferenceServiceTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private static LaunchPreset Preset(string id, string? prompt = "task", bool enabled = true) =>
        new(id, id, "", LLM.Codex, new(), [], prompt, enabled, 1, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public async Task GlobalPresetsAndBaseClisNeverReadLegacyEnvironments()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetGlobalCacheValueAsync(LlmPickerPreferenceService.CacheKey, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var store = new Mock<ILaunchStore>(MockBehavior.Strict);
        store.Setup(s => s.ListPresetsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Preset("agent"), Preset("quick", ""), Preset("disabled", enabled: false)]);
        var items = (await new LlmPickerPreferenceService(repository.Object, store.Object).GetAsync(Ct)).Items;
        Assert.Equal(11, items.Count(i => i.Kind == "base"));
        Assert.Contains(items, i => i.Key == "preset:agent:codex" && i.Kind == "agent" && i.Enabled);
        Assert.Contains(items, i => i.Key == "preset:quick:codex" && i.Kind == "quick-launch");
        Assert.Contains(items, i => i.Key == "preset:disabled:codex" && !i.Enabled);
        Assert.DoesNotContain(items, i => i.Kind == "environment");
    }

    [Fact]
    public async Task VisibilityAndOrderRoundTripWithoutMutatingDefinitions()
    {
        string? json = null;
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetGlobalCacheValueAsync(LlmPickerPreferenceService.CacheKey, It.IsAny<CancellationToken>())).ReturnsAsync(() => json);
        repository.Setup(r => r.SetGlobalCacheValueAsync(LlmPickerPreferenceService.CacheKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string value, CancellationToken _) => json = value).Returns(Task.CompletedTask);
        var store = new Mock<ILaunchStore>(MockBehavior.Strict);
        store.Setup(s => s.ListPresetsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Preset("a"), Preset("b")]);
        var service = new LlmPickerPreferenceService(repository.Object, store.Object);
        var first = (await service.GetAsync(Ct)).Items;
        var update = first.Select(i => i.Kind == "base" ? i : i with { Order = i.Key.Contains(":b:") ? 0 : 1, Enabled = i.Key.Contains(":b:") }).ToList();
        await service.SaveAsync(new(update), Ct);
        var loaded = (await service.GetAsync(Ct)).Items.Where(i => i.Kind != "base").ToArray();
        Assert.Equal("preset:b:codex", loaded[0].Key);
        Assert.False(loaded[1].Enabled);
        await service.ResetAsync(Ct);
        Assert.All((await service.GetAsync(Ct)).Items, i => Assert.True(i.Enabled));
        store.Verify(s => s.ListPresetsAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task InvalidSnapshotsCannotDropOrInventSelections()
    {
        var repository = new Mock<IRepository>();
        var service = new LlmPickerPreferenceService(repository.Object);
        var items = (await service.GetAsync(Ct)).Items;
        await Assert.ThrowsAsync<LlmPickerPreferenceValidationException>(() => service.SaveAsync(new([]), Ct));
        var duplicate = items.ToList(); duplicate[1] = duplicate[0];
        await Assert.ThrowsAsync<LlmPickerPreferenceValidationException>(() => service.SaveAsync(new(duplicate), Ct));
        var unknown = items.ToList(); unknown[0] = unknown[0] with { Key = "env:42:codex" };
        await Assert.ThrowsAsync<LlmPickerPreferenceValidationException>(() => service.SaveAsync(new(unknown), Ct));
        repository.Verify(r => r.SaveLlmPickerStateAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<int, bool>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
