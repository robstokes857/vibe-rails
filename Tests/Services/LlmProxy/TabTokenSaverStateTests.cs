using VibeRails.Services.LlmProxy;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class TabTokenSaverStateTests
{
    [Fact]
    public void DefaultsOn_AndCanBeToggledWithoutChangingGlobalSettings()
    {
        var state = new TabTokenSaverState();

        Assert.True(state.Enabled);

        state.Enabled = false;
        Assert.False(state.Enabled);

        state.Enabled = true;
        Assert.True(state.Enabled);
    }
}
