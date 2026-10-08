using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.VCA.Validators;

/// <summary>Adapts the aggregate quality rule for the legacy per-file validation API.</summary>
public sealed class CodeQualityValidator(Rule supportedRule) : IRuleValidator
{
    internal const string SnapshotKey = "CodeQualitySnapshot";

    /// <inheritdoc />
    public Rule SupportedRule => supportedRule;

    /// <inheritdoc />
    public Task<RuleValidationResult> ValidateAsync(string filePath, RuleWithEnforcement rule,
        string sourceFile, string rootPath, ValidationContext? context = null, CancellationToken ct = default)
    {
        if (!CodeQualityRule.TryParse(rule.RuleText, out var grade))
            return Task.FromResult(new RuleValidationResult(false, "UNSUPPORTED: Code quality minimum must be A, B or C."));

        if (context?.AdditionalData?.GetValueOrDefault(SnapshotKey) is not GitStagedSnapshot snapshot)
            return Task.FromResult(new RuleValidationResult(false, "UNSUPPORTED: Code quality needs an aggregate source snapshot."));

        return Task.FromResult(CodeQualityRule.Evaluate(grade, CodeQualityRule.InScope(snapshot, sourceFile), ct));
    }
}
