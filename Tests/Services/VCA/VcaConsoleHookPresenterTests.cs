using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public class VcaConsoleHookPresenterTests
{
    [Fact]
    public async Task WritePreflightEventAsync_AnsiRunStarted_RendersBoxedBanner()
    {
        var (presenter, output) = CreatePresenter(VcaConsoleStyle.Ansi);

        await presenter.WritePreflightEventAsync(
            Event(GitPreflightEventType.RunStarted, GitPreflightStepStatus.Running,
                "Checking 2 staged file(s).",
                new Dictionary<string, string>
                {
                    ["hookKind"] = nameof(VcaHookKind.PreCommit),
                    ["repositoryPath"] = @"C:\repo"
                }),
            TestContext.Current.CancellationToken);

        var text = AnsiText.Strip(output.ToString());
        Assert.Contains("╭", text);
        Assert.Contains("VibeRails · Git Guard", text);
        Assert.Contains("Pre-Commit", text);
        Assert.Contains("\x1b[38;2;", output.ToString(), StringComparison.Ordinal);
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

    private static (VcaConsoleHookPresenter Presenter, StringWriter Output) CreatePresenter(
        VcaConsoleStyle? style)
    {
        var output = new StringWriter();
        var presenter = new VcaConsoleHookPresenter(new VcaHookConsoleOptions(
            output,
            new StringWriter(),
            new StringReader(""),
            EnableSpinner: false,
            style));
        return (presenter, output);
    }

    private static GitPreflightEvent Event(
        GitPreflightEventType type,
        GitPreflightStepStatus status,
        string message,
        IReadOnlyDictionary<string, string>? details = null,
        bool? commitAllowed = null,
        int? stepNumber = null,
        long? durationMs = null) =>
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
            StepCount: 3);
}
