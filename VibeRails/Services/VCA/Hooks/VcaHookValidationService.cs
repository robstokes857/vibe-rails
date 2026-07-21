using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookValidationService
{
    Task<VcaHookValidationResult> ValidateAsync(
        VcaHookInvocation invocation,
        string workingDirectory,
        CancellationToken cancellationToken);

    Task<VcaHookValidationResult> ValidateAsync(
        VcaHookInvocation invocation,
        GitStagedSnapshot snapshot,
        CancellationToken cancellationToken);
}

public sealed class VcaRulesHookValidationService : IVcaHookValidationService
{
    public async Task<VcaHookValidationResult> ValidateAsync(
        VcaHookInvocation invocation,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var snapshot = invocation.DemoUi
            ? GitStagedSnapshot.Preview(workingDirectory)
            : await new GitStagedSnapshotProvider().CaptureAsync(workingDirectory, cancellationToken);
        return await ValidateAsync(invocation, snapshot, cancellationToken);
    }

    public async Task<VcaHookValidationResult> ValidateAsync(
        VcaHookInvocation invocation,
        GitStagedSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workingDirectory = snapshot.RepositoryPath;
        var validateCommitMessage = invocation.Kind is VcaHookKind.CommitMessage
            or VcaHookKind.AcknowledgeCommitMessage;
        string? commitMessage = null;

        if (validateCommitMessage)
        {
            if (string.IsNullOrWhiteSpace(invocation.CommitMessagePath)
                || !File.Exists(invocation.CommitMessagePath))
            {
                return new VcaHookValidationResult(
                    $"ERROR: Commit message file not found: {invocation.CommitMessagePath ?? "(missing)"}",
                    new VcaHookValidationSummary(
                        HasError: true,
                        HasStopViolation: false,
                        HasCommitViolations: false,
                        RequiredAcknowledgments: []));
            }

            commitMessage = await VcaCommitMessageCleaner.StripCommentsAsync(
                await File.ReadAllTextAsync(invocation.CommitMessagePath, cancellationToken),
                workingDirectory,
                cancellationToken);
        }

        var report = await RulesTool.ValidateVcaReportAsync(
            workingDirectory,
            commitMessage,
            validateCommitMessage,
            cancellationToken,
            snapshot,
            workingTreeScope: invocation.WorkingTreeScope);
        return new VcaHookValidationResult(
            report.Output,
            new VcaHookValidationSummary(
                report.HasError,
                report.HasStopViolation,
                report.RequiredAcknowledgments.Count > 0,
                report.RequiredAcknowledgments,
                report.StagedFileCount,
                report.ApplicableRuleCount,
                report.Findings));
    }
}
