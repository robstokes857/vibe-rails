using System.Text.Json;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class AutomationTabStateTests
{
    [Theory]
    [InlineData(JobRunStatus.Succeeded)]
    [InlineData(JobRunStatus.Failed)]
    [InlineData(JobRunStatus.Cancelled)]
    [InlineData(JobRunStatus.TimedOut)]
    [InlineData(JobRunStatus.Interrupted)]
    public async Task FinishedRunWithFlushedRecordingCanBeReclaimedAndCannotRestart(JobRunStatus status)
    {
        var state = Started();
        var jobs = Jobs(status);
        var sessions = Sessions(DateTime.UtcNow);

        Assert.True(await state.TryReserveForReclamationAsync(jobs.Object, sessions.Object,
            _ => Task.FromResult<TerminalStatusResponse?>(new(false)), TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(state.BeginSessionStart);
        Assert.False(await state.TryReserveForReclamationAsync(jobs.Object, sessions.Object,
            _ => throw new InvalidOperationException("Already reserved"), TestContext.Current.CancellationToken));
        Assert.Equal("outer", state.LastSession?.SessionId);
    }

    [Theory]
    [InlineData(JobRunStatus.Queued)]
    [InlineData(JobRunStatus.Running)]
    [InlineData((JobRunStatus)99)]
    public async Task NonterminalOrUnknownRunNeverReclaimsItsTab(JobRunStatus status)
    {
        Assert.False(await Started().TryReserveForReclamationAsync(Jobs(status).Object,
            Mock.Of<ISessionStore>(MockBehavior.Strict), _ => throw new InvalidOperationException("No status read expected"),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveOrUnavailableChildIsNeverReclaimed(bool active)
    {
        Assert.False(await Started().TryReserveForReclamationAsync(Jobs(JobRunStatus.Succeeded).Object,
            Mock.Of<ISessionStore>(MockBehavior.Strict),
            _ => Task.FromResult<TerminalStatusResponse?>(active ? new(true, "outer") : null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnfinishedRecordingProtectsFinalOutputFlush()
    {
        Assert.False(await Started().TryReserveForReclamationAsync(Jobs(JobRunStatus.Succeeded).Object,
            Sessions(null).Object, _ => Task.FromResult<TerminalStatusResponse?>(new(false)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnstartedAutomationHasNoReclaimableSession()
    {
        Assert.False(await new AutomationTabState("run", "workflow").TryReserveForReclamationAsync(
            Mock.Of<IJobStore>(MockBehavior.Strict), Mock.Of<ISessionStore>(MockBehavior.Strict),
            _ => throw new InvalidOperationException("No status read expected"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RepurposedAutomationHostIsNeverReclaimedForItsOldRun()
    {
        var state = Started();
        state.BeginSessionStart();
        state.EndSessionStart(new(true, "new-session"));

        Assert.False(await state.TryReserveForReclamationAsync(Jobs(JobRunStatus.Succeeded).Object,
            Mock.Of<ISessionStore>(MockBehavior.Strict), _ => throw new InvalidOperationException("No status read expected"),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionStartRacingCompletionChecksProtectsHostEvenIfStartFinishes(bool finishStart)
    {
        var state = Started();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readContinue = new TaskCompletionSource<TerminalStatusResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reclaim = state.TryReserveForReclamationAsync(Jobs(JobRunStatus.Succeeded).Object,
            Sessions(DateTime.UtcNow).Object, _ =>
            {
                readStarted.SetResult();
                return readContinue.Task;
            }, TestContext.Current.CancellationToken);
        await readStarted.Task;
        state.BeginSessionStart();
        if (finishStart)
            state.EndSessionStart(null); // Even a failed start invalidates the old status check.
        readContinue.SetResult(new(false));

        Assert.False(await reclaim);
        if (!finishStart)
            state.EndSessionStart(null);
        Assert.False(await state.TryReserveForReclamationAsync(Jobs(JobRunStatus.Succeeded).Object,
            Sessions(DateTime.UtcNow).Object, _ => Task.FromResult<TerminalStatusResponse?>(new(false)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AmbiguousFailedStartNeverReclaimsUsingTheOlderRecording()
    {
        var state = Started();
        state.BeginSessionStart();
        state.EndSessionStart(null);

        Assert.Equal("outer", state.LastSession?.SessionId);
        Assert.False(await state.TryReserveForReclamationAsync(Mock.Of<IJobStore>(MockBehavior.Strict),
            Mock.Of<ISessionStore>(MockBehavior.Strict), _ => throw new InvalidOperationException("No status read expected"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StatusFailureDoesNotReserveHostOrPreventLaterRetry()
    {
        var state = Started();
        var jobs = Jobs(JobRunStatus.Succeeded);
        var sessions = Sessions(DateTime.UtcNow);
        await Assert.ThrowsAsync<HttpRequestException>(() => state.TryReserveForReclamationAsync(jobs.Object,
            sessions.Object, _ => throw new HttpRequestException("Offline"), TestContext.Current.CancellationToken));
        Assert.True(await state.TryReserveForReclamationAsync(jobs.Object, sessions.Object,
            _ => Task.FromResult<TerminalStatusResponse?>(new(false)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AutomationIdentityAndUnavailableStatusSurviveAotSerialization()
    {
        var tab = new TerminalTabStatusResponse("tab", DateTime.UtcNow, false, "outer", "shell", "/project",
            "run", "Nightly build", StatusAvailable: false);
        var json = JsonSerializer.Serialize(tab, AppJsonSerializerContext.Default.TerminalTabStatusResponse);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("run", document.RootElement.GetProperty("jobRunId").GetString());
        Assert.Equal("Nightly build", document.RootElement.GetProperty("automationName").GetString());
        Assert.False(document.RootElement.GetProperty("statusAvailable").GetBoolean());
        Assert.Equal(tab, JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.TerminalTabStatusResponse));
    }

    private static AutomationTabState Started()
    {
        var state = new AutomationTabState("run", "workflow");
        state.BeginSessionStart();
        state.EndSessionStart(new(true, "outer", "shell", "/project"));
        return state;
    }

    private static Mock<IJobStore> Jobs(JobRunStatus status)
    {
        var jobs = new Mock<IJobStore>(MockBehavior.Strict);
        jobs.Setup(store => store.GetRunAsync("run", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobRunRecord("run", 1, JobTriggerKind.Manual, "manual:run", status, "workflow", "/project",
                LLM.NotSet, null, null, null, null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, 0, null,
                false, null, TerminalSessionId: "outer"));
        return jobs;
    }

    private static Mock<ISessionStore> Sessions(DateTime? ended)
    {
        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        sessions.Setup(store => store.GetSessionByIdAsync("outer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionResponse("outer", "shell", null, "/project", DateTime.UtcNow, ended, ended is null ? null : 0));
        return sessions;
    }
}
