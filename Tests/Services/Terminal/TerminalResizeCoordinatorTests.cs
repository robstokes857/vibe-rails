using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Pins the resize authority policy. Session b92fb476 (Claude, 2026-08-10): the PTY was
/// resized 129x26 -> 145x29 by a surface that was not the attached VS Code webview; the
/// webview is never told about foreign geometry changes (no resize rebroadcast exists),
/// so it wrapped the 145-wide repaints at 129 cols — shredded input-box chrome and the
/// cursor stranded on a blank row. Policy: while a local web viewer is attached, remote
/// resizes are ignored. See runbooks/terminal/TERMINAL.md "## 2026-08-10 Passive-viewer
/// resize shreds the webview".
/// </summary>
public sealed class TerminalResizeCoordinatorTests
{
    [Fact]
    public void RemoteResize_IsIgnored_WhileLocalWebViewerAttached()
    {
        // The b92fb476 case: remote surface reports its geometry while Rob's webview owns the tab.
        Assert.True(TerminalResizeCoordinator.ShouldIgnoreResize(
            TerminalIoSource.RemoteWebUi, hasLocalWebViewer: true));
    }

    [Fact]
    public void RemoteResize_Applies_OnRemoteOnlySessions()
    {
        // VS Code closed / webview detached: the remote viewer keeps full resize authority.
        Assert.False(TerminalResizeCoordinator.ShouldIgnoreResize(
            TerminalIoSource.RemoteWebUi, hasLocalWebViewer: false));
    }

    [Theory]
    [InlineData(TerminalIoSource.LocalWebUi)]  // the local viewer's own resizes always apply
    [InlineData(TerminalIoSource.LocalCli)]    // native-console poll exempt on purpose: native + web coexistence unchanged
    [InlineData(TerminalIoSource.Unknown)]
    [InlineData(TerminalIoSource.Pty)]
    [InlineData(TerminalIoSource.AgentTool)]
    public void NonRemoteSources_AreNeverIgnored(TerminalIoSource source)
    {
        Assert.False(TerminalResizeCoordinator.ShouldIgnoreResize(source, hasLocalWebViewer: true));
        Assert.False(TerminalResizeCoordinator.ShouldIgnoreResize(source, hasLocalWebViewer: false));
    }
}
