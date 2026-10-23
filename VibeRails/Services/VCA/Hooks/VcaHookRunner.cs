using System.Diagnostics;
using Serilog;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookRunner
{
    Task<int> RunAsync(VcaHookInvocation invocation, CancellationToken cancellationToken);
}

public sealed class VcaHookRunner : IVcaHookRunner
{
    private readonly IGitPreflightPipeline _preflightPipeline;
    private readonly IVcaHookValidationAnalyzer _analyzer;
    private readonly IVcaHookPresenter _presenter;
    private readonly ICommitMessageCoAuthorCleaner _coAuthorCleaner;
    private readonly IHumanSignaturePrompt? _signaturePrompt;

    /// <summary>How many times one commit-msg run offers the signing popup.</summary>
    internal const int MaxSignatureRounds = 3;

    public VcaHookRunner(
        IGitPreflightPipeline preflightPipeline,
        IVcaHookValidationAnalyzer analyzer,
        IVcaHookPresenter presenter,
        ICommitMessageCoAuthorCleaner coAuthorCleaner,
        IHumanSignaturePrompt? signaturePrompt = null)
    {
        _preflightPipeline = preflightPipeline;
        _analyzer = analyzer;
        _presenter = presenter;
        _coAuthorCleaner = coAuthorCleaner;
        _signaturePrompt = signaturePrompt;
    }

