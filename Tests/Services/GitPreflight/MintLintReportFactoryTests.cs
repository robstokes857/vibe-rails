using MintLint;
using VibeRails.Services.GitPreflight;
using Xunit;

namespace Tests.Services.GitPreflight;

public sealed class MintLintReportFactoryTests
{
    private const string ConcerningSource = """
        public class Worker
        {
            public int Evaluate(int a, int b)
            {
                if (a > 0) { if (b > 0) { if (a > b) { if (a % 2 == 0) { if (b % 2 == 0) { return 1; } } } } }
                if (a < 0 && b < 0 || a == b && b != 1 || a == 2 && b == 3 || a == 4 && b == 5) { return 2; }
                if (a == 6) { return 3; }
                if (a == 7) { return 4; }
                if (a == 8) { return 5; }
                if (a == 9) { return 6; }
                if (a == 10) { return 7; }
                return 0;
            }
        }
        """;

    [Fact]
    public void ComputePriority_ScalesConcernByReach()
    {
        Assert.Equal(100.0, MintLintReportFactory.ComputePriority(100, 0));
        Assert.Equal(200.0, MintLintReportFactory.ComputePriority(100, 9));
        Assert.Equal(300.0, MintLintReportFactory.ComputePriority(100, 99));
    }

    [Fact]
    public void EffectiveConcern_CountsInheritedDebtHalf()
    {
        Assert.Equal(100.0, MintLintReportFactory.EffectiveConcern(100, null));       // new file: full blame
        Assert.Equal(100.0, MintLintReportFactory.EffectiveConcern(100, 0));          // clean before: full blame
        Assert.Equal(50.0, MintLintReportFactory.EffectiveConcern(100, 100));         // was already this bad: half
        Assert.Equal(60.0, MintLintReportFactory.EffectiveConcern(100, 80));          // 20 new + half of the old 80
        Assert.Equal(30.0, MintLintReportFactory.EffectiveConcern(60, 100));          // improved: only half the rest
    }

    [Fact]
    public void Build_DiscountsPreExistingConcernInPriority()
    {
        var scan = MintLintScorer.Score(MintLintAnalyzer.AnalyzeSources(
        [
            new SourceInput("src/inherited.cs", ConcerningSource),
            new SourceInput("src/fresh.cs", ConcerningSource.Replace("Worker", "FreshWorker"))
        ]));

        var report = MintLintReportFactory.Build(
            scan,
            skippedFileCount: 0,
            baselineScoreByFile: new Dictionary<string, double>
            {
                // inherited.cs was exactly this bad before the change; fresh.cs has no baseline.
                ["src/inherited.cs"] = scan.Files.First(file => file.File == "src/inherited.cs").Overall.Score
            });

        var fresh = report.Files.First(file => file.File == "src/fresh.cs");
        var inherited = report.Files.First(file => file.File == "src/inherited.cs");
        Assert.Null(fresh.BaselineScore);
        Assert.NotNull(inherited.BaselineScore);
        Assert.Equal(0.0, inherited.IntroducedScore);
        Assert.True(fresh.Priority > inherited.Priority);
        Assert.Equal("src/fresh.cs", report.Files[0].File);
    }

    [Fact]
    public void Build_RanksWidelyReferencedFilesAboveEquallyBadUnusedOnes()
    {
        var scan = MintLintScorer.Score(MintLintAnalyzer.AnalyzeSources(
        [
            new SourceInput("src/dead.cs", ConcerningSource),
            new SourceInput("src/hot.cs", ConcerningSource.Replace("Worker", "HotWorker"))
        ]));
        Assert.Equal(2, scan.Files.Count);
        Assert.Equal(scan.Files[0].Overall.Score, scan.Files[1].Overall.Score);

        var report = MintLintReportFactory.Build(
            scan,
            skippedFileCount: 0,
            contentByFile: null,
            referencedByFile: new Dictionary<string, int>
            {
                ["src/dead.cs"] = 0,
                ["src/hot.cs"] = 99
            });

        Assert.Equal("src/hot.cs", report.Files[0].File);
        Assert.Equal(99, report.Files[0].ReferencedByCount);
        Assert.True(report.Files[0].Priority > report.Files[1].Priority);
    }

    [Fact]
    public void Build_ExtractsSnippetForTheWorstOffender()
    {
        var scan = MintLintScorer.Score(MintLintAnalyzer.AnalyzeSources(
            [new SourceInput("src/worker.cs", ConcerningSource)]));

        var report = MintLintReportFactory.Build(
            scan,
            skippedFileCount: 0,
            contentByFile: new Dictionary<string, string> { ["src/worker.cs"] = ConcerningSource });

        Assert.NotNull(report.WorstMetrics);
        var cyclomatic = Assert.Single(report.WorstMetrics, metric => metric.Name == "cyclomatic_complexity");
        Assert.Equal("Evaluate", cyclomatic.Source);
        Assert.NotNull(cyclomatic.Line);
        Assert.Contains("Evaluate", cyclomatic.Snippet);
        var fileCyclomatic = Assert.Single(
            report.Files[0].Categories.SelectMany(category => category.Metrics),
            metric => metric.Name == "cyclomatic_complexity");
        Assert.Contains("Evaluate", fileCyclomatic.Snippet);
        // Worst offenders are sorted most concerning first.
        for (int i = 1; i < report.WorstMetrics.Count; i++)
        {
            Assert.True(report.WorstMetrics[i - 1].Score >= report.WorstMetrics[i].Score);
        }
    }
}
