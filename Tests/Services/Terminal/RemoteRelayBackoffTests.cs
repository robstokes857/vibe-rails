using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// VIBE-157: every terminal process probed an unavailable relay on its own ~30 s schedule. The
/// shared gate lets one process at a time probe during an outage and backs off with its age.
/// </summary>
public sealed class RemoteRelayBackoffTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-relay-backoff-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 14, 5, 0, 0, DateTimeKind.Utc);

    private string StatePath => Path.Combine(_directory, RemoteRelayBackoff.FileName);

    private RemoteRelayBackoff Create(string? path = null) =>
        new(path ?? StatePath, () => _now, () => 0.5); // 0.5 → no jitter (0.8 + 0.5 * 0.4 = 1.0)

    public RemoteRelayBackoffTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void HealthyHost_NeverWaits()
    {
        var gate = Create();
        Assert.Equal(TimeSpan.Zero, gate.TryClaimProbe());
        Assert.Equal(TimeSpan.Zero, gate.TryClaimProbe());
        Assert.False(gate.IsInOutage);
        Assert.False(File.Exists(StatePath)); // Nothing is written while the host is healthy.
    }

    [Fact]
    public void DuringAnOutage_OnlyOneProcessProbesAtATime()
    {
        var first = Create();
        var second = Create(); // Another terminal process sharing the same file.
        first.ReportHostFailure();

        Assert.Equal(RemoteRelayBackoff.FirstDelay, second.TryClaimProbe());

        _now += RemoteRelayBackoff.FirstDelay;
        Assert.Equal(TimeSpan.Zero, first.TryClaimProbe());
        // The probe is claimed: the other process waits for its outcome instead of probing too.
        Assert.Equal(RemoteRelayBackoff.ProbeLease, second.TryClaimProbe());

        second.ReportHostAvailable();
        Assert.Equal(TimeSpan.Zero, first.TryClaimProbe());
        Assert.Equal(TimeSpan.Zero, second.TryClaimProbe());
    }

    [Fact]
    public void DelayGrowsWithTheOutageAge_NotWithHowManyProcessesReported()
    {
        var gate = Create();
        gate.ReportHostFailure();
        gate.ReportHostFailure();
        gate.ReportHostFailure(); // Three simultaneous reporters do not triple the delay.
        Assert.Equal(RemoteRelayBackoff.FirstDelay, gate.TryClaimProbe());

        _now += TimeSpan.FromSeconds(90);
        gate.ReportHostFailure();
        Assert.Equal(TimeSpan.FromSeconds(90), gate.TryClaimProbe());

        _now += TimeSpan.FromHours(2);
        gate.ReportHostFailure();
        Assert.Equal(RemoteRelayBackoff.MaxDelay, gate.TryClaimProbe());
    }

    [Fact]
    public void RetryAfterIsHonoredUpToAnHour()
    {
        var gate = Create();
        gate.ReportHostFailure(TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), gate.TryClaimProbe());

        gate.ReportHostFailure(TimeSpan.FromDays(3));
        Assert.Equal(RemoteRelayBackoff.MaxRetryAfter, gate.TryClaimProbe());
    }

    [Theory]
    [InlineData("not a state file")]
    [InlineData("1 2 3")]
    [InlineData("-5 7")]
    [InlineData("99999999999999999999 1")]
    public void UnreadableStateCountsAsHealthy(string contents)
    {
        File.WriteAllText(StatePath, contents);
        Assert.Equal(TimeSpan.Zero, Create().TryClaimProbe());
    }

    [Fact]
    public void FarFutureStateNeverWaitsPastTheCap()
    {
        File.WriteAllText(StatePath, $"{_now.Ticks} {_now.AddYears(5).Ticks}");
        Assert.Equal(RemoteRelayBackoff.MaxRetryAfter, Create().TryClaimProbe());
    }

    [Fact]
    public async Task WaitForTurn_ReturnsWhenAnotherProcessReportsTheHostBack()
    {
        // Real clock: the waiter polls the shared file, so a success elsewhere releases it.
        var waiter = new RemoteRelayBackoff(StatePath);
        var other = new RemoteRelayBackoff(StatePath);
        other.ReportHostFailure(TimeSpan.FromMinutes(30));

        var wait = waiter.WaitForTurnAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        other.ReportHostAvailable();
        await wait.WaitAsync(RemoteRelayBackoff.PollInterval + TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
