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

    public VcaHookRunner(
        IGitPreflightPipeline preflightPipeline,
        IVcaHookValidationAnalyzer analyzer,
        IVcaHookPresenter presenter)
    {
        _preflightPipeline = preflightPipeline;
        _analyzer = analyzer;
        _presenter = presenter;
    }

    public async Task<int> RunAsync(VcaHookInvocation invocation, CancellationToken cancellationToken)
    {
        var workingDirectory = invocation.WorkingDirectory ?? Directory.GetCurrentDirectory();

        try
        {
            var preflightResult = await _preflightPipeline.RunAsync(
                new GitPreflightRequest(workingDirectory, invocation),
                async (preflightEvent, ct) =>
                    await _presenter.WritePreflightEventAsync(preflightEvent, ct),
                cancellationToken);
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

            var validationOutput = string.Join(
                Environment.NewLine,
                preflightResult.Steps
                    .First(step => step.StepId == VcaPreflightStep.Id)
                    .Output);

            return invocation.Kind switch
            {
                VcaHookKind.AcknowledgeCommitMessage => await RunAcknowledgmentPromptValidationAsync(
                    invocation,
                    validationOutput,
                    summary),
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

        var rawCommitMessage = await File.ReadAllTextAsync(invocation.CommitMessagePath);
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
        VcaHookValidationSummary summary)
    {
        if (summary.HasError || summary.HasStopViolation)
        {
            await _presenter.WriteFailureAsync("Blocking VCA validation still fails. Fix STOP-level violations before committing.");
            await PauseIfInteractiveAsync();
            return 1;
        }

        if (!summary.HasCommitViolations)
        {
            await _presenter.WriteSuccessAsync("No VCA commit acknowledgments were required.");
            await PauseIfInteractiveAsync();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(invocation.CommitMessagePath) ||
            !File.Exists(invocation.CommitMessagePath))
        {
            await _presenter.WriteErrorAsync($"Commit message file not found: {invocation.CommitMessagePath ?? "(missing)"}");
            await PauseIfInteractiveAsync();
            return 1;
        }

        var rawCommitMessage = await File.ReadAllTextAsync(invocation.CommitMessagePath);
        var commitMessage = await VcaCommitMessageCleaner.StripCommentsAsync(
            rawCommitMessage,
            invocation.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            CancellationToken.None);
        var missingAcknowledgments = _analyzer.GetMissingAcknowledgments(
            commitMessage,
            summary.RequiredAcknowledgments);

        if (missingAcknowledgments.Count == 0)
        {
            await _presenter.WriteSuccessAsync("VCA commit acknowledgments already found.");
            await PauseIfInteractiveAsync();
            return 0;
        }

        var accepted = await PromptAndAppendAcknowledgmentsAsync(
            invocation.CommitMessagePath,
            validationOutput,
            missingAcknowledgments);

        await PauseIfInteractiveAsync();
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
                summary) == 0;
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
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to launch VCA acknowledgment prompt");
            return false;
        }
    }

    private async Task<bool> PromptAndAppendAcknowledgmentsAsync(
        string commitMessagePath,
        string validationOutput,
        IReadOnlyList<string> missingAcknowledgments)
    {
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

        await File.AppendAllTextAsync(commitMessagePath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        await _presenter.WriteSuccessAsync("VCA acknowledgment appended to the commit message.");
        return true;
    }

    private async Task PauseIfInteractiveAsync()
    {
        if (VcaHookProcessLaunch.CanPromptInCurrentConsole())
        {
            await _presenter.ReadLineAsync("Press Enter to close this VCA prompt.");
        }
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
