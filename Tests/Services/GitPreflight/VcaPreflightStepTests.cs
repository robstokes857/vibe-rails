using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.GitPreflight;

public sealed class VcaPreflightStepTests
{
    [Fact]
    public async Task ExecuteAsync_SurfacesWarnOnlyFindingsAsWarning()
    {
        var finding = new VcaRuleFinding(
            VcaRuleFindingKind.Warning,
            "WARN",
            "Package file changes",
            "package.json changed",
            "AGENTS.md",
            "Review this finding before committing.");
        var validation = new VcaHookValidationResult(
            "WARNINGS:\n  [WARN] Package file changes: package.json changed",
            new VcaHookValidationSummary(
                HasError: false,
                HasStopViolation: false,
                HasCommitViolations: false,
                RequiredAcknowledgments: [],
                StagedFileCount: 1,
                ApplicableRuleCount: 1,
                Findings: [finding]));
        var step = new VcaPreflightStep(new StubValidationService(validation));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vca-preflight-step-tests"));
        var request = new GitPreflightRequest(
            root,
            new VcaHookInvocation(
                VcaHookKind.PreCommit,
                CommitMessagePath: null,
                WorkingDirectory: root,
                DemoUi: false,
                DemoDuration: TimeSpan.Zero,
                PromptForAcknowledgment: false));
        var context = new GitPreflightStepContext(
            Guid.NewGuid().ToString("N"),
            request,
            new GitStagedSnapshot(root, [], []),
            (_, _, _) => ValueTask.CompletedTask);

        var result = await step.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(GitPreflightStepStatus.Warning, result.Status);
        Assert.Equal("1", result.Details!["warningCount"]);
        Assert.Same(validation.Summary, result.VcaSummary);
    }

    private sealed class StubValidationService(VcaHookValidationResult result)
        : IVcaHookValidationService
    {
        public Task<VcaHookValidationResult> ValidateAsync(
            VcaHookInvocation invocation,
            string workingDirectory,
            CancellationToken cancellationToken) => Task.FromResult(result);

        public Task<VcaHookValidationResult> ValidateAsync(
            VcaHookInvocation invocation,
            GitStagedSnapshot snapshot,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
