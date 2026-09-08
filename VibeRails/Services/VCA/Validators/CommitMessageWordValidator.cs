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
            if (!CommitMessageWordRule.TryParse(rule.RuleText, out var wordRule))
            {
                return Task.FromResult(new RuleValidationResult(
                    false,
                    $"UNSUPPORTED: invalid commit-message word list. {CommitMessageWordRule.SyntaxHelp}"));
            }

            var commitMessage = context?.CommitMessage;
            if (commitMessage is null)
            {
                return Task.FromResult(new RuleValidationResult(true, "Deferred: checked against the final commit message by commit-msg."));
            }

            var foundWords = wordRule.FindMatches(commitMessage);

            if (foundWords.Count > 0)
            {
                return Task.FromResult(new RuleValidationResult(
                    false,
                    $"Commit message contains forbidden words: {string.Join(", ", foundWords)}"));
            }

            return Task.FromResult(new RuleValidationResult(true));
        }

    }
}
