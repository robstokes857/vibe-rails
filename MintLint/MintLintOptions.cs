using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Configuration for an analysis run.
/// </summary>
public sealed record MintLintOptions
{
    /// <summary>Default options: no extra excludes, warning at 10 and error at 20.</summary>
    public static MintLintOptions Default { get; } = new();

    /// <summary>
    /// Glob-style patterns (matched against each file's forward-slash relative path) for
    /// files to exclude in addition to the built-in directory excludes. <c>*</c> matches
    /// any run of characters and <c>?</c> matches a single character.
    /// </summary>
    public IReadOnlyList<string> ExtraExcludes { get; init; } = Array.Empty<string>();

    /// <summary>Cyclomatic complexity at or above which a file is classified "warning".</summary>
    public int WarningThreshold { get; init; } = 10;

    /// <summary>Cyclomatic complexity at or above which a file is classified "error".</summary>
    public int ErrorThreshold { get; init; } = 20;
}
