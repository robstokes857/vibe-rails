using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Board;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.GitPreflight;

[Collection("ProcessEnvIsolation")]
public sealed class CodeQualityExemptionHookTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "exemption-hook"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string Messy = "public class Messy { public int Run(int a, int b) { "
        + string.Join("\n", Enumerable.Range(0, 8).Select(i => $"var dependency{i} = new Repository();"))
        + string.Join("\n", Enumerable.Range(0, 90).Select(i => $"if (a == {i} && b > 0) {{ a += b; }}"))
        + " return a; } }";

    [Theory]
    [InlineData("first", true)]
    [InlineData("second", false)]
    [InlineData(null, false)]
    public async Task HookUsesOnlyItsOwnCardsExemptionsForBothEngines(string? session, bool exempt)
    {
        var store = Store();
        using var services = new ServiceCollection().AddSingleton(store.Object).BuildServiceProvider();
        var provider = new SnapshotProvider(Snapshot());
        var pipeline = new GitPreflightPipeline(provider, provider,
            [new VcaPreflightStep(new VcaRulesHookValidationService(provider)), new MintLintPreflightStep(), new JobsStep()],
            new CodeQualityExemptionService(services));
        var previous = Environment.GetEnvironmentVariable(LocalToolApiContext.CurrentSessionIdVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalToolApiContext.CurrentSessionIdVariable, session);
            var result = await pipeline.RunAsync(Request(), cancellationToken: Ct);
            Assert.Equal(exempt, result.CommitAllowed);
            var mint = Assert.Single(result.Steps, step => step.StepId == MintLintPreflightStep.Id);
            Assert.Equal(exempt ? "0" : "1", mint.Details!["supportedFileCount"]);
            Assert.Equal(exempt ? "1" : "0", mint.Details["ignoredFileCount"]);
        }
        finally { Environment.SetEnvironmentVariable(LocalToolApiContext.CurrentSessionIdVariable, previous); }
    }

    [Fact]
    public async Task ExemptionsNeverBypassOtherVcaRulesOrFollowASessionIntoAnotherRepository()
    {
        var store = Store();
        using var services = new ServiceCollection().AddSingleton(store.Object).BuildServiceProvider();
        var service = new CodeQualityExemptionService(services);
        var previous = Environment.GetEnvironmentVariable(LocalToolApiContext.CurrentSessionIdVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalToolApiContext.CurrentSessionIdVariable, "first");
            var snapshot = Snapshot() with { AgentFiles = [new("vc.rules.md", "## Vibe Rails Rules\n- File Lock('generated.cs') (STOP)\n- Code quality minimum C (STOP)\n")] };
            var report = await new VcaRulesHookValidationService(new SnapshotProvider(snapshot), exemptions: service)
                .ValidateAsync(Request().Invocation, snapshot, Ct);
            Assert.True(report.Summary.HasStopViolation);
            Assert.Contains("File Lock", report.Output);
            Assert.Contains("no added code", report.Output);
            Assert.Empty(await service.GetRulesAsync(Root + "-other", Ct));
        }
        finally { Environment.SetEnvironmentVariable(LocalToolApiContext.CurrentSessionIdVariable, previous); }
    }

    [Fact]
    public async Task UnreadableExemptionsDoNotBypassTheQualityGate()
    {
        var store = new Mock<IBoardStore>();
        store.Setup(s => s.GetCodeQualityExemptionsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unavailable"));
        using var services = new ServiceCollection().AddSingleton(store.Object).BuildServiceProvider();
        var provider = new SnapshotProvider(Snapshot());
        var pipeline = new GitPreflightPipeline(provider, provider,
            [new VcaPreflightStep(new VcaRulesHookValidationService(provider)), new MintLintPreflightStep(), new JobsStep()],
            new CodeQualityExemptionService(services));
        var events = new List<GitPreflightEvent>();
        var result = await pipeline.RunAsync(Request(), (e, _) => { events.Add(e); return ValueTask.CompletedTask; }, Ct);
        Assert.False(result.CommitAllowed);
        Assert.Contains(events, e => e.Message.Contains("no files were exempted"));
    }

    private static Mock<IBoardStore> Store()
    {
        var store = new Mock<IBoardStore>();
        store.Setup(s => s.FindSessionLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string session, CancellationToken _) => new BoardSessionLink(session, session, Root));
        store.Setup(s => s.GetCodeQualityExemptionsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string project, string? card, CancellationToken _) => project == Root && card == "first"
                ? [new CodeQualityExemption("exempt", "generated.cs", "first", "Generated fixture", BoardAuthor.User(), DateTime.UtcNow)]
                : []);
        return store;
    }

    private static GitStagedSnapshot Snapshot() => new(Root,
        [new("generated.cs", Path.Combine(Root, "generated.cs"), GitStagedChangeKind.Modified, true, false, 120, Messy, AddedContent: Messy)],
        [new("vc.rules.md", "## Vibe Rails Rules\n- Code quality minimum C (STOP)\n")]);

    private static GitPreflightRequest Request() => new(Root,
        new(VcaHookKind.PreCommit, null, Root, false, TimeSpan.Zero, false), IncludeReport: false);

    private sealed class SnapshotProvider(GitStagedSnapshot snapshot) : IGitStagedSnapshotProvider, IGitWorkingTreeSnapshotProvider
    {
        public Task<GitStagedSnapshot> CaptureAsync(string workingDirectory, CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task<GitStagedSnapshot> CaptureWorkingTreeAsync(string workingDirectory, CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task<GitStagedSnapshot> CaptureUnpushedAsync(string workingDirectory, CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class JobsStep : IGitPreflightStep
    {
        public string StepId => AutomatedWorkflowsPreflightStep.Id;
        public string DisplayName => "Jobs";
        public bool CanBlock => false;
        public Task<GitPreflightStepResult> ExecuteAsync(GitPreflightStepContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new GitPreflightStepResult(StepId, DisplayName, GitPreflightStepStatus.Skipped, "No jobs", [], 0, false));
    }
}
