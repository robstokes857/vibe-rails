using Moq;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.HttpRelay;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Integrations;

public sealed class AccountDisplayTests : IDisposable
{
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

    public void Dispose()
    {
        store.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}
