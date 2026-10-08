namespace VibeRails.Services.VCA.Validators;

/// <summary>Applies the session-link rule to the legacy validation context.</summary>
public sealed class SessionShareCommitValidator : IRuleValidator
{
    public Rule SupportedRule => Rule.RequireVibeRailsSessionLink;

    public Task<RuleValidationResult> ValidateAsync(string filePath, RuleWithEnforcement rule,
        string sourceFile, string rootPath, ValidationContext? context = null, CancellationToken ct = default)
    {
        if (context?.CommitMessage is null)
            return Task.FromResult(new RuleValidationResult(true, SessionShareCommitRule.DeferredMessage));
        var found = SessionShareCommitRule.HasShareLink(context.CommitMessage);
        return Task.FromResult(new RuleValidationResult(found,
            found ? "Commit message contains a VibeRails session sharing link" : SessionShareCommitRule.MissingMessage));
    }
}
