using System.Security;
using System.Security.Cryptography;
using System.Text;
using VibeRails.DTOs;

namespace VibeRails.Services.FileSystem;

/// <summary>
/// Read-only, non-recursive local filesystem browser used by the reusable picker UI.
/// </summary>
public sealed class FileSystemBrowserService : IFileSystemBrowserService
{
    internal const int MaxEntries = 5_000;
    internal const int MaxSearchLength = 256;
    internal const int MaxCursorLength = 2_048;
    private const string DirectoryKind = "directory";
    private const string FileKind = "file";
    private const string CursorVersion = "1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly IComparer<FileSystemEntryResponse> EntryComparer =
        Comparer<FileSystemEntryResponse>.Create(CompareEntries);

    private static StringComparer HostPathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison HostPathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public FileSystemBrowseResponse Browse(
        string? path,
        string defaultPath,
        bool includeHidden,
        string? search = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hasExplicitPath = !string.IsNullOrWhiteSpace(path);
        string currentPath;
        string normalizedDefaultPath;
        if (!hasExplicitPath)
        {
            normalizedDefaultPath = NormalizeDirectory(defaultPath);
            currentPath = normalizedDefaultPath;
        }
        else
        {
            currentPath = NormalizeDirectory(path!);
            try
            {
                normalizedDefaultPath = NormalizeDirectory(defaultPath);
            }
            catch (FileSystemBrowseException)
            {
                // A deleted or inaccessible project root must not prevent recovery through the
                // address bar. The valid explicit location becomes Home for this response.
                normalizedDefaultPath = currentPath;
            }
        }
        var normalizedSearch = NormalizeSearch(search);
        var normalizedPageSize = NormalizePageSize(pageSize);
        var cursorScope = BuildCursorScope(currentPath, includeHidden, normalizedSearch);
        var after = DecodeCursor(cursor, cursorScope);
        var page = EnumerateEntries(
            currentPath,
            includeHidden,
            normalizedSearch,
            after,
            normalizedPageSize,
            cursorScope,
            cancellationToken);

        return new FileSystemBrowseResponse(
            normalizedDefaultPath,
            currentPath,
            GetLocationLabel(currentPath),
            GetParentPath(currentPath),
            BuildBreadcrumbs(currentPath),
            GetRoots(currentPath, cancellationToken),
            page.Entries,
            page.HasMore,
            page.NextCursor,
            page.TotalCount,
            normalizedSearch);
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new FileSystemBrowseException(
                FileSystemBrowseError.InvalidPath,
                "Path must be a fully qualified local directory.");
        }

