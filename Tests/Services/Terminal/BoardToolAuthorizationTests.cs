using System.Text.Json;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Environments.Steps;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

[Collection("ProcessEnvIsolation")]
public sealed class BoardToolAuthorizationTests
{
    [Fact]
    public void OrdinaryLaunchDefaultsToNoBoardToolAuthorization_IncludingAotJson()
    {
        Assert.False(new StartTerminalRequest(Cli: "Claude").AuthorizeBoardTools);
        var omitted = JsonSerializer.Deserialize("{\"cli\":\"claude\"}", AppJsonSerializerContext.Default.StartTerminalRequest);
        Assert.False(omitted!.AuthorizeBoardTools);
        var authorized = new StartTerminalRequest(Cli: "Claude", AuthorizeBoardTools: true);
        var serialized = JsonSerializer.Serialize(authorized, AppJsonSerializerContext.Default.StartTerminalRequest);
        Assert.True(JsonSerializer.Deserialize(serialized, AppJsonSerializerContext.Default.StartTerminalRequest)!.AuthorizeBoardTools);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionAndRunner_ForwardAuthorizationIntoCommandPreparation(bool authorized)
    {
        var state = new Mock<ITerminalStateService>();
        state.Setup(s => s.CreateSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync("board-authorization-session");
        var command = new Mock<ICommandService>(MockBehavior.Strict);
        var reachedPreparation = new ReachedPreparation();
        command.Setup(c => c.PrepareSessionAsync(LLM.Claude, "saved-environment", It.IsAny<string[]>(), "card prompt", "",
                "board-authorization-session", authorized))
            .ThrowsAsync(reachedPreparation);
        var runner = new TerminalRunner(state.Object, command.Object, Mock.Of<ILocalToolApiContext>(),
            Mock.Of<ILlmProxySessionState>(), Mock.Of<IAutomationConsumer>(), Mock.Of<IRepository>(),
            Mock.Of<IEnvironmentStepRunner>(), Mock.Of<IAppEventBus>());
        var session = new TerminalSessionService(state.Object, runner, Mock.Of<ILocalClientTracker>());

        // Stop at command preparation: this exercises both concrete forwarding layers without
        // spawning a PTY or making any CLI/configuration changes.
        var thrown = await Assert.ThrowsAsync<ReachedPreparation>(() => session.StartSessionAsync(LLM.Claude, Path.GetTempPath(),
            environmentName: "saved-environment", resolveInitialPrompt: () => Task.FromResult<string?>("card prompt"), authorizeBoardTools: authorized));
        Assert.Same(reachedPreparation, thrown);
        command.VerifyAll();
        state.Verify(s => s.CompleteSessionAsync("board-authorization-session", -1), Times.Once);
    }

    private sealed class ReachedPreparation : Exception;
}
