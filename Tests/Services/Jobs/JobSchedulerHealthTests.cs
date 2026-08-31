using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

[Collection(JobSchedulerHostedServiceTestCollection.Name)]
public sealed class JobSchedulerHealthTests
{
    [Fact]
    public void CycleContended_IsHealthyWithoutReplacingTheLastOwnerCycle()
    {
        var ownerStarted = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var ownerCompleted = ownerStarted.AddSeconds(2);
        var contenderStarted = ownerStarted.AddSeconds(10);
        var contenderCompleted = contenderStarted.AddMilliseconds(25);
        var health = new JobSchedulerHealth();

        health.CycleStarted(ownerStarted);
        health.LeaseChanged(true);
        health.CycleCompleted(
            ownerCompleted,
            ownsLease: true,
            schedulesEnqueued: 3,
            runsLaunched: 2,
            runsReaped: 1,
            stalledLaunchesFailed: 4);
        health.CycleFailed(
            ownerCompleted.AddSeconds(1),
            new InvalidOperationException("transient failure"));

        health.CycleStarted(contenderStarted);
        health.LeaseChanged(false);
        health.CycleContended(contenderCompleted);

        var snapshot = health.GetSnapshot();
        Assert.Equal(contenderStarted, snapshot.LastCycleStartedUtc);
        Assert.Equal(contenderCompleted, snapshot.LastCycleCompletedUtc);
        Assert.Equal(ownerCompleted, snapshot.LastSuccessfulCycleUtc);
        Assert.False(snapshot.OwnsSchedulerLease);
        Assert.Equal(3, snapshot.LastSchedulesEnqueued);
        Assert.Equal(2, snapshot.LastRunsLaunched);
        Assert.Equal(1, snapshot.LastRunsReaped);
        Assert.Equal(4, snapshot.LastStalledLaunchesFailed);
        Assert.Null(snapshot.LastErrorUtc);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public void CycleFailed_BoundsThePublishedErrorAndRecordsWhenItHappened()
    {
        var failedUtc = new DateTime(2026, 8, 30, 12, 30, 0, DateTimeKind.Utc);
        var health = new JobSchedulerHealth();
        var rawError = new string('x', JobSchedulerHealth.MaximumErrorLength + 200);

        health.CycleFailed(failedUtc, new InvalidOperationException(rawError));

        var snapshot = health.GetSnapshot();
        Assert.Equal(failedUtc, snapshot.LastCycleCompletedUtc);
        Assert.Equal(failedUtc, snapshot.LastErrorUtc);
        Assert.NotNull(snapshot.LastError);
        Assert.Equal(JobSchedulerHealth.MaximumErrorLength, snapshot.LastError.Length);
        Assert.EndsWith("…", snapshot.LastError, StringComparison.Ordinal);
        Assert.Null(snapshot.LastSuccessfulCycleUtc);
    }

    [Fact]
    public void CycleCompleted_AfterLeaseLoss_DoesNotAdvanceSuccessAndPublishesTheLoss()
    {
        var previousSuccess = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var lostUtc = previousSuccess.AddMinutes(1);
        var health = new JobSchedulerHealth();
        health.CycleCompleted(previousSuccess, true, 1, 1, 0, 0);

        health.CycleCompleted(lostUtc, false, 2, 0, 0, 0);

        var snapshot = health.GetSnapshot();
        Assert.Equal(previousSuccess, snapshot.LastSuccessfulCycleUtc);
        Assert.Equal(lostUtc, snapshot.LastCycleCompletedUtc);
        Assert.Equal(lostUtc, snapshot.LastErrorUtc);
        Assert.Equal(JobSchedulerHealth.LeaseLostError, snapshot.LastError);
        Assert.False(snapshot.OwnsSchedulerLease);
    }
}
