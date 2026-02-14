using VibeRails.Services;

namespace VibeRails.Services.VCA.Validators
{
    /// <summary>
    /// Validates that changed files are documented in AGENTS.md Files section
    /// </summary>
    public class LogAllFileChangesValidator : IRuleValidator
    {
        private readonly IAgentFileService _agentFileService;
        private readonly IPathNormalizer _pathNormalizer;

        public Rule SupportedRule => Rule.LogAllFileChanges;

        public LogAllFileChangesValidator(
            IAgentFileService agentFileService,
            IPathNormalizer pathNormalizer)
        {
            _agentFileService = agentFileService;
            _pathNormalizer = pathNormalizer;
        }

        public async Task<RuleValidationResult> ValidateAsync(
            string filePath,
            RuleWithEnforcement rule,
            string sourceFile,
            string rootPath,
            ValidationContext? context = null,
            CancellationToken ct = default)
        {
            // Get documented files from the AGENTS.md Files section
            var documentedFiles = await _agentFileService.GetDocumentedFilesAsync(sourceFile, ct);

            // Normalize paths for comparison
            var normalizedDocumented = documentedFiles
                .Select(f => _pathNormalizer.Normalize(f, rootPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var normalizedFilePath = _pathNormalizer.Normalize(filePath, rootPath);

            if (!normalizedDocumented.Contains(normalizedFilePath))
            {
                return new RuleValidationResult(
                    false,
                    $"File not documented in {Path.GetFileName(sourceFile)} Files section");
            }

            return new RuleValidationResult(true);
        }
    }
}
