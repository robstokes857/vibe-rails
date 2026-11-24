using System.Text;
using System.Text.RegularExpressions;
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
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

/// <summary>
/// VIBE-172. A remote viewer that takes its grid from snapshots learned the PTY's size only on a
/// replay or an unlock, so after the desktop resized the PTY it wrapped or clamped everything
/// drawn for the new grid. A viewer that asks (<c>__cmd__:resize-snapshots</c>) is now sent the
/// screen, without history, once a resize it did not make has settled. A viewer that does not ask
/// gets nothing new: the snapshot prologue would clear its scrollback.
/// </summary>
public sealed class RemoteViewerResizeSnapshotTests
{
    private const string Prologue = "\u001b[?1049;1047;47l";
    private static readonly TimeSpan Settled = TerminalRunner.RemoteResizeSnapshotDelay + TimeSpan.FromMilliseconds(400);

    [Theory]
    [InlineData(PtyConsoleHost.Inbox, TerminalIoSource.LocalWebUi)]
    [InlineData(PtyConsoleHost.Inbox, TerminalIoSource.LocalCli)]
    [InlineData(PtyConsoleHost.Bundled, TerminalIoSource.LocalWebUi)]
    [InlineData(PtyConsoleHost.Bundled, TerminalIoSource.LocalCli)]
    public async Task AViewerThatAskedIsSentTheScreenAtTheNewSizeWithoutHistory(PtyConsoleHost host, TerminalIoSource source)
    {
        await using var fixture = new Fixture(host);
        fixture.Publish(string.Concat(Enumerable.Range(1, 60).Select(i => $"hist-{i:D2}\r\n")) + "$ ");
        var unlock = fixture.Unlock();
        Assert.Equal((80, 24), Grid(unlock));
        AssertShows(unlock, "hist-01");
        fixture.AskForResizeSnapshots();

        fixture.Resize(120, 40, source);

        var screen = One(await fixture.SnapshotsAsync());
        Assert.Equal((120, 40), Grid(screen));
        // The screen only: what scrolled into history is not sent again after every resize.
        AssertShows(screen, "hist-01", expected: false);
        AssertShows(screen, "hist-60");
    }

    [Fact]
    public async Task ABurstOfResizesSendsOneScreenAtTheSizeItEndsAt()
    {
        await using var fixture = new Fixture();
        fixture.Unlock();
        fixture.AskForResizeSnapshots();

        // A dragged window: each resize lands before the previous one has settled.
        foreach (var (cols, rows) in new[] { (90, 26), (100, 30), (110, 34), (120, 40) })
        {
            fixture.Resize(cols, rows);
            await Task.Delay(60);
        }

        var screen = One(await fixture.SnapshotsAsync());
        Assert.Equal((120, 40), Grid(screen));
    }

    [Fact]
    public async Task ABurstThatEndsAtTheViewersGridSendsNothing()
    {
        await using var fixture = new Fixture();
        fixture.Unlock();
        fixture.AskForResizeSnapshots();

        fixture.Resize(120, 40);
        fixture.Resize(80, 24);

        None(await fixture.SnapshotsAfterAsync(Settled));
    }

    [Fact]
    public async Task TheRemoteViewersOwnResizeSendsNothing()
    {
        await using var fixture = new Fixture();
        fixture.Unlock();
        fixture.AskForResizeSnapshots();

        // No local viewer is attached, so the remote resize applies to the PTY.
        fixture.Relay.Raise(r => r.OnResizeRequested += null, 120, 40);

        None(await fixture.SnapshotsAfterAsync(Settled));
        Assert.Equal((120, 40), (fixture.Terminal.Cols, fixture.Terminal.Rows));
    }

    [Fact]
    public async Task AViewerThatDoesNotAskIsSentNothing()
    {
        // The xterm viewer fits its own container and keeps its own scrollback.
        await using var fixture = new Fixture();
        fixture.Unlock();

        fixture.Resize(120, 40);

        None(await fixture.SnapshotsAfterAsync(Settled));
    }

