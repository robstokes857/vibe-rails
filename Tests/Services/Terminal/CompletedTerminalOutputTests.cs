using System.Reflection;
using System.Text;
using Moq;
using Pty.Net;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

[Collection("ProcessEnvIsolation")]
public sealed class CompletedTerminalOutputTests
{
    [Fact]
    public async Task FinalOutputSurvivesPtyDisposalAndStopsBeingAvailableWhenTheHostIsReused()
    {
        var pty = new Mock<IPtyConnection>();
        await using var terminal = new TerminalPty(pty.Object, 40, 5);
        var emulator = Emulator(terminal);
        emulator.Write(string.Join("\r\n", Enumerable.Range(0, 30).Select(i => $"review line {i}")));
        var service = new TerminalSessionService(Mock.Of<ITerminalStateService>(), null!, Mock.Of<ILocalClientTracker>());
        service.RegisterExternalTerminal(terminal, "completed", "/project");
        // Model the owned browser PTY without launching a shell or touching application state.
        typeof(TerminalSessionService).GetField("s_externallyOwned", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, false);
        try
        {
            await service.StopSessionAsync();
            Assert.False(service.HasActiveSession);
            Assert.Null(service.ActiveSessionId);
            pty.Verify(value => value.Dispose(), Times.Once);
            var snapshot = await service.CaptureSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(snapshot);
            Assert.Equal("completed", snapshot.SessionId);
            Assert.True(snapshot.XtermUiBytes!.IncludesScrollback);
            var output = Encoding.UTF8.GetString(Convert.FromBase64String(snapshot.XtermUiBytes.Base64));
            Assert.Contains("review line 0", output);
            Assert.Contains("review line 29", output);
            Assert.False((await service.SendInputAsync(new("ignored"), TestContext.Current.CancellationToken)).Success);

            await using var next = new TerminalPty(Mock.Of<IPtyConnection>(), 40, 5);
            service.RegisterExternalTerminal(next, "next", "/project");
            Assert.Equal("next", (await service.CaptureSnapshotAsync(TestContext.Current.CancellationToken))!.SessionId);
            await service.UnregisterTerminalAsync();
            Assert.Null(await service.CaptureSnapshotAsync(TestContext.Current.CancellationToken));
        }
        finally { await service.UnregisterTerminalAsync(); }
    }

    [Fact]
    public async Task CompletedAlternateScreenBecomesScrollableWithoutChangingTheLiveEmulator()
    {
        await using var terminal = new TerminalPty(Mock.Of<IPtyConnection>(), 40, 5);
        var emulator = Emulator(terminal);
        emulator.Write("\x1b[?1049h\x1b[?1000;1006;2004hReview complete");
        var completed = terminal.CaptureCompletedSnapshotData();
        var viewer = new TerminalEmulator.Terminal(cols: 40, rows: 5, scrollbackSize: 100);
        viewer.Write(completed.XtermReplayBytes.AsSpan());
        Assert.False(viewer.IsAlternateScreen);
        Assert.False(viewer.BracketedPasteActive);
        Assert.Empty(viewer.GetInputReportingModes());
        Assert.False(viewer.CursorVisible);
        Assert.Contains("Review complete", viewer.GetScreenText()[0]);
        Assert.True(emulator.IsAlternateScreen);
        Assert.True(emulator.BracketedPasteActive);
        var live = new TerminalEmulator.Terminal(cols: 40, rows: 5, scrollbackSize: 100);
        live.Write(terminal.CaptureSnapshotData().XtermReplayBytes.AsSpan());
        Assert.True(live.IsAlternateScreen);
    }

    private static TerminalEmulator.Terminal Emulator(TerminalPty terminal) =>
        (TerminalEmulator.Terminal)typeof(TerminalPty).GetField("_emulator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(terminal)!;
}
