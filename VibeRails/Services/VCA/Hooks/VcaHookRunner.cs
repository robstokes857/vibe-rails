using System.Diagnostics;
using Serilog;

namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookRunner
{
    Task<int> RunAsync(VcaHookInvocation invocation, CancellationToken cancellationToken);
}

public sealed class VcaHookRunner : IVcaHookRunner
{
    private readonly IVcaHookValidationService _validationService;
    private readonly IVcaHookValidationAnalyzer _analyzer;
    private readonly IVcaHookFileProvider _fileProvider;
    private readonly IVcaHookPresenter _presenter;

    public VcaHookRunner(
        IVcaHookValidationService validationService,
        IVcaHookValidationAnalyzer analyzer,
        IVcaHookFileProvider fileProvider,
        IVcaHookPresenter presenter)
    {
        _validationService = validationService;
        _analyzer = analyzer;
        _fileProvider = fileProvider;
        _presenter = presenter;
    }

    public async Task<int> RunAsync(VcaHookInvocation invocation, CancellationToken cancellationToken)
    {
        var workingDirectory = invocation.WorkingDirectory ?? Directory.GetCurrentDirectory();

        try
        {
            var stagedFiles = invocation.Kind == VcaHookKind.Preview
                ? GetPreviewFiles()
                : await _fileProvider.GetStagedFilesAsync(workingDirectory, cancellationToken);

            var displayInfo = BuildDisplayInfo(invocation, stagedFiles);
            var validationResult = await _presenter.RunWithProgressAsync(
                displayInfo,
                ct => GetValidationResultAsync(invocation, workingDirectory, ct),
                cancellationToken);

            return invocation.Kind switch
            {
                VcaHookKind.AcknowledgeCommitMessage => await RunAcknowledgmentPromptValidationAsync(
                    invocation,
                    validationResult.Output,
                    validationResult.Summary),
                VcaHookKind.CommitMessage => await RunCommitMessageValidationAsync(
                    invocation,
                    validationResult.Output,
                    validationResult.Summary,
                    workingDirectory,
                    cancellationToken),
                VcaHookKind.Preview => await RunPreviewAsync(validationResult.Output),
                _ => await RunPreCommitValidationAsync(validationResult.Output, validationResult.Summary)
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VCA git hook validation failed");
            await _presenter.WriteErrorAsync($"VCA git hook validation failed: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> RunPreCommitValidationAsync(
        string validationOutput,
        VcaHookValidationSummary summary)
    {
        await _presenter.WriteValidationOutputAsync(validationOutput);

        if (_analyzer.ShouldBlockPreCommit(summary))
        {
            await _presenter.WriteFailureAsync("VCA pre-commit checks failed. Commit blocked.");
            return 1;
        }

        if (summary.HasCommitViolations)
        {
            await _presenter.WriteWarningAsync("COMMIT-level VCA violations detected. The commit message must include the listed acknowledgments.");
            return 0;
        }

        await _presenter.WriteSuccessAsync("VCA pre-commit checks passed. Git commit can continue.");
        return 0;
    }

    private async Task<int> RunPreviewAsync(string validationOutput)
    {
        await _presenter.WriteValidationOutputAsync(validationOutput);
        await _presenter.WriteSuccessAsync("VCA hook preview completed.");
        return 0;
    }

    private async Task<int> RunCommitMessageValidationAsync(
        VcaHookInvocation invocation,
        string validationOutput,
        VcaHookValidationSummary summary,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (summary.HasError || summary.HasStopViolation)
        {
            await _presenter.WriteValidationOutputAsync(validationOutput);
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
        var commitMessage = StripCommitComments(rawCommitMessage);
        var missingAcknowledgments = _analyzer.GetMissingAcknowledgments(
            commitMessage,
            summary.RequiredAcknowledgments);

        if (missingAcknowledgments.Count == 0)
        {
            await _presenter.WriteSuccessAsync("VCA commit acknowledgments found. Git commit can continue.");
            return 0;
        }

        if (invocation.PromptForAcknowledgment &&
            await TryPromptForAcknowledgmentsAsync(invocation, workingDirectory, cancellationToken))
        {
            await _presenter.WriteSuccessAsync("VCA commit acknowledgments added. Git commit can continue.");
            return 0;
        }

        await _presenter.WriteValidationOutputAsync(validationOutput);
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
            await _presenter.WriteValidationOutputAsync(validationOutput);
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
        var commitMessage = StripCommitComments(rawCommitMessage);
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
        CancellationToken cancellationToken)
    {
        if (CanPromptInCurrentConsole())
        {
            return await RunAcknowledgmentPromptInCurrentProcessAsync(invocation);
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

    private async Task<bool> RunAcknowledgmentPromptInCurrentProcessAsync(VcaHookInvocation invocation)
    {
        var validationResult = await GetValidationResultAsync(
            invocation with { Kind = VcaHookKind.AcknowledgeCommitMessage },
            invocation.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            CancellationToken.None);

        return await RunAcknowledgmentPromptValidationAsync(
            invocation,
            validationResult.Output,
            validationResult.Summary) == 0;
    }

    private async Task<bool> PromptAndAppendAcknowledgmentsAsync(
        string commitMessagePath,
        string validationOutput,
        IReadOnlyList<string> missingAcknowledgments)
    {
        await _presenter.WriteValidationOutputAsync(validationOutput);
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

    private async Task<VcaHookValidationResult> GetValidationResultAsync(
        VcaHookInvocation invocation,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (invocation.DemoUi)
        {
            await Task.Delay(invocation.DemoDuration, cancellationToken);
            return new VcaHookValidationResult(
                "PASS: VCA hook preview completed.",
                new VcaHookValidationSummary(
                    HasError: false,
                    HasStopViolation: false,
                    HasCommitViolations: false,
                    RequiredAcknowledgments: []));
        }

        return await _validationService.ValidateAsync(workingDirectory, cancellationToken);
    }

    private static VcaHookDisplayInfo BuildDisplayInfo(
        VcaHookInvocation invocation,
        IReadOnlyList<string> stagedFiles)
    {
        var title = invocation.Kind switch
        {
            VcaHookKind.AcknowledgeCommitMessage => "Commit Acknowledgment",
            VcaHookKind.CommitMessage => "Commit Message",
            VcaHookKind.Preview => "Preview",
            _ => "Pre-Commit"
        };

        var subtitle = invocation.Kind switch
        {
            VcaHookKind.AcknowledgeCommitMessage => "Collecting VCA commit acknowledgment",
            VcaHookKind.CommitMessage => "Checking VCA commit-message requirements",
            VcaHookKind.Preview => "Previewing the VCA hook experience",
            _ => "Validating staged files against VCA rules"
        };

        var reason = invocation.Kind switch
        {
            VcaHookKind.AcknowledgeCommitMessage => "Reason: VCA needs an acknowledgment before Git records this commit.",
            VcaHookKind.CommitMessage => "Reason: git commit-msg hook is checking required VCA acknowledgments.",
            VcaHookKind.Preview => "Reason: manual preview of the VCA hook progress UI.",
            _ => "Reason: git pre-commit hook is running VCA validation before Git creates the commit."
        };

        return new VcaHookDisplayInfo(title, subtitle, reason, stagedFiles);
    }

    private static IReadOnlyList<string> GetPreviewFiles() =>
    [
        "src/security/AuthPolicy.cs",
        "src/payments/CardVault.cs",
        "package.json"
    ];

    // Mirrors git's default commit-message cleanup: lines whose first character is the
    // comment char ('#') are removed from the recorded message.
    private static string StripCommitComments(string commitMessage)
    {
        var kept = new List<string>();
        foreach (var line in commitMessage.Split('\n'))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    private static bool CanPromptInCurrentConsole() =>
        Environment.UserInteractive &&
        !Console.IsInputRedirected &&
        !Console.IsOutputRedirected;

    private async Task PauseIfInteractiveAsync()
    {
        if (CanPromptInCurrentConsole())
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

        var commandLineArgs = Environment.GetCommandLineArgs();
        var args = hookArgs.Select(QuoteArgument).ToList();

        if (Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            commandLineArgs.Length > 0 &&
            commandLineArgs[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            args.Insert(0, QuoteArgument(commandLineArgs[0]));
        }

        return (processPath, string.Join(" ", args));
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return $"\"{value.Replace("\"", "\\\"")}\"";
    }
}
