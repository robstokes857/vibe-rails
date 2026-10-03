using System.Text;
using Pty.Net;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

/// <summary>
/// Real ConPTY regression for VIBE-45. ConPTY keeps its output pipe open while any process is still
/// attached to the console, so a shell that has exited never produced EOF when its Worker left a
/// compiler server or MCP server behind; the Automation tab's session stayed "active" for hours and
/// the finished agent never left the robot menu. The session must end on the shell's own exit.
/// No VibeRails host, database or network request is involved.
/// </summary>
public sealed class ConPtyOrphanedClientExitTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Requires Windows ConPTY")]
    public async Task ShellExitEndsTheSessionWhileAnOrphanedClientKeepsTheConsoleOpen()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var ct = timeout.Token;
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        // -NoNewWindow attaches the sleeper to this console and Start-Process does not wait for it, so the
        // shell exits with code 5 while a client it spawned still holds the console open. Without the
        // process-exit path the read loop would only see EOF once the sleeper itself ends, 40 s later.
        var script = "Start-Process -FilePath '" + powershell + "' -NoNewWindow -ArgumentList " +
            "'-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 40' | Out-Null; exit 5";
        var pty = await PtyProvider.SpawnAsync(new PtyOptions
        {
            App = powershell,
            CommandLine = ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            Cwd = Path.GetTempPath(), Cols = 100, Rows = 30
        }, ct);
        await using var terminal = new TerminalPty(pty, cols: 100, rows: 30);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Exited += (_, code) => exited.TrySetResult(code);
        terminal.StartReadLoop();

        var started = DateTime.UtcNow;
        var exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.Equal(5, exitCode);
        Assert.True(terminal.HasExited);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20),
            "the session must end on the shell's exit, not when the orphaned client finally lets go");
    }
}
