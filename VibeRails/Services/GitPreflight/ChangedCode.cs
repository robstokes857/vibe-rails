using MintLint;

namespace VibeRails.Services.GitPreflight;

/// <summary>
/// Hands a captured file to MintLint's change analysis, for Git Guard's Code quality step, the
/// <c>Code quality minimum</c> rule and lane checks alike, so all three grade the same code the same way.
/// </summary>
internal static class ChangedCode
{
    /// <summary>
    /// The file as a change: its current text, the lines the change added with their line numbers,
    /// and the text before. A wholly new file is handed over as one text, so it is measured once.
    /// The caller has already checked that <see cref="GitStagedFileSnapshot.Content"/> is present.
    /// </summary>
    public static ChangedSource ToChange(GitStagedFileSnapshot file)
    {
        if (file.AddedContent is null || (file.PreviousContent is null && IsWholeFile(file.AddedLineNumbers)))
            return new(file.RelativePath, file.AddedContent ?? file.Content!);
        return new(file.RelativePath, file.Content!, file.AddedContent, file.AddedLineNumbers, file.PreviousContent);
    }

    /// <summary>True when the added lines are lines 1 to N in order: every line of a new file.</summary>
    private static bool IsWholeFile(IReadOnlyList<int>? addedLineNumbers)
    {
        if (addedLineNumbers is not { Count: > 0 } lines) return false;
        for (var index = 0; index < lines.Count; index++)
            if (lines[index] != index + 1) return false;
        return true;
    }
}
