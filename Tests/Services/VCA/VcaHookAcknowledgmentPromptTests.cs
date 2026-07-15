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
    }

    private sealed class TestSnapshotProvider : IGitStagedSnapshotProvider
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
