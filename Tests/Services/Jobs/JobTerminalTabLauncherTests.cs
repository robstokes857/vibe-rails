using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using VibeRails.Services.Terminal;
using VibeRails.Services.Workspaces;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobTerminalTabLauncherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkflowStartsInsideRecordedShellAndRetainsWorkerWorkspaceAndArgs(bool withWorker)
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.GetTempPath();
        var workspace = Path.Combine(directory, "worker-clone");
        var run = Run(directory, withWorker);
        var tabs = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        var workspaces = new Mock<IRunWorkspaceService>(MockBehavior.Strict);
        var sequence = new MockSequence();
        if (withWorker)
        {
            var environment = new LLM_Environment { Id = 7, CustomName = "worker", LLM = LLM.Claude, CustomArgs = "--model opus", ProjectPath = directory };
            repository.Setup(service => service.GetEnvironmentByIdAsync(7, token)).ReturnsAsync(environment);
            repository.Setup(service => service.TouchEnvironmentLastUsedAsync(7, token)).Returns(Task.CompletedTask);
            workspaces.Setup(service => service.ResolveAsync(environment, directory, token))
                .ReturnsAsync(new WorkspaceResolution(workspace, null));
        }
        StartTerminalRequest? request = null;
        TerminalInputRequest? input = null;
        tabs.InSequence(sequence).Setup(service => service.CreateAutomationTabAsync(run.Id, run.JobName, token))
            .ReturnsAsync(new TerminalTabStatusResponse("tab", DateTime.UtcNow, false));
        tabs.InSequence(sequence).Setup(service => service.StartSessionAsync("tab", It.IsAny<StartTerminalRequest>(), token))
            .Callback<string, StartTerminalRequest, CancellationToken>((_, value, _) => request = value)
            .ReturnsAsync(new TerminalStatusResponse(true, "outer-session", "shell", withWorker ? workspace : directory));
        store.InSequence(sequence).Setup(service => service.LinkRunTerminalSessionAsync(run.Id, "outer-session", token))
            .Returns(Task.CompletedTask);
        tabs.InSequence(sequence).Setup(service => service.SendInputAsync("tab", It.IsAny<TerminalInputRequest>(), token))
            .Callback<string, TerminalInputRequest, CancellationToken>((_, value, _) => input = value)
            .ReturnsAsync(new TerminalInputResponse(true, "sent"));

        var result = await new JobTerminalTabLauncher(tabs.Object, repository.Object, store.Object, workspaces.Object,
            new Mock<IBoardStore>(MockBehavior.Strict).Object).LaunchAsync(run, token);

        Assert.True(result.Success);
        Assert.Equal("shell", request!.Cli);
        Assert.False(request.MakeRemote);
        Assert.Equal(withWorker ? workspace : directory, request.WorkingDirectory);
        Assert.Equal("outer-session", input!.ExpectedSessionId);
        Assert.True(input.Submit);
        Assert.Contains("--job-run", input.Text);
        if (withWorker)
        {
            Assert.Contains("--env-id", input.Text);
            Assert.Contains("--model", input.Text);
            Assert.Contains("opus", input.Text);
            Assert.Contains(workspace, input.Text);
        }
        else Assert.DoesNotContain("--env", input.Text);
        tabs.VerifyAll();
        store.VerifyAll();
        repository.VerifyAll();
        workspaces.VerifyAll();
    }

    [Fact]
    public async Task MissingWorkerNeverCreatesATerminalTab()
    {
        var token = TestContext.Current.CancellationToken;
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetEnvironmentByIdAsync(7, token)).ReturnsAsync((LLM_Environment?)null);
        var tabs = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
        var result = await new JobTerminalTabLauncher(tabs.Object, repository.Object, new Mock<IJobStore>().Object,
            new Mock<IRunWorkspaceService>(MockBehavior.Strict).Object, new Mock<IBoardStore>().Object)
            .LaunchAsync(Run(Path.GetTempPath(), true), token);
        Assert.False(result.Success);
        tabs.VerifyNoOtherCalls();
    }

    private static JobRunRecord Run(string directory, bool worker) => new(
        "run", 1, JobTriggerKind.Manual, "manual:run", JobRunStatus.Queued, "workflow", directory,
        worker ? LLM.Claude : LLM.NotSet, worker ? 7 : null, worker ? "worker" : null, null, null,
        DateTime.UtcNow, null, null, null, null, false, null, LaunchInTerminalTab: true);
}
