using System.Text.Json;
using Tests.Services.VCA;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Git;
using VibeRails.Services.Board;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.Launches;

[Collection("Rule launch environment")]
public sealed class RuleLaunchTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rule-launch-" + Guid.NewGuid().ToString("N"));
    private LaunchStore store = null!;
    private LaunchService service = null!;
    private LaunchEventService events = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Rule = "File Lock('protected.txt')";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(root);
        await Git("init", "-q");
        await Git("config", "user.email", "test@example.invalid");
        await Git("config", "user.name", "Test");
        await Git("config", "core.hooksPath", ".git/hooks");
        await Git("config", "commit.gpgsign", "false");
        store = OpenStore();
        var resolver = new Mock<IJobExecutableResolver>();
        resolver.Setup(x => x.Resolve(It.IsAny<JobScriptRuntime>()))
            .Returns(new JobExecutable("fixture-interpreter", []));
        service = new(store, new AutomationScriptService(resolver.Object));
        events = new(store, service);
    }

    private LaunchStore OpenStore() => new($"Data Source={Path.Combine(root, ".git", "launches.db")};Pooling=False");
    private async Task Git(params string[] args)
    {
        var result = await GitCli.RunAsync(root, args, Ct);
        Assert.True(result.Succeeded, result.StdErr);
    }
    private async Task Write(string path, string text)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, text, Ct);
    }
    private async Task Stage(string level = "STOP", string rule = Rule)
    {
        await Write("vc.rules.md", $"## Vibe Rails Rules\n- {rule} ({level})\n");
        await Write("protected.txt", "changed\n");
        await Git("add", "vc.rules.md", "protected.txt");
    }
    private async Task<LaunchTriggerBinding> Bind(bool script = false, string rule = Rule, string path = "vc.rules.md")
    {
        string id;
        if (script)
        {
            await Write("respond.py", "print('response')\n");
            id = (await service.SaveActionAsync(root, null, new("Response", "", "script",
                new("respond.py", JobScriptRuntime.Python, [])), Ct)).Id;
        }
        else id = (await service.SavePresetAsync(null, new("Response", "", "codex", InitialPrompt: "Resolve the violation"), Ct)).Preset.Id;
        return await service.SaveBindingAsync(root, null, new(id, script ? "action" : "preset",
            new(JobTriggerKind.RuleViolation), Rule: new(path, rule)), Ct);
    }
    private async Task<(int Code, string Output)> Hook(string kind = "pre-commit", bool enqueue = true, string message = "Change")
    {
        List<string> args = ["--vca-hook", kind, "--workdir", root];
        if (enqueue) args.Add("--enqueue-automations");
        if (kind == "commit-msg")
        {
            var path = Path.Combine(root, ".git", "COMMIT_EDITMSG");
            await File.WriteAllTextAsync(path, message, Ct);
            args.AddRange(["--commit-message", path]);
        }
        using var output = new StringWriter();
        var code = await VcaHookProcessHost.RunCoreAsync(args.ToArray(), services =>
        {
            services.AddSingleton(events);
            // No live user storage: the existing quality-exemption lookup is unrelated to admission.
            services.AddSingleton(Mock.Of<IBoardStore>());
            services.AddSingleton(Mock.Of<VibeRails.DB.IJobStore>(MockBehavior.Strict));
            services.AddSingleton(Mock.Of<VibeRails.DB.IReviewRequestStore>(MockBehavior.Strict));
        }, output, output, cancellationToken: Ct);
        return (code, output.ToString());
    }
    private async Task<IReadOnlyList<LaunchRun>> Runs() => (await OpenStore().ListRunsAsync(LaunchService.ProjectKey(root), ct: Ct)).Runs;

    [Theory]
    [InlineData("WARN", false, 0)]
    [InlineData("COMMIT", false, 0)]
    [InlineData("STOP", false, 1)]
    [InlineData("STOP", true, 1)]
    public async Task RealHookQueuesDurablyAndPreservesEnforcement(string level, bool script, int expectedCode)
    {
        await Stage(level);
        var binding = await Bind(script);
        var hook = await Hook();
        Assert.Equal(expectedCode, hook.Code);
        Assert.Contains("recorded run", hook.Output);
        var run = Assert.Single(await Runs()); // Another connection, no root/scheduler exists.
        Assert.Equal("queued", run.Status);
        Assert.Equal(binding.DefinitionId, run.DefinitionId);
        Assert.Equal(script ? "action" : "preset", run.DefinitionType);
        var evidence = Assert.IsType<RuleLaunchContext>(run.Snapshot.Context.RuleViolation);
        Assert.Equal(Rule, evidence.RuleText);
        Assert.Equal("vc.rules.md", evidence.SourcePath);
        Assert.Equal(level, evidence.Enforcement);
        Assert.Contains("protected.txt", evidence.ChangedFiles);
        Assert.Equal(root, evidence.RepositoryPath);
        Assert.Null(evidence.BaseCommit); // Unborn branch, no invented future commit.
        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(root, Ct);
        Assert.Equal(snapshot.StagedIdentity!.StagedTree, evidence.StagedTree);
        Assert.Equal(snapshot.StagedIdentity.BaseTree, evidence.BaseTree);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Reason));
        Assert.False(File.Exists(Path.Combine(root, ".git", "board.db")));
        Assert.Equal(!script, run.Snapshot.Context.NativeTerminal);
        if (level == "COMMIT")
        {
            Assert.Equal(1, (await Hook("commit-msg")).Code);
            Assert.Single(await Runs());
        }
    }

    [Fact]
    public async Task RepeatedHooksAndDuplicateBindingsShareOccurrenceButChangedStageIsNew()
    {
        await Stage();
        var binding = await Bind();
        await service.SaveBindingAsync(root, null, new(binding.DefinitionId, binding.DefinitionType,
            binding.Trigger, Rule: binding.Rule), Ct);
        await Hook();
        await Hook();
        await Hook("commit-msg");
        var original = Assert.Single(await Runs());
        await Write("protected.txt", "unstaged change\n");
        await Hook();
        Assert.Single(await Runs());
        await Git("add", "protected.txt");
        await Hook();
        var changed = Assert.Single((await Runs()).Where(run => run.Id != original.Id));
        Assert.NotEqual(original.Snapshot.Context.RuleViolation!.StagedTree, changed.Snapshot.Context.RuleViolation!.StagedTree);
        Assert.Equal(original.Snapshot.Context.RuleViolation.StagedTree, (await store.GetRunAsync(original.Id, Ct))!.Snapshot.Context.RuleViolation!.StagedTree);
    }

    [Theory]
    [InlineData("SKIP", Rule)]
    [InlineData("DISABLED", Rule)]
    [InlineData("STOP", "not a recognized rule")]
    [InlineData("STOP", "File Lock('unchanged.txt')")]
    public async Task NonViolationsNeverQueue(string level, string rule)
    {
        await Stage(level, rule);
        await Bind(rule: rule);
        Assert.Equal(0, (await Hook()).Code);
        Assert.Empty(await Runs());
    }

    [Fact]
    public async Task DeferredCommitMessageViolationQueuesOnlyWhenEvaluated()
    {
        const string rule = "Check commit message for: wip, temporary";
        await Stage("STOP", rule);
        await Bind(rule: rule);
        Assert.Equal(0, (await Hook()).Code);
        Assert.Empty(await Runs());
        Assert.Equal(0, (await Hook("commit-msg", message: "Complete change")).Code);
        Assert.Empty(await Runs());
        Assert.Equal(1, (await Hook("commit-msg", message: "WIP change")).Code);
        Assert.Equal("commit-msg", Assert.Single(await Runs()).Snapshot.Context.RuleViolation!.HookStage);
        await Hook("commit-msg", message: "WIP change again");
        Assert.Single(await Runs());
    }

    [Fact]
    public async Task OrdinaryValidationAndPreviewDoNotLaunch()
    {
        await Stage();
        await Bind();
        await Hook(enqueue: false);
        await Hook("commit-msg", enqueue: false);
        await Hook("preview");
        var provider = new GitStagedSnapshotProvider();
        var report = await RulesTool.ValidateVcaReportAsync(provider, root, cancellationToken: Ct);
        Assert.NotEmpty(report.Violations!);
        var invocation = new VcaHookInvocation(VcaHookKind.PreCommit, null, root, false, TimeSpan.Zero, false, EnqueueAutomatedJobs: true);
        var result = await new VcaRulesHookValidationService(provider).ValidateAsync(invocation, root, Ct);
        Assert.NotEmpty(result.Summary.Violations!);
        Assert.Empty(await Runs());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("retired")]
    [InlineData("changed-script")]
    [InlineData("binding-disabled")]
    public async Task IneligibleResponsesDoNotChangeStop(string state)
    {
        await Stage();
        var binding = await Bind(script: state == "changed-script");
        if (state == "changed-script") await Write("respond.py", "print('unapproved bytes')\n");
        if (state == "retired") await service.RetireAsync("preset", binding.DefinitionId, 1, Ct);
        if (state == "disabled")
        {
            var preset = (await store.GetPresetAsync(binding.DefinitionId, Ct))!;
            await store.SavePresetAsync(preset with { Enabled = false }, 1, Ct);
        }
        if (state == "binding-disabled")
            await service.SaveBindingAsync(root, binding.Id, new(binding.DefinitionId, binding.DefinitionType,
                binding.Trigger, false, binding.Revision, binding.Rule), Ct);
        var hook = await Hook();
        Assert.Equal(1, hook.Code);
        if (state != "binding-disabled") Assert.Contains("could not queue", hook.Output);
        Assert.Empty(await Runs());
    }

    [Fact]
    public async Task ContextDeliveryIsLiteralAndMarksOnlyTheChildEnvironment()
    {
        await Stage();
        await Bind();
        await Hook();
        var run = Assert.Single(await Runs());
        var path = (await RuleLaunchDelivery.WriteContextAsync(run, Ct, Path.Combine(root, ".git", "contexts")))!;
        var read = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, Ct), AppJsonSerializerContext.Default.RuleLaunchContext);
        Assert.Equal(Rule, read!.RuleText);
        var child = RuleLaunchDelivery.EnvironmentFor(run, path);
        Assert.Equal(run.Id, child[RuleLaunchDelivery.SuppressionVariable]);
        Assert.Equal(path, child[RuleLaunchDelivery.ContextVariable]);
        Assert.Contains(path.Replace("\\", "\\\\"), RuleLaunchDelivery.AppendPrompt("Saved task", path));
        Assert.False(RuleLaunchDelivery.IsResponseProcess);
    }

    [Fact]
    public async Task RuleBindingCannotSelectCheckActionsOrEscapeTheRepository()
    {
        await Stage();
        var action = await service.SaveActionAsync(root, null, new("Check", "", "vca", Check: new("working-tree")), Ct);
        await Assert.ThrowsAsync<LaunchValidationException>(() => service.SaveBindingAsync(root, null,
            new(action.Id, "action", new(JobTriggerKind.RuleViolation), Rule: new("vc.rules.md", Rule)), Ct));
        var binding = await Bind();
        await Assert.ThrowsAsync<LaunchValidationException>(() => service.SaveBindingAsync(root, null,
            new(binding.DefinitionId, "preset", binding.Trigger, Rule: new("../vc.rules.md", Rule)), Ct));
    }

    [Fact]
    public async Task OwnCommitsAreSuppressedEvenForChangedSnapshots()
    {
        await Stage();
        await Bind();
        await Hook();
        var run = Assert.Single(await Runs());
        var previous = Environment.GetEnvironmentVariable(RuleLaunchDelivery.SuppressionVariable);
        try
        {
            Environment.SetEnvironmentVariable(RuleLaunchDelivery.SuppressionVariable, run.Id);
            await Write("protected.txt", "agent edit\n");
            await Git("add", "protected.txt");
            var hook = await Hook();
            Assert.Equal(1, hook.Code);
            Assert.Contains("prevent a launch loop", hook.Output);
            await Hook("commit-msg");
            Assert.Single(await Runs());
        }
        finally { Environment.SetEnvironmentVariable(RuleLaunchDelivery.SuppressionVariable, previous); }
        await Hook();
        Assert.Equal(2, (await Runs()).Count);
    }

    [Fact]
    public async Task ConcurrentAdmissionHasOneDurableRun()
    {
        await Stage();
        await Bind();
        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(root, Ct);
        VcaRuleViolation[] violations = [new(Rule, "vc.rules.md", "STOP", "Locked file changed", ["protected.txt"])];
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            await events.EnqueueRuleViolationsAsync(snapshot, VcaHookKind.PreCommit, violations, Ct), Ct)));
        Assert.Single(await Runs());
    }

    [Fact]
    public async Task MissingTargetProducesDiagnosticWithoutChangingEnforcement()
    {
        await Stage();
        var binding = await Bind();
        var missing = new Mock<ILaunchStore>();
        missing.Setup(x => x.ListBindingsAsync(It.IsAny<string?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { binding });
        events = new(missing.Object, new(missing.Object, Mock.Of<IAutomationScriptService>()));
        var hook = await Hook();
        Assert.Equal(1, hook.Code);
        Assert.Contains("Preset is unavailable", hook.Output);
        Assert.Empty(await Runs());
    }

    [Fact]
    public async Task SameTextInDifferentFilesHasIndependentIdentityAndScope()
    {
        await Stage();
        await Write("nested/vc.rules.md", $"## Vibe Rails Rules\n- {Rule} (WARN)\n");
        await Write("nested/protected.txt", "nested change\n");
        await Git("add", "nested");
        var binding = await Bind();
        await service.SaveBindingAsync(root, null, new(binding.DefinitionId, binding.DefinitionType, binding.Trigger,
            Rule: new("nested/vc.rules.md", Rule)), Ct);
        await Hook();
        var runs = await Runs();
        Assert.Equal(2, runs.Count);
        var rootRun = Assert.Single(runs, run => run.Snapshot.Context.RuleViolation!.SourcePath == "vc.rules.md");
        var nestedRun = Assert.Single(runs, run => run.Snapshot.Context.RuleViolation!.SourcePath == "nested/vc.rules.md");
        Assert.NotEqual(rootRun.OccurrenceId, nestedRun.OccurrenceId);
        Assert.All(nestedRun.Snapshot.Context.RuleViolation!.ChangedFiles, path => Assert.StartsWith("nested/", path));
    }

    [Fact]
    public async Task MessageOnlyCommitAttemptCanQueueDeferredViolation()
    {
        const string rule = "Check commit message for: wip";
        await Stage("STOP", rule);
        await Git("commit", "-qm", "Baseline");
        await Bind(rule: rule);
        Assert.Equal(1, (await Hook("commit-msg", message: "WIP amended message")).Code);
        var evidence = Assert.Single(await Runs()).Snapshot.Context.RuleViolation!;
        Assert.Empty(evidence.ChangedFiles);
        Assert.NotNull(evidence.BaseCommit);
        Assert.Equal(evidence.BaseTree, evidence.StagedTree);
    }

    [Fact]
    public async Task RemovedRuleCanBeDisabledAndCannotBeReenabledUntilRestored()
    {
        await Stage();
        var binding = await Bind();
        await Write("vc.rules.md", "## Vibe Rails Rules\n");
        var disabled = await service.SaveBindingAsync(root, binding.Id, new(binding.DefinitionId, binding.DefinitionType,
            binding.Trigger, false, binding.Revision, binding.Rule), Ct);
        Assert.False(disabled.Enabled);
        await Assert.ThrowsAsync<LaunchValidationException>(() => service.SaveBindingAsync(root, binding.Id,
            new(binding.DefinitionId, binding.DefinitionType, binding.Trigger, true, disabled.Revision, binding.Rule), Ct));
    }

    [Fact]
    public async Task DisableBetweenCaptureAndEnqueueIsRecheckedByStore()
    {
        await Stage();
        var binding = await Bind();
        await Hook();
        var captured = Assert.Single(await Runs()).Snapshot;
        await service.SaveBindingAsync(root, binding.Id, new(binding.DefinitionId, binding.DefinitionType,
            binding.Trigger, false, binding.Revision, binding.Rule), Ct);
        await Assert.ThrowsAsync<LaunchConflictException>(() => store.EnqueueAsync(captured with
        { Context = captured.Context with { OccurrenceId = "new-occurrence" } }, ct: Ct));
        Assert.Single(await Runs());
    }

    [Theory]
    [InlineData(VcaHookKind.PreCommit, false)]
    [InlineData(VcaHookKind.PreCommit, true)]
    [InlineData(VcaHookKind.CommitMessage, false)]
    [InlineData(VcaHookKind.CommitMessage, true)]
    public async Task StyledConsoleShowsAdmissionResultsEvenWhenGenericAutomationsAreSkipped(VcaHookKind kind, bool retired)
    {
        await Stage();
        var binding = await Bind();
        if (retired) await service.RetireAsync("preset", binding.DefinitionId, 1, Ct);
        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(root, Ct);
        var invocation = new VcaHookInvocation(kind, null, root, false, TimeSpan.Zero, false, EnqueueAutomatedJobs: true);
        var vca = new GitPreflightStepResult(VcaPreflightStep.Id, "VCA", GitPreflightStepStatus.Blocked,
            "Blocked", [], 0, true, VcaSummary: new(false, true, false, [], Violations:
                [new(Rule, "vc.rules.md", "STOP", "Locked file changed", ["protected.txt"])]));
        var result = await new AutomatedWorkflowsPreflightStep(launches: events).ExecuteAsync(
            new("test", new(root, invocation, EnqueueAutomatedJobs: true), snapshot,
                (_, _, _) => ValueTask.CompletedTask, [vca]), Ct);
        Assert.False(result.Blocking);
        using var output = new StringWriter();
        var presenter = new VcaConsoleHookPresenter(new VcaHookConsoleOptions(output, output,
            new StringReader(""), false, VcaConsoleStyle.Ansi, 120));
        GitPreflightEvent Event(GitPreflightEventType type, GitPreflightStepStatus status, string message) =>
            new("test", 1, DateTimeOffset.UtcNow, type, result.StepId, status, message, StepNumber: 3, StepCount: 3);
        await presenter.WritePreflightEventAsync(Event(GitPreflightEventType.StepStarted,
            GitPreflightStepStatus.Running, result.DisplayName), Ct);
        foreach (var line in result.Output)
            await presenter.WritePreflightEventAsync(Event(GitPreflightEventType.StepOutput, result.Status, line), Ct);
        await presenter.WritePreflightEventAsync(Event(GitPreflightEventType.StepFinished, result.Status, result.Summary), Ct);
        Assert.Contains(retired ? "could not queue" : "recorded run", AnsiText.Strip(output.ToString()));
    }

    public ValueTask DisposeAsync()
    {
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing non-temp cleanup.");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
        return ValueTask.CompletedTask;
    }
}

[CollectionDefinition("Rule launch environment", DisableParallelization = true)]
public sealed class RuleLaunchEnvironmentCollection;
