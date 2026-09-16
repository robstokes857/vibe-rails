using VibeRails.DTOs;
using VibeRails.Services;
using TokenSaver;
using TokenSaver.Pipeline;

namespace VibeRails.DB;


/// <summary>
/// How an ignore entry matches a file path. Stored as a lowercase string in the
/// MatchKind column; <see cref="CodeAnalyzerIgnoreStore"/> accepts the constants
/// directly for ease of use.
/// </summary>
public static class CodeAnalyzerIgnoreMatchKind
{
    public const string File = "file";
    public const string Directory = "directory";
}

/// <summary>One file the user excluded from Code quality scans in one repository.</summary>
/// <param name="Path">Repository-relative path, forward slashes.</param>
/// <param name="MatchKind">"file" (exact match) or "directory" (the path itself plus everything under it).</param>
/// <param name="ReasonKind">"test" / "config" / "other", or null when no reason was given.</param>
/// <param name="ReasonText">Optional free-text note (used with "other", but stored for any kind).</param>
public sealed record CodeAnalyzerIgnoredFile(
    string Path,
    string MatchKind,
    string? ReasonKind,
    string? ReasonText,
    DateTime CreatedUtc)
{
    /// <summary>Convenience overload for the common file case (default MatchKind = file).</summary>
    public CodeAnalyzerIgnoredFile(string path, string? reasonKind, string? reasonText, DateTime createdUtc)
        : this(path, CodeAnalyzerIgnoreMatchKind.File, reasonKind, reasonText, createdUtc) { }
}

/// <summary>
/// Persists the Code quality ignore list to state.db, keyed per repository so one state.db can
/// serve many checkouts. Unlike the token-saver tally this store THROWS on failure: an ignore
/// the user believes was saved but wasn't would silently reintroduce files into results.
/// </summary>
public interface ICodeAnalyzerIgnoreStore
{
    Task<IReadOnlyList<CodeAnalyzerIgnoredFile>> ListAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>
    /// The full set of ignore rules for a repository (file + directory). Callers use
    /// <see cref="CodeAnalyzerIgnoreStore.IsIgnored"/> to test a path against the list.
    /// Replaces the older GetIgnoredPathsAsync, which only carried exact-match paths.
    /// </summary>
    Task<IReadOnlyList<CodeAnalyzerIgnoredFile>> GetIgnoreRulesAsync(string repositoryPath, CancellationToken cancellationToken);

    Task UpsertAsync(string repositoryPath, CodeAnalyzerIgnoredFile file, CancellationToken cancellationToken);

    /// <summary>Upserts many ignore rules in a single transaction (bulk endpoint).</summary>
    Task UpsertManyAsync(string repositoryPath, IReadOnlyList<CodeAnalyzerIgnoredFile> files, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(string repositoryPath, string path, CancellationToken cancellationToken);
}

