using System.Text.RegularExpressions;

namespace VibeRails.Services.Board;

/// <summary>A saved GitHub pull request or commit reference; no repository access is implied.</summary>
public sealed record BoardCodeLink(string Id, string Url, string Kind, string Label, DateTime CreatedUtc);

/// <summary>Validates inert GitHub references without fetching remote content.</summary>
public static partial class BoardCodeLinks
{
    /// <summary>Maximum number of saved GitHub references on one card.</summary>
    public const int MaxLinks = 50;

    /// <summary>Returns a canonical HTTPS URL and a label derived solely from its path.</summary>
    public static BoardCodeLink Parse(string? value)
    {
        var text = value?.Trim() ?? "";
        var match = text.Length <= 500 ? GitHubUrl().Match(text) : Match.Empty;
        // Browsers resolve dot path segments before navigation; neither names a repository.
        if (!match.Success || match.Groups[2].Value is "." or "..")
            throw new BoardValidationException("Enter a GitHub pull request or commit URL: https://github.com/owner/repo/pull/123 or /commit/<sha>.");
        var repo = match.Groups[1].Value.ToLowerInvariant() + "/" + match.Groups[2].Value.ToLowerInvariant();
        var kind = match.Groups[3].Value.ToLowerInvariant();
        var reference = match.Groups[4].Value.ToLowerInvariant();
        if (kind == "pull" && (!long.TryParse(reference, out var number) || number <= 0)
            || kind == "commit" && !Sha().IsMatch(reference))
            throw new BoardValidationException("The GitHub link must contain a positive pull request number or a 7–40 character commit SHA.");
        if (kind == "pull") reference = long.Parse(reference).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new("", $"https://github.com/{repo}/{kind}/{reference}", kind == "pull" ? "pull_request" : "commit",
            kind == "pull" ? $"{repo} PR #{reference}" : $"{repo} · {reference[..Math.Min(7, reference.Length)]}", default);
    }

    [GeneratedRegex(@"\Ahttps://github\.com/([a-z0-9](?:[a-z0-9-]*[a-z0-9])?)/([a-z0-9_.-]+)/(pull|commit)/([a-z0-9]+)/?(?:#[a-z0-9_.-]*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubUrl();
    [GeneratedRegex(@"\A[0-9a-f]{7,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha();
}
