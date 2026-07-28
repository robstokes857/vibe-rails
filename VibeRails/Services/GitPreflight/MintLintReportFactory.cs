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
        IReadOnlyDictionary<string, double>? baselineScoreByFile = null,
        IReadOnlyDictionary<string, IReadOnlyList<int>>? sourceLineNumbersByFile = null)
    {
        List<MintLintFileReportResponse> files = [];
        Dictionary<string, (MintLintWorstMetricResponse Entry, double Score, double Value)> worst =
            new(StringComparer.Ordinal);
        // Running totals per metric so the score card can describe the whole changeset
        // (averages), not just its single worst measurement.
        Dictionary<string, (double SumValue, double SumScore, int Count)> totals =
            new(StringComparer.Ordinal);

        foreach (var file in scan.Files)
        {
            List<MintLintCategoryResponse> categories = [];
            // A metric can belong to more than one category (hard-coded dependencies is
            // in both Coupling and Testability); count it once per file in the totals.
            HashSet<string> countedForFile = new(StringComparer.Ordinal);
            foreach (var category in file.Categories)
            {
                List<MintLintMetricResponse> metrics = [];
                foreach (var metric in category.Metrics)
                {
                    var sourceLine = MapSourceLine(
                        sourceLineNumbersByFile,
                        file.File,
                        metric.Line);
                    metrics.Add(new MintLintMetricResponse(
                        metric.Name,
                        metric.Value,
                        metric.Score,
                        metric.Warn,
                        metric.Critical,
                        metric.HigherIsBetter,
                        metric.Source,
                        sourceLine,
                        ExtractSnippet(contentByFile, file.File, sourceLine),
                        metric.Direction.ToString()));

                    if (countedForFile.Add(metric.Name))
                    {
                        totals[metric.Name] = totals.TryGetValue(metric.Name, out var runningTotal)
                            ? (runningTotal.SumValue + metric.Value, runningTotal.SumScore + metric.Score, runningTotal.Count + 1)
                            : (metric.Value, metric.Score, 1);
                    }

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
                            sourceLine,
                            ExtractSnippet(contentByFile, file.File, sourceLine),
                            metric.Direction.ToString()), metric.Score, metric.Value);
                    }
                }

                categories.Add(new MintLintCategoryResponse(
                    category.Name,
                    category.Score,
                    category.Weight,
                    category.WeightedScore,
                    metrics,
                    category.Direction.ToString()));
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
            worstMetrics,
            BuildOverview(scan),
            BuildScorecard(worst, totals));
    }

    /// <summary>
    /// The score card's fixed roster, in display order. Combined rows report the worst
    /// of their member metrics (hard-coded dependencies deliberately feeds both Coupling
    /// and Testability — it hurts both).
    /// </summary>
    private static readonly (string Label, string[] Metrics)[] ScorecardDefinitions =
    [
        ("Cyclomatic complexity", ["cyclomatic_complexity"]),
        ("Cognitive complexity", ["cognitive_complexity"]),
        ("NPath complexity", ["npath_complexity"]),
        ("Lack of cohesion (LCOM4)", ["lack_of_cohesion"]),
        ("Maintainability index", ["maintainability_index"]),
        ("Halstead difficulty", ["halstead_difficulty"]),
        ("Coupling", ["fan_out", "hard_coded_dependencies"]),
        ("Testability", ["hard_coded_dependencies", "ambient_dependencies"]),
    ];

    /// <summary>
    /// Every roster entry is always present: a metric the scan never measured comes back
    /// with <c>Measured = false</c> so the card can say "not measured" instead of
    /// silently dropping the row. Averages describe the changeset; the worst measurement
    /// rides along for context. A combined row uses the member with the higher AVERAGE
    /// risk across the scan.
    /// </summary>
    private static List<MintLintScorecardEntryResponse> BuildScorecard(
        IReadOnlyDictionary<string, (MintLintWorstMetricResponse Entry, double Score, double Value)> worstByMetric,
        IReadOnlyDictionary<string, (double SumValue, double SumScore, int Count)> totalsByMetric)
    {
        List<MintLintScorecardEntryResponse> scorecard = [];
        foreach (var (label, metricNames) in ScorecardDefinitions)
        {
            string? winnerName = null;
            (double SumValue, double SumScore, int Count) winnerTotals = default;
            foreach (var metricName in metricNames)
            {
                if (!totalsByMetric.TryGetValue(metricName, out var candidate) || candidate.Count == 0)
                {
                    continue;
                }

                var candidateMean = candidate.SumScore / candidate.Count;
                var winnerMean = winnerName is null ? double.MinValue : winnerTotals.SumScore / winnerTotals.Count;
                if (winnerName is null
                    || candidateMean > winnerMean
                    || (candidateMean == winnerMean && candidate.Count > winnerTotals.Count))
                {
                    winnerName = metricName;
                    winnerTotals = candidate;
                }
            }

            if (winnerName is null || !worstByMetric.TryGetValue(winnerName, out var worstEntry))
            {
                scorecard.Add(new MintLintScorecardEntryResponse(label, Measured: false));
                continue;
            }

            scorecard.Add(new MintLintScorecardEntryResponse(
                label,
                Measured: true,
                winnerTotals.Count,
                Math.Round(winnerTotals.SumValue / winnerTotals.Count, 3),
                Math.Round(winnerTotals.SumScore / winnerTotals.Count, 1),
                worstEntry.Entry.Value,
                worstEntry.Entry.Score,
                worstEntry.Entry.Direction,
                winnerName,
                worstEntry.Entry.File,
                worstEntry.Entry.Source,
                worstEntry.Entry.Line));
        }

        return scorecard;
    }

    /// <summary>
    /// The Overview strip, decided here rather than in the browser: one card per category
    /// seen in the scan, carrying its worst concern, which way its raw values read, and
    /// the single worst measurement behind that concern. Worst concern first.
    /// </summary>
    private static List<MintLintOverviewCardResponse> BuildOverview(ScanResult scan)
    {
        Dictionary<string, (CategoryScore Category, MetricScore? Worst, string File)> cards = new(StringComparer.Ordinal);
        // Per-category running totals: the card's headline is the AVERAGE risk across
        // the files that have the category (the changeset picture); the worst file's
        // number rides along as WorstConcern.
        Dictionary<string, (double Sum, int Count)> categoryTotals = new(StringComparer.Ordinal);
        foreach (var file in scan.Files)
        {
            foreach (var category in file.Categories)
            {
                categoryTotals[category.Name] = categoryTotals.TryGetValue(category.Name, out var runningTotal)
                    ? (runningTotal.Sum + category.Score, runningTotal.Count + 1)
                    : (category.Score, 1);

                MetricScore? worstMetric = null;
                foreach (var metric in category.Metrics)
                {
                    if (worstMetric is null
                        || metric.Score > worstMetric.Score
                        || (metric.Score == worstMetric.Score && WorseValue(metric, worstMetric.Value)))
                    {
                        worstMetric = metric;
                    }
                }

                if (!cards.TryGetValue(category.Name, out var current)
                    || category.Score > current.Category.Score)
                {
                    cards[category.Name] = (category, worstMetric, file.File);
                }
            }
        }

        List<MintLintOverviewCardResponse> overview = [];
        foreach (var (category, worstMetric, filePath) in cards.Values)
        {
            var (sum, count) = categoryTotals[category.Name];
            overview.Add(new MintLintOverviewCardResponse(
                category.Name,
                Math.Round(sum / count, 1),
                category.Direction.ToString(),
                category.Score,
                worstMetric?.Name,
                worstMetric?.Value,
                worstMetric?.Direction.ToString(),
                worstMetric is null ? null : filePath));
        }

        overview.Sort(static (left, right) =>
        {
            int byConcern = right.Concern.CompareTo(left.Concern);
            return byConcern != 0 ? byConcern : string.CompareOrdinal(left.Category, right.Category);
        });
        return overview;
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

    private static int? MapSourceLine(
        IReadOnlyDictionary<string, IReadOnlyList<int>>? sourceLineNumbersByFile,
        string file,
        int? fragmentLine)
    {
        if (fragmentLine is not > 0
            || sourceLineNumbersByFile is null
            || !sourceLineNumbersByFile.TryGetValue(file, out var sourceLines))
        {
            return fragmentLine;
        }

        var index = fragmentLine.Value - 1;
        return index >= 0 && index < sourceLines.Count
            ? sourceLines[index]
            : fragmentLine;
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
