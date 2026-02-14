using System.Text.RegularExpressions;
using VibeRails.Services;

namespace VibeRails.Services.VCA.Validators
{
    /// <summary>
    /// Validator that checks commit messages for forbidden words
    /// </summary>
    public class CommitMessageWordValidator : IRuleValidator
    {
        public Rule SupportedRule => Rule.CheckCommitMessageForWords;

        public Task<RuleValidationResult> ValidateAsync(
            string filePath,
            RuleWithEnforcement rule,
            string sourceFile,
            string rootPath,
            ValidationContext? context = null,
            CancellationToken ct = default)
        {
            var words = ExtractWordsFromRuleText(rule.RuleText);

            if (words.Count == 0)
            {
                return Task.FromResult(new RuleValidationResult(
                    true,
                    "No words specified in rule"));
            }

            var commitMessage = context?.CommitMessage;
            if (string.IsNullOrEmpty(commitMessage))
            {
                return Task.FromResult(new RuleValidationResult(true));
            }

            var foundWords = FindForbiddenWords(commitMessage, words);

            if (foundWords.Count > 0)
            {
                return Task.FromResult(new RuleValidationResult(
                    false,
                    $"Commit message contains forbidden words: {string.Join(", ", foundWords)}"));
            }

            return Task.FromResult(new RuleValidationResult(true));
        }

        private List<string> ExtractWordsFromRuleText(string ruleText)
        {
            var match = Regex.Match(ruleText, @":\s*(.+)$");
            if (!match.Success)
            {
                return new List<string>();
            }

            return match.Groups[1].Value
                .Split(',')
                .Select(w => w.Trim())
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .ToList();
        }

        private List<string> FindForbiddenWords(string commitMessage, List<string> words)
        {
            var foundWords = new List<string>();

            foreach (var word in words)
            {
                var escapedWord = Regex.Escape(word);
                var pattern = $@"\b{escapedWord}\b";

                if (Regex.IsMatch(commitMessage, pattern, RegexOptions.IgnoreCase))
                {
                    foundWords.Add(word);
                }
            }

            return foundWords;
        }
    }
}