    [Fact]
    public async Task ALockedViewerCannotAskAndIsSentNothing()
    {
        await using var fixture = new Fixture();
        fixture.AskForResizeSnapshots();
        Assert.Equal(TerminalControlProtocol.Locked, fixture.LastControl);

        fixture.Resize(120, 40);

        None(await fixture.SnapshotsAfterAsync(Settled));
    }

    [Fact]
    public async Task ARequestWithAPayloadIsIgnoredAndNoRequestIsRecordedAsACommand()
    {
        await using var fixture = new Fixture();
        fixture.Unlock();
        fixture.Relay.Raise(r => r.OnCommandReceived += null, TerminalControlProtocol.ResizeSnapshotsCommandName, "");
        fixture.Resize(120, 40);
        None(await fixture.SnapshotsAfterAsync(Settled));

        fixture.AskForResizeSnapshots();
        // A malformed copy after a valid request does not cancel it.
        fixture.Relay.Raise(r => r.OnCommandReceived += null, TerminalControlProtocol.ResizeSnapshotsCommandName, "100x30");

        Assert.Equal((120, 40), Grid(One(await fixture.SnapshotsAsync())));
        // Viewer protocol, not something the user did: no frame reached the session's command hooks.
        fixture.State.Verify(s => s.RecordRemoteCommand(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<TerminalIoSource>()), Times.Never);
    }

    public enum ViewerChange { Replay, BrowserDisconnected, RelayReconnected }

    [Theory]
    [InlineData(ViewerChange.Replay)]
    [InlineData(ViewerChange.BrowserDisconnected)]
    [InlineData(ViewerChange.RelayReconnected)]
    public async Task ANewViewerMustAskAgain(ViewerChange change)
    {
        await using var fixture = new Fixture();
        fixture.Unlock();
        fixture.AskForResizeSnapshots();

        // A takeover (by the xterm viewer, say) arrives as a replay request; the others re-lock.
        if (change == ViewerChange.Replay)
        {
            fixture.Relay.Raise(r => r.OnReplayRequested += null);
            Assert.Equal((80, 24), Grid(One(await fixture.SnapshotsAsync())));
        }
        else
        {
            if (change == ViewerChange.BrowserDisconnected)
                fixture.Relay.Raise(r => r.OnBrowserDisconnected += null);
            else
                fixture.Relay.Raise(r => r.OnReconnected += null);
            fixture.Unlock();
        }

        fixture.Resize(120, 40);
        None(await fixture.SnapshotsAfterAsync(Settled));

        fixture.Resize(100, 30);
        fixture.AskForResizeSnapshots();
        Assert.Equal((100, 30), Grid(One(await fixture.SnapshotsAsync())));
    }

    [Fact]
    public async Task AResizeBeforeTheViewerAsksIsSentWhenItAsks()
    {
        await using var fixture = new Fixture();
        fixture.Unlock();
        fixture.Resize(120, 40);
        None(await fixture.SnapshotsAfterAsync(Settled));

        // The viewer's request crossed the resize: its last snapshot was 80x24.
        fixture.AskForResizeSnapshots();

        Assert.Equal((120, 40), Grid(One(await fixture.SnapshotsAsync())));
    }

    [Fact]
    public async Task AResizeDeferredForASynchronizedFrameIsSentOnlyOnceApplied()
    {
        await using var fixture = new Fixture();
        fixture.Unlock();
        fixture.AskForResizeSnapshots();
        fixture.Publish("\u001b[?2026h");

        fixture.Resize(120, 40);
        Assert.Equal((80, 24), (fixture.Terminal.Cols, fixture.Terminal.Rows));
        fixture.Publish("frame\u001b[?2026l");

        var screen = One(await fixture.SnapshotsAsync());
        Assert.Equal((120, 40), Grid(screen));
        AssertShows(screen, "frame");
    }

