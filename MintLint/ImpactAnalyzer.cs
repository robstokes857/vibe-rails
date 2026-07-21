using System;
using System.Collections.Generic;
using System.IO;

namespace MintLint;

/// <summary>
/// Estimates how widely analyzed files are used across a codebase. For each target file it
/// counts the distinct <em>other</em> source files under the root that reference any of the
/// target's declared top-level identifiers (class and function names) as a whole word.
/// A hotspot referenced from forty files matters more than the same smell in dead code;
/// this count lets reports rank concern by reach.
/// </summary>
public static class ImpactAnalyzer
{
    private const int MaxScannedFiles = 5000;
    private const long MaxFileBytes = 1_500_000;
    private const int MinNameLength = 3;

    /// <summary>
    /// Counts, for each target, the distinct other source files that reference one of its
    /// declared names. Keys of the result are <see cref="FileMetrics.File"/> display paths.
    /// Synthetic scopes (<c>global</c>, <c>anonymous</c>) and names shorter than three
    /// characters are ignored because they match far too much unrelated code.
    /// </summary>
    /// <param name="rootDirectory">Directory to scan for referencing files (typically the repository root).</param>
    /// <param name="targets">Previously analyzed files whose reach should be measured.</param>
    /// <param name="candidateFiles">
    /// Root-relative paths of the files allowed to count as referencers (typically the
    /// repository's tracked files). Null scans every supported file under the root.
    /// </param>
    /// <param name="options">Analysis options; <see cref="MintLintOptions.Default"/> when null.</param>
    public static IReadOnlyDictionary<string, int> CountReferencingFiles(
        string rootDirectory,
        IReadOnlyList<FileMetrics> targets,
        IReadOnlyList<string>? candidateFiles = null,
        MintLintOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(targets);
        options ??= MintLintOptions.Default;

        var (counts, lookups) = BuildLookups(targets);

        if (lookups.Count == 0 || !Directory.Exists(rootDirectory))
        {
            return counts;
        }

        string fullRoot = Path.GetFullPath(rootDirectory);
        List<string> files = candidateFiles is null
            ? MintLintAnalyzer.EnumerateSourceFiles(fullRoot, fullRoot, options)
            : ResolveCandidateFiles(fullRoot, candidateFiles);
        int scanned = 0;
        foreach (string file in files)
        {
            if (++scanned > MaxScannedFiles)
            {
                break;
            }

            string relative;
            try
            {
                if (new FileInfo(file).Length > MaxFileBytes)
                {
                    continue;
                }

                relative = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            }
            catch (IOException)
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            CountSourceReferences(relative, text, lookups, counts);
        }

        return counts;
    }

    /// <summary>
    /// Counts references from an already captured source corpus. This is the immutable-source
    /// counterpart to <see cref="CountReferencingFiles"/>: callers such as a Git HEAD scan can
    /// rank impact without reading a newer index or working tree from disk.
    /// </summary>
    public static IReadOnlyDictionary<string, int> CountReferencingSources(
        IReadOnlyList<FileMetrics> targets,
        IReadOnlyList<SourceInput> sources)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(sources);

        var (counts, lookups) = BuildLookups(targets);
        if (lookups.Count == 0)
        {
            return counts;
        }

        HashSet<string> seen = new(PathComparer);
        int scanned = 0;
        foreach (SourceInput source in sources)
        {
            if (++scanned > MaxScannedFiles)
            {
                break;
            }

            string path = source.Path.Replace('\\', '/');
            if (!seen.Add(path) || !MintLintAnalyzer.SupportsFile(path))
            {
                continue;
            }

            CountSourceReferences(path, source.Content, lookups, counts);
        }

        return counts;
    }

    private static List<string> ResolveCandidateFiles(string fullRoot, IReadOnlyList<string> candidateFiles)
    {
        List<string> resolved = [];
        HashSet<string> seen = new(PathComparer);
        foreach (string candidate in candidateFiles)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !MintLintAnalyzer.SupportsFile(candidate))
            {
                continue;
            }

            try
            {
                string fullPath = Path.GetFullPath(Path.Combine(fullRoot, candidate));
                string relative = Path.GetRelativePath(fullRoot, fullPath);
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (seen.Add(fullPath))
                {
                    resolved.Add(fullPath);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Candidate paths are public input. Invalid paths cannot participate in analysis.
            }
        }

        return resolved;
    }

    private static HashSet<string> DeclaredNames(FileMetrics target)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (ClassMetrics type in target.Classes)
        {
            AddName(names, type.Name);
        }

        foreach (FunctionMetrics function in target.Functions)
        {
            AddName(names, function.Name);
        }

        return names;
    }

    private static (Dictionary<string, int> Counts, List<(string File, HashSet<string> Names)> Lookups)
        BuildLookups(IReadOnlyList<FileMetrics> targets)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        List<(string File, HashSet<string> Names)> lookups = [];
        foreach (FileMetrics target in targets)
        {
            counts[target.File] = 0;
            HashSet<string> names = DeclaredNames(target);
            if (names.Count > 0)
            {
                lookups.Add((target.File, names));
            }
        }

        return (counts, lookups);
    }

    private static void CountSourceReferences(
        string relativePath,
        string text,
        IReadOnlyList<(string File, HashSet<string> Names)> lookups,
        Dictionary<string, int> counts)
    {
        HashSet<string> identifiers = ExtractIdentifiers(text);
        foreach ((string targetFile, HashSet<string> names) in lookups)
        {
            if (PathsEqual(relativePath, targetFile))
            {
                continue;
            }

            foreach (string name in names)
            {
                if (identifiers.Contains(name))
                {
                    counts[targetFile]++;
                    break;
                }
            }
        }
    }

    private static void AddName(HashSet<string> names, string name)
    {
        if (name.Length >= MinNameLength && name != "global" && name != "anonymous")
        {
            names.Add(name);
        }
    }

    /// <summary>One pass over the text collecting every distinct identifier-shaped word.</summary>
    private static HashSet<string> ExtractIdentifiers(string text)
    {
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                if (i - start >= MinNameLength)
                {
                    identifiers.Add(text[start..i]);
                }
            }
            else
            {
                i++;
            }
        }

        return identifiers;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(left, right, PathComparison);
    }

    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
