using System.Text.RegularExpressions;

namespace VibeRails.Utils;

/// <summary>
/// Validates a user-supplied environment name before it becomes a path segment under
/// <c>~/.vibe_rails/envs/{name}</c> and the value of <c>CLAUDE_CONFIG_DIR</c> /
/// <c>CODEX_HOME</c> / <c>XDG_*_HOME</c> for spawned CLIs. A name containing
/// directory separators, dots, drive letters, etc. could redirect those env vars
/// outside the intended root and end up reading or writing files anywhere on disk.
/// </summary>
public static partial class EnvironmentNameValidator
{
    private const int MaxLength = 64;

    // Anchored: 1..MaxLength chars. First char must be alphanumeric — blocks a
    // leading '-' (which looks like a CLI flag if the name is ever emitted
    // unquoted) and a leading space. Remaining chars: letters/digits/space/
    // underscore/hyphen — no dot (blocks "."/".."), no separator (/ \), no colon
    // (drive letters / ADS), no quote / wildcard / control char.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\- ]{0,63}$")]
    private static partial Regex SafeNamePattern();

    // Windows reserved device names (case-insensitive): Directory.CreateDirectory
    // resolves these to a device rather than a folder, so envs/{name} would break.
    // The charset above forbids the trailing-extension forms ("CON.txt") by banning '.'.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Returns null when <paramref name="name"/> is safe to use as the envs/ path
    /// segment, otherwise a human-readable error describing the problem.
    /// </summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Name is required.";

        var trimmed = name.Trim();
        if (trimmed.Length > MaxLength)
            return $"Name must be {MaxLength} characters or fewer.";

        // Belt and suspenders: even if the regex below changes, never allow these.
        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains('\0')
            || trimmed.Contains("..", StringComparison.Ordinal))
        {
            return "Name must not contain path separators, '..', or control characters.";
        }

        if (!SafeNamePattern().IsMatch(trimmed))
        {
            return "Name must start with a letter or digit and may contain only letters, digits, spaces, underscores, and hyphens.";
        }

        if (ReservedDeviceNames.Contains(trimmed))
        {
            return $"'{trimmed}' is a reserved device name on Windows. Choose a different name.";
        }

        return null;
    }

    /// <summary>
    /// Resolves the on-disk directory for <paramref name="envName"/> under
    /// <paramref name="envBasePath"/> and guarantees the result stays inside that root.
    /// The launch paths (terminal/start, cli/launch, sandbox) take the name straight from
    /// the request body without calling <see cref="Validate"/>, so this is the last line of
    /// defense: a "../", absolute, or rooted name must not redirect CLAUDE_CONFIG_DIR /
    /// CODEX_HOME / XDG_*_HOME outside the envs root. Throws <see cref="ArgumentException"/>
    /// when the name would escape. Unlike <see cref="Validate"/> this only enforces
    /// containment (not the create-time charset), so it never rejects an already-created
    /// environment whose name predates the stricter rules.
    /// </summary>
    public static string ResolveEnvironmentDirectory(string envBasePath, string? envName)
    {
        if (string.IsNullOrEmpty(envName))
            throw new ArgumentException("Environment name is required.", nameof(envName));

        var baseFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(envBasePath));
        var combinedFull = Path.GetFullPath(Path.Combine(baseFull, envName));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Must be a strict subdirectory of the root (StartsWith base + separator), which
        // also rejects the root itself (e.g. "." or a trailing-dot name).
        if (!combinedFull.StartsWith(baseFull + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException(
                $"Environment name '{envName}' resolves outside the environments directory.", nameof(envName));
        }

        return combinedFull;
    }

    /// <summary>
    /// Returns true only when <paramref name="candidatePath"/> resolves to a location
    /// strictly inside <paramref name="envBasePath"/>. Unlike <see cref="ResolveEnvironmentDirectory"/>
    /// this takes an already-formed path (e.g. one stored on a DB row) rather than a name,
    /// so it can gate destructive filesystem operations — such as a recursive delete — on a
    /// path that may predate the create-time validation or have been hand-edited.
    /// </summary>
    public static bool IsWithinEnvironmentRoot(string envBasePath, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            return false;

        string baseFull;
        string candidateFull;
        try
        {
            baseFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(envBasePath));
            candidateFull = Path.GetFullPath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Strict subdirectory (base + separator) — also rejects the root itself.
        return candidateFull.StartsWith(baseFull + Path.DirectorySeparatorChar, comparison);
    }
}
