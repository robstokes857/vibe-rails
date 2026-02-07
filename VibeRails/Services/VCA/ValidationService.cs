using VibeRails.Services;

namespace VibeRails.Services.VCA
{
    /// <summary>
    /// Result of validating a single file against a rule
    /// </summary>
    public record FileValidationResult(
        string FilePath,
        string RuleName,
        Enforcement Enforcement,
        bool Passed,
        string SourceFile,
        string? Message = null);

    /// <summary>
    /// Set of validation results for all files and rules
    /// </summary>
    public record VcaValidationResultSet(
        List<FileValidationResult> Results,
        int TotalFiles,
        int TotalRules);

    /// <summary>
    /// Main validation service that orchestrates file and rule validation
    /// </summary>
    public class ValidationService
    {
        private readonly IValidatorList _validatorList;
        private readonly IFileAndRuleParser _fileAndRuleParser;

        public ValidationService(IValidatorList validatorList, IFileAndRuleParser fileAndRuleParser)
        {
            _validatorList = validatorList;
            _fileAndRuleParser = fileAndRuleParser;
        }

        /// <summary>
        /// Validates all changed files against their applicable rules
        /// </summary>
        /// <param name="rootPath">Repository root path</param>
        /// <param name="stagedOnly">If true, only validate staged files (pre-commit). If false, validate all changed files.</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<VcaValidationResultSet> ValidateAsync(string rootPath, bool stagedOnly, CancellationToken cancellationToken)
        {
            var results = new List<FileValidationResult>();
            var filesAndRules = await _fileAndRuleParser.GetFilesAndRulesAsync(rootPath, stagedOnly, cancellationToken);

            int totalRules = 0;

            foreach (var (filePath, rulesWithSource) in filesAndRules)
            {
                foreach (var ruleWithSource in rulesWithSource)
                {
                    totalRules++;

                    var isValid = await _validatorList.IsGoodCodeAsync(
                        filePath,
                        ruleWithSource.Rule,
                        ruleWithSource.SourceFile,
                        rootPath,
                        cancellationToken);

                    // Only collect violations (failed validations)
                    if (!isValid)
                    {
                        results.Add(new FileValidationResult(
                            filePath,
                            ruleWithSource.Rule.RuleText,
                            ruleWithSource.Rule.Enforcement,
                            false,
                            ruleWithSource.SourceFile,
                            $"Validation failed for rule: {ruleWithSource.Rule.RuleText}"));
                    }
                }
            }

            return new VcaValidationResultSet(results, filesAndRules.Count, totalRules);
        }
    }

    /// <summary>
    /// Registry that maps rules to validators without requiring 25+ constructor params
    /// </summary>
    public interface IValidatorList
    {
        /// <summary>
        /// Validates a single file against a rule
        /// </summary>
        /// <param name="filePath">The file to validate</param>
        /// <param name="rule">The rule to check</param>
        /// <param name="sourceFile">The AGENTS.md file where this rule came from (for context-aware validation)</param>
        /// <param name="rootPath">The repository root path</param>
        /// <param name="cancellation">Cancellation token</param>
        /// <returns>True if validation passes, false if it fails</returns>
        public Task<bool> IsGoodCodeAsync(string filePath, RuleWithEnforcement rule, string sourceFile, string rootPath, CancellationToken cancellation);
    }

    /// <summary>
    /// Gets the list of files and rules to validate
    /// Depends on IGitService and IAgentFileService
    /// </summary>
    public interface IFileAndRuleParser
    {
        /// <summary>
        /// Gets changed files from git and maps them to their applicable rules with enforcement levels
        /// </summary>
        /// <param name="rootPath">Repository root path</param>
        /// <param name="stagedOnly">If true, only get staged files (pre-commit). If false, get all changed files.</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Dictionary where key=file path, value=list of rules with source AGENTS.md file</returns>
        public Task<Dictionary<string, List<RuleWithSource>>> GetFilesAndRulesAsync(string rootPath, bool stagedOnly, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Rule with its source AGENTS.md file for context-aware validation
    /// </summary>
    public record RuleWithSource(RuleWithEnforcement Rule, string SourceFile);
}
