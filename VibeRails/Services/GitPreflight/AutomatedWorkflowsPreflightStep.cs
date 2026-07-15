namespace VibeRails.Services.GitPreflight;

using VibeRails.Services.VCA.Hooks;

public sealed class AutomatedWorkflowsPreflightStep : IGitPreflightStep
{
    public const string Id = "automated-workflows";
    public const string PlaceholderMessage = "Placeholder for automated workflows";

    public string StepId => Id;
    public string DisplayName => "Automated workflows";
    public bool CanBlock => false;

    public async Task<GitPreflightStepResult> ExecuteAsync(
        GitPreflightStepContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.Invocation.Kind is VcaHookKind.CommitMessage
            or VcaHookKind.AcknowledgeCommitMessage)
        {
            const string skippedMessage = "Automated workflows already ran during pre-commit; commit-message step skipped.";
            await context.WriteOutputAsync(skippedMessage, null, cancellationToken);
            return new GitPreflightStepResult(
                Id,
                DisplayName,
                GitPreflightStepStatus.Skipped,
                skippedMessage,
                [skippedMessage],
                DurationMs: 0,
                Blocking: false);
        }

        await context.WriteOutputAsync(PlaceholderMessage, null, cancellationToken);
        return new GitPreflightStepResult(
            Id,
            DisplayName,
            GitPreflightStepStatus.Skipped,
            PlaceholderMessage,
            [PlaceholderMessage],
            DurationMs: 0,
            Blocking: false);
    }
}
