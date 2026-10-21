namespace VibeRails.Services.VCA.Validators;

/// <summary>
/// Human verified / Signed Approval in the legacy per-file validation API. That API has no staged
/// tree, so it cannot check a signature: before a message exists the rule defers, and once one
/// exists it reports UNSUPPORTED rather than guessing. Git Guard's commit-msg hook is the verifier.
/// </summary>
public sealed class HumanSignatureValidator(Rule supportedRule) : IRuleValidator
{
    /// <inheritdoc />
    public Rule SupportedRule => supportedRule;

    /// <inheritdoc />
    public Task<RuleValidationResult> ValidateAsync(string filePath, RuleWithEnforcement rule,
        string sourceFile, string rootPath, ValidationContext? context = null, CancellationToken ct = default)
    {
        if (context?.CommitMessage is null)
            return Task.FromResult(new RuleValidationResult(true, HumanSignatureRule.HumanVerifiedDeferredMessage));
        return Task.FromResult(new RuleValidationResult(false,
            "UNSUPPORTED: commit signatures are verified only by Git Guard's commit-msg hook, against the staged tree."));
    }
}
