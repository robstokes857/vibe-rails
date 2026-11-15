using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Board;

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

public sealed class VcaRulesHookValidationService(
    IGitStagedSnapshotProvider snapshotProvider,
    ICommitSignatureVerifier? signatureVerifier = null, CodeQualityExemptionService? exemptions = null,
    IStopBypassVerifier? stopBypassVerifier = null) : IVcaHookValidationService
{
    public async Task<VcaHookValidationResult> ValidateAsync(
        VcaHookInvocation invocation,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var snapshot = invocation.DemoUi
            ? GitStagedSnapshot.Preview(workingDirectory)
            : await snapshotProvider.CaptureAsync(workingDirectory, cancellationToken);
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
                StopBypassFormat.BeforeScissors(await File.ReadAllTextAsync(invocation.CommitMessagePath, cancellationToken)),
                workingDirectory,
                cancellationToken);
        }

        VerifiedStopBypass? stopBypass = null;
        string? bypassError = null;
        if (invocation.EnqueueAutomatedJobs && !invocation.DemoUi && !invocation.WorkingTreeScope
            && invocation.Kind is VcaHookKind.PreCommit or VcaHookKind.CommitMessage)
        {
            try
            {
                var result = await new StopBypassService(snapshotProvider, verifier: stopBypassVerifier).ResolveAsync(
                    snapshot, validateCommitMessage ? invocation.CommitMessagePath : null, commitMessage, cancellationToken);
                bypassError = result.Error;
                stopBypass = result.Verified;
                // ResolveAsync may append the approved audit trailers. Evaluate the message Git
                // will record, including the reason, so unrelated message rules still apply.
                if (validateCommitMessage)
                {
                    commitMessage = await VcaCommitMessageCleaner.StripCommentsAsync(
                        StopBypassFormat.BeforeScissors(await File.ReadAllTextAsync(invocation.CommitMessagePath!, cancellationToken)), workingDirectory, cancellationToken);
                    if (stopBypass is not null && (StopBypassFormat.Parse(commitMessage) is not { } evidence
                        || !StopBypassFormat.BuildPayload(evidence.Scope).SequenceEqual(StopBypassFormat.BuildPayload(stopBypass.Scope))))
                    {
                        bypassError = "Git's message cleanup removed or changed the signed STOP reason or evidence. The complete signed exception must remain in the commit message.";
                        stopBypass = null;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                bypassError = "The local STOP bypass could not be read or verified. Request approval again. " + ex.Message;
            }
        }

        var report = await RulesTool.ValidateVcaReportAsync(
            snapshotProvider,
            workingDirectory,
            commitMessage,
            validateCommitMessage,
            cancellationToken,
            snapshot,
            workingTreeScope: invocation.WorkingTreeScope,
            signatureVerifier: signatureVerifier, exemptions: exemptions, stopBypass: stopBypass);
        return new VcaHookValidationResult(
            report.Output + (bypassError is null ? "" : "\nERROR: " + bypassError),
            new VcaHookValidationSummary(
                report.HasError || bypassError is not null,
                report.HasStopViolation,
                report.RequiredAcknowledgments.Count > 0,
                report.RequiredAcknowledgments,
                report.StagedFileCount,
                report.ApplicableRuleCount,
                report.Findings,
                report.Violations));
    }
}
