using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobDaemonProcessHostTests
{
    [Fact]
    public void IsRequested_RecognizesOnlyTheStandaloneDaemonArgument()
    {
        Assert.True(JobDaemonProcessHost.IsRequested(["--job-daemon"]));
        Assert.True(JobDaemonProcessHost.IsRequested(["vb", "--JOB-DAEMON"]));
        Assert.False(JobDaemonProcessHost.IsRequested(["--job-daemon=true"]));
        Assert.False(JobDaemonProcessHost.IsRequested(["--job-daemon-service", "status"]));
    }

    [Fact]
    public void IsRequested_DoesNotInspectArgumentsAfterTheOptionSentinel()
    {
        Assert.False(JobDaemonProcessHost.IsRequested(["--", "--job-daemon"]));
        Assert.False(JobDaemonProcessHost.IsRequested(["--env", "claude", "--", "--job-daemon"]));
    }
}
