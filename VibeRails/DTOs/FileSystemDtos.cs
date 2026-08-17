namespace VibeRails.DTOs;

/// <summary>
/// A named filesystem location used for breadcrumbs and local filesystem roots.
/// </summary>
public sealed record FileSystemLocationResponse(
    string Label,
    string Path);

/// <summary>
/// Metadata for one immediate child of the directory being browsed. The picker API never
/// returns file contents.
/// </summary>
public sealed record FileSystemEntryResponse(
    string Name,
    string Path,
    string Kind,
    bool IsHidden,
    bool IsSymbolicLink,
    long? Size,
    DateTimeOffset? LastModifiedUtc,
    string? Extension);

/// <summary>
/// One level of the authenticated local filesystem browser.
/// </summary>
public sealed record FileSystemBrowseResponse(
    string DefaultPath,
    string CurrentPath,
    string CurrentName,
    string? ParentPath,
    List<FileSystemLocationResponse> Breadcrumbs,
    List<FileSystemLocationResponse> Roots,
    List<FileSystemEntryResponse> Entries,
    bool Truncated,
    string? NextCursor = null,
    long? TotalCount = null,
    string? Search = null);