    [Fact]
    public async Task TheScreenWaitsForTheRepaintFrameAfterAResize()
    {
        await using var fixture = new Fixture();
        fixture.Unlock();
        fixture.AskForResizeSnapshots();

        fixture.Resize(120, 40);
        // The program answers the resize with a repaint inside a synchronized-output frame.
        fixture.Publish("\u001b[?2026h\u001b[2J\u001b[Hrepaint-start");
        None(await fixture.SnapshotsAfterAsync(TerminalRunner.RemoteResizeSnapshotDelay + TimeSpan.FromMilliseconds(60)));
        fixture.Publish(" repaint-end\u001b[?2026l");

        var screen = One(await fixture.SnapshotsAsync());
        AssertShows(screen, "repaint-start repaint-end");
    }

    /// <summary>
    /// String assertions here never echo a snapshot: <c>dotnet test</c> (the VSTest adapter) drops
    /// the result of a test whose failure message carries raw ESC bytes, so a failure would vanish
    /// from the run instead of failing it. Seen while mutation-testing this class.
    /// </summary>
    private static void AssertShows(string snapshot, string text, bool expected = true) =>
        Assert.True(snapshot.Contains(text, StringComparison.Ordinal) == expected,
            $"expected {(expected ? "" : "no ")}\"{text}\" in {Printable(snapshot)}");

    private static string One(List<string> snapshots)
    {
        Assert.True(snapshots.Count == 1, $"expected one snapshot, got {snapshots.Count}: {string.Join(" | ", snapshots.Select(Printable))}");
        return snapshots[0];
    }

    private static void None(List<string> snapshots) =>
        Assert.True(snapshots.Count == 0, $"expected no snapshot, got {snapshots.Count}: {string.Join(" | ", snapshots.Select(Printable))}");

    private static string Printable(string output)
    {
        var printable = Regex.Replace(output, @"[\u0000-\u001f\u007f]", c => $"<{(int)c.Value[0]:x2}>");
        return printable.Length <= 600 ? printable : printable[..600] + "...";
    }

