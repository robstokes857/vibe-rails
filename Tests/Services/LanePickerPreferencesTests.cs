using Moq;
using VibeRails.DB;
using VibeRails.Services.Launches;
using Xunit;

namespace Tests.Services;

/// <summary>VIBE-337: the Board lane picker's Manage list. Visibility only, one row per entry.</summary>
public sealed class LanePickerPreferencesTests
{
    [Fact]
    public async Task PerEntryChoicesSurviveOtherWindowsAndStaySeparateFromQuickActions()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new Dictionary<string, string>
        {
            ["unrelated"] = "private",
            [QuickActionPreferences.KeyPrefix + "preset:agent"] = "1"
        };
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetAllGlobalCacheAsync(ct)).ReturnsAsync(cache);
        repository.Setup(r => r.SetGlobalCacheValueAsync(It.IsAny<string>(), It.IsAny<string>(), ct))
            .Callback((string key, string value, CancellationToken _) => cache[key] = value).Returns(Task.CompletedTask);
        var first = new LanePickerPreferences(repository.Object);
        var second = new LanePickerPreferences(repository.Object);

        // A shortcut hidden from Quick actions is not hidden from the lane picker.
        Assert.Empty((await first.GetAsync(ct)).HiddenKeys);
        await first.SaveAsync(new("check:vca", true), ct);
        await second.SaveAsync(new("action:script", true), ct);
        await first.SaveAsync(new("preset:gone-since", true), ct);
        Assert.Equal(["action:script", "check:vca", "preset:gone-since"], (await second.GetAsync(ct)).HiddenKeys);
        await second.SaveAsync(new("check:vca", false), ct);
        Assert.Equal(["action:script", "preset:gone-since"], (await first.GetAsync(ct)).HiddenKeys);

        Assert.Equal("1", cache[LanePickerPreferences.KeyPrefix + "action:script"]);
        Assert.Equal("0", cache[LanePickerPreferences.KeyPrefix + "check:vca"]);
        Assert.Equal(["preset:agent"], (await new QuickActionPreferences(repository.Object).GetAsync(ct)).HiddenKeys);
        Assert.Equal("private", cache["unrelated"]);
        // Strict repository: no definition, lane or Quick actions write can slip in.
    }

    [Theory]
    [InlineData("")]
    [InlineData("check:")]
    [InlineData(":vca")]
    [InlineData("script:registration")] // Library scripts are Quick actions only; lanes run script Actions.
    [InlineData("check:vca\n")]
    [InlineData(null)]
    public async Task InvalidKeysNeverWrite(string? key)
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new LanePickerPreferences(repository.Object)
            .SaveAsync(new(key!, true), TestContext.Current.CancellationToken));
        Assert.Equal("Choose a valid lane picker entry.", error.Message);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OverlongKeysNeverWrite()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        await Assert.ThrowsAsync<ArgumentException>(() => new LanePickerPreferences(repository.Object)
            .SaveAsync(new("action:" + new string('x', 250), true), TestContext.Current.CancellationToken));
        repository.VerifyNoOtherCalls();
    }
}
