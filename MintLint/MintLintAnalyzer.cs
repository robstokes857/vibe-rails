using System;
using System.Collections.Generic;
using System.IO;

namespace MintLint;

/// <summary>
/// Entry point for computing source-code metrics. Supports C#, JavaScript/TypeScript,
/// Python, Go, Rust, C, Java, C++, PHP, Ruby, Bash, and PowerShell; the target language
/// is selected from each file's extension.
/// </summary>
public static class MintLintAnalyzer
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.Ordinal)
    {
        ".git", ".next", ".turbo", ".venv", ".vs", ".cache", "__pycache__", "bin", "build",
        "coverage", "dist", "node_modules", "obj", "out", "target", "venv"
    };

    /// <summary>
    /// Analyzes a single file or every supported source file beneath a directory.
    /// Well-known build/dependency directories (for example <c>node_modules</c> and
    /// <c>.git</c>) are skipped, as are files matched by <see cref="MintLintOptions.ExtraExcludes"/>.
    /// </summary>
    /// <param name="path">A file or directory path. Relative paths resolve against the current directory.</param>
    /// <param name="options">Analysis options; <see cref="MintLintOptions.Default"/> is used when null.</param>
    /// <returns>One <see cref="FileMetrics"/> per analyzed file, ordered by path.</returns>
    /// <exception cref="FileNotFoundException">The path does not exist.</exception>
    public static IReadOnlyList<FileMetrics> AnalyzePath(string path, MintLintOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= MintLintOptions.Default;

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The path to analyze was not found.", fullPath);
        }

        string baseDirectory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath;
        List<string> files = EnumerateSourceFiles(fullPath, baseDirectory, options);
        files.Sort(static (left, right) => ComparePaths(left, right));

        List<ParsedSource> parsedSources = [];
        foreach (string file in files)
        {
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

            ILanguageParser parser = GetParser(file);
            parsedSources.Add(parser.Parse(file, RelativeDisplayPath(file, baseDirectory), text));
        }

        return BuildMetrics(parsedSources, options);
    }

    /// <summary>
    /// Analyzes a path and scores the results in one call: the convenience entry point for
    /// the "where should I look?" scan. Equivalent to <see cref="AnalyzePath"/> followed by
    /// <see cref="MintLintScorer.Score(IReadOnlyList{FileMetrics}, ScoringProfile?, IScoreCalculator?)"/>.
    /// </summary>
    /// <param name="path">A file or directory path.</param>
    /// <param name="options">Analysis options; <see cref="MintLintOptions.Default"/> when null.</param>
    /// <param name="profile">Scoring profile (thresholds and weights); <see cref="ScoringProfile.Default"/> when null.</param>
    public static ScanResult ScanPath(string path, MintLintOptions? options = null, ScoringProfile? profile = null)
    {
        return MintLintScorer.Score(AnalyzePath(path, options), profile);
    }

    /// <summary>
    /// Returns whether MintLint can analyze a file based on its extension. The path does
    /// not need to exist because only its logical extension is inspected.
    /// </summary>
    public static bool SupportsFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            LanguageRegistry.TryGetParser(Path.GetExtension(path), out _);
    }

    /// <summary>
    /// Analyzes in-memory source files as one batch. Unsupported paths and paths matched
    /// by <see cref="MintLintOptions.ExtraExcludes"/> are skipped, just as they are during
    /// a directory scan. Supported results are ordered deterministically by their
    /// normalized forward-slash paths. Cross-file duplication is measured across the
    /// complete supported batch.
    /// </summary>
    /// <param name="sources">Logical source paths paired with their complete text content.</param>
    /// <param name="options">Analysis options; <see cref="MintLintOptions.Default"/> is used when null.</param>
    public static IReadOnlyList<FileMetrics> AnalyzeSources(
        IReadOnlyList<SourceInput> sources,
        MintLintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        options ??= MintLintOptions.Default;

        List<SourceInput> included = [];
        for (int i = 0; i < sources.Count; i++)
        {
            SourceInput source = sources[i] ?? throw new ArgumentException(
                "The source collection cannot contain null entries.", nameof(sources));
            string displayPath = NormalizeDisplayPath(source.Path);
            if (SupportsFile(displayPath) && !IsExcludedDisplayPath(displayPath, options))
            {
                included.Add(source);
            }
        }

        included.Sort(static (left, right) => CompareSourceInputs(left, right));

        List<ParsedSource> parsedSources = [];
        foreach (SourceInput source in included)
        {
            string displayPath = NormalizeDisplayPath(source.Path);
            parsedSources.Add(GetParser(displayPath).Parse(source.Path, displayPath, source.Content));
        }

        return BuildMetrics(parsedSources, options);
    }

    /// <summary>
    /// Analyzes and scores in-memory source files in one call. Equivalent to
    /// <see cref="AnalyzeSources"/> followed by
    /// <see cref="MintLintScorer.Score(IReadOnlyList{FileMetrics}, ScoringProfile?, IScoreCalculator?)"/>.
    /// </summary>
    public static ScanResult ScanSources(
        IReadOnlyList<SourceInput> sources,
        MintLintOptions? options = null,
        ScoringProfile? profile = null)
    {
        return MintLintScorer.Score(AnalyzeSources(sources, options), profile);
    }

    /// <summary>
    /// Analyzes a single source file. Cross-file duplication cannot be measured for one
    /// file, so <see cref="FileMetrics.DuplicationRatio"/> is always zero; use
    /// <see cref="AnalyzePath"/> when duplication matters.
    /// </summary>
    /// <param name="path">Path to a supported source file.</param>
    /// <param name="baseDirectory">Directory the reported path is made relative to; defaults to the file's directory.</param>
    /// <param name="options">Analysis options; <see cref="MintLintOptions.Default"/> is used when null.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="ArgumentException">The file extension is not supported.</exception>
    public static FileMetrics AnalyzeFile(string path, string? baseDirectory = null, MintLintOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= MintLintOptions.Default;

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The source file to analyze was not found.", fullPath);
        }

        if (!IsSupportedSource(fullPath))
        {
            throw new ArgumentException("The file extension is not supported by the analyzer.", nameof(path));
        }

        string effectiveBase = baseDirectory is null
            ? Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory()
            : Path.GetFullPath(baseDirectory);

        string text = File.ReadAllText(fullPath);
        ParsedSource parsed = GetParser(fullPath).Parse(fullPath, RelativeDisplayPath(fullPath, effectiveBase), text);
        return MetricEngine.BuildFileMetrics(parsed, 0.0, options);
    }

    /// <summary>
    /// Re-evaluates <see cref="FileMetrics.ComplexityLevel"/> for already-analyzed files
    /// against new thresholds, without re-parsing. The level reflects each file's most
    /// complex function (its highest cyclomatic complexity).
    /// </summary>
    /// <param name="files">Files previously produced by the analyzer.</param>
    /// <param name="warningThreshold">Cyclomatic complexity at or above which a file is "warning".</param>
    /// <param name="errorThreshold">Cyclomatic complexity at or above which a file is "error".</param>
    public static IReadOnlyList<FileMetrics> ClassifyFiles(
        IReadOnlyList<FileMetrics> files,
        int warningThreshold = 10,
        int errorThreshold = 20)
    {
        ArgumentNullException.ThrowIfNull(files);
        List<FileMetrics> classified = [];
        foreach (FileMetrics file in files)
        {
            classified.Add(MetricEngine.Reclassify(file, warningThreshold, errorThreshold));
        }

        return classified;
    }

    internal static List<string> EnumerateSourceFiles(string root, string baseDirectory, MintLintOptions options)
    {
        List<string> results = [];
        if (File.Exists(root))
        {
            if (IsSupportedSource(root) && !IsExcluded(root, baseDirectory, options))
            {
                results.Add(root);
            }

            return results;
        }

        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current);
            }
            catch (IOException)
            {
                directories = [];
            }
            catch (UnauthorizedAccessException)
            {
                directories = [];
            }

            foreach (string directory in directories)
            {
                string name = Path.GetFileName(directory);
                if (!ExcludedDirectoryNames.Contains(name))
                {
                    pending.Push(directory);
                }
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch (IOException)
            {
                files = [];
            }
            catch (UnauthorizedAccessException)
            {
                files = [];
            }

            foreach (string file in files)
            {
                if (IsSupportedSource(file) && !IsExcluded(file, baseDirectory, options))
                {
                    results.Add(file);
                }
            }
        }

        return results;
    }

    private static bool IsSupportedSource(string path)
    {
        return SupportsFile(path);
    }

    private static ILanguageParser GetParser(string path)
    {
        if (LanguageRegistry.TryGetParser(Path.GetExtension(path), out ILanguageParser parser))
        {
            return parser;
        }

        throw new ArgumentException("The file extension is not supported by the analyzer.", nameof(path));
    }

    private static bool IsExcluded(string path, string baseDirectory, MintLintOptions options)
    {
        string relative = RelativeDisplayPath(path, baseDirectory);
        return IsExcludedDisplayPath(relative, options);
    }

    private static bool IsExcludedDisplayPath(string displayPath, MintLintOptions options)
    {
        foreach (string pattern in options.ExtraExcludes)
        {
            string normalizedPattern = pattern.Replace('\\', '/');
            if (WildcardMatcher.IsMatch(displayPath, normalizedPattern))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<FileMetrics> BuildMetrics(
        IReadOnlyList<ParsedSource> parsedSources,
        MintLintOptions options)
    {
        double[] duplicationRatios = DuplicationAnalyzer.ComputeRatios(parsedSources);
        List<FileMetrics> results = [];
        for (int i = 0; i < parsedSources.Count; i++)
        {
            results.Add(MetricEngine.BuildFileMetrics(parsedSources[i], duplicationRatios[i], options));
        }

        return results;
    }

    private static int CompareSourceInputs(SourceInput left, SourceInput right)
    {
        string leftPath = NormalizeDisplayPath(left.Path);
        string rightPath = NormalizeDisplayPath(right.Path);
        int comparison = ComparePaths(leftPath, rightPath);
        return comparison != 0
            ? comparison
            : string.Compare(left.Content, right.Content, StringComparison.Ordinal);
    }

    private static int ComparePaths(string left, string right)
    {
        int comparison = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return comparison != 0
            ? comparison
            : string.Compare(left, right, StringComparison.Ordinal);
    }

    private static string NormalizeDisplayPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static string RelativeDisplayPath(string path, string baseDirectory)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(baseDirectory, path);
        }
        catch (ArgumentException)
        {
            relative = path;
        }

        return relative.Replace('\\', '/');
    }
}
