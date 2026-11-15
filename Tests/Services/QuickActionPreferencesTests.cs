using Moq;
using VibeRails.DB;
using VibeRails.Services.Launches;
using Xunit;

namespace Tests.Services;

public sealed class QuickActionPreferencesTests
{
    [Fact]
    public async Task PerItemChoicesSurviveOtherClientsWithoutTouchingOtherSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new Dictionary<string, string> { ["unrelated"] = "private", ["ui.llm-picker.launches.v1"] = "unchanged" };
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetAllGlobalCacheAsync(ct)).ReturnsAsync(cache);
        repository.Setup(r => r.SetGlobalCacheValueAsync(It.IsAny<string>(), It.IsAny<string>(), ct))
            .Callback((string key, string value, CancellationToken _) => cache[key] = value).Returns(Task.CompletedTask);
        var first = new QuickActionPreferences(repository.Object);
        var second = new QuickActionPreferences(repository.Object);
        Assert.Empty((await first.GetAsync(ct)).HiddenKeys);
        await first.SaveAsync(new("preset:agent", true), ct);
        await second.SaveAsync(new("script:registration", true), ct);
        await first.SaveAsync(new("action:check", true), ct);
        Assert.Equal(["action:check", "preset:agent", "script:registration"], (await second.GetAsync(ct)).HiddenKeys);
        await second.SaveAsync(new("preset:agent", false), ct);
        Assert.Equal(["action:check", "script:registration"], (await first.GetAsync(ct)).HiddenKeys);
        Assert.Equal("unchanged", cache["ui.llm-picker.launches.v1"]);
        Assert.Equal("private", cache["unrelated"]);
        // Strict repository: no legacy environment or definition writes can slip in.
    }

    [Theory]
    [InlineData("")]
    [InlineData("preset:")]
    [InlineData("other:item")]
    [InlineData("preset:item\n")]
    [InlineData(null)]
    public async Task InvalidKeysNeverWrite(string? key)
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        await Assert.ThrowsAsync<ArgumentException>(() => new QuickActionPreferences(repository.Object)
            .SaveAsync(new(key!, true), TestContext.Current.CancellationToken));
        repository.VerifyNoOtherCalls();
    }
}
