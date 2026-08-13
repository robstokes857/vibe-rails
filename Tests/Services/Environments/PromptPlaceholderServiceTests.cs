using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Cli;
using VibeRails.Services.Environments;
using Xunit;

namespace Tests.Services.Environments;

/// <summary>
/// Everything here runs against a fake ICliWrapper and a mocked repository — no processes, no
/// database — except the working directory handed to CreateCapturedScript, which writes a real
/// temp script file (and deletes it on dispose).
/// </summary>
public class PromptPlaceholderServiceTests
{
    private const int EnvironmentId = 7;
    private static readonly string WorkDir = Path.GetTempPath();

    // 2026-08-12 14:35 — the clock every built-in test asserts against. The provider pins the
    // local zone to UTC so GetLocalNow() is this instant verbatim on any machine.
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 14, 35, 0, TimeSpan.Zero);

    [Fact]
    public async Task NullEmptyAndTokenFreePrompts_PassThroughUntouched()
    {
        var (service, repository, cli) = BuildService();

        Assert.Null(await service.ResolveAsync(null, Context(), TestContext.Current.CancellationToken));
        Assert.Equal("", await service.ResolveAsync("", Context(), TestContext.Current.CancellationToken));
        Assert.Equal("plain text", await service.ResolveAsync("plain text", Context(), TestContext.Current.CancellationToken));

        repository.VerifyNoOtherCalls();
        Assert.Empty(cli.Requests);
    }

    [Theory]
    [InlineData("{{datetime}}", "2026-08-12 14:35")]
    [InlineData("{{date}}", "2026-08-12")]
    [InlineData("{{time}}", "14:35")]
    [InlineData("{{DateTime}}", "2026-08-12 14:35")]
    [InlineData("{{DATE}}", "2026-08-12")]
    public async Task TimeBuiltins_ResolveFromTheInjectedClock(string template, string expected)
    {
        var (service, _, _) = BuildService();

        Assert.Equal(expected, await service.ResolveAsync(template, Context(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnvName_ResolvesFromTheContext()
    {
        var (service, _, _) = BuildService();

        Assert.Equal(
            "Worker Nightly here",
            await service.ResolveAsync(
                "Worker {{env_name}} here",
                Context(environmentName: "Nightly"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DefaultArgument_IsIgnoredOnBuiltins()
    {
        var (service, _, _) = BuildService();

        Assert.Equal(
            "2026-08-12",
            await service.ResolveAsync("""{{date default="never"}}""", Context(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UserVariables_AreLeftExactlyAsWritten()
    {
        var (service, _, _) = BuildService();

        var template = """Review {{branch_name}} and {{path default="docs/runbook.md"}} on {{date}}""";

        Assert.Equal(
            """Review {{branch_name}} and {{path default="docs/runbook.md"}} on 2026-08-12""",
            await service.ResolveAsync(template, Context(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitBranch_OutsideARepository_ResolvesToTheFallbackText()
    {
        // Path.GetTempPath() is not a git repository, so the real GitService lookup comes back
        // empty and the fallback wording is substituted.
        var (service, _, _) = BuildService();

        Assert.Equal("(no git branch)", await service.ResolveAsync("{{git_branch}}", Context(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StepToken_RunsTheStepAndSplicesItsOutputIn()
    {
        var (service, repository, cli) = BuildService();
        var step = NewStep("Run Tests", "npm test");
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, step.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);
        cli.NextStandardOutput = "42 passed\n";

        var resolved = await service.ResolveAsync(
            $"Test results:\n{{{{step:{step.Id}}}}}", Context(), TestContext.Current.CancellationToken);

        Assert.Equal("Test results:\n42 passed", resolved);
        Assert.Equal(WorkDir, Assert.Single(cli.Requests).WorkingDirectory);
    }

    [Fact]
    public async Task StepOutput_IsNeverReScannedForPlaceholders()
    {
        // Built-ins resolve on the template before step outputs are spliced in, so a step that
        // prints "{{datetime}}" must reach the CLI literally.
        var (service, repository, cli) = BuildService();
        var step = NewStep("Echo", "echo");
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, step.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);
        cli.NextStandardOutput = "see {{datetime}} and {{env_name}}";

        var resolved = await service.ResolveAsync($"{{{{step:{step.Id}}}}}", Context(), TestContext.Current.CancellationToken);

        Assert.Equal("see {{datetime}} and {{env_name}}", resolved);
    }

    [Fact]
    public async Task StepReferencedTwice_RunsOnce()
    {
        var (service, repository, cli) = BuildService();
        var step = NewStep("Info", "git log -1");
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, step.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);
        cli.NextStandardOutput = "abc123";

        var resolved = await service.ResolveAsync(
            $"{{{{step:{step.Id}}}}} and again {{{{step:{step.Id}}}}}", Context(), TestContext.Current.CancellationToken);

        Assert.Equal("abc123 and again abc123", resolved);
        Assert.Single(cli.Requests);
    }

    [Fact]
    public async Task DeletedStep_SubstitutesTheDeletedText()
    {
        var (service, repository, _) = BuildService();
        var missingId = Guid.NewGuid().ToString();
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, missingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EnvironmentStep?)null);

        var resolved = await service.ResolveAsync($"Output: {{{{step:{missingId}}}}}", Context(), TestContext.Current.CancellationToken);

        Assert.Equal($"Output: {PromptPlaceholderService.DeletedStepText}", resolved);
    }

    [Fact]
    public async Task StepToken_WithoutAnEnvironment_SubstitutesTheDeletedText()
    {
        var (service, repository, cli) = BuildService();

        var resolved = await service.ResolveAsync(
            $"{{{{step:{Guid.NewGuid()}}}}}",
            new PromptPlaceholderContext(WorkDir),
            TestContext.Current.CancellationToken);

        Assert.Equal(PromptPlaceholderService.DeletedStepText, resolved);
        repository.VerifyNoOtherCalls();
        Assert.Empty(cli.Requests);
    }

    [Fact]
    public async Task FailingStep_KeepsItsOutputAndAppendsTheExitNote()
    {
        var (service, repository, cli) = BuildService();
        var step = NewStep("Run Tests", "npm test");
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, step.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);
        cli.NextStandardOutput = "3 failed";
        cli.NextExitCode = 1;

        var resolved = await service.ResolveAsync($"{{{{step:{step.Id}}}}}", Context(), TestContext.Current.CancellationToken);

        Assert.Equal("3 failed\n(step \"Run Tests\" exited with code 1)", resolved);
    }

    [Fact]
    public async Task TimedOutStep_AppendsTheTimeoutNote()
    {
        var (service, repository, cli) = BuildService();
        var step = NewStep("Slow", "sleep 9999");
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, step.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);
        cli.NextTimedOut = true;
        cli.NextExitCode = -1;

        var resolved = await service.ResolveAsync($"{{{{step:{step.Id}}}}}", Context(), TestContext.Current.CancellationToken);

        Assert.Equal("(step \"Slow\" timed out)", resolved);
    }

    [Fact]
    public async Task LongStepOutput_IsCappedWithATruncationMarker()
    {
        var (service, repository, cli) = BuildService();
        var step = NewStep("Big", "type big.log");
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, step.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);
        cli.NextStandardOutput = new string('x', PromptPlaceholderService.MaxStepOutputChars + 500);

        var resolved = await service.ResolveAsync($"{{{{step:{step.Id}}}}}", Context(), TestContext.Current.CancellationToken);

        Assert.EndsWith("… (output truncated)", resolved);
        Assert.Equal(
            PromptPlaceholderService.MaxStepOutputChars + "… (output truncated)".Length,
            resolved!.Length);
    }

    [Fact]
    public async Task MalformedStepId_SubstitutesTheDeletedTextWithoutARepositoryCall()
    {
        var (service, repository, cli) = BuildService();

        var resolved = await service.ResolveAsync("{{step:12345}}", Context(), TestContext.Current.CancellationToken);

        Assert.Equal(PromptPlaceholderService.DeletedStepText, resolved);
        repository.VerifyNoOtherCalls();
        Assert.Empty(cli.Requests);
    }

    [Fact]
    public async Task DisabledStep_IsNotRun_AndSaysSoInPlaceOfItsOutput()
    {
        // The steps list and the Initial Message are edited in different places, so the toggle
        // has to mean "do not run this" everywhere. Silently running a step the user switched
        // off is the dangerous half; the other half is a resolved prompt that reads as if a
        // real command produced empty output.
        var (service, repository, cli) = BuildService();
        var step = NewStep("Deploy", "./deploy.sh");
        step.Enabled = false;
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, step.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);

        var resolved = await service.ResolveAsync(
            $"Status: {{{{step:{step.Id}}}}}", Context(), TestContext.Current.CancellationToken);

        Assert.Equal($"Status: {PromptPlaceholderService.DisabledStepText("Deploy")}", resolved);
        Assert.Empty(cli.Requests);
    }

    [Fact]
    public async Task CancelledStep_Throws_RatherThanLaunchingWithAHalfResolvedPrompt()
    {
        // Cancellation means shutdown or an abandoned launch. Substituting the partial output
        // would hand the CLI a prompt describing a command that never finished.
        var (service, repository, cli) = BuildService();
        var step = NewStep("Slow", "sleep 9999");
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, step.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);
        cli.NextCancelled = true;

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ResolveAsync($"{{{{step:{step.Id}}}}}", Context(), TestContext.Current.CancellationToken));

        Assert.Contains("Slow", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvedPrompt_OverTheCharacterCap_Throws()
    {
        var (service, repository, cli) = BuildService();
        var step = NewStep("Dump", "cat huge.log");
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, step.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);
        cli.NextStandardOutput = new string('x', PromptPlaceholderService.MaxStepOutputChars);

        // Each reference contributes a capped step output, so enough of them clear the limit
        // even though no single one can.
        var references = string.Concat(Enumerable.Repeat($"{{{{step:{step.Id}}}}}\n", 10));

        var thrown = await Assert.ThrowsAsync<PromptTooLongException>(() =>
            service.ResolveAsync(references, Context(), TestContext.Current.CancellationToken));

        Assert.Equal(PromptPlaceholderService.MaxResolvedPromptChars, thrown.MaxChars);
        Assert.True(thrown.ActualChars > thrown.MaxChars);
    }

    [Fact]
    public async Task PromptWithNoTokensAtAll_IsStillLengthChecked()
    {
        // The cap is a guarantee about what gets launched, not about what substitution did, so
        // it cannot sit behind the "does this contain {{" shortcut.
        var (service, _, _) = BuildService();
        var oversized = new string('y', PromptPlaceholderService.MaxResolvedPromptChars + 1);

        await Assert.ThrowsAsync<PromptTooLongException>(() =>
            service.ResolveAsync(oversized, Context(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PromptExactlyAtTheCap_IsAllowed()
    {
        var (service, _, _) = BuildService();
        var atLimit = new string('y', PromptPlaceholderService.MaxResolvedPromptChars);

        Assert.Equal(atLimit, await service.ResolveAsync(atLimit, Context(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuiltinOutput_IsNeverReScannedForPlaceholders()
    {
        // Same guarantee as the step-output case, for the values substitution itself produces.
        // Resolution is a single pass over the original template: an environment named
        // "{{date}}" leaves that text in the prompt instead of turning into today's date.
        var (service, _, _) = BuildService();

        var resolved = await service.ResolveAsync(
            "env is {{env_name}}",
            Context(environmentName: "{{date}}"),
            TestContext.Current.CancellationToken);

        Assert.Equal("env is {{date}}", resolved);
    }

    [Fact]
    public async Task StepTimeout_IsPassedToTheRun()
    {
        var (service, repository, cli) = BuildService();
        var step = NewStep("Quick", "echo hi");
        step.TimeoutSeconds = 45;
        repository
            .Setup(item => item.GetStepByIdAsync(EnvironmentId, step.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);

        await service.ResolveAsync($"{{{{step:{step.Id}}}}}", Context(), TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(cli.Requests).Timeout);
    }

    // ---------------------------------------------------------------------------------------

    private static PromptPlaceholderContext Context(string? environmentName = "review") =>
        new(WorkDir, EnvironmentId, environmentName);

    private static EnvironmentStep NewStep(string name, string command) => new()
    {
        Id = Guid.NewGuid().ToString(),
        EnvironmentId = EnvironmentId,
        Phase = EnvironmentStepPhase.Manual,
        Name = name,
        Command = command
    };

    private static (PromptPlaceholderService Service, Mock<IRepository> Repository, FakeCapturingCliWrapper Cli) BuildService()
    {
        var repository = new Mock<IRepository>();
        var cli = new FakeCapturingCliWrapper();
        var service = new PromptPlaceholderService(repository.Object, cli, new FixedTimeProvider(Now));
        return (service, repository, cli);
    }

    /// <summary>GetLocalNow() returns the fixed instant: UTC clock + UTC local zone.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeCapturingCliWrapper : ICliWrapper
    {
        public List<CliRequest> Requests { get; } = [];
        public string NextStandardOutput { get; set; } = "";
        public int NextExitCode { get; set; }
        public bool NextTimedOut { get; set; }
        public bool NextCancelled { get; set; }

        public Task<CliResult> RunAsync(
            CliRequest request,
            Func<CliOutputLine, ValueTask>? onLine = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new CliResult(
                NextExitCode,
                NextTimedOut,
                NextCancelled,
                NextStandardOutput,
                StandardError: "",
                Duration: TimeSpan.FromMilliseconds(5),
                CommandLine: request.Executable));
        }

        public Task<CliResult> RunInNewTerminalAsync(
            CliTerminalRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Prompt steps must run hidden and captured — RunInNewTerminalAsync is the visible-window path.");
    }
}
