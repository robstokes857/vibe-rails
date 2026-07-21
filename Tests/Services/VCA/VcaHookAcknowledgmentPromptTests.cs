using VibeRails.Services.VCA.Hooks;
using VibeRails.Services.GitPreflight;
using Xunit;

namespace Tests.Services.VCA;

public class VcaHookAcknowledgmentPromptTests
{
    [Fact]
    public async Task AcknowledgeMode_AppendsMissingTokensWithReason()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"vca_ack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var commitMessagePath = Path.Combine(tempDir, "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(commitMessagePath, "Add feature\n", TestContext.Current.CancellationToken);

        try
        {
            var token = "[VCA:AGENTS.md:log-all-file-changes]";
            var presenter = new TestPresenter("Documented in issue 123");
            var validationService = new TestValidationService(token);
            var runner = new VcaHookRunner(
                new GitPreflightPipeline(
                    new TestSnapshotProvider(tempDir),
                    new TestSnapshotProvider(tempDir),
                    [
                        new VcaPreflightStep(validationService),
                        new MintLintPreflightStep(),
                        new AutomatedWorkflowsPreflightStep()
                    ]),
                new VcaHookValidationAnalyzer(),
                presenter);

            var exitCode = await runner.RunAsync(
                new VcaHookInvocation(
                    VcaHookKind.AcknowledgeCommitMessage,
                    commitMessagePath,
                    tempDir,
                    DemoUi: false,
                    DemoDuration: TimeSpan.Zero,
                    PromptForAcknowledgment: false),
                TestContext.Current.CancellationToken);

            var commitMessage = await File.ReadAllTextAsync(commitMessagePath, TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
            Assert.Contains("VCA acknowledgments:", commitMessage);
            Assert.Contains($"{token} Reason: Documented in issue 123", commitMessage);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AcknowledgeMode_ObservesCancellationAfterPreflight()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"vca_ack_cancel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var commitMessagePath = Path.Combine(tempDir, "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(
            commitMessagePath,
            "Add feature\n",
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();

        try
        {
            var summary = new VcaHookValidationSummary(
                HasError: false,
                HasStopViolation: false,
                HasCommitViolations: true,
                RequiredAcknowledgments: ["[VCA:AGENTS.md:rule]"]);
            var runner = new VcaHookRunner(
                new CancelAfterPreflightPipeline(cancellation, tempDir, summary),
                new VcaHookValidationAnalyzer(),
                new TestPresenter("should not be read"));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
                new VcaHookInvocation(
                    VcaHookKind.AcknowledgeCommitMessage,
                    commitMessagePath,
                    tempDir,
                    DemoUi: false,
                    DemoDuration: TimeSpan.Zero,
                    PromptForAcknowledgment: false),
                cancellation.Token));

            Assert.Equal(
                "Add feature\n",
                await File.ReadAllTextAsync(commitMessagePath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class TestValidationService : IVcaHookValidationService
    {
        private readonly string _token;

        public TestValidationService(string token)
        {
            _token = token;
        }

        public Task<VcaHookValidationResult> ValidateAsync(
            VcaHookInvocation invocation,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            Task.FromResult(new VcaHookValidationResult(
                "Validated 1 file(s) against 1 rule(s).\n\nCOMMIT-LEVEL VIOLATIONS (require acknowledgment in commit message):",
                new VcaHookValidationSummary(
                    HasError: false,
                    HasStopViolation: false,
                    HasCommitViolations: true,
                    RequiredAcknowledgments: [_token])));

        public Task<VcaHookValidationResult> ValidateAsync(
            VcaHookInvocation invocation,
            GitStagedSnapshot snapshot,
            CancellationToken cancellationToken) =>
            ValidateAsync(invocation, snapshot.RepositoryPath, cancellationToken);
    }

    private sealed class TestSnapshotProvider : IGitStagedSnapshotProvider, IGitWorkingTreeSnapshotProvider
    {
        private readonly string _repositoryPath;

        public TestSnapshotProvider(string repositoryPath)
        {
            _repositoryPath = repositoryPath;
        }

        public Task<GitStagedSnapshot> CaptureAsync(string workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(new GitStagedSnapshot(
                _repositoryPath,
                [new GitStagedFileSnapshot(
                    "src/demo.cs",
                    Path.Combine(_repositoryPath, "src", "demo.cs"),
                    GitStagedChangeKind.Modified,
                    ExistsInIndex: true,
                    IsBinary: false,
                    ChangedLineCount: 1,
                    Content: "class Demo { }")],
                []));

        public Task<GitStagedSnapshot> CaptureWorkingTreeAsync(string workingDirectory, CancellationToken cancellationToken) =>
            CaptureAsync(workingDirectory, cancellationToken);

        public Task<GitStagedSnapshot> CaptureUnpushedAsync(string workingDirectory, CancellationToken cancellationToken) =>
            CaptureAsync(workingDirectory, cancellationToken);
    }

    private sealed class CancelAfterPreflightPipeline(
        CancellationTokenSource cancellation,
        string repositoryPath,
        VcaHookValidationSummary summary) : IGitPreflightPipeline
    {
        public Task<GitPreflightRunResult> RunAsync(
            GitPreflightRequest request,
            Func<GitPreflightEvent, CancellationToken, ValueTask>? eventSink = null,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromResult(new GitPreflightRunResult(
                Guid.NewGuid().ToString("N"),
                repositoryPath,
                [],
                [new GitPreflightStepResult(
                    VcaPreflightStep.Id,
                    "VCA",
                    GitPreflightStepStatus.Warning,
                    "Acknowledgment required",
                    ["Acknowledgment required"],
                    0,
                    Blocking: false,
                    VcaSummary: summary)],
                GitPreflightStepStatus.Warning,
                CommitAllowed: true,
                DurationMs: 0));
        }
    }

    private sealed class TestPresenter : IVcaHookPresenter
    {
        private readonly string _response;

        public TestPresenter(string response)
        {
            _response = response;
        }

        public async Task<T> RunWithProgressAsync<T>(
            VcaHookDisplayInfo displayInfo,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) =>
            await operation(cancellationToken);

        public Task WriteValidationOutputAsync(string validationOutput) => Task.CompletedTask;

        public Task WriteSuccessAsync(string message) => Task.CompletedTask;

        public Task WriteWarningAsync(string message) => Task.CompletedTask;

        public Task WriteFailureAsync(string message) => Task.CompletedTask;

        public Task WriteErrorAsync(string message) => Task.CompletedTask;

        public Task<string?> ReadLineAsync(string prompt) => Task.FromResult<string?>(_response);
    }
}