    public async Task<int> RunAsync(VcaHookInvocation invocation, CancellationToken cancellationToken)
    {
        var workingDirectory = invocation.WorkingDirectory ?? Directory.GetCurrentDirectory();

        try
        {
            // Hook scripts from this version onwards clean the message in a separate pass that runs
            // before any chained commit-msg hook, and say so here. Cleaning again after that hook
            // has already inspected the message is the ordering bug this flag exists to end; doing
            // it inline is kept only for hook scripts installed before the separate pass existed.
            if (invocation.Kind == VcaHookKind.CommitMessage && !invocation.CoAuthorsAlreadyCleaned)
            {
                var removedCount = await _coAuthorCleaner.RemoveAsync(
                    invocation.CommitMessagePath,
                    cancellationToken);
                if (removedCount > 0)
                {
                    var suffix = removedCount == 1 ? "trailer" : "trailers";
                    await _presenter.WriteWarningAsync(
                        $"Removed {removedCount} {suffix} from the commit message (Co-authored-by / Claude-Session cleanup).");
                }
            }

            var preflightRequest = new GitPreflightRequest(
                workingDirectory,
                invocation,
                EnqueueAutomatedJobs: invocation.Kind == VcaHookKind.PreCommit
                    && !invocation.DemoUi
                    && invocation.EnqueueAutomatedJobs,
                IncludeReport: false);
            var preflightResult = await RunPreflightAsync(preflightRequest, cancellationToken);
            var summary = preflightResult.VcaSummary;
            if (summary == null)
            {
                if (preflightResult.Status == GitPreflightStepStatus.Cancelled)
                {
                    return 1;
                }

                await _presenter.WriteErrorAsync("VCA did not produce a validation result. Commit blocked.");
                return 1;
            }

            var validationOutput = VcaOutput(preflightResult);

            if (invocation.Kind == VcaHookKind.CommitMessage)
            {
                (summary, validationOutput) = await CollectHumanSignaturesAsync(
                    invocation,
                    workingDirectory,
                    preflightRequest,
                    summary,
                    validationOutput,
                    cancellationToken);
            }

            return invocation.Kind switch
            {
                VcaHookKind.AcknowledgeCommitMessage => await RunAcknowledgmentPromptValidationAsync(
                    invocation,
                    validationOutput,
                    summary,
                    cancellationToken),
                VcaHookKind.CommitMessage => await RunCommitMessageValidationAsync(
                    invocation,
                    validationOutput,
                    summary,
                    workingDirectory,
                    cancellationToken),
                VcaHookKind.Preview => 0,
                _ => RunPreCommitValidation(summary)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VCA git hook validation failed");
            await _presenter.WriteErrorAsync($"VCA git hook validation failed: {ex.Message}");
            return 1;
        }
    }

    private Task<GitPreflightRunResult> RunPreflightAsync(GitPreflightRequest request, CancellationToken cancellationToken) =>
        _preflightPipeline.RunAsync(
            request,
            async (preflightEvent, ct) =>
                await _presenter.WritePreflightEventAsync(preflightEvent, ct),
            cancellationToken);

    private static string VcaOutput(GitPreflightRunResult result) =>
        string.Join(
            Environment.NewLine,
            result.Steps.First(step => step.StepId == VcaPreflightStep.Id).Output);

    /// <summary>
    /// Findings a person can resolve by signing: Human verified / Signed Approval violations at a
    /// blocking level. WARN findings never open the popup; nothing about them stops the commit.
    /// </summary>
    internal static IReadOnlyList<VcaRuleFinding> PendingSignatures(VcaHookValidationSummary summary) =>
        (summary.Findings ?? [])
            .Where(finding => finding.SignatureRequest is not null
                && finding.Kind is VcaRuleFindingKind.Blocked or VcaRuleFindingKind.AcknowledgmentRequired)
            .ToList();

    /// <summary>
    /// Offers the Git Guard signing popup while the commit is missing a person's signature, then
    /// re-validates the message. Skipped when nobody can answer (the hook script passes no prompt
    /// flag in CI or for a redirected commit without a popup) and when something unrelated would
    /// block the commit anyway: nobody should type a key password for a commit that cannot land.
    /// </summary>
    private async Task<(VcaHookValidationSummary Summary, string Output)> CollectHumanSignaturesAsync(
        VcaHookInvocation invocation,
        string workingDirectory,
        GitPreflightRequest preflightRequest,
        VcaHookValidationSummary summary,
        string validationOutput,
        CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxSignatureRounds; round++)
        {
            var pending = PendingSignatures(summary);
            if (pending.Count == 0 || _signaturePrompt is null)
                break;

            // Said out loud: a silent block is what sent an agent to run the hook by hand (VIBE-200).
            if (!invocation.PromptForAcknowledgment)
            {
                await _presenter.WriteWarningAsync(
                    $"This commit needs a person's signature, but Git Guard did not open the signing popup: {DescribeMissingSigningPopup()}. "
                    + "A person can make this commit from their own terminal, VS Code or a Git GUI, where commit-msg opens the popup.");
                break;
            }

            var otherBlockers = summary.HasError
                || (summary.Findings ?? []).Any(finding =>
                    finding.Kind == VcaRuleFindingKind.Blocked && finding.SignatureRequest is null);
            if (otherBlockers)
            {
                await _presenter.WriteWarningAsync(
                    "This commit needs a person's signature, but other blocking violations must be fixed first.");
                break;
            }

            await _presenter.WriteWarningAsync("This commit needs a person's signature. Opening the Git Guard signing prompt...");
            if (!await _signaturePrompt.RequestAsync(invocation, workingDirectory, pending, cancellationToken))
            {
                await _presenter.WriteFailureAsync("No signature was added to the commit message.");
                break;
            }

            await _presenter.WriteSuccessAsync("Signature added. Checking the commit again...");
            var recheck = await RunPreflightAsync(preflightRequest, cancellationToken);
            if (recheck.VcaSummary is null)
                break;
            summary = recheck.VcaSummary;
            validationOutput = VcaOutput(recheck);
        }

        return (summary, validationOutput);
    }

    /// <summary>
    /// Why this commit-msg run cannot ask a person, in terms they can act on. The hook script asks for
    /// prompts only with a terminal on both stdin and stdout, or with the Git Guard console it requests
    /// on Windows when both are redirected and no CI variable is set.
    /// </summary>
    internal static string DescribeMissingSigningPopup()
    {
        if (!OperatingSystem.IsWindows())
            return "the popup is available on Windows only in this version";
        if (VcaHookConsoleRespawn.AutomatedEnvironmentVariable() is { } variable)
            return $"{variable} is set in this commit's environment, and Git Guard never opens a window under CI";
        if (!Environment.UserInteractive)
            return "this commit runs without an interactive desktop";
        return "this hook run has neither a terminal on both its input and output nor a Git Guard console to show the popup from";
    }

