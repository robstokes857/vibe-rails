using VibeRails.Services;

namespace VibeRails.Services.VCA.Validators
{
    /// <summary>
    /// Validates files that exceed a line count threshold
    /// </summary>
    public class FileChangesOverLinesValidator : IRuleValidator
    {
        private readonly IFileReader _fileReader;
        private readonly int _threshold;
        private readonly Rule _rule;

        public Rule SupportedRule => _rule;

        public FileChangesOverLinesValidator(
            IFileReader fileReader,
            int threshold,
            Rule rule)
        {
            _fileReader = fileReader;
            _threshold = threshold;
            _rule = rule;
        }

        public async Task<RuleValidationResult> ValidateAsync(
            string filePath,
            RuleWithEnforcement rule,
            string sourceFile,
            string rootPath,
            CancellationToken ct)
        {
            var fullPath = Path.Combine(rootPath, filePath);
            if (!await _fileReader.ExistsAsync(fullPath, ct))
                return new RuleValidationResult(true);

            var lineCount = await _fileReader.GetLineCountAsync(fullPath, ct);
            if (lineCount > _threshold)
            {
                return new RuleValidationResult(
                    false,
                    $"File has {lineCount} lines (threshold: {_threshold})");
            }

            return new RuleValidationResult(true);
        }
    }
}
