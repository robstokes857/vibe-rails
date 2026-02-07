using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services;
using VibeRails.Services.VCA.Validators;

namespace VibeRails.Services.VCA
{
    /// <summary>
    /// Registry that maps Rule types to their validators using factory pattern
    /// Avoids needing 25+ constructor parameters
    /// </summary>
    public class ValidatorList : IValidatorList
    {
        private readonly Dictionary<Rule, IRuleValidator> _validators;
        private readonly IRulesService _rulesService;

        public ValidatorList(IServiceProvider serviceProvider)
        {
            _rulesService = serviceProvider.GetRequiredService<IRulesService>();

            // Build validator map using factory pattern
            _validators = new Dictionary<Rule, IRuleValidator>
            {
                // Log file changes rules
                [Rule.LogAllFileChanges] = serviceProvider.GetRequiredService<LogAllFileChangesValidator>(),

                [Rule.LogFileChangesOver5Lines] = ActivatorUtilities.CreateInstance<FileChangesOverLinesValidator>(
                    serviceProvider, 5, Rule.LogFileChangesOver5Lines),

                [Rule.LogFileChangesOver10Lines] = ActivatorUtilities.CreateInstance<FileChangesOverLinesValidator>(
                    serviceProvider, 10, Rule.LogFileChangesOver10Lines),

                // Cyclomatic complexity rules
                [Rule.CyclomaticComplexityUnder20] = ActivatorUtilities.CreateInstance<CyclomaticComplexityValidator>(
                    serviceProvider, 20, Rule.CyclomaticComplexityUnder20),

                [Rule.CyclomaticComplexityUnder35] = ActivatorUtilities.CreateInstance<CyclomaticComplexityValidator>(
                    serviceProvider, 35, Rule.CyclomaticComplexityUnder35),

                [Rule.CyclomaticComplexityUnder60] = ActivatorUtilities.CreateInstance<CyclomaticComplexityValidator>(
                    serviceProvider, 60, Rule.CyclomaticComplexityUnder60),

                [Rule.CyclomaticComplexityDisabled] = new NoOpValidator(
                    Rule.CyclomaticComplexityDisabled, "Complexity checking disabled"),

                // Test coverage rules
                [Rule.RequireTestCoverageMinimum50] = ActivatorUtilities.CreateInstance<TestCoverageValidator>(
                    serviceProvider, 50, Rule.RequireTestCoverageMinimum50),

                [Rule.RequireTestCoverageMinimum70] = ActivatorUtilities.CreateInstance<TestCoverageValidator>(
                    serviceProvider, 70, Rule.RequireTestCoverageMinimum70),

                [Rule.RequireTestCoverageMinimum80] = ActivatorUtilities.CreateInstance<TestCoverageValidator>(
                    serviceProvider, 80, Rule.RequireTestCoverageMinimum80),

                [Rule.RequireTestCoverageMinimum100] = ActivatorUtilities.CreateInstance<TestCoverageValidator>(
                    serviceProvider, 100, Rule.RequireTestCoverageMinimum100),

                [Rule.SkipTestCoverage] = new NoOpValidator(
                    Rule.SkipTestCoverage, "Test coverage checking skipped"),

                // Package change detection
                [Rule.PackageChangeDetected] = serviceProvider.GetRequiredService<PackageChangeValidator>()
            };
        }

        public async Task<bool> IsGoodCodeAsync(
            string filePath,
            RuleWithEnforcement rule,
            string sourceFile,
            string rootPath,
            CancellationToken cancellation)
        {
            // Parse the rule text to get the Rule enum
            if (!_rulesService.TryParse(rule.RuleText, out Rule parsedRule))
            {
                // Unknown rules pass by default
                return true;
            }

            // Get the validator for this rule
            if (!_validators.TryGetValue(parsedRule, out var validator))
            {
                // No validator found - pass by default
                return true;
            }

            // Execute validation
            var result = await validator.ValidateAsync(filePath, rule, sourceFile, rootPath, cancellation);
            return result.IsValid;
        }
    }
}
