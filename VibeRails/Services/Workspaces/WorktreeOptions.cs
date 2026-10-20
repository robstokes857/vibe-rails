using System.Globalization;
using System.Text.RegularExpressions;

namespace VibeRails.Services.Workspaces;

/// <summary>Optional arguments of {{worktree}}. Seven days is both the default and minimum.</summary>
public sealed partial record WorktreeOptions(string? Path = null, int? DeleteAfterDays = 7)
{
    /// <summary>Parses quoted or unquoted optional values without shell expansion or escaping.</summary>
    public static WorktreeOptions Parse(string arguments)
    {
        string? path = null;
        int? days = 7;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset < arguments.Length)
        {
            if (string.IsNullOrWhiteSpace(arguments[offset..])) break;
            var match = ArgumentRegex().Match(arguments, offset);
            if (!match.Success || match.Index != offset)
                throw new ArgumentException("Invalid {{worktree}} options. Use path=\"...\" and delete_after_days=7 (or never).");
            var name = match.Groups[1].Value;
            var value = match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
            if (!seen.Add(name)) throw new ArgumentException($"Duplicate worktree option: {name}.");
            switch (name.ToLowerInvariant())
            {
                case "path":
                    if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("The worktree path cannot be empty.");
                    path = value;
                    break;
                case "delete_after_days":
                    if (value.Equals("never", StringComparison.OrdinalIgnoreCase)) days = null;
                    else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 7 and <= 36500) days = parsed;
                    else throw new ArgumentException("Worktree delete_after_days must be between 7 and 36500, or never.");
                    break;
                default: throw new ArgumentException($"Unknown worktree option: {name}.");
            }
            offset += match.Length;
        }
        return new WorktreeOptions(path, days);
    }

    // Backslashes are literal, so quoted Windows paths need no extra escaping.
    [GeneratedRegex("\\G\\s+([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s\"']+))(?=\\s|$)")]
    private static partial Regex ArgumentRegex();
}
