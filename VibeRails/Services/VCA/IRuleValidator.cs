using VibeRails.Services;

namespace VibeRails.Services.VCA
{
    /// <summary>
    /// Strategy interface for validators - each rule type implements this
    /// </summary>
    public interface IRuleValidator
    {
        /// <summary>
        /// The rule type this validator handles
        /// </summary>
        Rule SupportedRule { get; }

        /// <summary>
        /// Validates a single file against the rule
        /// </summary>
        /// <param name="filePath">The file to validate</param>
        /// <param name="rule">The rule to check</param>
        /// <param name="sourceFile">The AGENTS.md file where this rule came from (for context-aware validation)</param>
        /// <param name="rootPath">The repository root path</param>
        /// <param name="ct">Cancellation token</param>
        Task<RuleValidationResult> ValidateAsync(
            string filePath,
            RuleWithEnforcement rule,
            string sourceFile,
            string rootPath,
            CancellationToken ct);
    }

    /// <summary>
    /// Result of a single rule validation
    /// </summary>
    public record RuleValidationResult(
        bool IsValid,
        string? Message = null,
        object? Metadata = null);
}
