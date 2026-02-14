using VibeRails.Services;

namespace VibeRails.Services.VCA.Validators
{
    /// <summary>
    /// Validates cyclomatic complexity of code files
    /// </summary>
    public class CyclomaticComplexityValidator : IRuleValidator
    {
        private readonly IFileReader _fileReader;
        private readonly IFileClassifier _fileClassifier;
        private readonly int _threshold;
        private readonly Rule _rule;

        public Rule SupportedRule => _rule;

        public CyclomaticComplexityValidator(
            IFileReader fileReader,
            IFileClassifier fileClassifier,
            int threshold,
            Rule rule)
        {
            _fileReader = fileReader;
            _fileClassifier = fileClassifier;
            _threshold = threshold;
            _rule = rule;
        }

        public async Task<RuleValidationResult> ValidateAsync(
            string filePath,
            RuleWithEnforcement rule,
            string sourceFile,
            string rootPath,
            ValidationContext? context = null,
            CancellationToken ct = default)
        {
            var fullPath = Path.Combine(rootPath, filePath);
            if (!await _fileReader.ExistsAsync(fullPath, ct))
                return new RuleValidationResult(true);

            if (!_fileClassifier.IsComplexityCheckable(filePath))
                return new RuleValidationResult(true);

            var complexity = await EstimateComplexity(fullPath, ct);
            if (complexity > _threshold)
            {
                return new RuleValidationResult(
                    false,
                    $"Estimated complexity {complexity} exceeds threshold {_threshold}");
            }

            return new RuleValidationResult(true);
        }

        private async Task<int> EstimateComplexity(string filePath, CancellationToken ct)
        {
            var content = await _fileReader.ReadAllTextAsync(filePath, ct);

            var decisionKeywords = new[] {
                " if ", " if(", " else ", " switch ", " case ",
                " for ", " for(", " foreach ", " foreach(",
                " while ", " while(", " do ", " catch ",
                "&&", "||", "?"
            };

            int complexity = 1;
            foreach (var keyword in decisionKeywords)
            {
                complexity += CountOccurrences(content, keyword);
            }

            return complexity;
        }

        private int CountOccurrences(string content, string keyword)
        {
            int count = 0;
            int index = 0;
            while ((index = content.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                count++;
                index += keyword.Length;
            }
            return count;
        }
    }
}
