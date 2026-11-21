using Moq;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.HttpRelay;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Integrations;

// Signing in, switching accounts and removing the key each happen in one root backend. These cover
// every other VibeRails process: a second window and the tab children read the key once at startup.
[Collection("ProcessEnvIsolation")]
public sealed class SavedAccountFollowerTests : IDisposable
{
    private readonly string originalApiKey = ParserConfigs.GetApiKey();
    private readonly bool originalRoute = ParserConfigs.GetRouteThroughVibeRailsAi();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "viberails-account-follower-tests", Guid.NewGuid().ToString("N"));
    private readonly string path;
    private readonly SettingsFile store;
    private readonly Mock<IRemoteHttpRelayClient> relay = new();
    private readonly AppEventBus bus = new();
    private readonly List<AppEvent> events = [];
    private readonly IDisposable subscription;

    public SavedAccountFollowerTests()
    {
        path = Path.Combine(directory, "settings.json");
        store = new SettingsFile(path);
        subscription = bus.Subscribe(events.Add);
    }

    // What startup does: read the file, use its key, remember which file that was.
    private SavedAccountFollower Started(string apiKey, bool isRoot = true)
    {
        store.Save(new Settings { ApiKey = apiKey });
        ParserConfigs.SetApiKey(apiKey);
        var applied = store.Stamp();
        Assert.NotNull(applied);
        return new SavedAccountFollower(store, () => applied, relay.Object, isRoot ? bus : null);
    }

    // Another VibeRails process has its own SettingsFile over the same path.
    private void SaveFromAnotherProcess(Action<Settings> change)
    {
        using var other = new SettingsFile(path);
        using var lease = other.AcquireWriteLock();
        var settings = other.LoadFresh();
        change(settings);
        other.Save(settings);
    }

    [Fact]
    public void AKeySavedInAnotherWindowBecomesThisProcessKeyAndRefreshesItsDashboard()
    {
        using var follower = Started("first-account-key");
        Assert.False(follower.Check());

        SaveFromAnotherProcess(s => s.ApiKey = "second-account-key-from-another-window");

        Assert.True(follower.Check());
        Assert.Equal("second-account-key-from-another-window", ParserConfigs.GetApiKey());
        var changed = Assert.Single(events);
        Assert.Equal(SavedAccountFollower.ChangedEvent, changed.Type);
        // The event is a prompt to read the saved account again; it carries no credential.
        Assert.Equal("{}", changed.Payload.GetRawText());
        relay.Verify(r => r.Reset(), Times.Once);
        Assert.False(follower.Check());
        Assert.Single(events);
    }

    [Fact]
    public void SigningOutInAnotherWindowStopsThisProcessUsingTheKey()
    {
        using var follower = Started("signed-in-account-key");
        ParserConfigs.SetRouteThroughVibeRailsAi(true);

        SaveFromAnotherProcess(s =>
        {
            s.ApiKey = "";
            // An older writer could leave the relay switch on; without a key it must not apply.
            s.RouteThroughVibeRailsAi = true;
        });

        Assert.True(follower.Check());
        Assert.Equal("", ParserConfigs.GetApiKey());
        Assert.False(ParserConfigs.GetRouteThroughVibeRailsAi());
        Assert.Single(events);
    }

    [Fact]
    public void ASignInAfterSigningOutIsFollowedToo()
    {
        using var follower = Started("");

        SaveFromAnotherProcess(s => s.ApiKey = "key-from-signing-in-again");

        Assert.True(follower.Check());
        Assert.Equal("key-from-signing-in-again", ParserConfigs.GetApiKey());
    }

    [Fact]
    public void ATabChildFollowsTheKeyWithoutADashboardToTell()
    {
        using var follower = Started("first-account-key", isRoot: false);

        SaveFromAnotherProcess(s => s.ApiKey = "second-account-key-from-the-root");

        Assert.True(follower.Check());
        Assert.Equal("second-account-key-from-the-root", ParserConfigs.GetApiKey());
        Assert.Empty(events);
    }

    // The settings route and sign-in activate their own key under the settings lock and publish
    // their own event. An unrelated settings save changes the file but not the account.
    [Fact]
    public void AChangeThisProcessAlreadyAppliedIsNotReportedAgain()
    {
        using var follower = Started("first-account-key");

        SaveFromAnotherProcess(s => s.ApiKey = "key-this-process-saved-itself");
        ParserConfigs.SetApiKey("key-this-process-saved-itself");
        Assert.False(follower.Check());

        SaveFromAnotherProcess(s => s.ComputerName = "Renamed elsewhere");
        Assert.False(follower.Check());

        Assert.Empty(events);
        relay.Verify(r => r.Reset(), Times.Never);
        Assert.Equal("key-this-process-saved-itself", ParserConfigs.GetApiKey());
    }

    // A host composed without startup never took its key from the settings file, so it must not
    // start taking one: unit tests run beside the developer's real settings.
    [Fact]
    public void WithoutStartupTheSettingsFileIsNeverApplied()
    {
        store.Save(new Settings { ApiKey = "saved-account-key" });
        ParserConfigs.SetApiKey("key-set-by-the-host");
        using var follower = new SavedAccountFollower(store, () => null, relay.Object, bus);

        SaveFromAnotherProcess(s => s.ApiKey = "another-saved-account-key");

        Assert.False(follower.Check());
        Assert.Equal("key-set-by-the-host", ParserConfigs.GetApiKey());
        Assert.Empty(events);
    }

    [Fact]
    public void ABrokenRelayOrSubscriberCannotUndoTheChange()
    {
        relay.Setup(r => r.Reset()).Throws(new InvalidOperationException("socket already disposed"));
        using var broken = bus.Subscribe(_ => throw new InvalidOperationException("Broken subscriber"));
        using var follower = Started("first-account-key");

        SaveFromAnotherProcess(s => s.ApiKey = "second-account-key-from-another-window");

        Assert.True(follower.Check());
        Assert.Equal("second-account-key-from-another-window", ParserConfigs.GetApiKey());
        Assert.False(follower.Check());
    }

    public void Dispose()
    {
        subscription.Dispose();
        ParserConfigs.SetApiKey(originalApiKey);
        ParserConfigs.SetRouteThroughVibeRailsAi(originalRoute);
        store.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}
