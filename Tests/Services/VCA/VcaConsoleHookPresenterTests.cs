using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public class VcaConsoleHookPresenterTests
{
    private const int TestWidth = 90;

    [Fact]
    public async Task WritePreflightEventAsync_AnsiRunStarted_RendersBrandHeaderAndRule()
    {
        var (presenter, output) = CreatePresenter(VcaConsoleStyle.Ansi);

        await presenter.WritePreflightEventAsync(
            Event(GitPreflightEventType.RunStarted, GitPreflightStepStatus.Running,
                "Checking 2 staged file(s).",
                new Dictionary<string, string>
                {
                    ["hookKind"] = nameof(VcaHookKind.PreCommit),
                    ["repositoryPath"] = @"C:\repo",
                    ["stagedFileCount"] = "2"
                }),
            TestContext.Current.CancellationToken);

        var text = AnsiText.Strip(output.ToString());
        Assert.Contains("VibeRails · Git Guard", text);
        Assert.Contains("Pre-Commit", text);
        Assert.Contains("─────", text);
        Assert.Contains(@"C:\repo · 2 staged file(s)", text);
        Assert.Contains("\x1b[38;2;", output.ToString(), StringComparison.Ordinal);
        // The staged list is summarised, never enumerated: a long list ahead of the findings
        // is what buried them.
        Assert.DoesNotContain("•", text);
    }

    [Fact]
    public async Task WritePreflightEventAsync_AnsiPassedStep_CollapsesToOneCheckedLine()
    {
        var (presenter, output) = CreatePresenter(VcaConsoleStyle.Ansi);

        await RunStepAsync(
            presenter,
            "VCA rule validation",
            GitPreflightStepStatus.Passed,
            "VCA rules passed.",
            ["Validated 2 staged file(s) against 4 applicable rule(s).", "PASS: All VCA rules satisfied."]);

        var text = AnsiText.Strip(output.ToString());
        Assert.Contains("✓", text);
        Assert.Contains("VCA rule validation", text);
        // A step that passed drops its transcript entirely — that is the whole point of the
        // progress list, and the dim style is what "muted" means here.
        Assert.DoesNotContain("PASS: All VCA rules satisfied.", text);
        Assert.DoesNotContain("Validated 2 staged file(s)", text);
        Assert.Contains("\x1b[2m", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritePreflightEventAsync_AnsiSkippedStep_ReadsAsSkippedRatherThanTimed()
    {
        var (presenter, output) = CreatePresenter(VcaConsoleStyle.Ansi);

        await RunStepAsync(
            presenter,
            "Automated workflows",
            GitPreflightStepStatus.Skipped,
            "Automated Jobs run after a successful commit, not during pre-commit.",
            ["Automated Jobs run after a successful commit, not during pre-commit."]);

        var text = AnsiText.Strip(output.ToString());
        Assert.Contains("Automated workflows", text);
        Assert.Contains("skipped", text);
    }

    [Fact]
    public async Task WritePreflightEventAsync_AnsiBlockedStep_ExpandsEveryFindingWorstFirst()
    {
        var (presenter, output) = CreatePresenter(VcaConsoleStyle.Ansi);

        await RunStepAsync(
            presenter,
            "VCA rule validation",
            GitPreflightStepStatus.Blocked,
            "VCA found STOP-level violations.",
            ["FAIL: STOP-level violations detected. Cannot commit."],
            new VcaHookValidationSummary(
                HasError: false,
                HasStopViolation: true,
                HasCommitViolations: true,
                RequiredAcknowledgments: ["[VCA:vc.rules.md:complexity]"],
                Findings:
                [
                    new VcaRuleFinding(
                        VcaRuleFindingKind.Warning,
                        "WARN",
                        "Log all file changes",
                        "3 file(s) not documented.",
                        "vc.rules.md",
                        "Review this finding before committing."),
                    new VcaRuleFinding(
                        VcaRuleFindingKind.AcknowledgmentRequired,
                        "COMMIT",
                        "Cyclomatic complexity < 20",
                        "Relay.cs estimated complexity 44 exceeds 20",
                        "vc.rules.md",
                        "Fix the issue, or include the shown acknowledgment token.",
                        "[VCA:vc.rules.md:complexity]"),
                    new VcaRuleFinding(
                        VcaRuleFindingKind.Blocked,
                        "STOP",
                        "Require test coverage minimum 80%",
                        "UNSUPPORTED: no coverage report for the staged snapshot.",
                        "vc.rules.md",
                        "Fix the issue and run validation again.")
                ]));

        var text = AnsiText.Strip(output.ToString());
        Assert.Contains("VCA found STOP-level violations.", text);
        Assert.Contains("STOP · Require test coverage minimum 80%", text);
        Assert.Contains("COMMIT · Cyclomatic complexity < 20", text);
        Assert.Contains("WARN · Log all file changes", text);
        Assert.Contains("[VCA:vc.rules.md:complexity] Reason: <your explanation>", text);

        // Worst first: a STOP the user cannot bypass outranks an acknowledgeable COMMIT,
        // which outranks a warning that does not block at all.
        Assert.True(
            text.IndexOf("STOP · ", StringComparison.Ordinal)
                < text.IndexOf("COMMIT · ", StringComparison.Ordinal),
            "STOP findings must be listed before COMMIT findings.");
        Assert.True(
            text.IndexOf("COMMIT · ", StringComparison.Ordinal)
                < text.IndexOf("WARN · ", StringComparison.Ordinal),
            "COMMIT findings must be listed before WARN findings.");

        // Guidance is only worth its lines when the finding is actually blocking.
        Assert.DoesNotContain("Review this finding before committing.", text);
    }

    [Fact]
    public async Task WritePreflightEventAsync_AnsiLongReason_WrapsInsideTheWindow()
    {
        var (presenter, output) = CreatePresenter(VcaConsoleStyle.Ansi);
        var reason = string.Join(" ", Enumerable.Repeat("coverage", 60));

        await RunStepAsync(
            presenter,
            "VCA rule validation",
            GitPreflightStepStatus.Blocked,
            "VCA found STOP-level violations.",
            [],
            new VcaHookValidationSummary(
                HasError: false,
                HasStopViolation: true,
                HasCommitViolations: false,
                RequiredAcknowledgments: [],
                Findings:
                [
                    new VcaRuleFinding(
                        VcaRuleFindingKind.Blocked,
                        "STOP",
                        "Require test coverage minimum 80%",
                        reason,
                        "vc.rules.md",
                        "Fix the issue and run validation again.")
                ]));

        var lines = AnsiText.Strip(output.ToString()).Split('\n').Select(line => line.TrimEnd('\r'));
        foreach (var line in lines)
        {
            Assert.True(
                line.Length <= TestWidth,
                $"Line exceeds the {TestWidth}-column window: {line.Length} chars.");
        }

        // Every wrapped continuation hangs under its finding rather than restarting at column 0,
        // which is what produced the stray "ore" / "ercentage." fragments in the old layout.
        var wrapped = AnsiText.Strip(output.ToString())
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Contains("coverage coverage", StringComparison.Ordinal))
            .ToList();
        Assert.True(wrapped.Count > 1, "The long reason should have wrapped onto several lines.");
        Assert.All(wrapped, line => Assert.StartsWith("           ", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WritePreflightEventAsync_AnsiPassedStepWithDeferredChecks_StillNamesThem()
    {
        var (presenter, output) = CreatePresenter(VcaConsoleStyle.Ansi);

        await RunStepAsync(
            presenter,
            "VCA rule validation",
            GitPreflightStepStatus.Passed,
            "VCA rules passed; commit-message checks were deferred.",
            [],
            new VcaHookValidationSummary(
                HasError: false,
                HasStopViolation: false,
                HasCommitViolations: false,
                RequiredAcknowledgments: [],
                Findings:
                [
                    new VcaRuleFinding(
                        VcaRuleFindingKind.Deferred,
                        "STOP",
                        "Check commit message for: wip",
                        "Evaluated by the commit-msg hook.",
                        "vc.rules.md",
                        "No pre-commit action is required.")
                ]));

        var text = AnsiText.Strip(output.ToString());
        Assert.Contains("✓", text);
        Assert.Contains("deferred to the commit-message hook", text);
        Assert.Contains("Check commit message for: wip", text);
    }

    [Fact]
    public async Task WritePreflightEventAsync_AnsiRunFinishedAllowed_RendersSuccessBar()
    {
        var (presenter, output) = CreatePresenter(VcaConsoleStyle.Ansi);

        await presenter.WritePreflightEventAsync(
            Event(GitPreflightEventType.RunFinished, GitPreflightStepStatus.Passed,
                "Commit allowed.", commitAllowed: true, durationMs: 1234),
            TestContext.Current.CancellationToken);

        var text = AnsiText.Strip(output.ToString());
        Assert.Contains("COMMIT ALLOWED", text);
        Assert.Contains("\x1b[48;2;", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritePreflightEventAsync_AnsiRunFinishedBlocked_RendersFailureBar()
    {
        var (presenter, output) = CreatePresenter(VcaConsoleStyle.Ansi);

        await presenter.WritePreflightEventAsync(
            Event(GitPreflightEventType.RunFinished, GitPreflightStepStatus.Blocked,
                "Commit blocked.", commitAllowed: false, durationMs: 42),
            TestContext.Current.CancellationToken);

        var text = AnsiText.Strip(output.ToString());
        Assert.Contains("COMMIT BLOCKED", text);
        Assert.Contains("\x1b[48;2;", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritePreflightEventAsync_PlainOutput_IsByteIdenticalToHistoricalFormat()
    {
        var (presenter, output) = CreatePresenter(style: null);

        await presenter.WritePreflightEventAsync(
            Event(GitPreflightEventType.RunStarted, GitPreflightStepStatus.Running,
                "Checking 1 staged file(s).",
                new Dictionary<string, string> { ["hookKind"] = nameof(VcaHookKind.PreCommit) }),
            TestContext.Current.CancellationToken);
        await presenter.WritePreflightEventAsync(
            Event(GitPreflightEventType.StepFinished, GitPreflightStepStatus.Passed,
                "VCA validation passed.", stepNumber: 1, durationMs: 10),
            TestContext.Current.CancellationToken);
        await presenter.WritePreflightEventAsync(
            Event(GitPreflightEventType.RunFinished, GitPreflightStepStatus.Passed,
                "Commit allowed.", commitAllowed: true, durationMs: 10),
            TestContext.Current.CancellationToken);

        var text = output.ToString();
        // Ordinal: culture-sensitive comparison treats ESC as ignorable, making
        // "\x1b[" collate equal to plain "[" and producing false matches.
        Assert.DoesNotContain("\x1b[", text, StringComparison.Ordinal);
        Assert.Contains("VibeRails · Git Guard — Pre-Commit", text);
        Assert.Contains(new string('─', 60), text);
        Assert.Contains("[1/3] ✓ [pass] VCA validation passed. · 0:00", text);
        Assert.Contains("✓ [pass] Commit allowed · 0:00", text);
    }

    [Fact]
    public async Task WritePreflightEventAsync_PlainOutput_StillStreamsStepTranscriptInOrder()
    {
        var (presenter, output) = CreatePresenter(style: null);

        await RunStepAsync(
            presenter,
            "VCA rule validation",
            GitPreflightStepStatus.Passed,
            "VCA rules passed.",
            ["PASS: All VCA rules satisfied."]);

        // Redirected consumers (the Rules page validator, VS Code's SCM log) read this stream
        // line by line, so buffering and dropping output is a styled-console behavior only.
        var text = output.ToString();
        Assert.Contains("      PASS: All VCA rules satisfied.", text);
        Assert.Contains("[1/3] ✓ [pass] VCA rules passed.", text);
    }

    private static async Task RunStepAsync(
        VcaConsoleHookPresenter presenter,
        string displayName,
        GitPreflightStepStatus status,
        string summary,
        IReadOnlyList<string> stepOutput,
        VcaHookValidationSummary? vcaSummary = null)
    {
        await presenter.WritePreflightEventAsync(
            Event(GitPreflightEventType.StepStarted, GitPreflightStepStatus.Running,
                displayName, stepNumber: 1),
            TestContext.Current.CancellationToken);

        foreach (var line in stepOutput)
        {
            await presenter.WritePreflightEventAsync(
                Event(GitPreflightEventType.StepOutput, GitPreflightStepStatus.Running,
                    line, stepNumber: 1),
                TestContext.Current.CancellationToken);
        }

        await presenter.WritePreflightEventAsync(
            Event(GitPreflightEventType.StepFinished, status, summary,
                stepNumber: 1, durationMs: 10, vcaSummary: vcaSummary),
            TestContext.Current.CancellationToken);
    }

    private static (VcaConsoleHookPresenter Presenter, StringWriter Output) CreatePresenter(
        VcaConsoleStyle? style)
    {
        var output = new StringWriter();
        var presenter = new VcaConsoleHookPresenter(new VcaHookConsoleOptions(
            output,
            new StringWriter(),
            new StringReader(""),
            EnableSpinner: false,
            style,
            // Pin the layout width: the test host's console is not the window the hook runs in.
            TestWidth + 1));
        return (presenter, output);
    }

    private static GitPreflightEvent Event(
        GitPreflightEventType type,
        GitPreflightStepStatus status,
        string message,
        IReadOnlyDictionary<string, string>? details = null,
        bool? commitAllowed = null,
        int? stepNumber = null,
        long? durationMs = null,
        VcaHookValidationSummary? vcaSummary = null) =>
        new(
            RunId: "run",
            Sequence: 1,
            TimestampUtc: DateTimeOffset.UtcNow,
            Type: type,
            StepId: stepNumber.HasValue ? "step" : null,
            Status: status,
            Message: message,
            Details: details,
            DurationMs: durationMs,
            Blocking: false,
            CommitAllowed: commitAllowed,
            StepNumber: stepNumber,
            StepCount: 3,
            VcaSummary: vcaSummary);
}
