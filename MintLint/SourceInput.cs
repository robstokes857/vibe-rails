using System;

namespace MintLint;

/// <summary>
/// An immutable source file supplied directly to MintLint without reading from disk.
/// This is useful for analyzing staged Git blobs whose content may differ from the
/// corresponding working-tree file.
/// </summary>
public sealed record SourceInput
{
    /// <summary>Creates an in-memory source file.</summary>
    /// <param name="path">Logical file path, including a supported extension.</param>
    /// <param name="content">Complete text content to analyze.</param>
    public SourceInput(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        Path = path;
        Content = content;
    }

    /// <summary>The logical path used for parser selection and result display.</summary>
    public string Path { get; }

    /// <summary>The complete in-memory source text.</summary>
    public string Content { get; }
}
