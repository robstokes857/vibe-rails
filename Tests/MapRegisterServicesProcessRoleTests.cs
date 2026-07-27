using VibeRails;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests;

public sealed class MapRegisterServicesProcessRoleTests
{
    public static TheoryData<string[], bool> ProcessRoles => new()
    {
        { [], true },
        { ["--web"], true },
        { ["--git-guard"], true },
        { ["--vs-code-v1"], true },
        { ["--vs-code-v1", "--parent-pid", "42"], false },
        { ["--vs-code-v1", "--parent-pid=42"], false },
        { ["--env", "nightly", "--workdir", @"C:\source\repo"], false },
        // The catastrophic misclassification: a spawned Automation run that counted as an
        // active root would start scheduling — every job terminal enqueueing more job terminals.
        { ["--env", "nightly", "--workdir", @"C:\source\repo", "--job-run", "run-1"], false },
    };

    [Theory]
    [MemberData(nameof(ProcessRoles))]
    public void IsActiveRootBackendProcess_ClassifiesSupportedProcessRoles(
        string[] args,
        bool expected)
    {
        Assert.Equal(expected, MapRegisterServices.IsActiveRootBackendProcess(args));
    }

    [Fact]
    public async Task RetiredJobTick_IsRecognizedButPerformsNoWork()
    {
        Assert.True(JobTickProcessHost.IsRequested(["--job-tick"]));
        Assert.Equal(0, await JobTickProcessHost.RunAsync(TestContext.Current.CancellationToken));
    }
}
