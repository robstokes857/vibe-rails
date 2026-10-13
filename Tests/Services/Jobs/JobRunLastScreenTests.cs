using System.Text;
using Moq;
using VibeRails.Data.Replay;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

/// <summary>
/// A failed Automation used to say only "Worker exited with code 1." while its own terminal had
/// printed the cause. These pin how the recording is turned back into that account.
/// </summary>
public sealed class JobRunLastScreenTests
{
    [Fact]
    public void FinalLineRejoinsTheWrappedErrorTheCliPrintedOnItsWayOut()
    {
        // The shape of the Codex auth failure behind thirteen failed "commit" runs: the TUI paints
        // and leaves, then its error wraps at the grid width.
        var screen = JobRunLastScreen.Render(Tail(30, 8,
            "\x1b[?1049h\x1b[2J\x1b[1;1H>_ OpenAI Codex\x1b[5;3HAsk Codex to do anything\x1b[?1049l",
            "Added global MCP server 'viberails-mcp'.\r\n",
            "Error: account/read failed: unauthorized (401) (code -32603)\r\n"));

        Assert.False(screen.EndedOnAlternateScreen);
        Assert.Equal(
            "Error: account/read failed: unauthorized (401) (code -32603)",
            JobRunLastScreen.FinalLine(screen));
        Assert.DoesNotContain(screen.Lines, line => line.Contains("Ask Codex", StringComparison.Ordinal));
    }

    [Fact]
    public void FinalLineSkipsBoxEdgesAndRules()
    {
        var screen = JobRunLastScreen.Render(Tail(40, 6, "Error: Invalid API key\r\n╰──────────╯\r\n────\r\n"));

        Assert.Equal("Error: Invalid API key", JobRunLastScreen.FinalLine(screen));
    }

    [Fact]
    public void FinalLineSkipsTheCliExitFooter()
    {
        var screen = JobRunLastScreen.Render(Tail(60, 8,
            "Error: stream disconnected before completion\r\n",
            "Token usage: total=1,234 input=1,000 output=234\r\n",
            "To continue this session, run codex resume 019a0b1c-2d3e-4f50-8a9b-0c1d2e3f4a5b\r\n"));

        Assert.Equal("Error: stream disconnected before completion", JobRunLastScreen.FinalLine(screen));
    }

    [Fact]
    public void FinalLineMasksCredentialsBecauseTheReasonIsServedBeyondTheRoot()
    {
        var screen = JobRunLastScreen.Render(Tail(80, 6,
            "Error: invalid key sk-proj-abcdefghijklmnopqrstuvwxyz0123 for https://bot:hunter2@api.example.test\r\n"));

        Assert.Equal("Error: invalid key XXXXX for https://bot:XXXXX@api.example.test", JobRunLastScreen.FinalLine(screen));
    }

    [Fact]
    public void ARecordingThatEndsInsideATuiHasNoFinalLineButKeepsItsLastFrame()
    {
        var screen = JobRunLastScreen.Render(Tail(40, 6,
            "shell history line\r\n",
            "\x1b[?1049h\x1b[2J\x1b[1;1H• Working (55s • esc to interrupt)\x1b[6;1H? for shortcuts"));

        Assert.True(screen.EndedOnAlternateScreen);
        // Its bottom row is the TUI's own chrome, not something it said.
        Assert.Null(JobRunLastScreen.FinalLine(screen));
        Assert.Equal(["• Working (55s • esc to interrupt)", "", "? for shortcuts"], JobRunLastScreen.Tail(screen));
    }

