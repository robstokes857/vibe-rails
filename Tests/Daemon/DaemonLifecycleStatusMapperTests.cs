using VibeRails.Daemon;
using VibeRails.Daemon.Ipc;
using Xunit;

namespace Tests.Daemon;

public sealed class DaemonLifecycleStatusMapperTests
{
    [Theory]
    [InlineData(
        (int)DaemonRegistrationCondition.NotInstalled,
        DaemonControlClientOutcome.Unreachable,
        DaemonLifecycleState.NotInstalled,
        false,
        false,
        false)]
    [InlineData(
        (int)DaemonRegistrationCondition.NotInstalled,
        DaemonControlClientOutcome.Success,
        DaemonLifecycleState.NeedsRepair,
        false,
        true,
        false)]
    [InlineData(
        (int)DaemonRegistrationCondition.Current,
        DaemonControlClientOutcome.Success,
        DaemonLifecycleState.Running,
        true,
        true,
        true)]
    [InlineData(
        (int)DaemonRegistrationCondition.Current,
        DaemonControlClientOutcome.Unreachable,
        DaemonLifecycleState.InstalledStopped,
        true,
        false,
        true)]
    [InlineData(
        (int)DaemonRegistrationCondition.Current,
        DaemonControlClientOutcome.Rejected,
        DaemonLifecycleState.NeedsRepair,
        true,
        true,
        true)]
    [InlineData(
        (int)DaemonRegistrationCondition.Current,
        DaemonControlClientOutcome.ProtocolMismatch,
        DaemonLifecycleState.NeedsRepair,
        true,
        true,
        true)]
    [InlineData(
        (int)DaemonRegistrationCondition.Current,
        DaemonControlClientOutcome.InvalidResponse,
        DaemonLifecycleState.NeedsRepair,
        true,
        false,
        true)]
    [InlineData(
        (int)DaemonRegistrationCondition.Stale,
        DaemonControlClientOutcome.Unreachable,
        DaemonLifecycleState.NeedsRepair,
        true,
        false,
        false)]
    public void Map_CombinesRegistrationAndLiveReachability(
        int conditionValue,
        DaemonControlClientOutcome outcome,
        DaemonLifecycleState expectedState,
        bool expectedInstalled,
        bool expectedReachable,
        bool expectedCurrent)
    {
        var status = DaemonLifecycleStatusMapper.Map(
            DaemonPlatformKind.Linux,
            new DaemonRegistrationInspection((DaemonRegistrationCondition)conditionValue),
            new DaemonControlClientResult(outcome, Error: "ipc detail"));

        Assert.Equal(expectedState, status.State);
        Assert.True(status.IsSupported);
        Assert.Equal(expectedInstalled, status.IsInstalled);
        Assert.Equal(expectedReachable, status.IsReachable);
        Assert.Equal(expectedCurrent, status.RegistrationIsCurrent);
    }

    [Fact]
    public void Map_RegistrationInspectionErrorWinsAsErrorState()
    {
        var status = DaemonLifecycleStatusMapper.Map(
            DaemonPlatformKind.Windows,
            new DaemonRegistrationInspection(
                DaemonRegistrationCondition.Error,
                Message: "inspection failed",
                Error: "task scheduler unavailable"),
            new DaemonControlClientResult(DaemonControlClientOutcome.Success));

        Assert.Equal(DaemonLifecycleState.Error, status.State);
        Assert.True(status.IsSupported);
        Assert.False(status.IsInstalled);
        Assert.True(status.IsReachable);
        Assert.False(status.RegistrationIsCurrent);
        Assert.Equal("inspection failed", status.Message);
        Assert.Equal("task scheduler unavailable", status.Error);
    }

    [Theory]
    [InlineData(DaemonPlatformKind.Unsupported, (int)DaemonRegistrationCondition.Current)]
    [InlineData(DaemonPlatformKind.MacOS, (int)DaemonRegistrationCondition.Unavailable)]
    public void Map_UnsupportedPlatformOrRegistrationIsUnavailable(
        DaemonPlatformKind platform,
        int conditionValue)
    {
        var status = DaemonLifecycleStatusMapper.Map(
            platform,
            new DaemonRegistrationInspection(
                (DaemonRegistrationCondition)conditionValue,
                "not available"),
            new DaemonControlClientResult(DaemonControlClientOutcome.Unreachable));

        Assert.Equal(DaemonLifecycleState.Unavailable, status.State);
        Assert.False(status.IsSupported);
        Assert.False(status.IsInstalled);
        Assert.False(status.IsReachable);
        Assert.False(status.RegistrationIsCurrent);
        Assert.Equal("not available", status.Message);
    }
}
