using System.Text.RegularExpressions;

namespace VibeRails.Services.VCA;

/// <summary>Shared syntax and matching for a plain comma-separated forbidden-word list.</summary>
public sealed record CommitMessageWordRule(IReadOnlyList<string> Words)
{
    public const string Template = "Check commit message for";
    public const string SyntaxHelp = "Use 'Check commit message for: wip, temporary, do not merge' with a nonempty comma-separated list. Do not use empty entries, quote wrappers, or control characters.";

    public static bool LooksLike(string? text) =>
        text?.TrimStart().StartsWith(Template, StringComparison.OrdinalIgnoreCase) == true;

    public static bool TryParse(string? text, out CommitMessageWordRule rule)
    {
        rule = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
            return false;

        var trimmed = text.Trim();
        if (!trimmed.StartsWith(Template + ":", StringComparison.OrdinalIgnoreCase)) return false;
        var words = trimmed[(Template.Length + 1)..].Split(',').Select(word => word.Trim()).ToList();
        if (words.Any(word => word.Length == 0 || word[0] is '\'' or '"' || word[^1] is '\'' or '"'))
            return false;

        rule = new CommitMessageWordRule(words.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        return true;
    }

    /// <summary>Matches literal words or phrases between non-word boundaries, including punctuation-bearing terms.</summary>
    public IReadOnlyList<string> FindMatches(string? message) => Words
        .Where(word => Regex.IsMatch(message ?? string.Empty, $@"(?<!\w){Regex.Escape(word)}(?!\w)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .ToList();
}
