namespace VibeRails.DTOs;

/// <summary>
/// Check actions persist a scope vector in Arguments. Values are configuration, never shell text.
/// Card scope selects linked commits; commit scope accepts one hexadecimal commit id.
/// A Code quality action may end with <c>minimum A|B|C</c>: below that overall grade the action
/// fails instead of only reporting (VIBE-278). Without it the check stays advisory.
/// </summary>
public sealed record JobCheckScope(string Kind, string? Base = null, string? Head = null, char? MinimumGrade = null)
{
    /// <summary>The argument that introduces a Code quality action's failing grade.</summary>
    public const string MinimumGradeArgument = "minimum";

    public static bool IsCheck(JobActionKind kind) => kind is JobActionKind.CodeQuality or JobActionKind.Vca;

    public static JobCheckScope Parse(IReadOnlyList<string>? arguments)
    {
        char? minimum = null;
        if (arguments is { Count: >= 3 } && arguments[^2] == MinimumGradeArgument)
        {
            if (arguments[^1] is not ("A" or "B" or "C"))
                throw new ArgumentException("The minimum Code quality grade must be A, B or C.");
            minimum = arguments[^1][0];
            arguments = arguments.Take(arguments.Count - 2).ToArray();
        }
        if (arguments is { Count: 1 } && arguments[0] is "card" or "working-tree" or "unpushed" or "repository")
            return new(arguments[0], MinimumGrade: minimum);
        if (arguments is { Count: 2 } && arguments[0] == "commit" && IsCommitId(arguments[1]))
            return new("commit", Head: arguments[1].ToLowerInvariant(), MinimumGrade: minimum);
        if (arguments is { Count: 3 } && arguments[0] == "range" && IsSha(arguments[1]) && IsSha(arguments[2]))
            return new("range", arguments[1].ToLowerInvariant(), arguments[2].ToLowerInvariant(), minimum);
        throw new ArgumentException("Choose card, commit with a 7–40 character commit SHA, working-tree, unpushed, repository, or a range with full base and head commit SHAs.");
    }

    /// <summary>Parse an action's vector; only Code quality may carry a minimum grade.</summary>
    public static JobCheckScope Parse(JobActionKind kind, IReadOnlyList<string>? arguments)
    {
        var scope = Parse(arguments);
        if (scope.MinimumGrade is not null && kind != JobActionKind.CodeQuality)
            throw new ArgumentException("Only Code quality checks have a minimum grade. VCA rules carry their own WARN, COMMIT and STOP levels.");
        return scope;
    }

    /// <summary>Whether a stored or supplied revision is a full hexadecimal commit SHA.</summary>
    public static bool IsSha(string? value) => value is { Length: 40 } && value.All(char.IsAsciiHexDigit);

    /// <summary>Whether a supplied commit id is hexadecimal and safe to resolve as a Git revision.</summary>
    public static bool IsCommitId(string? value) => value is { Length: >= 7 and <= 40 } && value.All(char.IsAsciiHexDigit);
}
