using System.Text.RegularExpressions;

namespace VibeRails.Services.VCA;

/// <summary>Recognizes public session replay links in commit messages without contacting the server.</summary>
public static partial class SessionShareCommitRule
{
    public const string Name = "Require VibeRails session link";
    public const string Trailer = "vibe-share:";
    public const string Guidance = "Call create_session_share_link with a displayName (or use Share session in the terminal tab), then add vibe-share:<returned URL> to the commit message. Put each session link on its own line. Links for active sessions are valid; replay becomes available after the session ends and uploads.";
    public const string DeferredMessage = "Deferred: checked against the final commit message by commit-msg. " + Guidance;
    public const string MissingMessage = "Commit message must include a VibeRails session sharing link. " + Guidance;

    /// <summary>Matches the catalog name, including hand-authored casing and surrounding whitespace.</summary>
    public static bool IsRule(string? text) => string.Equals(text?.Trim(), Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks dedicated trailers first, then URLs embedded in prose or Markdown.</summary>
    public static bool HasShareLink(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        using var lines = new StringReader(message);
        while (lines.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(Trailer, StringComparison.OrdinalIgnoreCase)
                && IsShareUrl(trimmed[Trailer.Length..].Trim())) return true;
        }

        foreach (Match match in UrlCandidates().Matches(message))
        {
            if (IsShareUrl(match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}'))) return true;
        }
        return false;
    }

    /// <summary>Accepts only the public URL shape returned by session sharing, never an API or lookalike host.</summary>
    public static bool IsShareUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.Equals(uri.Host, "viberails.ai", StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/shared/session"
            || !uri.Query.StartsWith("?key=", StringComparison.Ordinal)) return false;

        var key = uri.Query.AsSpan(5);
        if (key.Length != 64) return false;
        foreach (var character in key)
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return true;
    }

    [GeneratedRegex("https://[^\\s<>\"'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCandidates();
}
