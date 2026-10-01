namespace VibeRails.DTOs;

/// <summary>
/// Check actions persist a scope vector in Arguments: [working-tree], [unpushed], or
/// [range, base SHA, head SHA]. These values are configuration, never shell arguments.
/// A selected commit is represented by its parent and its own SHA; roots use repository scope.
/// </summary>
public sealed record JobCheckScope(string Kind, string? Base = null, string? Head = null)
{
    public static bool IsCheck(JobActionKind kind) => kind is JobActionKind.CodeQuality or JobActionKind.Vca;

    public static JobCheckScope Parse(IReadOnlyList<string>? arguments)
    {
        if (arguments is { Count: 1 } && arguments[0] is "working-tree" or "unpushed" or "repository")
            return new(arguments[0]);
        if (arguments is { Count: 3 } && arguments[0] == "range" && IsSha(arguments[1]) && IsSha(arguments[2]))
            return new("range", arguments[1].ToLowerInvariant(), arguments[2].ToLowerInvariant());
        throw new ArgumentException("Choose working-tree, unpushed, repository, or a range with full base and head commit SHAs. Linked commits do not define the scope.");
    }

    private static bool IsSha(string value) => value.Length == 40 && value.All(char.IsAsciiHexDigit);
}
