using Moq;
using VibeRails.Auth;
using VibeRails.Services.AgentTools;
using VibeRails.Services.LlmProxy;
using Xunit;

namespace Tests.Services.LlmProxy;

[Collection("ProcessEnvIsolation")]
public sealed class LocalLlmProxyContextTests
{
    [Fact]
    public void UsesCurrentProcessEndpointAndCredentials_WhenToolContextPointsAtParent()
    {
        var originalToolApiBase = Environment.GetEnvironmentVariable(
            LocalToolApiContext.ApiBaseUrlVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                LocalToolApiContext.ApiBaseUrlVariable,
                "http://127.0.0.1:4100");

            var auth = new Mock<IAuthService>();
            auth.Setup(x => x.GetInstanceToken()).Returns("child-session-token");
            auth.Setup(x => x.GetTabToken()).Returns("child-tab-token");

            var context = new LocalLlmProxyContext("http://127.0.0.1:4200/", auth.Object);

            Assert.Equal("http://127.0.0.1:4200", context.ApiBaseUrl);
            Assert.Equal("child-session-token", context.SessionToken);
            Assert.Equal("child-tab-token", context.TabToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                LocalToolApiContext.ApiBaseUrlVariable,
                originalToolApiBase);
        }
    }
}
