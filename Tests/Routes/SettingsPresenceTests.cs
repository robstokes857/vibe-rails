using VibeRails.Auth;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

public sealed class SettingsPresenceTests
{
    [Fact]
    public void FirstTimeConfirmationCodeIsRequiredAndSingleUse()
    {
        var service = new SettingsPresenceService();
        var settings = new Settings();
        Assert.False(service.Verify(null, settings));
        var challenge = service.Challenge(false);
        Assert.False(challenge.PinRequired);
        Assert.False(service.Verify("wrong", settings));
        Assert.True(service.Verify(challenge.Code, settings));
        Assert.False(service.Verify(challenge.Code, settings));
    }

    [Theory]
    [InlineData("1357")]
    [InlineData("123456")]
    [InlineData("  Secret7  ")]
    public void ConfiguredCredentialCannotBeReplacedByAConfirmationCodeAndGuessingIsThrottled(string credential)
    {
        var service = new SettingsPresenceService();
        var code = service.Challenge(false).Code;
        var (salt, hash) = Hasher.Hash(credential);
        var settings = new Settings { PinSalt = salt, PinHash = hash };
        Assert.Null(service.Challenge(true).Code);
        Assert.False(service.Verify(code, settings));
        Assert.True(service.Verify(credential, settings));
        for (var i = 0; i < 5; i++) Assert.False(service.Verify("0000", settings));
        service.Challenge(true);
        Assert.False(service.Verify(credential, settings));
    }
}
