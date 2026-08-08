using System.Text;
using System.Text.RegularExpressions;

namespace VibeRails.Utils;

/// <summary>
/// Turns an environment into a workspace name.
///
/// The two namespaces this bridges do not accept the same characters. An environment name may
/// contain spaces (<see cref="EnvironmentNameValidator"/> allows letters, digits, spaces,
/// underscores and hyphens); a workspace name becomes both a directory under
/// <c>~/.vibe_rails/sandboxes/</c> and a <em>git branch</em>, and SandboxService's own validation
/// rejects spaces outright. So "one name" cannot mean "the same string" — it means a
/// deterministic slug, and this is the only place that mapping is defined.
///
/// Slugging is lossy, so the slug alone is NOT a safe identity: "Nightly Review" and
/// "Nightly-Review" produce the same text. Every name therefore carries the environment id, which
/// is what actually makes it unique — the workspace root is global (flat
/// <c>sandboxes/{name}</c>), so two environments colliding there would fight over one directory.
///
/// Determinism still matters for <see cref="ForEnvironment"/>: it is called again on every launch
/// to find the workspace a previous launch created. If it ever returned a different value for the
/// same environment, a persistent workspace would be re-cloned instead of reused. Per-run names
/// are the opposite — they must never repeat, which is why they take a caller-supplied unique
/// token rather than relying on a one-second timestamp.
/// </summary>
public static class WorkspaceNameSlug
{
    /// <summary>Mirrors SandboxService.MaxSandboxNameLength; both cap the same path segment.</summary>
    public const int MaxLength = 64;

    /// <summary>Characters in a generated run token. Hex keeps it git-ref and path safe.</summary>
    private const int RunTokenLength = 8;

    // "-e{id}" plus, for run names, "-{yyyyMMdd-HHmmss}-{token}". Budgeted so even a
    // maximum-length environment name and a large id still fit under MaxLength.
    private const int MaxIdSuffixLength = 12;
    private const int RunSuffixLength = 16 + 1 + RunTokenLength;

    // Matches the "-yyyyMMdd-HHmmss-xxxxxxxx" tail that ForRun appends.
    private static readonly Regex RunSuffixPattern = new(
        @"^-\d{8}-\d{6}-[0-9a-f]{" + RunTokenLength + @"}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A token that makes one run's workspace unique. Random rather than a counter because runs
    /// can be started concurrently by different processes with no shared sequence.
    /// </summary>
    public static string NewRunToken() => Guid.NewGuid().ToString("N")[..RunTokenLength];

    /// <summary>
    /// The workspace name for an environment: slugged display name plus the environment id.
    /// Stable across calls, so a persistent workspace is found again rather than re-cloned.
    /// </summary>
    public static string ForEnvironment(string? environmentName, int environmentId)
    {
        var stem = Slugify(environmentName, MaxLength - MaxIdSuffixLength);
        return $"{stem}{IdSuffix(environmentId)}";
    }

    /// <summary>
    /// The workspace name for a single run. The timestamp is there to be readable and sortable;
    /// <paramref name="runToken"/> is what guarantees uniqueness, since two runs can easily start
    /// inside the same second. Both are parameters rather than ambient state so run names are
    /// reproducible in tests.
    /// </summary>
    public static string ForRun(string? environmentName, int environmentId, DateTime timestampUtc, string runToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runToken);

        var stem = Slugify(environmentName, MaxLength - MaxIdSuffixLength - RunSuffixLength);
        return $"{stem}{IdSuffix(environmentId)}-{timestampUtc:yyyyMMdd-HHmmss}-{runToken}";
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a run workspace belonging to this environment.
    ///
    /// Retention uses this to decide what it may prune, so it has to be exact in both directions:
    /// a false positive would delete a persistent workspace or a hand-made sandbox, and a false
    /// negative would let old run clones accumulate forever. Matching on the id suffix means an
    /// environment can never claim another environment's workspaces even when their names slug
    /// to the same text.
    /// </summary>
    public static bool IsRunNameFor(string? environmentName, int environmentId, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;

        var prefix = $"{Slugify(environmentName, MaxLength - MaxIdSuffixLength - RunSuffixLength)}{IdSuffix(environmentId)}";
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return RunSuffixPattern.IsMatch(candidate[prefix.Length..]);
    }

    private static string IdSuffix(int environmentId) => $"-e{environmentId}";

    private static string Slugify(string? name, int maxLength)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var builder = new StringBuilder(trimmed.Length);

        foreach (var ch in trimmed)
        {
            // Whitelist rather than blacklist: this string becomes a directory name and a git
            // ref, so anything not provably safe in both is dropped instead of passed through.
            if (char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '-')
            {
                builder.Append(ch);
            }
            else if (ch == ' ')
            {
                // Collapse whitespace runs so "Nightly   Review" is not "Nightly---Review".
                if (builder.Length > 0 && builder[^1] != '-')
                    builder.Append('-');
            }
        }

        // Git rejects a ref ending in '.lock' and dislikes leading/trailing punctuation; the
        // charset above already blocks dots, so trimming separators is enough here.
        var slug = builder.ToString().Trim('-', '_');

        if (slug.Length > maxLength)
            slug = slug[..maxLength].TrimEnd('-', '_');

        // A name that slugs away to nothing still has to produce a usable directory. The id
        // suffix keeps it unique, so a fixed fallback word is safe here.
        if (slug.Length == 0)
            return "workspace";

        // SandboxService requires an alphanumeric or underscore first character.
        return char.IsAsciiLetterOrDigit(slug[0]) || slug[0] == '_' ? slug : $"w{slug}";
    }
}
