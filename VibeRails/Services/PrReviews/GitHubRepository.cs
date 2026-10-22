using System.Text.RegularExpressions;

namespace VibeRails.Services.PrReviews;

/// <summary>
/// A github.com repository, parsed from a project's <c>origin</c> remote. Owner and name are
/// validated against GitHub's own character rules because they are passed to <c>gh</c> and
/// rendered into links; nothing else about the remote URL (credentials included) is kept.
/// </summary>
public sealed partial record GitHubRepository(string Owner, string Name)
{
    private static readonly string[] GitHubHosts = ["github.com", "www.github.com", "ssh.github.com"];

    /// <summary><c>owner/name</c>, the key PR review records are stored under.</summary>
    public string FullName => $"{Owner}/{Name}";

    public string Url => $"https://github.com/{FullName}";

    public string PullRequestUrl(int number) => $"{Url}/pull/{number}";

    /// <summary>
    /// Accepts the remote forms git and GitHub produce: <c>https://github.com/o/r(.git)</c> (with or
    /// without userinfo), <c>git@github.com:o/r(.git)</c>, <c>ssh://git@github.com(:port)/o/r(.git)</c>
    /// and <c>git://github.com/o/r(.git)</c>. Any other host is not GitHub.
    /// </summary>
    public static bool TryParseRemote(string? remoteUrl, out GitHubRepository repository)
    {
        repository = null!;
        var remote = remoteUrl?.Trim();
        if (string.IsNullOrEmpty(remote)) return false;

        string host;
        string path;
        var scp = ScpRemote().Match(remote);
        if (scp.Success)
        {
            host = scp.Groups["host"].Value;
            path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http" or "ssh" or "git")
        {
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            return false;
        }

        if (!GitHubHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) return false;

        var segments = path.Trim('/').Split('/');
        if (segments.Length != 2) return false;
        var owner = segments[0];
        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (!IsValidOwner(owner) || !IsValidName(name)) return false;

        repository = new GitHubRepository(owner, name);
        return true;
    }

    /// <summary>GitHub user and organization names: letters, digits and single hyphens, at most 39.</summary>
    public static bool IsValidOwner(string owner) => OwnerPattern().IsMatch(owner);

    /// <summary>Repository names: letters, digits, '.', '-' and '_', at most 100, never "." or "..".</summary>
    public static bool IsValidName(string name) => name is not ("." or "..") && NamePattern().IsMatch(name);

    [GeneratedRegex(@"^(?:[^@/\s]+@)?(?<host>[A-Za-z0-9.-]+):(?<path>(?!/)[^\s]+)$")]
    private static partial Regex ScpRemote();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex NamePattern();
}
