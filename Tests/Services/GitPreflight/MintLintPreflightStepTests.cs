using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.GitPreflight;

public sealed class MintLintPreflightStepTests
{
    [Fact]
    public async Task ExecuteAsync_ScansSupportedIndexContent_AndReportsSkippedFiles()
    {
        var snapshot = new GitStagedSnapshot(
            Directory.GetCurrentDirectory(),
            [
                File("src/demo.cs", "public class Demo { public void Run() { if (true) { } } }"),
                File("README.md", "not supported"),
                new GitStagedFileSnapshot(
                    "old.cs", "old.cs", GitStagedChangeKind.Deleted, false, false, 1, null)
            ],
            []);
        var messages = new List<string>();

        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext(
                "run",
                Request(),
                snapshot,
                (message, _, _) =>
                {
                    messages.Add(message);
                    return ValueTask.CompletedTask;
                }),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(GitPreflightStepStatus.Skipped, result.Status);
        Assert.Equal("1", result.Details!["supportedFileCount"]);
        Assert.Equal("2", result.Details["skippedFileCount"]);
        Assert.Contains(messages, message => message.Contains("src/demo.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_CommitMessage_DoesNotRescanSources()
    {
        var request = Request() with
        {
            Invocation = Request().Invocation with { Kind = VcaHookKind.CommitMessage }
        };
        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext(
                "run",
                request,
                new GitStagedSnapshot(Directory.GetCurrentDirectory(), [File("demo.cs", "class Demo { }")], []),
                (_, _, _) => ValueTask.CompletedTask),
            TestContext.Current.CancellationToken);

        Assert.Equal(GitPreflightStepStatus.Skipped, result.Status);
        Assert.Contains("already ran", result.Summary);
    }

    private static GitStagedFileSnapshot File(string path, string content) => new(
        path,
        path,
        GitStagedChangeKind.Modified,
        true,
        false,
        1,
        content);

    private static GitPreflightRequest Request() => new(
        Directory.GetCurrentDirectory(),
        new VcaHookInvocation(
            VcaHookKind.PreCommit,
            null,
            Directory.GetCurrentDirectory(),
            false,
            TimeSpan.Zero,
            false));
}
