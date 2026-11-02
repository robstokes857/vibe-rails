using VibeRails.Jobs;
using VibeRails.Services;
using Xunit;

namespace Tests.Jobs;

/// <summary>When the search backfill may run a large backlog (VIBE-172).</summary>
public sealed class SearchBackfillGateTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const int Large = SearchBackfillGate.SmallBacklog + 1;

    internal static readonly ResourceSnapshot Busy = new() { SystemCpuPercent = 60, QuietFor = TimeSpan.Zero, UserIdleFor = TimeSpan.Zero };
    internal static readonly ResourceSnapshot Quiet = new()
    {
        SystemCpuPercent = 5,
        QuietFor = SearchBackfillGate.QuietWindow,
        UserIdleFor = SearchBackfillGate.UserAwayWindow,
    };

    [Fact]
    public void FreshSmallWorkRunsRightAwayOnABusyMachine()
    {
        var gate = new SearchBackfillGate();

        Assert.Equal(SearchBackfillDecision.SmallBacklog, gate.Decide(1, Busy, Start));
        Assert.Equal(SearchBackfillDecision.SmallBacklog, gate.Decide(SearchBackfillGate.SmallBacklog, Busy, Start));
        Assert.Null(gate.LargeSince);
    }

    [Fact]
    public void ALargeBacklogHoldsWhileTheMachineIsBusyAndRunsOnceItIsIdle()
    {
        var gate = new SearchBackfillGate();

        Assert.Equal(SearchBackfillDecision.Hold, gate.Decide(18_000, Busy, Start));
        Assert.Equal(Start, gate.LargeSince);
        Assert.Equal(SearchBackfillDecision.Idle, gate.Decide(18_000, Quiet, Start.AddMinutes(4)));
        // Activity resumes mid-drain: hold again, still counting from when the backlog was first seen.
        Assert.Equal(SearchBackfillDecision.Hold, gate.Decide(12_000, Busy, Start.AddMinutes(10)));
        Assert.Equal(Start, gate.LargeSince);
    }

    [Fact]
    public void QuietCpuIsNotIdleWhileTheUserIsActive()
    {
        var gate = new SearchBackfillGate();
        var typing = Quiet with { UserIdleFor = TimeSpan.FromSeconds(5) };

        Assert.Equal(SearchBackfillDecision.Hold, gate.Decide(Large, typing, Start));
    }

    [Fact]
    public void ShortQuietSpellsAreNotIdle()
    {
        var gate = new SearchBackfillGate();
        var brief = Quiet with { QuietFor = SearchBackfillGate.QuietWindow - TimeSpan.FromSeconds(10) };

        Assert.Equal(SearchBackfillDecision.Hold, gate.Decide(Large, brief, Start));
    }

    [Fact]
    public void WithoutAnInputSignalQuietCpuAloneIsIdle()
    {
        // Linux and macOS report no user input.
        Assert.True(SearchBackfillGate.IsIdle(Quiet with { UserIdleFor = null }));
        Assert.False(SearchBackfillGate.IsIdle(Busy with { UserIdleFor = null }));
    }

    [Fact]
    public void ALargeBacklogStartsAnywayOnceItHasWaitedTheCap()
    {
        var gate = new SearchBackfillGate();

        Assert.Equal(SearchBackfillDecision.Hold, gate.Decide(Large, Busy, Start));
        Assert.Equal(SearchBackfillDecision.Hold, gate.Decide(Large, Busy, Start + SearchBackfillGate.MaxWait - TimeSpan.FromSeconds(1)));
        Assert.Equal(SearchBackfillDecision.WaitCapped, gate.Decide(Large, Busy, Start + SearchBackfillGate.MaxWait));
        Assert.Equal(SearchBackfillDecision.WaitCapped, gate.Decide(Large, Busy, Start.AddHours(2)));
    }

    [Fact]
    public void TheWaitStartsOverAfterTheBacklogDrains()
    {
        var gate = new SearchBackfillGate();
        gate.Decide(Large, Busy, Start);
        Assert.Equal(SearchBackfillDecision.SmallBacklog, gate.Decide(3, Busy, Start.AddMinutes(40)));

        var next = Start.AddDays(1);
        Assert.Equal(SearchBackfillDecision.Hold, gate.Decide(Large, Busy, next));
        Assert.Equal(next, gate.LargeSince);
    }
}
