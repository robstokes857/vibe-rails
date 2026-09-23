using VibeRails.DTOs;
using VibeRails.Services.Git;

namespace VibeRails.Services.Board;

/// <summary>
/// Repo-wide file names behind the card composer's <c>@path</c> typeahead (VB-35). Names only,
/// never contents; nothing outside the project root; nothing stored. The text the user writes is
/// the reference, so this is a convenience index, not a link table.
/// </summary>
public interface IBoardFileIndexService
{
    /// <summary>
    /// Case-insensitive contains match over repo-relative paths (forward slashes). File-name hits
    /// rank before directory-only hits; an empty query returns the first files alphabetically.
    /// </summary>
    Task<BoardFileSearchResponse> SearchAsync(string projectPath, string? query, CancellationToken cancellationToken = default);
}

public sealed class BoardFileIndexService : IBoardFileIndexService
{
    public const int MaxQueryLength = 256;
    public const int MaxResults = 50;
    /// <summary>A non-git project could be anything; stop walking rather than index a home directory.</summary>
    public const int MaxIndexedFiles = 50_000;
    /// <summary>This repo lists 1,371 files in ~60 ms, so a short cache is plenty and a new file shows up within seconds.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);
    // ~16 MB of NUL-separated paths is far past MaxIndexedFiles; a listing that long is a sign
    // git is doing something else, and the walk below is the safer answer.
    private const int MaxGitOutputChars = 16_000_000;
    private static readonly string[] SkippedDirectories = [".git", "bin", "obj", "node_modules"];

    private sealed record CacheEntry(string Root, DateTimeOffset At, IReadOnlyList<string> Files);

    private readonly TimeProvider _time;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>?>> _gitLister;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private volatile CacheEntry? _cache;

    public BoardFileIndexService() : this(TimeProvider.System, ListViaGitAsync) { }

    /// <summary>Test seam: a fake clock for the cache and a lister that stands in for <c>git ls-files</c> (null result = fall back to the directory walk).</summary>
    internal BoardFileIndexService(TimeProvider time, Func<string, CancellationToken, Task<IReadOnlyList<string>?>>? gitLister = null)
    {
        _time = time;
        _gitLister = gitLister ?? ListViaGitAsync;
    }

    public async Task<BoardFileSearchResponse> SearchAsync(string projectPath, string? query, CancellationToken cancellationToken = default)
    {
        // Windows users type backslashes; the index only ever holds forward slashes.
        var search = (query ?? string.Empty).Trim().Replace('\\', '/');
        if (search.Length > MaxQueryLength)
            throw new BoardValidationException($"Search must be {MaxQueryLength} characters or fewer.");
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            return new BoardFileSearchResponse([], false);

        var files = await GetFilesAsync(projectPath, cancellationToken);
        if (search.Length == 0)
            return new BoardFileSearchResponse(files.Take(MaxResults).ToList(), files.Count > MaxResults);

        var byName = new List<string>();
        var byDirectory = new List<string>();
        foreach (var file in files)
        {
            var slash = file.LastIndexOf('/');
            var name = slash < 0 ? file : file[(slash + 1)..];
            if (name.Contains(search, StringComparison.OrdinalIgnoreCase))
                byName.Add(file);
            else if (file.Contains(search, StringComparison.OrdinalIgnoreCase))
                byDirectory.Add(file);
        }
        var matched = byName.Count + byDirectory.Count;
        return new BoardFileSearchResponse(byName.Concat(byDirectory).Take(MaxResults).ToList(), matched > MaxResults);
    }

    private async Task<IReadOnlyList<string>> GetFilesAsync(string root, CancellationToken cancellationToken)
    {
        if (TryGetFresh(root) is { } fresh)
            return fresh;

        // One refresh at a time: a burst of keystrokes right after the cache expires must not
        // spawn a git process per keystroke.
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFresh(root) is { } refreshed)
                return refreshed;
            var files = await _gitLister(root, cancellationToken) ?? ListViaDirectoryWalk(root, cancellationToken);
            var sorted = files.ToList();
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            _cache = new CacheEntry(root, _time.GetUtcNow(), sorted);
            return sorted;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private IReadOnlyList<string>? TryGetFresh(string root)
    {
        var entry = _cache;
        if (entry is null || !string.Equals(entry.Root, root, StringComparison.OrdinalIgnoreCase))
            return null;
        return _time.GetUtcNow() - entry.At < CacheLifetime ? entry.Files : null;
    }

    /// <summary>
    /// Tracked plus untracked-but-not-ignored files, exactly what the Rules page lists. Null when
    /// git is missing, this is not a repository, or the call timed out, so the caller can walk.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> ListViaGitAsync(string root, CancellationToken cancellationToken)
    {
        try
        {
            var result = await GitCli.RunAsync(root,
                ["ls-files", "--cached", "--others", "--exclude-standard", "-z"],
                cancellationToken, GitTimeout, MaxGitOutputChars);
            if (!result.Succeeded || result.StdOut.Length >= MaxGitOutputChars)
                return null;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var files = new List<string>();
            foreach (var relative in result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // --cached also lists files deleted from disk but still in the index.
                if (!File.Exists(Path.Combine(root, relative)))
                    continue;
                if (seen.Add(relative))
                    files.Add(relative);
            }
            return files;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Non-git projects: a plain walk that skips VCS metadata and build output, bounded so a stray root cannot index a disk.</summary>
    private static IReadOnlyList<string> ListViaDirectoryWalk(string root, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            // Reparse points are skipped so a junction cannot loop the walk or escape the root.
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };
        var pending = new Stack<string>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                foreach (var file in Directory.EnumerateFiles(directory, "*", options))
                {
                    if (files.Count >= MaxIndexedFiles)
                        return files;
                    files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
                foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                        pending.Push(child);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            // A directory that vanished mid-walk: what was listed so far is still useful.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return files;
    }
}
