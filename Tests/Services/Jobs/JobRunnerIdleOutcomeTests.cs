using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobRunnerIdleOutcomeTests
{
    [Fact]
    public void IdleShutdown_IsSuccessfulCompletion()
    {
        var outcome = JobRunner.ResolveShutdownOutcome(
            JobRunStatus.Failed,
            currentExitCode: 17,
            currentError: "CLI failed",
            idleShutdownRequested: true,
            cancellationRequested: false);

        Assert.Equal(JobRunStatus.Succeeded, outcome.Status);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void ExplicitCancellation_OverridesIdleShutdown()
    {
        var outcome = JobRunner.ResolveShutdownOutcome(
            JobRunStatus.Failed,
            currentExitCode: 17,
            currentError: "CLI failed",
            idleShutdownRequested: true,
            cancellationRequested: true);

        Assert.Equal(JobRunStatus.Cancelled, outcome.Status);
        Assert.Equal(JobRunOutcome.ToExitCode(JobRunStatus.Cancelled), outcome.ExitCode);
        Assert.Equal("Automation was cancelled.", outcome.Error);
    }
}
