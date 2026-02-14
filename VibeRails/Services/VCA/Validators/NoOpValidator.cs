using VibeRails.Services;

namespace VibeRails.Services.VCA.Validators
{
    /// <summary>
    /// Validator for disabled rules or rules that always pass
    /// </summary>
    public class NoOpValidator : IRuleValidator
    {
        private readonly Rule _rule;
        private readonly string _message;

        public Rule SupportedRule => _rule;

        public NoOpValidator(Rule rule, string message = "Disabled")
        {
            _rule = rule;
            _message = message;
        }

        public Task<RuleValidationResult> ValidateAsync(
            string filePath,
            RuleWithEnforcement rule,
            string sourceFile,
            string rootPath,
            ValidationContext? context = null,
            CancellationToken ct = default)
        {
            return Task.FromResult(new RuleValidationResult(true, _message));
        }
    }
}
