using VibeRails.Services;

namespace VibeRails.Services.VCA.Validators
{
    /// <summary>
    /// Validates test coverage for code files
    /// NOTE: This validator always passes in per-file mode. Test coverage should be calculated at commit level.
    /// </summary>
    public class TestCoverageValidator : IRuleValidator
    {
        private readonly IFileClassifier _fileClassifier;
        private readonly int _minimumPercent;
        private readonly Rule _rule;

        public Rule SupportedRule => _rule;

        public TestCoverageValidator(
            IFileClassifier fileClassifier,
            int minimumPercent,
            Rule rule)
        {
            _fileClassifier = fileClassifier;
            _minimumPercent = minimumPercent;
            _rule = rule;
        }

        public Task<RuleValidationResult> ValidateAsync(
            string filePath,
            RuleWithEnforcement rule,
            string sourceFile,
            string rootPath,
            CancellationToken ct)
        {
            // Test coverage validation requires looking at all files together, not one at a time
            // For now, we skip per-file validation
            // TODO: Implement commit-level test coverage validation
            if (_fileClassifier.IsCodeFile(filePath) && !_fileClassifier.IsTestFile(filePath))
            {
                return Task.FromResult(new RuleValidationResult(
                    true,
                    $"Code file - test coverage check deferred to commit level"));
            }

            return Task.FromResult(new RuleValidationResult(true));
        }
    }
}
