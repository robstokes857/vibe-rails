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
    private static VcaHookValidationSummary Summary(
        bool hasError = false,
        bool hasStopViolation = false,
        bool hasCommitViolations = false,
        params string[] requiredAcknowledgments) =>
        new(hasError, hasStopViolation, hasCommitViolations, requiredAcknowledgments);

    [Fact]
    public void ShouldBlockPreCommit_BlocksForStopOrError_ButNotForCommitViolations()
    {
        var analyzer = new VcaHookValidationAnalyzer();

        Assert.True(analyzer.ShouldBlockPreCommit(Summary(hasStopViolation: true)));
        Assert.True(analyzer.ShouldBlockPreCommit(Summary(hasError: true)));
        // COMMIT-level violations are acknowledged in the commit message, not blocked here.
        Assert.False(analyzer.ShouldBlockPreCommit(
            Summary(hasCommitViolations: true, requiredAcknowledgments: "[VCA:AGENTS.md:log-all-file-changes]")));
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
