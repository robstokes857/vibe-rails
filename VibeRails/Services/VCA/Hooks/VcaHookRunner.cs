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
                VcaHookKind.CommitMessage => await RunCommitMessageValidationAsync(
                    invocation,
                    validationResult.Output,
                    validationResult.Summary),
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
        VcaHookValidationSummary summary)
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

        await _presenter.WriteValidationOutputAsync(validationOutput);
        await _presenter.WriteFailureAsync("Commit message missing required VCA acknowledgment token(s):");
        foreach (var token in missingAcknowledgments)
        {
            await _presenter.WriteFailureAsync($"  {token}");
        }

        await _presenter.WriteFailureAsync("Add the token(s) with a short reason, or fix the violations before committing.");
        return 1;
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
            VcaHookKind.CommitMessage => "Commit Message",
            VcaHookKind.Preview => "Preview",
            _ => "Pre-Commit"
        };

        var subtitle = invocation.Kind switch
        {
            VcaHookKind.CommitMessage => "Checking VCA commit-message requirements",
            VcaHookKind.Preview => "Previewing the VCA hook experience",
            _ => "Validating staged files against VCA rules"
        };

        var reason = invocation.Kind switch
        {
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
}