        // A leading // is network-style on Unix as well as Windows. Reject it before
        // Path.GetFullPath can collapse it into an ordinary-looking local root.
        if (IsNetworkOrDevicePath(path))
        {
            throw new FileSystemBrowseException(
                FileSystemBrowseError.InvalidPath,
                "Network and device paths are not supported.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (SecurityException)
        {
            throw AccessDenied();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath();
        }

        if (IsNetworkOrDevicePath(fullPath)
            || (OperatingSystem.IsWindows() && IsUnsupportedWindowsDrive(fullPath)))
        {
            throw new FileSystemBrowseException(
                FileSystemBrowseError.InvalidPath,
                "Network and device paths are not supported.");
        }

        var attributes = ValidateNavigationPath(fullPath);

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new FileSystemBrowseException(
                FileSystemBrowseError.InvalidPath,
                "Path must refer to a directory.");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static EntryPage EnumerateEntries(
        string currentPath,
        bool includeHidden,
        string? search,
        EntryCursor? after,
        int pageSize,
        string cursorScope,
        CancellationToken cancellationToken)
    {
        var entries = new SortedSet<FileSystemEntryResponse>(EntryComparer);
        long totalCount = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
            MatchCasing = MatchCasing.PlatformDefault,
            MatchType = MatchType.Simple
        };

        try
        {
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(currentPath, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryCreateEntry(entryPath, includeHidden, out var entry))
                    continue;

                if (search is not null
                    && !entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                    continue;

                totalCount++;

                if (after is not null && CompareEntryToCursor(entry, after.Value) <= 0)
                    continue;

                entries.Add(entry);
                if (entries.Count > pageSize + 1)
                    entries.Remove(entries.Max!);
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw AccessDenied();
        }
        catch (SecurityException)
        {
            throw AccessDenied();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw NotFound();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath();
        }
        catch (IOException)
        {
            throw new FileSystemBrowseException(
                FileSystemBrowseError.InvalidPath,
                "Directory could not be read.");
        }

        var pageEntries = entries.ToList();
        var hasMore = pageEntries.Count > pageSize;
        if (hasMore)
            pageEntries.RemoveAt(pageEntries.Count - 1);

        for (var index = 0; index < pageEntries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pageEntries[index] = AddOptionalMetadata(pageEntries[index]);
        }

        var nextCursor = hasMore && pageEntries.Count > 0
            ? EncodeCursor(pageEntries[^1], cursorScope)
            : null;
        return new EntryPage(pageEntries, hasMore, nextCursor, totalCount);
    }

    private static bool TryCreateEntry(
        string entryPath,
        bool includeHidden,
        out FileSystemEntryResponse entry)
    {
        entry = null!;

        try
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entryPath));
            if (string.IsNullOrEmpty(name))
                return false;

            // File.GetAttributes intentionally stats a Unix symlink target to determine whether
            // it points at a directory. Read LinkTarget first so listing a local link never opens
            // (or blocks on) a potentially remote target.
            var isSymbolicLink = !OperatingSystem.IsWindows()
                && new FileInfo(entryPath).LinkTarget is not null;
            var attributes = isSymbolicLink
                ? FileAttributes.ReparsePoint
                : File.GetAttributes(entryPath);

            var isHidden = attributes.HasFlag(FileAttributes.Hidden)
                || attributes.HasFlag(FileAttributes.System)
                || (!OperatingSystem.IsWindows() && name.StartsWith(".", StringComparison.Ordinal));

            if (isHidden && !includeHidden)
                return false;

            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            isSymbolicLink |= attributes.HasFlag(FileAttributes.ReparsePoint);

            entry = new FileSystemEntryResponse(
                name,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(entryPath)),
                isDirectory ? DirectoryKind : FileKind,
                isHidden,
                isSymbolicLink,
                Size: null,
                LastModifiedUtc: null,
                Extension: isDirectory ? null : Path.GetExtension(name));
            return true;
        }
        catch (Exception ex) when (IsRecoverableEntryException(ex))
        {
            // Files can disappear or become inaccessible between enumeration and metadata lookup.
            // Skip that one entry rather than failing the entire picker view.
            return false;
        }
    }

    private static FileSystemEntryResponse AddOptionalMetadata(FileSystemEntryResponse entry)
    {
        // Link metadata can require opening its target. Keep browsing metadata-only and never
        // touch a potentially remote target merely because its link is listed.
        if (entry.IsSymbolicLink)
            return entry;

        long? size = null;
        DateTimeOffset? lastModifiedUtc = null;
        if (string.Equals(entry.Kind, FileKind, StringComparison.Ordinal))
        {
            try
            {
                size = new FileInfo(entry.Path).Length;
            }
            catch (Exception ex) when (IsRecoverableEntryException(ex))
            {
                // Optional size metadata can race with filesystem changes or be unavailable for
                // special files. The entry remains useful without it.
            }
        }

        try
        {
            lastModifiedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(entry.Path));
        }
        catch (Exception ex) when (IsRecoverableEntryException(ex))
        {
            // Optional presentation metadata must not make the whole directory unreadable.
        }

        return entry with
        {
            Size = size,
            LastModifiedUtc = lastModifiedUtc
        };
    }

    private static FileAttributes ValidateNavigationPath(string fullPath)
    {
        var rootPath = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(rootPath))
            throw InvalidPath();

        var currentPath = Path.GetFullPath(rootPath);
        var attributes = ReadNavigationAttributes(currentPath);
        RejectReparseNavigation(attributes);

        var relativePath = Path.GetRelativePath(rootPath, fullPath);
        if (relativePath == ".")
            return attributes;

        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            attributes = ReadNavigationAttributes(currentPath);
            RejectReparseNavigation(attributes);
        }

        return attributes;
    }

    private static FileAttributes ReadNavigationAttributes(string path)
    {
        try
        {
            // On Unix, GetAttributes stats a symlink target to infer Directory. Detect the link
            // through readlink-backed LinkTarget and reject it before that target access occurs.
            if (!OperatingSystem.IsWindows() && new FileInfo(path).LinkTarget is not null)
            {
                throw new FileSystemBrowseException(
                    FileSystemBrowseError.InvalidPath,
                    "Folders reached through symbolic links or junctions cannot be opened.");
            }

            return File.GetAttributes(path);
        }
        catch (FileSystemBrowseException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw AccessDenied();
        }
        catch (SecurityException)
        {
            throw AccessDenied();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw NotFound();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath();
        }
        catch (IOException)
        {
            throw new FileSystemBrowseException(
                FileSystemBrowseError.InvalidPath,
                "Directory could not be read.");
        }
    }

    private static void RejectReparseNavigation(FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new FileSystemBrowseException(
                FileSystemBrowseError.InvalidPath,
                "Folders reached through symbolic links or junctions cannot be opened.");
        }
    }

    private static string? NormalizeSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return null;

        var normalized = search.Trim();
        if (normalized.Length > MaxSearchLength)
        {
            throw new FileSystemBrowseException(
                FileSystemBrowseError.InvalidPath,
                $"Search must be {MaxSearchLength} characters or fewer.");
        }

        return normalized;
    }

    private static int NormalizePageSize(int? pageSize)
    {
        var normalized = pageSize ?? MaxEntries;
        if (normalized is < 1 or > MaxEntries)
        {
            throw new FileSystemBrowseException(
                FileSystemBrowseError.InvalidPath,
                $"Page size must be between 1 and {MaxEntries}.");
        }

        return normalized;
    }

    private static string BuildCursorScope(
        string currentPath,
        bool includeHidden,
        string? search)
    {
        var scope = $"{currentPath}\0{(includeHidden ? '1' : '0')}\0{search ?? string.Empty}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }

    private static string EncodeCursor(FileSystemEntryResponse entry, string cursorScope)
    {
        var value = $"{CursorVersion}\0{cursorScope}\0{entry.Kind}\0{entry.Name}";
        return ToBase64Url(Encoding.UTF8.GetBytes(value));
    }

    private static EntryCursor? DecodeCursor(string? cursor, string expectedScope)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;

        if (cursor.Length > MaxCursorLength)
            throw InvalidCursor();

        try
        {
            var value = StrictUtf8.GetString(FromBase64Url(cursor));
            var parts = value.Split('\0');
            if (parts.Length != 4
                || parts[0] != CursorVersion
                || !string.Equals(parts[1], expectedScope, StringComparison.Ordinal)
                || (parts[2] != DirectoryKind && parts[2] != FileKind)
                || string.IsNullOrEmpty(parts[3])
                || parts[3].IndexOf(Path.DirectorySeparatorChar) >= 0
                || parts[3].IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                throw InvalidCursor();
            }

            return new EntryCursor(parts[2], parts[3]);
        }
        catch (FileSystemBrowseException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            throw InvalidCursor();
        }
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var remainder = base64.Length % 4;
        if (remainder == 1)
            throw new FormatException("Invalid Base64URL length.");
        if (remainder > 0)
            base64 = base64.PadRight(base64.Length + (4 - remainder), '=');
        return Convert.FromBase64String(base64);
    }

    private static int CompareEntryToCursor(
        FileSystemEntryResponse entry,
        EntryCursor cursor)
    {
        var entryDirectory = string.Equals(entry.Kind, DirectoryKind, StringComparison.Ordinal);
        var cursorDirectory = string.Equals(cursor.Kind, DirectoryKind, StringComparison.Ordinal);
        if (entryDirectory != cursorDirectory)
            return entryDirectory ? -1 : 1;

        var comparison = HostPathComparer.Compare(entry.Name, cursor.Name);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(entry.Name, cursor.Name);
    }

    private static int CompareEntries(FileSystemEntryResponse left, FileSystemEntryResponse right)
    {
        var leftDirectory = string.Equals(left.Kind, DirectoryKind, StringComparison.Ordinal);
        var rightDirectory = string.Equals(right.Kind, DirectoryKind, StringComparison.Ordinal);
        if (leftDirectory != rightDirectory)
            return leftDirectory ? -1 : 1;

        var comparison = HostPathComparer.Compare(left.Name, right.Name);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }

    private static string? GetParentPath(string currentPath)
    {
        var rootPath = Path.GetPathRoot(currentPath);
        if (string.IsNullOrEmpty(rootPath) || PathsEqual(currentPath, rootPath))
            return null;

        var parentPath = Path.GetDirectoryName(currentPath);
        return string.IsNullOrEmpty(parentPath)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
    }

    private static List<FileSystemLocationResponse> BuildBreadcrumbs(string currentPath)
    {
        var rootPath = Path.GetPathRoot(currentPath)
            ?? throw new FileSystemBrowseException(
                FileSystemBrowseError.InvalidPath,
                "Path must be a fully qualified local directory.");
        rootPath = Path.GetFullPath(rootPath);

        var breadcrumbs = new List<FileSystemLocationResponse>
        {
            new(GetRootLabel(rootPath), Path.TrimEndingDirectorySeparator(rootPath))
        };

        var relativePath = Path.GetRelativePath(rootPath, currentPath);
        if (relativePath == ".")
            return breadcrumbs;

        var cursor = rootPath;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);
            breadcrumbs.Add(new FileSystemLocationResponse(
                segment,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(cursor))));
        }

        return breadcrumbs;
    }

    private static List<FileSystemLocationResponse> GetRoots(
        string currentPath,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        try
        {
            candidates.AddRange(Directory.GetLogicalDrives());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The current path's root is added below, so drive discovery is optional UI metadata.
        }

        var currentRoot = Path.GetPathRoot(currentPath);
        if (!string.IsNullOrEmpty(currentRoot))
            candidates.Add(currentRoot);

        var roots = new List<FileSystemLocationResponse>();
        var seen = new HashSet<string>(HostPathComparer);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                if (!seen.Add(rootPath)
                    || IsNetworkOrDevicePath(rootPath)
                    || (OperatingSystem.IsWindows() && IsUnsupportedWindowsDrive(rootPath))
                    || !Directory.Exists(rootPath))
                {
                    continue;
                }

                roots.Add(new FileSystemLocationResponse(GetRootLabel(rootPath), rootPath));
            }
            catch (Exception ex) when (IsRecoverableEntryException(ex) || ex is FileSystemBrowseException)
            {
                // Unready removable drives and inaccessible roots are omitted from the picker.
            }
        }

        roots.Sort((left, right) => HostPathComparer.Compare(left.Label, right.Label));
        return roots;
    }

    private static string GetLocationLabel(string path)
    {
        var rootPath = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(rootPath) && PathsEqual(path, rootPath))
            return GetRootLabel(rootPath);

        var label = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(label) ? path : label;
    }

    private static string GetRootLabel(string rootPath)
    {
        if (!OperatingSystem.IsWindows())
            return rootPath;

        var label = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(label) ? rootPath : label;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            HostPathComparison);

    private static bool IsNetworkOrDevicePath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsUnsupportedWindowsDrive(string path)
    {
        var rootPath = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(rootPath))
            return true;

        try
        {
            return new DriveInfo(rootPath).DriveType is
                DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory;
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            NotSupportedException)
        {
            return true;
        }
    }

    private static bool IsRecoverableEntryException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException;

    private static FileSystemBrowseException InvalidPath() =>
        new(FileSystemBrowseError.InvalidPath, "Path is invalid.");

    private static FileSystemBrowseException InvalidCursor() =>
        new(FileSystemBrowseError.InvalidPath, "Continuation cursor is invalid or no longer applies.");

    private static FileSystemBrowseException NotFound() =>
        new(FileSystemBrowseError.NotFound, "Directory was not found.");

    private static FileSystemBrowseException AccessDenied() =>
        new(FileSystemBrowseError.AccessDenied, "Access to this directory was denied.");

    private readonly record struct EntryCursor(string Kind, string Name);

    private sealed record EntryPage(
        List<FileSystemEntryResponse> Entries,
        bool HasMore,
        string? NextCursor,
        long TotalCount);
}

internal enum FileSystemBrowseError
{
    InvalidPath,
    NotFound,
    AccessDenied
}

internal sealed class FileSystemBrowseException(
    FileSystemBrowseError error,
    string message) : Exception(message)
{
    public FileSystemBrowseError Error { get; } = error;
}
