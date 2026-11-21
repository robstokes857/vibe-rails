using Moq;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.HttpRelay;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Integrations;

// ApiKeyStore and the settings route also update process-wide runtime settings.
[Collection("ProcessEnvIsolation")]
public sealed class AccountDisplayTests : IDisposable
{
    private readonly string originalApiKey = ParserConfigs.GetApiKey();
    private readonly bool originalRemoteAccess = ParserConfigs.GetRemoteAccess();
    private readonly bool originalUseVsCodeTheme = ParserConfigs.GetUseVsCodeTheme();
    private readonly bool originalRouteThroughVibeRailsAi = ParserConfigs.GetRouteThroughVibeRailsAi();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "viberails-account-tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsFile store;
    private readonly ApiKeyStore keys;

    public AccountDisplayTests()
    {
        store = new SettingsFile(Path.Combine(directory, "settings.json"));
        store.Save(new Settings { ComputerName = "Original computer" });
        keys = new ApiKeyStore(Mock.Of<IRemoteHttpRelayClient>(), store);
    }

    [Fact]
    public void ApprovedEmailSurvivesReloadAndUnrelatedSettingsSave()
    {
        Assert.True(keys.TrySave("approved-private-key", "", "rob@example.com"));
        using var reopened = new SettingsFile(Path.Combine(directory, "settings.json"));
        var dto = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Renamed"), reopened);
        Assert.Equal("rob@example.com", dto.RemoteAccountEmail);
        Assert.DoesNotContain("approved-private-key", dto.ApiKey);
        var saved = AppSettingsRoutes.UpdateSettings(dto with { ApiKey = "", RemoteAccountEmail = "forged@example.com" }, reopened);
        Assert.Equal("rob@example.com", saved.RemoteAccountEmail);
        Assert.Equal("approved-private-key", reopened.LoadFresh().ApiKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacingOrClearingKeyRemovesPreviousIdentity(bool clear)
    {
        Assert.True(keys.TrySave("approved-private-key", "", "rob@example.com"));
        var dto = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Original computer"), store);
        var saved = AppSettingsRoutes.UpdateSettings(dto with { ApiKey = "different-private-key", ClearApiKey = clear }, store);
        Assert.Null(saved.RemoteAccountEmail);
        Assert.Null(store.LoadFresh().RemoteAccountKeyFingerprint);
    }

    [Fact]
    public void OlderWriterChangingKeyWithSameSuffixCannotRetainIdentity()
    {
        Assert.True(keys.TrySave("approved-private-abcd", "", "rob@example.com"));
        var settings = store.LoadFresh();
        settings.ApiKey = "replaced-private-abcd";
        store.Save(settings);
        var dto = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Renamed"), store);
        Assert.Null(dto.RemoteAccountEmail);
    }

    [Fact]
    public void RejectedLinkCannotOverwriteIdentityAndSuccessfulSwitchReplacesIt()
    {
        Assert.True(keys.TrySave("first-private-key", "", "first@example.com"));
        Assert.False(keys.TrySave("second-private-key", "", "second@example.com"));
        Assert.Equal("first@example.com", ApiKeyStore.GetAccountEmail(store.LoadFresh()));
        Assert.True(keys.TrySave("second-private-key", "first-private-key", "second@example.com"));
        Assert.Equal("second@example.com", ApiKeyStore.GetAccountEmail(store.LoadFresh()));
    }

    // A retry after the file was written is the same sign-in. It used to compare the file with
    // the key from before sign-in and report a completed sign-in as "key changed".
    [Fact]
    public void RetryingASaveThatAlreadyReachedTheFileSucceeds()
    {
        Assert.True(keys.TrySave("approved-private-key", "", "rob@example.com"));
        var written = File.GetLastWriteTimeUtc(Path.Combine(directory, "settings.json"));

        Assert.True(keys.TrySave("approved-private-key", "", "rob@example.com"));

        Assert.Equal("approved-private-key", store.LoadFresh().ApiKey);
        Assert.Equal("rob@example.com", ApiKeyStore.GetAccountEmail(store.LoadFresh()));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(directory, "settings.json")));
        // An older writer saved the same key without its identity: the retry completes it.
        var settings = store.LoadFresh();
        settings.RemoteAccountEmail = null;
        settings.RemoteAccountKeyFingerprint = null;
        store.Save(settings);
        Assert.True(keys.TrySave("approved-private-key", "", "rob@example.com"));
        Assert.Equal("rob@example.com", ApiKeyStore.GetAccountEmail(store.LoadFresh()));
    }

    [Fact]
    public void AFailedRelayResetCannotFailASavedKey()
    {
        var relay = new Mock<IRemoteHttpRelayClient>();
        relay.Setup(r => r.Reset()).Throws(new InvalidOperationException("socket already disposed"));
        var failing = new ApiKeyStore(relay.Object, store);

        Assert.True(failing.TrySave("approved-private-key", "", "rob@example.com"));

        Assert.Equal("approved-private-key", store.LoadFresh().ApiKey);
        Assert.Equal("approved-private-key", ParserConfigs.GetApiKey());
    }

    // VIBE-213 item 17: the inbox asks on every poll; config.json is re-read only when the key changes.
    [Fact]
    public void CachedAccountEmailRereadsSettingsOnlyWhenTheKeyChanges()
    {
        Assert.True(keys.TrySave("first-private-key", "", "first@example.com"));
        var key = "first-private-key";
        var reads = 0;
        var email = ApiKeyStore.CachedAccountEmail(() => key, () => { reads++; return store.LoadFresh(); });

        Assert.Equal("first@example.com", email());
        Assert.Equal("first@example.com", email());
        Assert.Equal(1, reads);

        // A key the file does not hold yet is not cached: the next ask reads again.
        key = "second-private-key";
        Assert.Null(email());
        Assert.Null(email());
        Assert.Equal(3, reads);
        Assert.True(keys.TrySave("second-private-key", "first-private-key", "second@example.com"));
        Assert.Equal("second@example.com", email());
        Assert.Equal("second@example.com", email());
        Assert.Equal(4, reads);
    }

    public void Dispose()
    {
        ParserConfigs.SetApiKey(originalApiKey);
        ParserConfigs.SetRemoteAccess(originalRemoteAccess);
        ParserConfigs.SetUseVsCodeTheme(originalUseVsCodeTheme);
        ParserConfigs.SetRouteThroughVibeRailsAi(originalRouteThroughVibeRailsAi);
        store.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}
