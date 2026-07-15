using System.Text.Json;
using MintLint;
using VibeRails.DTOs;

namespace VibeRails.Services.GitPreflight;

/// <summary>
/// Maps a MintLint <see cref="ScanResult"/> onto the wire report consumed by the Rules
/// page and Git Guard: the worst offender per metric (with the code that caused it),
/// every measured metric per file, and files ranked by priority — concern score scaled
/// by how widely the file's declared names are referenced across the repository. The
/// same JSON travels through the preflight event stream (as the string
/// <c>details["report"]</c>) and the /api/v1/code-analyzer response.
/// </summary>
public static class MintLintReportFactory
{
    public const string DetailsKey = "report";

    private const int SnippetMaxLines = 8;
    private const int SnippetMaxLineLength = 160;

    public static MintLintReportResponse Build(
        ScanResult scan,
        int skippedFileCount,
        IReadOnlyDictionary<string, string>? contentByFile = null,
        IReadOnlyDictionary<string, int>? referencedByFile = null,
        IReadOnlyDictionary<string, double>? baselineScoreByFile = null)
    {
        List<MintLintFileReportResponse> files = [];
        Dictionary<string, (MintLintWorstMetricResponse Entry, double Score, double Value)> worst =
            new(StringComparer.Ordinal);

        foreach (var file in scan.Files)
        {
            List<MintLintCategoryResponse> categories = [];
            foreach (var category in file.Categories)
            {
                List<MintLintMetricResponse> metrics = [];
                foreach (var metric in category.Metrics)
                {
                    metrics.Add(new MintLintMetricResponse(
                        metric.Name,
                        metric.Value,
                        metric.Score,
                        metric.Warn,
                        metric.Critical,
                        metric.HigherIsBetter,
                        metric.Source,
                        metric.Line,
                        ExtractSnippet(contentByFile, file.File, metric.Line)));

                    // Track the single worst measurement of each metric across the scan.
                    if (!worst.TryGetValue(metric.Name, out var current)
                        || metric.Score > current.Score
                        || (metric.Score == current.Score && WorseValue(metric, current.Value)))
                    {
                        worst[metric.Name] = (new MintLintWorstMetricResponse(
                            metric.Name,
                            file.File,
                            metric.Value,
                            metric.Score,
                            metric.Warn,
                            metric.Critical,
                            metric.HigherIsBetter,
                            metric.Source,
                            metric.Line,
                            ExtractSnippet(contentByFile, file.File, metric.Line)), metric.Score, metric.Value);
                    }
                }

                categories.Add(new MintLintCategoryResponse(
                    category.Name,
                    category.Score,
                    category.Weight,
                    category.WeightedScore,
                    metrics));
            }

            int referencedBy = referencedByFile?.GetValueOrDefault(file.File) ?? 0;
            double? baseline = baselineScoreByFile?.TryGetValue(file.File, out var baselineScore) == true
                ? baselineScore
                : null;
            files.Add(new MintLintFileReportResponse(
                file.File,
                file.Overall.Score,
                file.Overall.Rating,
                categories,
                referencedBy,
                ComputePriority(EffectiveConcern(file.Overall.Score, baseline), referencedBy),
                baseline,
                baseline is null ? null : Math.Round(file.Overall.Score - baseline.Value, 1)));
        }

        // Highest-priority files first: bad code that the rest of the codebase leans on
        // outranks equally bad code nothing references.
        files.Sort(static (left, right) =>
        {
            int byPriority = right.Priority.CompareTo(left.Priority);
            if (byPriority != 0)
            {
                return byPriority;
            }

            int byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.File, right.File);
        });

        List<MintLintWorstMetricResponse> worstMetrics = [.. worst.Values.Select(static entry => entry.Entry)];
        worstMetrics.Sort(static (left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.Name, right.Name);
        });

        return new MintLintReportResponse(
            scan.Overall.Score,
            scan.Overall.Rating,
            scan.Files.Count,
            skippedFileCount,
            files,
            worstMetrics);
    }

    /// <summary>Concern scaled by reach: refs 0 → ×1, 9 → ×2, 99 → ×3.</summary>
    public static double ComputePriority(double score, int referencedByCount)
    {
        return Math.Round(score * (1 + Math.Log10(1 + Math.Max(0, referencedByCount))), 1);
    }

    /// <summary>
    /// How much of a file's concern this change is on the hook for. Concern the change
    /// introduced counts fully; concern the file already had before counts half — legacy
    /// debt stays visible but doesn't outrank fresh damage. New files carry full concern.
    /// </summary>
    public static double EffectiveConcern(double score, double? baselineScore)
    {
        if (baselineScore is not double baseline)
        {
            return score;
        }

        return Math.Max(score - baseline, 0) + (0.5 * Math.Min(score, baseline));
    }

    private static bool WorseValue(MetricScore metric, double currentValue)
    {
        return metric.HigherIsBetter ? metric.Value < currentValue : metric.Value > currentValue;
    }

    /// <summary>
    /// Pulls the first few lines of the offending declaration so the report can show the
    /// code, not just name it. Returns null for file-level metrics with no line.
    /// </summary>
    private static string? ExtractSnippet(
        IReadOnlyDictionary<string, string>? contentByFile,
        string file,
        int? line)
    {
        if (line is not > 0 || contentByFile is null || !contentByFile.TryGetValue(file, out var content))
        {
            return null;
        }

        string[] lines = content.Replace("\r\n", "\n").Split('\n');
        int start = line.Value - 1;
        if (start < 0 || start >= lines.Length)
        {
            return null;
        }

        int end = Math.Min(lines.Length, start + SnippetMaxLines);
        List<string> snippet = [];
        for (int i = start; i < end; i++)
        {
            string text = lines[i].TrimEnd();
            snippet.Add(text.Length > SnippetMaxLineLength ? text[..SnippetMaxLineLength] + "…" : text);
        }

        while (snippet.Count > 0 && snippet[^1].Length == 0)
        {
            snippet.RemoveAt(snippet.Count - 1);
        }

        if (snippet.Count == 0)
        {
            return null;
        }

        if (end < lines.Length)
        {
            snippet.Add("…");
        }

        return string.Join('\n', snippet);
    }

    public static string ToJson(MintLintReportResponse report)
    {
        return JsonSerializer.Serialize(report, AppJsonSerializerContext.Default.MintLintReportResponse);
    }

    public static MintLintReportResponse? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.MintLintReportResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
