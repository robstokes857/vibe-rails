using VibeRails.Services;

namespace VibeRails.Services.VCA.Validators
{
    /// <summary>
    /// Validates package file changes (package.json, *.csproj, etc.)
    /// </summary>
    public class PackageChangeValidator : IRuleValidator
    {
        private readonly IFileClassifier _fileClassifier;

        public Rule SupportedRule => Rule.PackageChangeDetected;

        public PackageChangeValidator(IFileClassifier fileClassifier)
        {
            _fileClassifier = fileClassifier;
        }

        public Task<RuleValidationResult> ValidateAsync(
            string filePath,
            RuleWithEnforcement rule,
            string sourceFile,
            string rootPath,
            CancellationToken ct)
        {
            if (_fileClassifier.IsPackageFile(filePath))
            {
                return Task.FromResult(new RuleValidationResult(
                    false,
                    $"Package file changed: {filePath}"));
            }

            return Task.FromResult(new RuleValidationResult(true));
        }
    }
}
