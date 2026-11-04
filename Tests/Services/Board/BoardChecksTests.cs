using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardChecksTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-checks-" + Guid.NewGuid().ToString("N"));
    private string repo = null!, connection = null!, baseline = null!;
    private BoardStore store = null!;
    private BoardCardRecord card = null!;
    private ServiceProvider services = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo);
        await Git("init"); await Git("config", "user.email", "tests@local"); await Git("config", "user.name", "Checks");
        await Write("example.cs", "class Original {}\n");
        await Git("add", "."); await Git("-c", "core.hooksPath=", "commit", "-m", "initial");
        baseline = await Git("rev-parse", "HEAD");
        connection = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new BoardStore(connection, connection);
        await store.EnsureDefaultColumnsAsync(repo, Ct);
        card = await store.CreateCardAsync(repo, new(null, "Check", "", null, "medium", null, [], false), Ct);
        services = new ServiceCollection().AddSingleton<IBoardStore>(store).BuildServiceProvider();
    }

    private BoardCheckService Engine(IVcaHookValidationService? validator = null) =>
        new(new GitStagedSnapshotProvider(), validator ?? new VcaRulesHookValidationService(new GitStagedSnapshotProvider()), services);
    private Task<BoardCheckRecord> Run(JobActionKind kind, params string[] args) => Engine().ExecuteAsync(repo, repo,
        card.Key, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), kind, args, Ct);

    [Theory]
    [InlineData(JobActionKind.CodeQuality, "working-tree", false)]
    [InlineData(JobActionKind.CodeQuality, "working-tree", true)]
    [InlineData(JobActionKind.CodeQuality, "repository", false)]
    [InlineData(JobActionKind.CodeQuality, "repository", true)]
    [InlineData(JobActionKind.CodeQuality, "unpushed", false)]
    [InlineData(JobActionKind.CodeQuality, "unpushed", true)]
    [InlineData(JobActionKind.Vca, "working-tree", true)]
    [InlineData(JobActionKind.Vca, "repository", true)]
    [InlineData(JobActionKind.Vca, "unpushed", true)]
    public async Task UnbornBranchHasNothingToCheckAndPreservesWorkingFiles(JobActionKind kind, string scope, bool hasFiles)
    {
        var workspace = Path.Combine(root, "unborn");
        Directory.CreateDirectory(workspace);
        Assert.True((await GitCli.RunAsync(workspace, ["init", "--initial-branch=main"], Ct)).Succeeded);
        if (hasFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "staged.cs"), "class Staged {}\n", Ct);
            Assert.True((await GitCli.RunAsync(workspace, ["add", "staged.cs"], Ct)).Succeeded);
            await File.WriteAllTextAsync(Path.Combine(workspace, "untracked.cs"), "class Untracked {}\n", Ct);
        }
        var before = await GitCli.RunAsync(workspace, ["status", "--porcelain"], Ct);

        var result = await Engine().ExecuteAsync(repo, workspace, card.Key, "unborn", "check", kind, [scope], Ct);

        Assert.Equal("Skipped/not applicable", result.Status);
        Assert.Equal("Nothing to check: this branch has no commits yet.", result.Summary);
        Assert.Equal(0, result.FileCount);
        Assert.Equal(0, result.AnalyzedCount);
        Assert.Null(result.BaseCommit);
        Assert.Null(result.HeadCommit);
        Assert.Null(result.SnapshotHash);
        Assert.Null(result.Evidence);
        Assert.NotNull(result.EndedUtc);
        Assert.Contains(result.Limitations, text => text.Contains("no files were analyzed"));
        var saved = (await store.GetCheckAsync(repo, card.Id, result.Id, Ct))!;
        Assert.Equal(result.Status, saved.Status);
        Assert.Equal(result.Summary, saved.Summary);
        Assert.Equal(result.Limitations, saved.Limitations);
        Assert.NotNull(saved.EndedUtc);
        Assert.Equal(before.StdOut, (await GitCli.RunAsync(workspace, ["status", "--porcelain"], Ct)).StdOut);
        Assert.False((await GitCli.RunAsync(workspace, ["rev-parse", "--verify", "HEAD"], Ct)).Succeeded);
        if (hasFiles)
        {
            Assert.Equal("class Staged {}\n", await File.ReadAllTextAsync(Path.Combine(workspace, "staged.cs"), Ct));
            Assert.Equal("class Untracked {}\n", await File.ReadAllTextAsync(Path.Combine(workspace, "untracked.cs"), Ct));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidHeadStillFailsInsteadOfClaimingNothingToCheck(bool symbolic)
    {
        var invalid = new string('f', 40) + "\n";
        if (symbolic)
        {
            await Git("symbolic-ref", "HEAD", "refs/heads/broken");
            await File.WriteAllTextAsync(Path.Combine(repo, ".git", "refs", "heads", "broken"), invalid, Ct);
        }
        else await File.WriteAllTextAsync(Path.Combine(repo, ".git", "HEAD"), invalid, Ct);

        var result = await Run(JobActionKind.CodeQuality, "working-tree");

        Assert.Equal("Failed to run", result.Status);
        Assert.Contains("Cannot resolve HEAD", result.Summary);
    }

    [Fact]
    public async Task ExplicitRangeDoesNotRequireACommitOnTheCurrentBranch()
    {
        await Write("example.cs", "class Committed {}\n");
        await Git("add", ".");
        await Git("-c", "core.hooksPath=", "commit", "-m", "feature");
        var head = await Git("rev-parse", "HEAD");
        await Git("symbolic-ref", "HEAD", "refs/heads/unborn");

        var result = await Run(JobActionKind.CodeQuality, "range", baseline, head);

        Assert.Equal("Passed", result.Status);
        Assert.Equal(1, result.AnalyzedCount);
        Assert.Equal(head, result.HeadCommit);
    }

    [Fact]
    public async Task SavedEvidenceRetainsExactSourcesAndIgnoresAfterCheckoutChanges()
    {
        const string current = "// retained comment\r\nclass Changed { public string Name => \"λ\"; }\r\n";
        await Write("example.cs", current);
        await Write("ignored.cs", "class Ignored {}\n");
        await Write("vc.rules.md", "## Vibe Rails Rules\n- WARN: Check the source\n");
        await File.WriteAllBytesAsync(Path.Combine(repo, "binary.dat"), [0, 1, 2, 0, 3], Ct);
        var rule = new CodeAnalyzerIgnoredFile("ignored.cs", "file", "test", "Fixture code", DateTime.UtcNow);
        var ignores = new Mock<ICodeAnalyzerIgnoreStore>();
        ignores.Setup(x => x.GetIgnoreRulesAsync(repo, It.IsAny<CancellationToken>())).ReturnsAsync([rule]);
        var engine = new BoardCheckService(new(), new VcaRulesHookValidationService(new GitStagedSnapshotProvider()), services, ignores.Object);
        var result = await engine.ExecuteAsync(repo, repo, card.Key, "evidence", "quality", JobActionKind.CodeQuality, ["working-tree"], Ct);
        Assert.Equal(1, result.AnalyzedCount);
        ignores.Verify(x => x.GetIgnoreRulesAsync(repo, It.IsAny<CancellationToken>()), Times.Once);

        await Write("example.cs", "class Later {}\n");
        File.Delete(Path.Combine(repo, "ignored.cs"));
        var reopened = new BoardStore(connection, connection);
        var saved = (await reopened.GetCheckAsync(repo, card.Id, result.Id, Ct))!;
        Assert.Equal(result.ResultJson, saved.ResultJson);
        var evidence = Assert.IsType<BoardCheckEvidence>(saved.Evidence);
        var source = Assert.Single(evidence.Files, f => f.Path == "example.cs");
        Assert.Equal(current, source.Content);
        Assert.Equal("class Original {}\n", source.PreviousContent);
        Assert.NotNull(source.AddedContent);
        Assert.NotEmpty(source.AddedLineNumbers!);
        Assert.Equal("class Ignored {}\n", Assert.Single(evidence.Files, f => f.Path == "ignored.cs").Content);
        Assert.Null(Assert.Single(evidence.Files, f => f.Path == "binary.dat").Content);
        Assert.Equal(rule, Assert.Single(evidence.IgnoreRules!));
        Assert.Contains(evidence.RuleFiles, f => f.Path == "vc.rules.md" && f.Content.Contains("Check the source"));
        Assert.Contains(evidence.ImpactFiles!, f => f.Path == "example.cs" && f.Content == current);
        Assert.All(await reopened.GetChecksAsync(repo, card.Id, cancellationToken: Ct), row => Assert.Null(row.Evidence));
        Assert.All(await reopened.GetLatestChecksAsync(repo, card.Id, Ct), row => Assert.Null(row.Evidence));
        Assert.StartsWith("Stale", await engine.FreshnessAsync(repo, saved, Ct));
    }

    [Fact]
    public async Task OlderEvidenceWithoutSourcesStillLoadsAndFailedAnalysisKeepsCapturedText()
    {
        await Write("example.cs", "class BeforeFailure {}\n");
        var validation = new Mock<IVcaHookValidationService>();
        validation.Setup(x => x.ValidateAsync(It.IsAny<VcaHookInvocation>(), It.IsAny<GitStagedSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("analysis failed"));
        var failed = await Engine(validation.Object).ExecuteAsync(repo, repo, card.Key, "failure-evidence", "vca", JobActionKind.Vca, ["working-tree"], Ct);
        Assert.Equal("Failed to run", failed.Status);
        Assert.Equal("class BeforeFailure {}\n", Assert.Single(failed.Evidence!.Files).Content);
        var legacy = failed with { Id = "legacy", RunId = "legacy", Evidence = null };
        Assert.True(await store.SaveCheckAsync(repo, legacy, Ct));
        Assert.Null((await store.GetCheckAsync(repo, card.Id, legacy.Id, Ct))!.Evidence);
        Assert.NotNull((await store.GetCheckAsync(repo, card.Id, failed.Id, Ct))!.Evidence);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("--all")]
    [InlineData("0123456")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData(null)]
    public async Task FreshnessRejectsInvalidHistoricalRangeRevisionsBeforeGit(string? revision)
    {
        var valid = new BoardCheckRecord("old", card.Id, "run", "action", "VCA", "Passed", "range",
            baseline, baseline, "hash", null, "1", DateTime.UtcNow, DateTime.UtcNow, "Stored", 0, 0, 0, 0, []);
        foreach (var record in new[] { valid with { BaseCommit = revision }, valid with { HeadCommit = revision } })
            Assert.Equal("Unknown freshness: scope is unavailable", await Engine().FreshnessAsync(repo, record, Ct));
        // Capture itself rejects an invalid range before a git process can consume it.
        await Assert.ThrowsAsync<ArgumentException>(() => Engine().CaptureAsync(repo, new("range", revision, baseline), Ct));
    }

    [Fact]
    public async Task ReaderShowsQueueAndCancelledBeforeCheck_AndReconcilesInterruptedEvidence()
    {
        await using (var state = new SqliteConnection(connection))
        {
            await state.OpenAsync(Ct); await using var command = state.CreateCommand();
            command.CommandText = SqlStrings.CreateEnvironmentsTable;
            await command.ExecuteNonQueryAsync(Ct);
        }
        var jobs = new JobStore(connection, store);
        var job = await jobs.CreateJobAsync(new("Checks", repo, LLM.NotSet, null, "", null, true, [],
            Actions: ReviewCheckDefaults.Create()), Ct);
        var run = (await jobs.EnqueueBoardCardRunAsync(repo, job.Id, card.Key, Ct))!;
        var reader = new BoardChecksReader(store, jobs, Engine());
        var queued = (await reader.ReadAsync(repo, card.Id, 0, Ct))!;
        Assert.Equal(2, queued.Pending.Count); Assert.All(queued.Pending, item => Assert.Equal("Waiting", item.Status));
        await jobs.CompleteRunAsync(run, JobRunStatus.Cancelled, 3, "cancelled", Ct);
        var cancelled = (await reader.ReadAsync(repo, card.Id, 0, Ct))!;
        Assert.All(cancelled.Pending, item => Assert.Equal("Cancelled", item.Status));
        var actions = await jobs.GetRunActionsAsync(run, Ct);
        var incomplete = new BoardCheckRecord("incomplete", card.Id, run, actions[0].Id, "Code quality", "Running", "unpushed",
            null, null, null, null, "1", DateTime.UtcNow, null, "capture", 0, 0, 0, 0, []);
        Assert.True(await store.SaveCheckAsync(repo, incomplete, Ct));
        var afterRestart = (await reader.ReadAsync(repo, card.Id, 0, Ct))!;
        Assert.Equal("Cancelled", Assert.Single(afterRestart.Checks).Status);
    }

    [Fact]
    public async Task LatestPerToolSurvivesMoreThanOnePageOfReruns()
    {
        var old = await Run(JobActionKind.Vca, "working-tree");
        for (var i = 0; i < 51; i++)
            Assert.True(await store.SaveCheckAsync(repo, old with { Id = $"q{i}", ActionId = $"q{i}", Tool = "Code quality",
                StartedUtc = old.StartedUtc.AddSeconds(i + 1) }, Ct));
        Assert.Equal(50, (await store.GetChecksAsync(repo, card.Id, cancellationToken: Ct)).Count);
        Assert.Equal(2, (await store.GetChecksAsync(repo, card.Id, 50, Ct)).Count);
        var latest = await store.GetLatestChecksAsync(repo, card.Id, Ct);
        Assert.Equal(2, latest.Count); Assert.Contains(latest, row => row.Id == old.Id);
    }

    [Fact]
    public async Task WorkingTreeIncludesUnrelatedFiles_CleanAfterCommitIsSkipped_AndHistorySurvivesRestart()
    {
        await Write("example.cs", "class Changed { public int Add(int a, int b) => a + b; }\n");
        await Write("unrelated.xyz", "unsupported\n");
        var first = await Run(JobActionKind.CodeQuality, "working-tree");
        Assert.Equal(2, first.FileCount);
        Assert.Equal(1, first.AnalyzedCount); Assert.Equal(1, first.SkippedCount);
        Assert.NotEqual("Passed", first.Status);
        Assert.Contains(first.ScopeFiles!, path => path.Contains("unrelated.xyz"));
        Assert.NotNull(first.ResultJson); Assert.NotNull(first.SnapshotHash);
        await Git("add", "."); await Git("-c", "core.hooksPath=", "commit", "-m", "changes");
        var second = await Run(JobActionKind.CodeQuality, "working-tree");
        Assert.Equal("Skipped/not applicable", second.Status); Assert.Equal(0, second.AnalyzedCount);
        Assert.StartsWith("Stale", await Engine().FreshnessAsync(repo, first, Ct));
        var reopened = new BoardStore(connection, connection);
        var history = await reopened.GetChecksAsync(repo, card.Id, cancellationToken: Ct);
        Assert.Equal(2, history.Count); Assert.All(history, item => Assert.Null(item.ResultJson));
        Assert.Equal(first.ResultJson, (await reopened.GetCheckAsync(repo, card.Id, first.Id, Ct))!.ResultJson);
        Assert.False(await reopened.SaveCheckAsync(repo, first with { Summary = "overwrite" }, Ct));
        Assert.Null(await reopened.GetCheckAsync(repo + "-other", card.Id, first.Id, Ct));
        Assert.Empty(await reopened.GetChecksAsync(repo + "-other", card.Id, cancellationToken: Ct));
        Assert.False(await reopened.SaveCheckAsync(repo + "-other", first with { Id = "foreign", ActionId = "foreign" }, Ct));
    }

    [Fact]
    public async Task RangeUsesCommittedBlobsAndRules_IgnoresDirtyFiles_AndValidatesAncestry()
    {
        await Write("example.cs", "class Committed {}\n");
        await Git("add", "."); await Git("-c", "core.hooksPath=", "commit", "-m", "feature");
        var head = await Git("rev-parse", "HEAD");
        await Write("example.cs", "class Dirty {}\n");
        await Write("extra.cs", "class Extra {}\n");
        var snapshot = await Engine().CaptureAsync(repo, new("range", baseline, head), Ct);
        Assert.Contains("Committed", Assert.Single(snapshot.Files).Content);
        Assert.Equal(head, snapshot.CheckIdentity!.HeadCommit);
        var result = await Run(JobActionKind.CodeQuality, "range", baseline, head);
        Assert.Equal(1, result.AnalyzedCount); Assert.Equal("Passed", result.Status);
        Assert.Equal(baseline, result.BaseCommit); Assert.Equal(head, result.HeadCommit);
        Assert.StartsWith("Captured inputs match", await Engine().FreshnessAsync(repo, result, Ct));
        var reversed = await Run(JobActionKind.CodeQuality, "range", head, baseline);
        Assert.Equal("Failed to run", reversed.Status);
        var repository = await Run(JobActionKind.CodeQuality, "repository");
        Assert.Equal(1, repository.FileCount);
    }

    [Fact]
    public async Task MissingUpstreamAndUnsupportedFilesCannotPass_WithoutBoardContextStillRuns()
    {
        var noUpstream = await Run(JobActionKind.CodeQuality, "unpushed");
        Assert.Equal("Failed to run", noUpstream.Status);
        await Write("unknown.xyz", "unsupported\n");
        var unsupported = await Run(JobActionKind.CodeQuality, "working-tree");
        Assert.Equal("Skipped/not applicable", unsupported.Status);
        var standalone = await Engine().ExecuteAsync(repo, repo, null, "run", "action", JobActionKind.CodeQuality, ["working-tree"], Ct);
        Assert.Equal("", standalone.CardId);
        Assert.Equal("Skipped/not applicable", standalone.Status);
    }

    [Fact]
    public async Task VcaKeepsEnforcement_DeferredAndMissingRulesAreNotGreen_CancellationIsSaved()
    {
        await Write("example.cs", "class Changed {}\n");
        var noRules = await Run(JobActionKind.Vca, "working-tree");
        Assert.Equal("Skipped/not applicable", noRules.Status);
        var validation = new Mock<IVcaHookValidationService>();
        validation.Setup(v => v.ValidateAsync(It.IsAny<VcaHookInvocation>(), It.IsAny<GitStagedSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VcaHookValidationResult("STOP: rule", new(true, true, true, ["ack"], 1, 3,
                [new(VcaRuleFindingKind.Warning, "WARN", "warning", "reason", "vc.rules.md", "guidance"),
                 new(VcaRuleFindingKind.Blocked, "STOP", "stop", "reason", "vc.rules.md", "guidance"),
                 new(VcaRuleFindingKind.AcknowledgmentRequired, "COMMIT", "ack", "reason", "vc.rules.md", "guidance")] )));
        var failed = await Engine(validation.Object).ExecuteAsync(repo, repo, card.Key, "failure", "vca", JobActionKind.Vca, ["working-tree"], Ct);
        Assert.Equal("Failed to run", failed.Status);
        Assert.Contains("STOP", failed.ResultJson); Assert.Contains("COMMIT", failed.ResultJson); Assert.Contains("WARN", failed.ResultJson);
        validation.Setup(v => v.ValidateAsync(It.IsAny<VcaHookInvocation>(), It.IsAny<GitStagedSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var cancelled = await Engine(validation.Object).ExecuteAsync(repo, repo, card.Key, "cancel", "vca", JobActionKind.Vca, ["working-tree"], Ct);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("Cancelled", (await store.GetCheckAsync(repo, card.Id, cancelled.Id, Ct))!.Status);
    }

    [Theory]
    [InlineData(GitPreflightStepStatus.Blocked, 1, 0, "Findings")]
    [InlineData(GitPreflightStepStatus.Passed, 1, 1, "Skipped/not applicable")]
    [InlineData(GitPreflightStepStatus.Passed, 0, 0, "Skipped/not applicable")]
    [InlineData(GitPreflightStepStatus.Passed, 1, 0, "Passed")]
    public void VcaStatusDoesNotHideMissingCoverage(GitPreflightStepStatus status, int rules, int deferred, string expected)
    {
        var record = new BoardCheckRecord("id", "card", "run", "action", "VCA", "Running", "working-tree",
            null, null, null, null, "1", DateTime.UtcNow, null, "", 0, 0, 0, 0, []);
        var result = new GitPreflightStepResult("vca", "VCA", status, "summary", [], 0, true,
            new Dictionary<string, string> { ["deferredCount"] = deferred.ToString() }, new(false, status == GitPreflightStepStatus.Blocked, false, [], 0, rules));
        Assert.Equal(expected, BoardCheckService.Complete(record, new(repo, [], []), result, JobActionKind.Vca).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VcaCountsDeletionsAsSkippedCoverage(bool isBinary)
    {
        var record = new BoardCheckRecord("id", "card", "run", "action", "VCA", "Running", "working-tree",
            null, null, null, null, "1", DateTime.UtcNow, null, "", 0, 0, 0, 0, []);
        var result = new GitPreflightStepResult("vca", "VCA", GitPreflightStepStatus.Passed, "summary", [], 0, true,
            VcaSummary: new(false, false, false, [], 0, 1));
        var snapshot = new GitStagedSnapshot(repo,
            [new("deleted.cs", Path.Combine(repo, "deleted.cs"), GitStagedChangeKind.Deleted, false, isBinary, 0, null)], []);

        var completed = BoardCheckService.Complete(record, snapshot, result, JobActionKind.Vca);

        Assert.Equal(1, completed.SkippedCount);
        Assert.Equal("Skipped/not applicable", completed.Status);
        Assert.Contains(completed.Limitations!, limitation => limitation.Contains("deleted"));
    }

    private Task Write(string name, string content) => File.WriteAllTextAsync(Path.Combine(repo, name), content, Ct);
    private async Task<string> Git(params string[] args)
    {
        var result = await GitCli.RunAsync(repo, args, Ct); Assert.True(result.Succeeded, result.StdErr); return result.StdOut.Trim();
    }
    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
