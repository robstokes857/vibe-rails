using VibeRails.DTOs;

namespace VibeRails.Services.FileSystem;

/// <summary>
/// Enumerates one directory level for the authenticated local filesystem picker.
/// </summary>
public interface IFileSystemBrowserService
{
    /// <summary>
    /// Returns metadata for one local directory. A null or blank <paramref name="path"/> opens
    /// <paramref name="defaultPath"/>.
    /// </summary>
    FileSystemBrowseResponse Browse(
        string? path,
        string defaultPath,
        bool includeHidden,
        string? search = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default);
}