    // Commit-level (acknowledgment-required) violations are surfaced in the transcript but
    // do not block the pre-commit stage; they are enforced later by the commit-msg hook.
    // Only STOP-level violations and errors block here.
    private int RunPreCommitValidation(VcaHookValidationSummary summary) =>
        _analyzer.ShouldBlockPreCommit(summary) ? 1 : 0;

    private async Task<int> RunCommitMessageValidationAsync(
        VcaHookInvocation invocation,
        string validationOutput,
        VcaHookValidationSummary summary,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (summary.HasError || summary.HasStopViolation)
        {
            await _presenter.WriteFailureAsync("VCA commit-message checks failed because blocking validation still fails.");
            return 1;
        }

        if (!summary.HasCommitViolations)
        {
            await _presenter.WriteSuccessAsync("No VCA commit acknowledgments were required.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(invocation.CommitMessagePath) ||
            !File.Exists(invocation.CommitMessagePath))
        {
            await _presenter.WriteErrorAsync($"Commit message file not found: {invocation.CommitMessagePath ?? "(missing)"}");
            return 1;
        }

        var rawCommitMessage = await File.ReadAllTextAsync(
            invocation.CommitMessagePath,
            cancellationToken);
        // Match against the message git will actually record: drop comment lines. An
        // acknowledgment token pasted into the commented template region would otherwise
        // pass here but be stripped from the final commit, defeating the audit trail.
        var commitMessage = await VcaCommitMessageCleaner.StripCommentsAsync(
            rawCommitMessage,
            workingDirectory,
            cancellationToken);
        var missingAcknowledgments = _analyzer.GetMissingAcknowledgments(
            commitMessage,
            summary.RequiredAcknowledgments);

        if (missingAcknowledgments.Count == 0)
        {
            await _presenter.WriteSuccessAsync("VCA commit acknowledgments found. Git commit can continue.");
            return 0;
        }

        if (invocation.PromptForAcknowledgment &&
            await TryPromptForAcknowledgmentsAsync(
                invocation,
                workingDirectory,
                validationOutput,
                summary,
                cancellationToken))
        {
            await _presenter.WriteSuccessAsync("VCA commit acknowledgments added. Git commit can continue.");
            return 0;
        }

        await _presenter.WriteFailureAsync("Commit message missing required VCA acknowledgment token(s):");
        foreach (var token in missingAcknowledgments)
        {
            await _presenter.WriteFailureAsync($"  {token}");
        }

        await _presenter.WriteFailureAsync("Add the token(s) with a short reason, or fix the violations before committing.");
        return 1;
    }

    private async Task<int> RunAcknowledgmentPromptValidationAsync(
        VcaHookInvocation invocation,
        string validationOutput,
        VcaHookValidationSummary summary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (summary.HasError || summary.HasStopViolation)
        {
            await _presenter.WriteFailureAsync("Blocking VCA validation still fails. Fix STOP-level violations before committing.");
            await PauseIfInteractiveAsync(invocation);
            return 1;
        }

        if (!summary.HasCommitViolations)
        {
            await _presenter.WriteSuccessAsync("No VCA commit acknowledgments were required.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(invocation.CommitMessagePath) ||
            !File.Exists(invocation.CommitMessagePath))
        {
            await _presenter.WriteErrorAsync($"Commit message file not found: {invocation.CommitMessagePath ?? "(missing)"}");
            await PauseIfInteractiveAsync(invocation);
            return 1;
        }

        var rawCommitMessage = await File.ReadAllTextAsync(
            invocation.CommitMessagePath,
            cancellationToken);
        var commitMessage = await VcaCommitMessageCleaner.StripCommentsAsync(
            rawCommitMessage,
            invocation.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            cancellationToken);
        var missingAcknowledgments = _analyzer.GetMissingAcknowledgments(
            commitMessage,
            summary.RequiredAcknowledgments);

        if (missingAcknowledgments.Count == 0)
        {
            await _presenter.WriteSuccessAsync("VCA commit acknowledgments already found.");
            return 0;
        }

        var accepted = await PromptAndAppendAcknowledgmentsAsync(
            invocation.CommitMessagePath,
            validationOutput,
            missingAcknowledgments,
            cancellationToken);

        if (!accepted)
        {
            await PauseIfInteractiveAsync(invocation);
        }
        return accepted ? 0 : 1;
    }

    private async Task<bool> TryPromptForAcknowledgmentsAsync(
        VcaHookInvocation invocation,
        string workingDirectory,
        string validationOutput,
        VcaHookValidationSummary summary,
        CancellationToken cancellationToken)
    {
        if (VcaHookProcessLaunch.CanPromptInCurrentConsole())
        {
            return await RunAcknowledgmentPromptValidationAsync(
                invocation with { Kind = VcaHookKind.AcknowledgeCommitMessage },
                validationOutput,
                summary,
                cancellationToken) == 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var (fileName, arguments) = BuildCurrentProcessLaunch(
            [
                "--vca-hook",
                "acknowledge-commit-msg",
                "--commit-message",
                invocation.CommitMessagePath ?? "",
                "--workdir",
                workingDirectory
            ]);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            });

            if (process == null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to launch VCA acknowledgment prompt");
            return false;
        }
    }

    private async Task<bool> PromptAndAppendAcknowledgmentsAsync(
        string commitMessagePath,
        string validationOutput,
        IReadOnlyList<string> missingAcknowledgments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _presenter.WriteWarningAsync("This commit requires a VCA acknowledgment reason.");
        await _presenter.WriteWarningAsync("Required token(s):");

        foreach (var token in missingAcknowledgments)
        {
            await _presenter.WriteWarningAsync($"  {token}");
        }

        var reason = await _presenter.ReadLineAsync("Reason to append to the commit message (blank cancels): ");
        reason = NormalizeReason(reason);

        if (string.IsNullOrWhiteSpace(reason))
        {
            await _presenter.WriteFailureAsync("No reason entered. Commit canceled.");
            return false;
        }

        var lines = new List<string>
        {
            "",
            "VCA acknowledgments:"
        };

        foreach (var token in missingAcknowledgments.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"{token} Reason: {reason}");
        }

        await File.AppendAllTextAsync(
            commitMessagePath,
            string.Join(Environment.NewLine, lines) + Environment.NewLine,
            cancellationToken);
        await _presenter.WriteSuccessAsync("VCA acknowledgment appended to the commit message.");
        return true;
    }

