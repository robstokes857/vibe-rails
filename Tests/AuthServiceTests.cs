using VibeRails.Auth;
using Xunit;

namespace Tests;

public sealed class AuthServiceTests
{
    [Fact]
    public void TryGetUnconsumedBootstrapCodeExpiryUtc_ReturnsFalseBeforeCodeIsGenerated()
    {
        var authService = new AuthService();

        var hasCode = authService.TryGetUnconsumedBootstrapCodeExpiryUtc(out var expiryUtc);

        Assert.False(hasCode);
        Assert.Equal(default, expiryUtc);
    }

    [Fact]
    public void TryGetUnconsumedBootstrapCodeExpiryUtc_ReturnsExpiryAfterCodeIsGenerated()
    {
        var authService = new AuthService();
        var beforeGenerateUtc = DateTime.UtcNow;

        authService.GenerateBootstrapCode();

        var hasCode = authService.TryGetUnconsumedBootstrapCodeExpiryUtc(out var expiryUtc);

        Assert.True(hasCode);
        Assert.InRange((expiryUtc - beforeGenerateUtc).TotalSeconds, 119, 121);
    }

    [Fact]
    public void ValidateAndConsumeBootstrapCode_ClearsUnconsumedBootstrapCodeState()
    {
        var authService = new AuthService();
        var code = authService.GenerateBootstrapCode();

        var consumed = authService.ValidateAndConsumeBootstrapCode(code);
        var hasUnconsumedCode = authService.TryGetUnconsumedBootstrapCodeExpiryUtc(out _);

        Assert.True(consumed);
        Assert.False(hasUnconsumedCode);
    }

    [Fact]
    public void TryExpireUnconsumedBootstrapCode_BlocksSubsequentConsume()
    {
        var authService = new AuthService();
        var code = authService.GenerateBootstrapCode();
        Assert.True(authService.TryGetUnconsumedBootstrapCodeExpiryUtc(out var expiryUtc));

        var expired = authService.TryExpireUnconsumedBootstrapCode(expiryUtc);
        var consumedAfterExpire = authService.ValidateAndConsumeBootstrapCode(code);

        Assert.True(expired);
        Assert.False(consumedAfterExpire);
    }

    [Fact]
    public void TryExpireUnconsumedBootstrapCode_ReturnsFalseAfterConsume()
    {
        var authService = new AuthService();
        var code = authService.GenerateBootstrapCode();
        Assert.True(authService.TryGetUnconsumedBootstrapCodeExpiryUtc(out var expiryUtc));

        var consumed = authService.ValidateAndConsumeBootstrapCode(code);
        var expired = authService.TryExpireUnconsumedBootstrapCode(expiryUtc);

        Assert.True(consumed);
        Assert.False(expired);
    }

    [Fact]
    public void TryExpireUnconsumedBootstrapCode_IgnoresMismatchedExpiry()
    {
        // Stale-watcher scenario: a re-generation produced a fresh expiry, and the
        // prior watcher fires with the old timestamp. The guard must reject it so the
        // current (still-valid) code is not collateral-damaged.
        var authService = new AuthService();
        authService.GenerateBootstrapCode();
        Assert.True(authService.TryGetUnconsumedBootstrapCodeExpiryUtc(out var currentExpiry));

        var staleExpiry = currentExpiry.AddMilliseconds(-1);
        var expired = authService.TryExpireUnconsumedBootstrapCode(staleExpiry);
        var stillUnconsumed = authService.TryGetUnconsumedBootstrapCodeExpiryUtc(out _);

        Assert.False(expired);
        Assert.True(stillUnconsumed);
    }
}
