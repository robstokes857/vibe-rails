using System.Text;
using Moq;
using Pty.Net;
using VibeRails.Auth;
using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Environments.Steps;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using VibeRails.Services.Terminal.Consumers;
using VibeRails.Utils;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

public sealed class RemotePasswordAuthenticationTests
{
    [Theory]
    [InlineData("1234")]
    [InlineData("123456")]
    [InlineData("Summer7")]
    [InlineData("  Summer7  ")]
    [InlineData("秘密の鍵7")]
    [InlineData("x")]
    public async Task LegacyAndPasswordLoginSurviveBothReconnectsWithoutTypingCredentials(string credential)
    {
        await using var fixture = new Fixture(credential);
        fixture.Input(credential);
        Assert.Equal(TerminalControlProtocol.Unlocked, fixture.Controls.Last());
        Assert.NotEmpty(fixture.Output);
        Assert.Equal(1, fixture.Takeovers);

        fixture.Output.Clear();
        fixture.Relay.Raise(r => r.OnBrowserDisconnected += null);
        fixture.Input("wrong");
        Assert.Equal(TerminalControlProtocol.Locked, fixture.Controls.Last());
        Assert.Empty(fixture.Output);
        fixture.Input(credential);
        Assert.Equal(TerminalControlProtocol.Unlocked, fixture.Controls.Last());

        fixture.Output.Clear();
        fixture.Relay.Raise(r => r.OnReconnected += null);
        Assert.Equal(TerminalControlProtocol.Locked, fixture.Controls.Last());
        Assert.Empty(fixture.Output);
        fixture.Input(credential);
        Assert.Equal(TerminalControlProtocol.Unlocked, fixture.Controls.Last());
        Assert.NotEmpty(fixture.Output);

        // A late/repeated credential frame is consumed even after authorization.
        fixture.Input(credential);
        Assert.Equal(0, fixture.PtyInput.Length);
        Assert.Empty(fixture.Shutdowns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThirdFailureRequestsHostShutdownAfterBrowserAndRelayReconnects(bool relayFirst)
    {
        await using var fixture = new Fixture("Correct7");
        fixture.Input("Wrong1");
        fixture.Reconnect(relayFirst);
        fixture.Input("Wrong2");
        fixture.Reconnect(!relayFirst);
        Assert.Empty(fixture.Shutdowns);
        fixture.Input("Wrong3");

        Assert.Single(fixture.Shutdowns);
        Assert.StartsWith(TerminalControlProtocol.DisconnectBrowserCommand + ":", fixture.Controls.Last());
        Assert.Empty(fixture.Output);
        Assert.Equal(0, fixture.PtyInput.Length);
    }

    [Fact]
    public async Task OnlySuccessfulLoginResetsFailuresAndMissingCredentialDoesNotConsumeBudget()
    {
        await using var fixture = new Fixture("Correct7");
        fixture.Input("Wrong1");
        fixture.Input("Wrong2");
        fixture.Input("Correct7");
        fixture.Reconnect(true);
        fixture.Configured = false;
        for (var i = 0; i < 5; i++) fixture.Input("anything");
        fixture.Configured = true;
        fixture.Input("Wrong1");
        fixture.Reconnect(false);
        fixture.Input("Wrong2");
        Assert.Empty(fixture.Shutdowns);
        fixture.Input("Wrong3");
        Assert.Single(fixture.Shutdowns);
    }

    [Fact]
    public async Task BoundsMalformedUtf8AndExactWhitespaceUseTheSameFailureBudget()
    {
        await using var fixture = new Fixture("  Correct7  ");
        fixture.Input("Correct7"); // No trimming during verification.
        fixture.Input(new string('a', 129));
        var invalidUtf8 = Encoding.ASCII.GetBytes(TerminalControlProtocol.PinResponse).Concat(new byte[] { 0xff }).ToArray();
        fixture.Relay.Raise(r => r.OnInputReceived += null, invalidUtf8);
        Assert.Single(fixture.Shutdowns);
        Assert.Equal(1, fixture.Verifications); // Oversized and invalid encodings never reach PBKDF2.
        Assert.Equal(0, fixture.PtyInput.Length);
    }

    [Fact]
    public async Task MaximumUtf8CredentialAuthenticates()
    {
        var credential = new string('界', 128);
        await using var fixture = new Fixture(credential);
        fixture.Input(credential);
        Assert.Equal(TerminalControlProtocol.Unlocked, fixture.Controls.Last());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Mock<IRemoteTerminalConnection> Relay = new();
        internal readonly List<string> Controls = [];
        internal readonly List<string> Shutdowns = [];
        internal readonly List<byte[]> Output = [];
        internal readonly MemoryStream PtyInput = new();
        private readonly TerminalPty terminal;
        internal bool Configured = true;
        internal int Takeovers;
        internal int Verifications;

        internal Fixture(string credential)
        {
            var (salt, hash) = Hasher.Hash(credential);
            var pty = new Mock<IPtyConnection>();
            pty.SetupGet(p => p.WriterStream).Returns(PtyInput);
            terminal = new TerminalPty(pty.Object, 80, 24);
            Relay.SetupGet(r => r.IsConnected).Returns(true);
            Relay.Setup(r => r.SendControlAsync(It.IsAny<string>())).Callback<string>(Controls.Add).Returns(Task.CompletedTask);
            Relay.Setup(r => r.SendOutputAsync(It.IsAny<ReadOnlyMemory<byte>>()))
                .Callback<ReadOnlyMemory<byte>>(data => Output.Add(data.ToArray())).Returns(Task.CompletedTask);
            var runner = new TerminalRunner(Mock.Of<ITerminalStateService>(), Mock.Of<ICommandService>(),
                Mock.Of<ILocalToolApiContext>(), Mock.Of<ILlmProxySessionState>(), Mock.Of<IAutomationConsumer>(),
                Mock.Of<IRepository>(), Mock.Of<IEnvironmentStepRunner>(), Mock.Of<IAppEventBus>());
            runner.AttachRemoteViewer(terminal, Relay.Object, "fixture-session", "Fixture", _ =>
                { Takeovers++; return Task.CompletedTask; }, () => Configured,
                value => { Verifications++; return Hasher.Verify(value, salt, hash); }, Shutdowns.Add);
        }

        internal void Input(string credential) => Relay.Raise(r => r.OnInputReceived += null,
            Encoding.UTF8.GetBytes(TerminalControlProtocol.PinResponse + credential));

        internal void Reconnect(bool relay)
        {
            if (relay) Relay.Raise(r => r.OnReconnected += null);
            else Relay.Raise(r => r.OnBrowserDisconnected += null);
        }

        public async ValueTask DisposeAsync()
        {
            await terminal.DisposeAsync();
            PtyInput.Dispose();
        }
    }
}