    private async Task PauseIfInteractiveAsync(VcaHookInvocation invocation)
    {
        if (invocation.ConsoleWindowAttached ||
            !VcaHookProcessLaunch.CanPromptInCurrentConsole())
        {
            return;
        }

        var timeout = VcaHookProcessHost.ConsolePauseTimeout;
        var readTask = _presenter.ReadLineAsync(
            $"Press Enter to close this VCA prompt ({VcaHookProcessHost.DescribeAutoClose(timeout)})...");

        using var timer = new CancellationTokenSource();
        if (await Task.WhenAny(readTask, Task.Delay(timeout, timer.Token)) == readTask)
        {
            await readTask;
            return;
        }

        // Timed out with the read still outstanding. It cannot be cancelled: IVcaHookPresenter's
        // ReadLineAsync takes no token, and it bottoms out in Console.In.ReadLineAsync, which has
        // no cancellable form — a pending console read only ends when a line arrives or the handle
        // closes. That is survivable here and nowhere else in this file: this is the last thing the
        // hook does before returning its exit code, the read is sitting on a thread-pool thread
        // (background, so it cannot hold the process open), and the presenter released its write
        // lock before reading, so nothing else can block behind it. Observe a fault so it can never
        // surface as an unobserved task exception.
        _ = readTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string NormalizeReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : reason.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static (string FileName, string Arguments) BuildCurrentProcessLaunch(IReadOnlyList<string> hookArgs)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Unable to determine VibeRails executable path.");
        }

        var args = VcaHookProcessLaunch.WithManagedEntry(processPath, hookArgs);
        return (processPath, string.Join(" ", args.Select(VcaHookProcessLaunch.QuoteArgument)));
    }
}
