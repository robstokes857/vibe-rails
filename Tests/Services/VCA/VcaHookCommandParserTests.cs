using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public class VcaHookCommandParserTests
{
    [Theory]
    [InlineData(new[] { "--vca-hook", "pre-commit" }, true)]
    [InlineData(new[] { "--vca-hook", "commit-msg", "--commit-message", ".git/COMMIT_EDITMSG" }, true)]
    [InlineData(new[] { "--validate-vca", "--pre-commit" }, true)]
    [InlineData(new[] { "--commit-msg", ".git/COMMIT_EDITMSG" }, true)]
    [InlineData(new[] { "--env", "claude", "--", "--commit-msg" }, false)]
    [InlineData(new[] { "--version" }, false)]
    public void IsRequested_DetectsHookModes(string[] args, bool expected)
    {
        Assert.Equal(expected, VcaHookCommandParser.IsRequested(args));
    }

    [Fact]
    public void Parse_NewPreCommitCommand_ReturnsPreCommitInvocation()
    {
        var parser = new VcaHookCommandParser();

        var invocation = parser.Parse(["--vca-hook", "pre-commit", "--workdir", @"C:\repo"]);

        Assert.Equal(VcaHookKind.PreCommit, invocation.Kind);
        Assert.Equal(@"C:\repo", invocation.WorkingDirectory);
        Assert.False(invocation.DemoUi);
    }

    [Fact]
    public void Parse_NewCommitMessageCommand_ReturnsCommitMessageInvocation()
    {
        var parser = new VcaHookCommandParser();

        var invocation = parser.Parse(["--vca-hook", "commit-msg", "--commit-message", ".git/COMMIT_EDITMSG"]);

        Assert.Equal(VcaHookKind.CommitMessage, invocation.Kind);
        Assert.Equal(".git/COMMIT_EDITMSG", invocation.CommitMessagePath);
    }

    [Fact]
    public void Parse_PreviewCommand_EnablesDemoUi()
    {
        var parser = new VcaHookCommandParser();

        var invocation = parser.Parse(["--vca-hook", "preview", "--demo-duration-ms", "25"]);

        Assert.Equal(VcaHookKind.Preview, invocation.Kind);
        Assert.True(invocation.DemoUi);
        Assert.Equal(TimeSpan.FromMilliseconds(25), invocation.DemoDuration);
    }
}

public class VcaHookValidationAnalyzerTests
{
    [Fact]
    public void AnalyzeValidationOutput_ExtractsCommitAcknowledgments()
    {
        var analyzer = new VcaHookValidationAnalyzer();
        const string output = """
            COMMIT-LEVEL VIOLATIONS (require acknowledgment in commit message):
              [COMMIT] Log all file changes: one file missing
                Acknowledgment needed: [VCA:AGENTS.md:log-all-file-changes] Reason: <your explanation>

            To commit, include acknowledgments like:
              [VCA:AGENTS.md:log-all-file-changes] Reason: <explain why this is acceptable>
            """;

        var summary = analyzer.Analyze(output);

        Assert.True(summary.HasCommitViolations);
        Assert.False(summary.HasStopViolation);
        Assert.False(analyzer.ShouldBlockPreCommit(summary));
        Assert.Equal(new[] { "[VCA:AGENTS.md:log-all-file-changes]" }, summary.RequiredAcknowledgments);
    }

    [Fact]
    public void AnalyzeValidationOutput_BlocksPreCommitForStopOrError()
    {
        var analyzer = new VcaHookValidationAnalyzer();
        var stopSummary = analyzer.Analyze("FAIL: STOP-level violations detected.\n[STOP] Package file changes");
        var errorSummary = analyzer.Analyze("ERROR: Failed to validate VCA rules");

        Assert.True(analyzer.ShouldBlockPreCommit(stopSummary));
        Assert.True(analyzer.ShouldBlockPreCommit(errorSummary));
    }

    [Fact]
    public void GetMissingAcknowledgments_IsCaseInsensitive()
    {
        var analyzer = new VcaHookValidationAnalyzer();
        var required = new[]
        {
            "[VCA:AGENTS.md:log-all-file-changes]",
            "[VCA:DB-AGENTS.md:package-file-changes]"
        };

        var missing = analyzer.GetMissingAcknowledgments(
            "reason: [vca:agents.md:log-all-file-changes]",
            required);

        Assert.Equal(new[] { "[VCA:DB-AGENTS.md:package-file-changes]" }, missing);
    }
}