    [Fact]
    public void ATailThatBeginsInsideATuiStillKnowsWhichScreenItIsOn()
    {
        List<Frame> frames = [new(7, 0, "\x1b[1;1H› Ask Codex to do anything"u8.ToArray(), 40, 6)];
        var inside = JobRunLastScreen.Render(new SessionTail(frames, StartsOnAlternateScreen: true));

        Assert.True(inside.EndedOnAlternateScreen);
        Assert.Null(JobRunLastScreen.FinalLine(inside));

        // Leaving the TUI must not reveal its repaints as if they had been printed to the shell.
        frames.Add(new(8, 0, "\x1b[?1049lError: quota exceeded\r\n"u8.ToArray(), 40, 6));
        var after = JobRunLastScreen.Render(new SessionTail(frames, StartsOnAlternateScreen: true));

        Assert.Equal("Error: quota exceeded", JobRunLastScreen.FinalLine(after));
        Assert.DoesNotContain(after.Lines, line => line.Contains("Ask Codex", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScreenKeepsTheNewestLinesAndOneBlankPerGap()
    {
        var output = string.Concat(Enumerable.Range(1, 50).Select(index => $"line {index}\r\n")) + "\r\n\r\n\r\nlast\r\n";
        var screen = JobRunLastScreen.Render(Tail(40, 10, output));

        Assert.Equal(["line 48", "line 49", "line 50", "", "last"], JobRunLastScreen.Tail(screen, maxLines: 5));
    }

    [Fact]
    public void AttachReadsTheRunsOwnTerminalOnlyWhenTheRunEndedBadly()
    {
        var replay = new Mock<IReplayStore>(MockBehavior.Strict);
        replay.Setup(store => store.Tail("workflow", JobRunLastScreen.TailBytes))
            .Returns(Tail(40, 4, "Traceback\r\nValueError: bad input\r\n"));

        var failed = JobRunLastScreen.Attach(Run(JobRunStatus.Failed, "worker", "workflow"), replay.Object);
        Assert.Equal(["Traceback", "ValueError: bad input"], failed.LastScreen);

        foreach (var status in new[] { JobRunStatus.Queued, JobRunStatus.Running, JobRunStatus.Succeeded })
            Assert.Null(JobRunLastScreen.Attach(Run(status, "worker", "workflow"), replay.Object).LastScreen);
        replay.Verify(store => store.Tail(It.IsAny<string>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void ARunWhoseTerminalRecordedNothingGetsAnEmptyScreenRatherThanNone()
    {
        var replay = new Mock<IReplayStore>();
        replay.Setup(store => store.Tail("worker", It.IsAny<int>())).Returns(new SessionTail([], false));

        var interrupted = JobRunLastScreen.Attach(Run(JobRunStatus.Interrupted, "worker", null), replay.Object);

        Assert.NotNull(interrupted.LastScreen);
        Assert.Empty(interrupted.LastScreen);
        Assert.Null(JobRunLastScreen.Attach(Run(JobRunStatus.Failed, null, null), replay.Object).LastScreen);
    }

    [Fact]
    public void AnUnreadableRecordingLeavesTheRunAsItWas()
    {
        var replay = new Mock<IReplayStore>();
        replay.Setup(store => store.Tail("worker", It.IsAny<int>())).Throws(new IOException("database is locked"));
        var run = Run(JobRunStatus.TimedOut, "worker", null);

        Assert.Same(run, JobRunLastScreen.Attach(run, replay.Object));
        Assert.Null(JobRunLastScreen.FinalLine(replay.Object, "worker"));
        Assert.Null(JobRunLastScreen.FinalLine(null, "worker"));
    }

    private static SessionTail Tail(int cols, int rows, params string[] frames) =>
        new(frames.Select((text, index) => new Frame(index + 1, 0, Encoding.UTF8.GetBytes(text), cols, rows)).ToList(), false);

    private static JobRunResponse Run(JobRunStatus status, string? sessionId, string? terminalSessionId) => new(
        "run", 1, "Nightly", JobTriggerKind.Manual, status, "C:/repo", LLM.Codex, null, sessionId, 60,
        DateTime.UtcNow, null, null, null, null, false, TerminalSessionId: terminalSessionId);
}