    /// <summary>
    /// Measures a snapshot the way the HTML viewer does: screen rows are consecutive
    /// <c>CSI r;1H</c> moves from row 1, each followed by the full row of cells.
    /// </summary>
    private static (int Cols, int Rows) Grid(string snapshot)
    {
        Assert.True(snapshot.StartsWith(Prologue, StringComparison.Ordinal), "not a snapshot: " + Printable(snapshot));
        var start = snapshot.IndexOf("\u001b[1;1H", StringComparison.Ordinal);
        Assert.True(start >= 0, "a snapshot paints row 1");
        var rows = 0;
        var cols = 0;
        foreach (System.Text.RegularExpressions.Match move in Regex.Matches(snapshot[start..], @"\u001b\[(\d+);1H((?:(?!\u001b\[\d+;\d+H).)*)", RegexOptions.Singleline))
        {
            if (int.Parse(move.Groups[1].Value) != rows + 1)
                break;
            rows++;
            cols = Math.Max(cols, Regex.Replace(move.Groups[2].Value, @"\u001b\[[0-9;?]*[A-Za-z]", "").Length);
        }
        return (cols, rows);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Credential = "Correct7";
        internal readonly Mock<IRemoteTerminalConnection> Relay = new();
        internal readonly Mock<ITerminalStateService> State = new();
        private readonly List<string> _controls = [];
        private readonly List<string> _output = [];
        private readonly MemoryStream _ptyInput = new();
        private readonly string _sessionId = "resize-" + Guid.NewGuid().ToString("N");
        internal readonly TerminalPty Terminal;

        internal Fixture(PtyConsoleHost host = PtyConsoleHost.Inbox)
        {
            var (salt, hash) = Hasher.Hash(Credential);
            var pty = new Mock<IPtyConnection>();
            pty.SetupGet(p => p.WriterStream).Returns(_ptyInput);
            pty.SetupGet(p => p.ConsoleHost).Returns(host);
            Terminal = new TerminalPty(pty.Object, 80, 24);
            Terminal.AttachEmulator();
            Relay.SetupGet(r => r.IsConnected).Returns(true);
            Relay.Setup(r => r.SendControlAsync(It.IsAny<string>()))
                .Callback<string>(text => { lock (_controls) _controls.Add(text); }).Returns(Task.CompletedTask);
            Relay.Setup(r => r.SendOutputAsync(It.IsAny<ReadOnlyMemory<byte>>()))
                .Callback<ReadOnlyMemory<byte>>(data => { lock (_output) _output.Add(Encoding.UTF8.GetString(data.Span)); })
                .Returns(Task.CompletedTask);
            var runner = new TerminalRunner(State.Object, Mock.Of<ICommandService>(),
                Mock.Of<ILocalToolApiContext>(), Mock.Of<ILlmProxySessionState>(), Mock.Of<IAutomationConsumer>(),
                Mock.Of<IRepository>(), Mock.Of<IEnvironmentStepRunner>(), Mock.Of<IAppEventBus>());
            runner.AttachRemoteViewer(Terminal, Relay.Object, _sessionId, "Fixture", _ => Task.CompletedTask,
                () => true, value => Hasher.Verify(value, salt, hash), _ => { });
        }

        internal string LastControl
        {
            get { lock (_controls) return _controls.Last(); }
        }

        /// <summary>Program output, through every consumer as the read loop would deliver it.</summary>
        internal void Publish(string text) => Terminal.PublishOutput(Encoding.UTF8.GetBytes(text));

        /// <summary>Enters the PIN and returns the full snapshot the unlock sends; output is cleared.</summary>
        internal string Unlock()
        {
            Relay.Raise(r => r.OnInputReceived += null, Encoding.UTF8.GetBytes(TerminalControlProtocol.PinResponse + Credential));
            Assert.Equal(TerminalControlProtocol.Unlocked, LastControl);
            return One(TakeSnapshots());
        }

        internal void AskForResizeSnapshots() =>
            Relay.Raise(r => r.OnCommandReceived += null, TerminalControlProtocol.ResizeSnapshotsCommandName, null);

        internal void Resize(int cols, int rows, TerminalIoSource source = TerminalIoSource.LocalWebUi) =>
            TerminalResizeCoordinator.ApplyResize(Terminal, State.Object, _sessionId, cols, rows, source);

        /// <summary>The snapshots sent within <paramref name="wait"/>.</summary>
        internal async Task<List<string>> SnapshotsAfterAsync(TimeSpan wait)
        {
            await Task.Delay(wait);
            return TakeSnapshots();
        }

        /// <summary>Waits for a snapshot, then long enough for a second one to show up if one is coming.</summary>
        internal async Task<List<string>> SnapshotsAsync()
        {
            for (var waited = 0; waited < 5000 && !HasSnapshot(); waited += 10)
                await Task.Delay(10);
            return await SnapshotsAfterAsync(Settled);
        }

        private bool HasSnapshot()
        {
            lock (_output) return _output.Any(IsSnapshot);
        }

        // Live output also reaches the relay; only the snapshots are of interest here.
        private List<string> TakeSnapshots()
        {
            lock (_output)
            {
                var snapshots = _output.Where(IsSnapshot).ToList();
                _output.Clear();
                return snapshots;
            }
        }

        private static bool IsSnapshot(string output) => output.StartsWith(Prologue, StringComparison.Ordinal);

        public async ValueTask DisposeAsync()
        {
            TerminalResizeCoordinator.ClearSession(_sessionId);
            await Terminal.DisposeAsync();
            _ptyInput.Dispose();
        }
    }
}
