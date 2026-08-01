using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public class VcaHookCommandParserTests
{
    [Theory]
    [InlineData(new[] { "--vca-hook", "pre-commit" }, true)]
    [InlineData(new[] { "--vca-hook", "commit-msg", "--commit-message", ".git/COMMIT_EDITMSG" }, true)]
    [InlineData(new[] { "--vca-hook", "clean-commit-msg", "--commit-message", ".git/COMMIT_EDITMSG" }, true)]
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

        var invocation = parser.Parse([
            "--vca-hook",
            "commit-msg",
            "--commit-message",
            ".git/COMMIT_EDITMSG",
            "--prompt-acknowledgment"]);

        Assert.Equal(VcaHookKind.CommitMessage, invocation.Kind);
        Assert.Equal(".git/COMMIT_EDITMSG", invocation.CommitMessagePath);
        Assert.True(invocation.PromptForAcknowledgment);
    }

    [Fact]
    public void Parse_AcknowledgeCommand_ReturnsAcknowledgeInvocation()
    {
        var parser = new VcaHookCommandParser();

        var invocation = parser.Parse(["--vca-hook", "acknowledge-commit-msg", "--commit-message", ".git/COMMIT_EDITMSG"]);

        Assert.Equal(VcaHookKind.AcknowledgeCommitMessage, invocation.Kind);
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

    [Theory]
    [InlineData("clean-commit-msg")]
    [InlineData("clean-commit-message")]
    public void Parse_CleanCommitMessageCommand_ReturnsTheCleanupOnlyKind(string kind)
    {
        var parser = new VcaHookCommandParser();

        var invocation = parser.Parse(["--vca-hook", kind, "--commit-message", ".git/COMMIT_EDITMSG"]);

        Assert.Equal(VcaHookKind.CleanCommitMessage, invocation.Kind);
        Assert.Equal(".git/COMMIT_EDITMSG", invocation.CommitMessagePath);
    }

    [Fact]
    public void Parse_CoAuthorsCleanedFlag_TellsTheRunnerCleanupAlreadyRan()
    {
        // The hook script runs cleanup as its own pass, ahead of any chained commit-msg hook, so
        // that hook sees the text git will record. This flag is how it says so — without it the
        // runner cleans inline afterwards, which is the ordering bug the separate pass removed.
        var parser = new VcaHookCommandParser();

        var invocation = parser.Parse([
            "--vca-hook",
            "commit-msg",
            "--co-authors-cleaned",
            "--commit-message",
            ".git/COMMIT_EDITMSG"]);

        Assert.Equal(VcaHookKind.CommitMessage, invocation.Kind);
        Assert.True(invocation.CoAuthorsAlreadyCleaned);
    }

    [Fact]
    public void Parse_WithoutCoAuthorsCleanedFlag_KeepsTheInlineCleanupForOlderHookScripts()
    {
        // Hook scripts are stamped with the app version and only rewritten on an upgrade, so an
        // installed repo can keep invoking the old single-pass form for a while. Defaulting to
        // false is what keeps the policy applying there at all.
        var parser = new VcaHookCommandParser();

        var invocation = parser.Parse([
            "--vca-hook", "commit-msg", "--commit-message", ".git/COMMIT_EDITMSG"]);

        Assert.False(invocation.CoAuthorsAlreadyCleaned);
    }

    [Fact]
    public void Parse_ConsoleWindowFlag_RequestsDedicatedConsole()
    {
        var parser = new VcaHookCommandParser();

        var invocation = parser.Parse(["--vca-hook", "pre-commit", "--console-window"]);

        Assert.True(invocation.ShowConsoleWindow);
        Assert.False(invocation.ConsoleWindowAttached);
    }

    [Fact]
    public void Parse_ConsoleWindowAttachedFlag_MarksRespawnedChild()
    {
        var parser = new VcaHookCommandParser();

        var invocation = parser.Parse([
            "--vca-hook",
            "pre-commit",
            "--console-window-attached",
            "--workdir",
            @"C:\repo"]);

        Assert.True(invocation.ShowConsoleWindow);
        Assert.True(invocation.ConsoleWindowAttached);
        Assert.Equal(@"C:\repo", invocation.WorkingDirectory);
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
            "[vca:agents.md:log-all-file-changes] reason: reviewed and accepted",
            required);

        Assert.Equal(new[] { "[VCA:DB-AGENTS.md:package-file-changes]" }, missing);
    }

    [Fact]
    public void GetMissingAcknowledgments_RejectsTokenWithoutReason()
    {
        var analyzer = new VcaHookValidationAnalyzer();
        const string token = "[VCA:AGENTS.md:log-all-file-changes]";

        var missing = analyzer.GetMissingAcknowledgments(token, [token]);

        Assert.Equal([token], missing);
    }
}
