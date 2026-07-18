using VibeRails.Services.LlmProxy;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class LlmProxySessionStateTests
{
    [Fact]
    public void OpenCodeActivation_RemainsActiveUntilEveryLeaseIsReleased()
    {
        var state = new LlmProxySessionState();

        var first = state.ActivateOpenCodeProxy();
        var second = state.ActivateOpenCodeProxy();
        Assert.True(state.OpenCodeProxyActive);

        first.Dispose();
        first.Dispose(); // Leases are idempotent; a double-dispose must not underflow the count.
        Assert.True(state.OpenCodeProxyActive);

        second.Dispose();
        Assert.False(state.OpenCodeProxyActive);
    }
}
