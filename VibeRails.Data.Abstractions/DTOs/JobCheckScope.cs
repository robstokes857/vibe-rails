namespace VibeRails.DTOs;

/// <summary>
/// Check actions persist a scope vector in Arguments. Values are configuration, never shell text.
/// Card scope selects linked commits; commit scope accepts one hexadecimal commit id.
/// </summary>
public sealed record JobCheckScope(string Kind, string? Base = null, string? Head = null)
{
    public static bool IsCheck(JobActionKind kind) => kind is JobActionKind.CodeQuality or JobActionKind.Vca;

    public static JobCheckScope Parse(IReadOnlyList<string>? arguments)
    {
        if (arguments is { Count: 1 } && arguments[0] is "card" or "working-tree" or "unpushed" or "repository")
            return new(arguments[0]);
        if (arguments is { Count: 2 } && arguments[0] == "commit" && IsCommitId(arguments[1]))
            return new("commit", Head: arguments[1].ToLowerInvariant());
        if (arguments is { Count: 3 } && arguments[0] == "range" && IsSha(arguments[1]) && IsSha(arguments[2]))
            return new("range", arguments[1].ToLowerInvariant(), arguments[2].ToLowerInvariant());
        throw new ArgumentException("Choose card, commit with a 7–40 character commit SHA, working-tree, unpushed, repository, or a range with full base and head commit SHAs.");
    }

    /// <summary>Whether a stored or supplied revision is a full hexadecimal commit SHA.</summary>
    public static bool IsSha(string? value) => value is { Length: 40 } && value.All(char.IsAsciiHexDigit);

    /// <summary>Whether a supplied commit id is hexadecimal and safe to resolve as a Git revision.</summary>
    public static bool IsCommitId(string? value) => value is { Length: >= 7 and <= 40 } && value.All(char.IsAsciiHexDigit);
}
