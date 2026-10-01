using Microsoft.Data.Sqlite;
using System.Text.Json;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Git;
using VibeRails.Services.Jobs;
using VibeRails.Services.Terminal;
using VibeRails.Services.Workspaces;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using Xunit;

namespace Tests.Services.Board;

public sealed class SwitchReviewerTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "switch-reviewer-" + Guid.NewGuid().ToString("N"));
    private string project = null!, state = null!;
    private Repository repository = null!;
    private BoardStore boards = null!;
    private ReviewRoutingService routing = null!;
    private JobStore jobs = null!;
    private BoardCardRecord card = null!;
    private Mock<IJobExecutableResolver> executables = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        project = Path.Combine(root, "repo"); Directory.CreateDirectory(project);
        await Git("init"); await Git("config", "user.email", "tests@local"); await Git("config", "user.name", "Tests");
        await File.WriteAllTextAsync(Path.Combine(project, "file.txt"), "baseline", Ct);
        await Git("add", "."); await Git("-c", "core.hooksPath=", "commit", "-m", "baseline");
        state = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        repository = new Repository(state);
        boards = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", state);
        executables = new Mock<IJobExecutableResolver>();
        executables.Setup(e => e.Resolve(It.IsAny<LLM>())).Returns("test-cli");
        routing = new(boards, repository, executables.Object);
        jobs = new JobStore(state, boards, new SnapshotFactory(routing));
        await boards.EnsureDefaultColumnsAsync(project, Ct);
        card = await boards.CreateCardAsync(project, new(null, "Work", "", "base:opencode", "medium", null, [], false), Ct);
    }

    [Theory]
    [InlineData("claude", "codex")]
    [InlineData("codex", "claude")]
    public async Task DefaultDirectionsUseExplicitCodingSource_NotAssignmentOrNewestSession(string coder, string reviewer)
    {
        await Source(coder);
        await Session("opencode", "chat");
        await Session("opencode", "code_review");
        var selected = await Resolve();
        Assert.Equal(reviewer, selected.Resolution.Provider);
        Assert.Equal(coder, selected.Resolution.Source.Provider);
        Assert.False(selected.Resolution.UsedFallback);
        Assert.False(selected.Resolution.Overridden);
        Assert.Equal("base:opencode", (await boards.FindCardAsync(project, card.Id, Ct))!.Assignee);
    }

    [Fact]
    public async Task SameProviderMapping_AdditionalProvider_AndPerReviewOverride()
    {
        await Source("codex");
        var policy = ReviewerRouting.SwitchDefault() with { Mappings = [new("codex", new("base:codex")), new("opencode", new("base:claude"))] };
        Assert.Equal("codex", (await Resolve(policy)).Resolution.Provider);
        var once = await routing.ResolveAsync(project, card.Key, policy, new("base:claude", new(Model: "sonnet[1m]")), Ct);
        Assert.Equal("claude", once.Resolution.Provider); Assert.True(once.Resolution.Overridden);
        Assert.Equal("sonnet[1m]", once.Resolution.Selected.Options!.Model); Assert.False(once.Resolution.Selected.Options.Yolo);
        await Source("opencode");
        Assert.Equal("claude", (await Resolve(policy)).Resolution.Provider);
        Assert.Equal("codex", (await Resolve()).Resolution.Provider);
        Assert.True((await Resolve()).Resolution.UsedFallback);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("mixed")]
    [InlineData("human")]
    public async Task UnknownMixedAndHumanUseVisibleConfiguredFallback(string kind)
    {
        await Session("claude", "launch");
        await routing.SaveSettingsAsync(project, card.Id, new(SourceKind: kind, Description: "Shared changes"), Ct);
        var r = (await Resolve(ReviewerRouting.SwitchDefault() with { Fallback = new("base:opencode") })).Resolution;
        Assert.Equal(kind, r.Source.Kind); Assert.Null(r.Source.Provider);
        Assert.Equal("opencode", r.Provider); Assert.True(r.UsedFallback);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("planning")]
    [InlineData("code_review")]
    public async Task DiscussionPlanningAndReviewsCannotBeDeclaredAsCoder_EvenOnAdditionalCards(string intent)
    {
        var session = await Session("claude", intent);
        var other = await boards.CreateCardAsync(project, new(null, "Other", "", null, "medium", null, [], false), Ct);
        await boards.LinkSessionAsync(project, other.Id, session, null, "base:claude", "claude", "Attached", "mcp", Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.SaveSettingsAsync(project, other.Id,
            new("session", session, "Claimed coding"), Ct));
    }

    [Fact]
    public async Task HistoricalChatContextExcludesOldLaunchOriginWithoutRewritingIt()
    {
        var session = await Session("claude", "launch");
        await boards.RecordContextSampleAsync(project, card.Id, new(session, "chat", "claude", "base:claude", 0, 0, 0, 0, "{}"), Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.SaveSettingsAsync(project, card.Id, new("session", session, "Coding"), Ct));
        Assert.Equal("launch", (await boards.GetAgentSessionsAsync(project, card.Id, session, Ct)).Single().Session.Origin);
    }

    [Fact]
    public async Task MultiCardSessionNeedsIndependentScopeDeclaration_AndForeignSessionIsRejected()
    {
        var session = await Source("claude");
        var other = await boards.CreateCardAsync(project, new(null, "Other", "", null, "medium", null, [], false), Ct);
        await boards.LinkSessionAsync(project, other.Id, session, null, "base:claude", "claude", "Same session", "mcp", Ct);
        Assert.Null((await routing.ResolveAsync(project, other.Id, ReviewerRouting.SwitchDefault(), null, Ct)).Resolution.Source.Provider);
        await routing.SaveSettingsAsync(project, other.Id, new("session", session, "Only the work for the other card"), Ct);
        Assert.Equal("claude", (await routing.ResolveAsync(project, other.Id, ReviewerRouting.SwitchDefault(), null, Ct)).Resolution.Source.Provider);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.SaveSettingsAsync(project, card.Id, new("session", "unlinked", "Wrong session"), Ct));
        Assert.Null(await routing.SaveSettingsAsync(root, card.Id, new(), Ct));
        Assert.False(await boards.SaveReviewSettingsAsync(root, card.Id, new(), Ct));
    }

    [Fact]
    public async Task CustomEnvironmentPinsIdentityModelAndScope_RejectsChangeOrMissingEnvironment()
    {
        await Source("claude");
        var environment = new LLM_Environment { CustomName = "Review model", LLM = LLM.Codex, ProjectPath = project,
            CustomArgs = "--model gpt-6.1", WorkspaceMode = EnvironmentWorkspaceMode.PerRun };
        await repository.SaveEnvironmentAsync(environment, Ct);
        var policy = ReviewerRouting.SwitchDefault() with { Mappings = [new("claude", new($"env:{environment.Id}:codex"))] };
        var snapshot = await Resolve(policy);
        Assert.Equal("Review model", snapshot.Resolution.Reviewer); Assert.Equal(project, snapshot.Resolution.Workspace);
        Assert.Equal("gpt-6.1", snapshot.Resolution.Model);
        Assert.NotNull(await routing.RequireLaunchAsync(snapshot, Ct));
        await repository.TouchEnvironmentLastUsedAsync(environment.Id, Ct);
        Assert.NotNull(await routing.RequireLaunchAsync(snapshot, Ct));
        environment.CustomArgs = "--model gpt-6.2"; await repository.UpdateEnvironmentAsync(environment, Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.RequireLaunchAsync(snapshot, Ct));
        var missing = await Resolve(ReviewerRouting.SwitchDefault() with { Fallback = new("env:999999:claude"), Mappings = [] });
        Assert.Equal("claude", missing.Resolution.Provider); Assert.Contains("unavailable", missing.Resolution.Problem);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.RequireLaunchAsync(missing, Ct));
    }

    [Fact]
    public async Task MissingProviderCanBeInstalledThenRetried_WithoutSubstitution()
    {
        await Source("codex"); executables.Setup(e => e.Resolve(LLM.Claude)).Returns((string?)null);
        var snapshot = await Resolve();
        Assert.Equal("claude", snapshot.Resolution.Provider); Assert.NotNull(snapshot.Resolution.Problem);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.RequireLaunchAsync(snapshot, Ct));
        executables.Setup(e => e.Resolve(LLM.Claude)).Returns("installed-cli");
        Assert.Null(await routing.RequireLaunchAsync(snapshot, Ct));
    }

    [Fact]
    public async Task QueueAndRetryFreezeRouting_SourceModelAndScope_AndStayInCodeReviews()
    {
        await Source("codex");
        var worker = new LLM_Environment { CustomName = "Switch reviewer", LLM = LLM.Codex, Purpose = "code_review",
            ReviewerRouting = ReviewerRouting.SwitchDefault(), AutomationWorker = true, CustomPrompt = "Review carefully" };
        await repository.SaveEnvironmentAsync(worker, Ct);
        var job = await jobs.CreateJobAsync(new("Review", project, LLM.Codex, worker.Id, "Review", null, true, []), Ct);
        var id = await jobs.EnqueueBoardCardRunAsync(project, job.Id, card.Key, Ct);
        var queued = (await jobs.GetRunAsync(id!, Ct))!;
        Assert.Equal(LLM.Claude, queued.Llm); Assert.Null(queued.EnvironmentId); Assert.Equal("code_review", queued.Purpose);
        Assert.Equal(LLM.Claude, Assert.Single(queued.Actions!).Llm);
        Assert.NotNull(queued.ReviewLaunch!.Resolution.InputHash);
        worker.ReviewerRouting = ReviewerRouting.SwitchDefault() with { Mappings = [new("codex", new("base:codex"))] };
        await repository.UpdateEnvironmentAsync(worker, Ct);
        await Source("claude");
        Assert.Equivalent(queued.ReviewLaunch, (await jobs.GetRunAsync(id!, Ct))!.ReviewLaunch);
        await jobs.CompleteRunAsync(id!, JobRunStatus.Failed, 1, "test", Ct);
        var retry = (await jobs.GetRunAsync((await jobs.EnqueueRetryAsync(id!, Ct))!, Ct))!;
        Assert.Equivalent(queued.ReviewLaunch, retry.ReviewLaunch);
        Assert.Equal(card.Key, JobRunner.GetBoardCardKey(retry));
        Assert.Equal(2, (await boards.GetReviewRunsAsync(project, card.Id, 0, Ct)).Count);
        Assert.Equal("codex", (await boards.GetReviewRunsAsync(project, card.Id, 0, Ct)).First().Routing!.Source.Provider);
        Assert.Null(await routing.RequireLaunchAsync(retry.ReviewLaunch!, Ct));
        await File.WriteAllTextAsync(Path.Combine(project, "file.txt"), "new work", Ct);
        var error = await Assert.ThrowsAsync<BoardValidationException>(() => routing.RequireLaunchAsync(retry.ReviewLaunch!, Ct));
        Assert.Contains("inputs changed", error.Message);
        Assert.NotEqual(queued.ReviewLaunch.Resolution.InputHash, (await Resolve()).Resolution.InputHash);
    }

    [Theory]
    [InlineData(null, "range")]
    [InlineData("working-tree", "working-tree")]
    [InlineData("repository", "repository")]
    public async Task StarterReviewCapturesCommittedHandoffUnlessTheCardHasAnExplicitScope(string? savedScope, string expectedScope)
    {
        await Git("branch", "-M", "main");
        var baseline = (await GitCli.RunAsync(project, ["rev-parse", "HEAD"], Ct)).StdOut.Trim();
        await Git("remote", "add", "origin", project);
        await Git("update-ref", "refs/remotes/origin/main", baseline);
        await Git("branch", "--set-upstream-to=origin/main", "main");
        await File.WriteAllTextAsync(Path.Combine(project, "file.txt"), "committed task changes", Ct);
        await Git("add", "."); await Git("-c", "core.hooksPath=", "commit", "-m", "handoff");
        Assert.Empty((await GitCli.RunAsync(project, ["status", "--porcelain"], Ct)).StdOut.Trim());
        if (savedScope is not null)
            await routing.SaveSettingsAsync(project, card.Id, new(Scope: savedScope), Ct);
        var seed = Assert.Single(await boards.GetPendingStarterWorkflowsAsync(project, Ct));
        var job = await jobs.EnsureBoardReviewRecipeAsync(project, seed.ColumnId, seed.RecipeId, Ct);
        await boards.CompleteStarterWorkflowAsync(project, seed.ColumnId, job, Ct);
        await boards.MoveCardAsync(project, card.Id, seed.ColumnId, null, Ct);
        var runId = Assert.Single(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(2), Ct));
        var run = (await jobs.GetRunAsync(runId, Ct))!;
        var snapshot = run.ReviewLaunch!.Resolution;
        Assert.Equal(expectedScope, snapshot.Scope);
        Assert.NotNull(snapshot.InputHash);
        Assert.Null(snapshot.Problem);
        Assert.Equal(savedScope, (await boards.GetReviewSettingsAsync(project, card.Id, Ct))?.Scope);
        if (savedScope is null) Assert.Equal(baseline, snapshot.BaseCommit);
        var session = Guid.NewGuid().ToString();
        await repository.CreateSessionAsync(session, "codex", null, project, 1, runId);
        await BoardAutomationSessionLinker.LinkAsync(boards, run, session, cancellationToken: Ct);
        var reviews = new BoardReviewService(boards, Mock.Of<IBoardService>());
        var report = await reviews.BeginAsync(project, card.Id, session, project, snapshot.Scope, "Task handoff",
            snapshot.BaseCommit, snapshot.HeadCommit, snapshot.IncludeDirty, Ct);
        if (savedScope == "working-tree") Assert.Empty(report.ScopeFiles!);
        else Assert.Contains(report.ScopeFiles!, file => file.Contains("file.txt", StringComparison.Ordinal));
        await jobs.CompleteRunAsync(runId, JobRunStatus.Failed, 1, "retry", Ct);
        var retry = (await jobs.GetRunAsync((await jobs.EnqueueRetryAsync(runId, Ct))!, Ct))!;
        Assert.Equivalent(run.ReviewLaunch, retry.ReviewLaunch);
    }

    [Fact]
    public async Task OtherAutomationsUsingTheStarterWorkerKeepTheirOrdinaryScopeDefault()
    {
        var seed = Assert.Single(await boards.GetPendingStarterWorkflowsAsync(project, Ct));
        var starterId = await jobs.EnsureBoardReviewRecipeAsync(project, seed.ColumnId, seed.RecipeId, Ct);
        var starter = (await jobs.GetJobAsync(starterId, Ct))!;
        var other = await jobs.CreateJobAsync(new(BoardReviewDefaults.Name, project, LLM.Codex,
            starter.EnvironmentId, "Review", null, true, []), Ct);
        var runId = (await jobs.EnqueueBoardCardRunAsync(project, other.Id, card.Key, Ct))!;
        var snapshot = (await jobs.GetRunAsync(runId, Ct))!.ReviewLaunch!.Resolution;
        Assert.Equal("working-tree", snapshot.Scope);
        Assert.Null(snapshot.Problem);
        Assert.Null(await boards.GetReviewSettingsAsync(project, card.Id, Ct));
    }

    [Fact]
    public async Task LaneQueueUsesTheSameSnapshotContract()
    {
        await Source("claude");
        var worker = new LLM_Environment { CustomName = "Switch", LLM = LLM.Claude, Purpose = "code_review", ReviewerRouting = ReviewerRouting.SwitchDefault() };
        await repository.SaveEnvironmentAsync(worker, Ct);
        var job = await jobs.CreateJobAsync(new("Lane review", project, LLM.Claude, worker.Id, "Review", null, true, []), Ct);
        var lane = (await boards.GetColumnsAsync(project, Ct)).Single(c => c.Name == "Review");
        await boards.SaveLaneAutomationAsync(project, lane.Id, [job.Id], 0, Ct);
        await boards.MoveCardAsync(project, card.Id, lane.Id, null, Ct);
        var id = Assert.Single(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(2), Ct));
        var run = (await jobs.GetRunAsync(id, Ct))!;
        Assert.Equal("codex", run.ReviewLaunch!.Resolution.Provider);
        Assert.Equal("claude", run.ReviewLaunch.Resolution.Source.Provider);
    }

    [Fact]
    public async Task RoutingHistoryCannotBeRewritten_AndOldWritersKeepFixedBehavior()
    {
        var r = (await Resolve()).Resolution;
        var review = new BoardReviewRecord("review", card.Id, r.Provider, r.Reviewer, DateTime.UtcNow, Routing: r);
        Assert.True(await boards.SaveReviewAsync(project, review, Ct));
        Assert.False(await boards.SaveReviewAsync(project, review with { Routing = r with { Overridden = true } }, Ct));
        await using var db = new SqliteConnection(state); await db.OpenAsync(Ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO Environments (CustomName, LLM, CreatedUTC, LastUsedUTC) VALUES ('Old writer', 2, $now, $now) RETURNING Id;";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct));
        Assert.Null((await repository.GetEnvironmentByIdAsync(id, Ct))!.ReviewerRouting);
        Assert.Null(await routing.PrepareAsync(project, id, card.Key, Ct));
    }

    [Fact]
    public async Task FixedPolicyUsesItsTarget_AndMalformedPoliciesAreRejected()
    {
        await Source("codex");
        var fixedPolicy = ReviewerRouting.SwitchDefault() with { Mode = "fixed" };
        Assert.Equal("codex", (await Resolve(fixedPolicy)).Resolution.Provider);
        Assert.False((await Resolve(fixedPolicy)).Resolution.UsedFallback);
        Assert.Throws<BoardValidationException>(() => ReviewRoutingService.Validate(fixedPolicy with { Mappings = null! }, "code_review"));
        Assert.Throws<BoardValidationException>(() => ReviewRoutingService.Validate(fixedPolicy with { Fallback = null! }, "code_review"));
        var upper = await Resolve(fixedPolicy with { Fallback = new("BASE:CODEX") });
        Assert.Equal("base:codex", upper.Resolution.Selected.Selection);
        Assert.Throws<BoardValidationException>(() => ReviewRoutingService.Validate(fixedPolicy with { Mappings = [new("openai", new("base:codex"))] }, "code_review"));
    }

    [Fact]
    public async Task SwitchWorkerStepReferencesRecordARecoverablePrerequisiteInsteadOfDroppingSteps()
    {
        var worker = new LLM_Environment { CustomName = "Switch with steps", LLM = LLM.Codex, Purpose = "code_review",
            ReviewerRouting = ReviewerRouting.SwitchDefault(), CustomPrompt = "Review {{step:configured-step}}" };
        await repository.SaveEnvironmentAsync(worker, Ct);
        var snapshot = (await routing.PrepareAsync(project, worker.Id, card.Key, Ct))!;
        Assert.Contains("Move the steps", snapshot.Resolution.Problem);
        var error = await Assert.ThrowsAsync<BoardValidationException>(() => routing.RequireLaunchAsync(snapshot, Ct));
        Assert.Contains("Move the steps", error.Message);
    }

    [Fact]
    public async Task DirectCustomReviewerPreservesProjectCheckout_AndClientCannotSetServerOnlyFlag()
    {
        var environment = new LLM_Environment { CustomName = "Cloned reviewer", LLM = LLM.Claude, ProjectPath = project,
            CustomArgs = "--model sonnet[1m]", WorkspaceMode = EnvironmentWorkspaceMode.PerRun };
        await repository.SaveEnvironmentAsync(environment, Ct);
        var tabs = new Mock<ITerminalTabHostService>();
        tabs.SetupGet(t => t.MaxTabs).Returns(8);
        tabs.Setup(t => t.ListTabsAsync(Ct)).ReturnsAsync([]);
        tabs.Setup(t => t.CreateTabAsync(Ct)).ReturnsAsync(new TerminalTabStatusResponse("tab", DateTime.UtcNow, false));
        StartTerminalRequest? request = null;
        tabs.Setup(t => t.StartSessionAsync("tab", It.IsAny<StartTerminalRequest>(), Ct))
            .Callback<string, StartTerminalRequest, CancellationToken>((_, r, _) => request = r)
            .ReturnsAsync(new TerminalStatusResponse(true, Guid.NewGuid().ToString(), "claude", project));
        var launcher = new BoardLaunchService(boards, repository, tabs.Object, new Mock<IBoardContextEstimator>().Object, routing);
        await launcher.LaunchAsync(project, card.Id, null, Ct, "code_review",
            new ReviewLaunchRequest(Override: new($"env:{environment.Id}:claude")));
        Assert.Equal(project, request!.WorkingDirectory);
        Assert.True(request.PreserveWorkingDirectory);
        Assert.Equal("claude", request.Cli);
        Assert.Equal(environment.CustomName, request.EnvironmentName);
        Assert.Contains("--- review routing data ---", request.InitialPrompt);
        var clientRequest = JsonSerializer.Deserialize("{\"preserveWorkingDirectory\":true}", AppJsonSerializerContext.Default.StartTerminalRequest)!;
        Assert.False(clientRequest.PreserveWorkingDirectory);
        Assert.DoesNotContain("preserveWorkingDirectory", JsonSerializer.Serialize(request, AppJsonSerializerContext.Default.StartTerminalRequest));
    }

    [Fact]
    public async Task RoutedJobArgumentsUseFrozenProviderModelAndCheckoutWithoutPermissionBypass()
    {
        var snapshot = await routing.ResolveAsync(project, card.Key, ReviewerRouting.SwitchDefault(), new("base:claude", new(Model: "sonnet[1m]")), Ct);
        var worker = new LLM_Environment { CustomName = "Switch argv", LLM = LLM.Codex, Purpose = "code_review", ReviewerRouting = ReviewerRouting.SwitchDefault() };
        await repository.SaveEnvironmentAsync(worker, Ct);
        var job = await jobs.CreateJobAsync(new("Review argv", project, LLM.Codex, worker.Id, "", null, true, []), Ct);
        var run = (await jobs.GetRunAsync((await jobs.EnqueueBoardCardRunAsync(project, job.Id, card.Key, Ct))!, Ct))!;
        var args = ReviewRoutingService.BuildJobArguments(run with { ReviewLaunch = snapshot }, null);
        Assert.Contains("claude", args); Assert.Contains(project, args); Assert.Contains("sonnet[1m]", args);
        Assert.DoesNotContain(args, a => a.Contains("dangerously") || a.Contains("bypass") || a.Contains("--yolo"));
    }

    [Fact]
    public async Task RoutedNativeAndTabLaunchersKeepTheSelectedEnvironmentAndProjectCheckout()
    {
        var environment = new LLM_Environment { CustomName = "Reviewer clone", LLM = LLM.Claude, ProjectPath = project,
            CustomArgs = "--model sonnet[1m]", WorkspaceMode = EnvironmentWorkspaceMode.PerRun };
        await repository.SaveEnvironmentAsync(environment, Ct);
        var snapshot = await routing.ResolveAsync(project, card.Key, ReviewerRouting.SwitchDefault(), new($"env:{environment.Id}:claude"), Ct);
        var run = new JobRunRecord("run", 1, JobTriggerKind.Manual, "manual:run", JobRunStatus.Queued, "Review", project,
            LLM.Claude, environment.Id, environment.CustomName, null, null, DateTime.UtcNow, null, null, null, null, false, null,
            Purpose: "code_review", ReviewLaunch: snapshot);
        var store = new Mock<IJobStore>();
        store.Setup(s => s.GetLaunchableRunsAsync(Ct)).ReturnsAsync([run]);
        store.Setup(s => s.TryClaimLaunchAsync(run.Id, It.IsAny<int>(), Ct)).ReturnsAsync(new JobLaunchClaim(JobLaunchClaimOutcome.Claimed, 1));
        var process = new Mock<IJobProcessLauncher>();
        IReadOnlyList<string>? arguments = null;
        process.Setup(p => p.Launch(project, It.IsAny<IReadOnlyList<string>>(), false))
            .Callback<string, IReadOnlyList<string>, bool>((_, a, _) => arguments = a).Returns(new LaunchResult(true, "started"));
        var environmentLauncher = new Mock<IEnvironmentLaunchService>(MockBehavior.Strict);
        Assert.Equal(1, await new JobLaunchService(store.Object, environmentLauncher.Object, process.Object, reviewRouting: routing).LaunchQueuedRunsAsync(Ct));
        Assert.Contains("--env-id", arguments!); Assert.Contains(environment.Id.ToString(), arguments!);
        Assert.Contains("sonnet[1m]", arguments!); environmentLauncher.VerifyNoOtherCalls();

        var tabs = new Mock<ITerminalTabHostService>();
        tabs.Setup(t => t.CreateAutomationTabAsync(run.Id, run.JobName, Ct)).ReturnsAsync(new TerminalTabStatusResponse("tab", DateTime.UtcNow, false));
        StartTerminalRequest? terminal = null; TerminalInputRequest? input = null;
        tabs.Setup(t => t.StartSessionAsync("tab", It.IsAny<StartTerminalRequest>(), Ct))
            .Callback<string, StartTerminalRequest, CancellationToken>((_, r, _) => terminal = r)
            .ReturnsAsync(new TerminalStatusResponse(true, "recording", "shell", project));
        tabs.Setup(t => t.SendInputAsync("tab", It.IsAny<TerminalInputRequest>(), Ct))
            .Callback<string, TerminalInputRequest, CancellationToken>((_, r, _) => input = r).ReturnsAsync(new TerminalInputResponse(true, "sent"));
        var workspaces = new Mock<IRunWorkspaceService>(MockBehavior.Strict);
        Assert.True((await new JobTerminalTabLauncher(tabs.Object, repository, store.Object, workspaces.Object, boards, reviewRouting: routing).LaunchAsync(run, Ct)).Success);
        Assert.Equal(project, terminal!.WorkingDirectory); Assert.Equal("shell", terminal.Cli);
        Assert.Contains("sonnet[1m]", input!.Text); Assert.Contains("--env-id", input.Text);
        workspaces.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BeginRejectsDifferentCheckoutScopeAndChangedInputs()
    {
        var snapshot = (await Resolve()).Resolution;
        var session = await Session("codex", "code_review");
        await boards.SaveReviewAsync(project, new("frozen", card.Id, "codex", "Codex", DateTime.UtcNow, SessionId: session, Routing: snapshot), Ct);
        var reviews = new BoardReviewService(boards, new Mock<IBoardService>().Object);
        await Assert.ThrowsAsync<BoardValidationException>(() => reviews.BeginAsync(project, card.Id, session, root, "working-tree", "changes", null, null, false, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => reviews.BeginAsync(project, card.Id, session, project, "repository", "changes", null, null, false, Ct));
        await File.WriteAllTextAsync(Path.Combine(project, "file.txt"), "changed after queue", Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => reviews.BeginAsync(project, card.Id, session, project, "working-tree", "changes", null, null, false, Ct));
    }

    private Task<ReviewLaunchSnapshot> Resolve(ReviewerRouting? policy = null) => routing.ResolveAsync(project, card.Key, policy ?? ReviewerRouting.SwitchDefault(), null, Ct);
    private async Task<string> Source(string cli)
    {
        var session = await Session(cli, "launch");
        await routing.SaveSettingsAsync(project, card.Id, new("session", session, "Implemented the selected changes"), Ct);
        return session;
    }
    private async Task<string> Session(string cli, string origin)
    {
        var id = Guid.NewGuid().ToString();
        await repository.CreateSessionAsync(id, cli, null, project, 1);
        await boards.LinkSessionAsync(project, card.Id, id, null, "base:" + cli, cli, "Session", origin, Ct);
        return id;
    }
    private async Task Git(params string[] args)
    {
        var result = await GitCli.RunAsync(project, args, Ct); Assert.True(result.Succeeded, result.StdErr);
    }
    private sealed class SnapshotFactory(ReviewRoutingService service) : IReviewRunSnapshotFactory
    {
        public Task<ReviewLaunchSnapshot?> PrepareAsync(string project, int workerId, string? cardKey, CancellationToken cancellationToken,
            string defaultScope = "working-tree")
            => service.PrepareAsync(project, workerId, cardKey, cancellationToken, defaultScope);
    }
    public ValueTask DisposeAsync()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true); return ValueTask.CompletedTask;
    }
}
