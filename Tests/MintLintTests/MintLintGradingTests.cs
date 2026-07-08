using MintLint;
using Xunit;

namespace Tests.MintLintTests;

/// <summary>
/// End-to-end grading tests for the MintLint analyzer: feed it real C#, TypeScript,
/// JavaScript, and Python files and verify both the raw metrics and the interpreted
/// scores. Each language has a deliberately clean fixture (expected rating "Clean")
/// and a deliberately awful one (expected rating "AtRisk"). The messy fixtures share
/// one shape so the expected numbers are hand-checkable: a function with 16 `if`,
/// 5 `&amp;&amp;`, and 5 `||` (cyclomatic 27), an 8-parameter signature, and a 6-deep
/// nested block.
/// </summary>
public sealed class MintLintGradingTests : IDisposable
{
    private readonly string _root;

    public MintLintGradingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mintlint-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---------------------------------------------------------------- fixtures

    private const string CleanCSharp = """
        namespace MintLintFixtures
        {
            public static class TemperatureConverter
            {
                public static double ToFahrenheit(double celsius)
                {
                    return celsius * 1.8 + 32.0;
                }

                public static double ToCelsius(double value)
                {
                    return (value - 32.0) / 1.8;
                }
            }
        }
        """;

    private const string MessyCSharp = """
        using System;
        using System.IO;

        namespace MintLintFixtures
        {
            public class OrderProcessor
            {
                public string Evaluate(int a, int b, int c, int d, int e, int f, int g, int h)
                {
                    var total = 0;
                    if (a > 0 && b > 0) { total += 1; }
                    if (b > 1 || c > 1) { total += 2; }
                    if (c > 2 && d > 2) { total += 3; }
                    if (d > 3 || e > 3) { total += 4; }
                    if (e > 4 && f > 4) { total += 5; }
                    if (f > 5 || g > 5) { total += 6; }
                    if (g > 6 && h > 6) { total += 7; }
                    if (h > 7 || a > 7) { total += 8; }
                    if (a > 8 && c > 8) { total += 9; }
                    if (b > 9 || d > 9) { total += 10; }
                    if (total > 10)
                    {
                        if (total > 20)
                        {
                            if (total > 30)
                            {
                                if (total > 40)
                                {
                                    if (total > 50)
                                    {
                                        if (total > 60)
                                        {
                                            total += 100;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    return total.ToString();
                }

                public string Prepare()
                {
                    var repository = new OrderRepository();
                    var mailer = new SmtpMailer();
                    var audit = new AuditTrail();
                    Console.WriteLine(DateTime.Now);
                    var home = Environment.GetEnvironmentVariable("HOME");
                    var payload = File.ReadAllText("orders.txt");
                    return repository.Describe(mailer, audit, home, payload);
                }
            }
        }
        """;

    private const string CleanTypeScript = """
        export interface Labeled {
            label: string;
        }

        export function describe(item: Labeled): string {
            return "Item: " + item.label;
        }

        export function isBlank(text: string): boolean {
            return text.trim().length === 0;
        }
        """;

    private const string MessyTypeScript = """
        import { HttpClient } from "./http";
        import { AuditLog } from "./audit";

        export interface ScoreCard {
            total: number;
            label: string;
        }

        export class ScoreKeeper {
            private history: number[] = [];

            evaluate(a: number, b: number, c: number, d: number, e: number, f: number, g: number, h: number): string {
                let total = 0;
                if (a > 0 && b > 0) { total += 1; }
                if (b > 1 || c > 1) { total += 2; }
                if (c > 2 && d > 2) { total += 3; }
                if (d > 3 || e > 3) { total += 4; }
                if (e > 4 && f > 4) { total += 5; }
                if (f > 5 || g > 5) { total += 6; }
                if (g > 6 && h > 6) { total += 7; }
                if (h > 7 || a > 7) { total += 8; }
                if (a > 8 && c > 8) { total += 9; }
                if (b > 9 || d > 9) { total += 10; }
                if (total > 10) {
                    if (total > 20) {
                        if (total > 30) {
                            if (total > 40) {
                                if (total > 50) {
                                    if (total > 60) {
                                        total += 100;
                                    }
                                }
                            }
                        }
                    }
                }
                this.history.push(total);
                return String(total);
            }

            upload(): void {
                const client = new HttpClient();
                const audit = new AuditLog();
                const spooler = new PrintSpooler();
                console.log(Date.now());
                fetch("https://example.test/scores");
                localStorage.setItem("scores", "1");
            }
        }
        """;

    private const string CleanJavaScript = """
        export function formatName(person) {
            return person.first + " " + person.last;
        }

        export const initials = (person) => person.first.charAt(0) + person.last.charAt(0);
        """;

    private const string MessyJavaScript = """
        import { limits } from "./limits";

        export function evaluate(a, b, c, d, e, f, g, h) {
            let total = limits.floor;
            if (a > 0 && b > 0) { total += 1; }
            if (b > 1 || c > 1) { total += 2; }
            if (c > 2 && d > 2) { total += 3; }
            if (d > 3 || e > 3) { total += 4; }
            if (e > 4 && f > 4) { total += 5; }
            if (f > 5 || g > 5) { total += 6; }
            if (g > 6 && h > 6) { total += 7; }
            if (h > 7 || a > 7) { total += 8; }
            if (a > 8 && c > 8) { total += 9; }
            if (b > 9 || d > 9) { total += 10; }
            if (total > 10) {
                if (total > 20) {
                    if (total > 30) {
                        if (total > 40) {
                            if (total > 50) {
                                if (total > 60) {
                                    total += 100;
                                }
                            }
                        }
                    }
                }
            }
            return total;
        }

        export const submit = (payload) => {
            const client = new ApiClient();
            const policy = new RetryPolicy();
            const spooler = new PrintSpooler();
            console.log(Date.now());
            fetch("https://example.test/submit");
            setTimeout(function () { spooler.flush(payload); }, 1000);
            return client.send(policy, payload);
        };
        """;

    private const string CleanPython = """
        def add(left, right):
            return left + right


        def scale(value, factor):
            return value * factor
        """;

    private const string MessyPython = """
        import os


        def evaluate(a, b, c, d, e, f, g, h):
            total = 0
            if a > 0 and b > 0:
                total += 1
            if b > 1 or c > 1:
                total += 2
            if c > 2 and d > 2:
                total += 3
            if d > 3 or e > 3:
                total += 4
            if e > 4 and f > 4:
                total += 5
            if f > 5 or g > 5:
                total += 6
            if g > 6 and h > 6:
                total += 7
            if h > 7 or a > 7:
                total += 8
            if a > 8 and c > 8:
                total += 9
            if b > 9 or d > 9:
                total += 10
            if total > 10:
                if total > 20:
                    if total > 30:
                        if total > 40:
                            if total > 50:
                                if total > 60:
                                    total += 100
            return str(total)


        def report(total):
            print(total)
            home = os.getenv("HOME")
            with open("scores.txt") as handle:
                payload = handle.read()
            return home, payload
        """;

    // ------------------------------------------------------------------- C#

    [Fact]
    public void CSharpCleanFile_GradesClean()
    {
        string path = WriteFixture("clean.cs", CleanCSharp);

        FileMetrics metrics = MintLintAnalyzer.AnalyzeFile(path);

        Assert.Equal("ok", metrics.ComplexityLevel);
        Assert.Equal(1, FunctionNamed(metrics, "ToFahrenheit").Cyclomatic);
        Assert.Equal(1, FunctionNamed(metrics, "ToCelsius").Cyclomatic);
        Assert.Equal(0, metrics.HardCodedDependencies);
        Assert.Equal(0, metrics.AmbientDependencies);
        Assert.Equal(0, metrics.FanOut);
        ClassMetrics converter = Assert.Single(metrics.Classes);
        Assert.Equal("TemperatureConverter", converter.Name);
        Assert.Equal(2, converter.MethodCount);

        AssertGradedClean(MintLintScorer.Score(metrics));
    }

    [Fact]
    public void CSharpMessyFile_GradesAtRisk()
    {
        string path = WriteFixture("messy.cs", MessyCSharp);

        FileMetrics metrics = MintLintAnalyzer.AnalyzeFile(path);

        Assert.Equal(3, metrics.Functions.Count); // global + Evaluate + Prepare
        Assert.Equal(27, FunctionNamed(metrics, "Evaluate").Cyclomatic);
        Assert.Equal("error", metrics.ComplexityLevel);
        Assert.Equal(6, metrics.MaxNestingDepth);
        Assert.Equal(8, metrics.MaxParameterCount);
        Assert.Equal(3, metrics.HardCodedDependencies); // OrderRepository, SmtpMailer, AuditTrail
        Assert.Equal(4, metrics.AmbientDependencies);   // Console, DateTime, Environment, File
        Assert.Equal(2, metrics.FanOut);                // System, System.IO
        ClassMetrics processor = Assert.Single(metrics.Classes);
        Assert.Equal("OrderProcessor", processor.Name);
        Assert.Equal(2, processor.MethodCount);

        FileScore score = MintLintScorer.Score(metrics);
        AssertGradedAtRisk(score);
        Assert.Equal(27.0, score.Metrics["cyclomatic_complexity"]);
        Assert.Equal(8.0, score.Metrics["parameter_count"]);
        Assert.Equal(100.0, CategoryScoreOf(score, "Complexity"));
    }

    // ----------------------------------------------------------- TypeScript

    [Fact]
    public void TypeScriptCleanFile_GradesClean()
    {
        string path = WriteFixture("clean.ts", CleanTypeScript);

        FileMetrics metrics = MintLintAnalyzer.AnalyzeFile(path);

        // Return-type annotations must not hide the functions from the analyzer.
        Assert.Equal(1, FunctionNamed(metrics, "describe").Cyclomatic);
        Assert.Equal(1, FunctionNamed(metrics, "isBlank").Cyclomatic);
        Assert.Equal("ok", metrics.ComplexityLevel);
        Assert.Equal(1, metrics.MaxParameterCount);
        Assert.Empty(metrics.Classes); // the interface is not a class
        Assert.Equal(0, metrics.HardCodedDependencies);
        Assert.Equal(0, metrics.AmbientDependencies);

        AssertGradedClean(MintLintScorer.Score(metrics));
    }

    [Fact]
    public void TypeScriptMessyFile_GradesAtRisk()
    {
        string path = WriteFixture("messy.ts", MessyTypeScript);

        FileMetrics metrics = MintLintAnalyzer.AnalyzeFile(path);

        Assert.Equal(3, metrics.Functions.Count); // global + evaluate + upload
        Assert.Equal(27, FunctionNamed(metrics, "evaluate").Cyclomatic);
        Assert.Equal(1, FunctionNamed(metrics, "upload").Cyclomatic);
        Assert.Equal("error", metrics.ComplexityLevel);
        Assert.Equal(6, metrics.MaxNestingDepth);
        Assert.Equal(8, metrics.MaxParameterCount);
        Assert.Equal(3, metrics.HardCodedDependencies); // HttpClient, AuditLog, PrintSpooler
        Assert.Equal(4, metrics.AmbientDependencies);   // console, Date, fetch, localStorage — NOT this.history, which is a field, not the browser global
        Assert.Equal(2, metrics.FanOut);                // ./http, ./audit
        ClassMetrics keeper = Assert.Single(metrics.Classes);
        Assert.Equal("ScoreKeeper", keeper.Name);
        Assert.Equal(2, keeper.MethodCount);
        Assert.Equal(1, keeper.FieldCount); // history
        Assert.Equal(2, keeper.Lcom4);      // evaluate touches history, upload touches nothing

        FileScore score = MintLintScorer.Score(metrics);
        AssertGradedAtRisk(score);
        Assert.Equal(27.0, score.Metrics["cyclomatic_complexity"]);
    }

    // ----------------------------------------------------------- JavaScript

    [Fact]
    public void JavaScriptCleanFile_GradesClean()
    {
        string path = WriteFixture("clean.js", CleanJavaScript);

        FileMetrics metrics = MintLintAnalyzer.AnalyzeFile(path);

        Assert.Equal(1, FunctionNamed(metrics, "formatName").Cyclomatic);
        Assert.Equal(1, FunctionNamed(metrics, "initials").Cyclomatic); // arrow function
        Assert.Equal("ok", metrics.ComplexityLevel);
        Assert.Equal(0, metrics.HardCodedDependencies);
        Assert.Equal(0, metrics.AmbientDependencies);

        AssertGradedClean(MintLintScorer.Score(metrics));
    }

    [Fact]
    public void JavaScriptMessyFile_GradesAtRisk()
    {
        string path = WriteFixture("messy.js", MessyJavaScript);

        FileMetrics metrics = MintLintAnalyzer.AnalyzeFile(path);

        Assert.Equal(4, metrics.Functions.Count); // global + evaluate + submit + setTimeout callback
        Assert.Equal(27, FunctionNamed(metrics, "evaluate").Cyclomatic);
        Assert.Equal(1, FunctionNamed(metrics, "submit").Cyclomatic);
        Assert.Equal("error", metrics.ComplexityLevel);
        Assert.Equal(6, metrics.MaxNestingDepth);
        Assert.Equal(8, metrics.MaxParameterCount);
        Assert.Equal(3, metrics.HardCodedDependencies); // ApiClient, RetryPolicy, PrintSpooler
        Assert.Equal(4, metrics.AmbientDependencies);   // console, Date, fetch, setTimeout
        Assert.Equal(1, metrics.FanOut);                // ./limits

        AssertGradedAtRisk(MintLintScorer.Score(metrics));
    }

    // --------------------------------------------------------------- Python

    [Fact]
    public void PythonCleanFile_GradesClean()
    {
        string path = WriteFixture("clean.py", CleanPython);

        FileMetrics metrics = MintLintAnalyzer.AnalyzeFile(path);

        Assert.Equal(3, metrics.Functions.Count); // global + add + scale
        Assert.Equal(1, FunctionNamed(metrics, "add").Cyclomatic);
        Assert.Equal(1, FunctionNamed(metrics, "scale").Cyclomatic);
        Assert.Equal("ok", metrics.ComplexityLevel);
        Assert.Equal(2, metrics.MaxParameterCount);
        Assert.Equal(0, metrics.AmbientDependencies);
        Assert.Equal(0, metrics.FanOut);

        AssertGradedClean(MintLintScorer.Score(metrics));
    }

    [Fact]
    public void PythonMessyFile_GradesAtRisk()
    {
        string path = WriteFixture("messy.py", MessyPython);

        FileMetrics metrics = MintLintAnalyzer.AnalyzeFile(path);

        Assert.Equal(3, metrics.Functions.Count); // global + evaluate + report
        Assert.Equal(27, FunctionNamed(metrics, "evaluate").Cyclomatic);
        Assert.Equal("error", metrics.ComplexityLevel);
        Assert.Equal(6, metrics.MaxNestingDepth);
        Assert.Equal(8, metrics.MaxParameterCount);
        Assert.Equal(3, metrics.AmbientDependencies); // print, os, open
        Assert.Equal(1, metrics.FanOut);              // os

        AssertGradedAtRisk(MintLintScorer.Score(metrics));
    }

    // ------------------------------------------------- cross-file behavior

    [Fact]
    public void ScanPath_GradesAllFourLanguages_WorstFirst()
    {
        WriteFixture("clean.cs", CleanCSharp);
        WriteFixture("messy.cs", MessyCSharp);
        WriteFixture("clean.ts", CleanTypeScript);
        WriteFixture("messy.ts", MessyTypeScript);
        WriteFixture("clean.js", CleanJavaScript);
        WriteFixture("messy.js", MessyJavaScript);
        WriteFixture("clean.py", CleanPython);
        WriteFixture("messy.py", MessyPython);
        WriteFixture(Path.Combine("node_modules", "vendor.js"), MessyJavaScript);

        ScanResult result = MintLintAnalyzer.ScanPath(_root);

        Assert.Equal(8, result.Files.Count); // node_modules is skipped

        for (int i = 1; i < result.Files.Count; i++)
        {
            Assert.True(
                result.Files[i - 1].Overall.Score >= result.Files[i].Overall.Score,
                "Files must be ordered by descending concern.");
        }

        string[] worstFour = [.. result.Files.Take(4).Select(f => f.File).OrderBy(f => f, StringComparer.Ordinal)];
        Assert.Equal(new[] { "messy.cs", "messy.js", "messy.py", "messy.ts" }, worstFour);
        Assert.All(result.Files.Take(4), f => Assert.Equal("AtRisk", f.Overall.Rating));
        Assert.All(result.Files.Skip(4), f => Assert.Equal("Clean", f.Overall.Rating));

        double expectedMean = Math.Round(result.Files.Sum(f => f.Overall.Score) / result.Files.Count, 1);
        Assert.Equal(expectedMean, result.Overall.Score);
    }

    [Fact]
    public void AnalyzePath_FlagsCrossFileDuplication()
    {
        const string sharedMethod = """
                    public int Total(int[] values)
                    {
                        int total = 0;
                        for (int i = 0; i < values.Length; i++)
                        {
                            total += values[i];
                        }
                        return total;
                    }
            """;
        string alphaPath = WriteFixture(Path.Combine("dup", "alpha.cs"), $$"""
            namespace DupFixtures
            {
                public class AlphaReport
                {
            {{sharedMethod}}
                }
            }
            """);
        WriteFixture(Path.Combine("dup", "beta.cs"), $$"""
            namespace DupFixtures
            {
                public class BetaReport
                {
            {{sharedMethod}}

                    public int Twice(int seed)
                    {
                        return seed * 2;
                    }
                }
            }
            """);

        IReadOnlyList<FileMetrics> together = MintLintAnalyzer.AnalyzePath(Path.Combine(_root, "dup"));

        Assert.Equal(2, together.Count);
        Assert.All(together, m => Assert.True(
            m.DuplicationRatio > 0.1,
            $"{m.File} should be flagged as duplicated but had ratio {m.DuplicationRatio}."));

        // A single file has nothing to be duplicated against.
        Assert.Equal(0.0, MintLintAnalyzer.AnalyzeFile(alphaPath).DuplicationRatio);
    }

    [Fact]
    public void AnalyzeFile_UsesCustomComplexityThresholds()
    {
        string path = WriteFixture("messy.cs", MessyCSharp);

        FileMetrics relaxed = MintLintAnalyzer.AnalyzeFile(path, options: new MintLintOptions
        {
            WarningThreshold = 30,
            ErrorThreshold = 60
        });

        Assert.Equal("ok", relaxed.ComplexityLevel); // cyclomatic 27 < 30
    }

    [Fact]
    public void ClassifyFiles_ReclassifiesAgainstNewThresholds()
    {
        string path = WriteFixture("clean.cs", CleanCSharp);
        IReadOnlyList<FileMetrics> files = [MintLintAnalyzer.AnalyzeFile(path)];

        Assert.Equal("ok", files[0].ComplexityLevel);
        Assert.Equal("warning", MintLintAnalyzer.ClassifyFiles(files, warningThreshold: 1, errorThreshold: 50)[0].ComplexityLevel);
        Assert.Equal("error", MintLintAnalyzer.ClassifyFiles(files, warningThreshold: 1, errorThreshold: 1)[0].ComplexityLevel);
    }

    [Fact]
    public void AnalyzePath_HonorsExtraExcludes()
    {
        WriteFixture(Path.Combine("excludes", "clean.cs"), CleanCSharp);
        WriteFixture(Path.Combine("excludes", "messy.cs"), MessyCSharp);

        IReadOnlyList<FileMetrics> files = MintLintAnalyzer.AnalyzePath(
            Path.Combine(_root, "excludes"),
            new MintLintOptions { ExtraExcludes = ["messy*"] });

        FileMetrics only = Assert.Single(files);
        Assert.Equal("clean.cs", only.File);
    }

    [Fact]
    public void AnalyzeFile_RejectsUnsupportedAndMissingFiles()
    {
        string unsupported = WriteFixture("notes.txt", "just some notes");

        Assert.Throws<ArgumentException>(() => MintLintAnalyzer.AnalyzeFile(unsupported));
        Assert.Throws<FileNotFoundException>(() => MintLintAnalyzer.AnalyzeFile(Path.Combine(_root, "missing.cs")));
        Assert.Throws<FileNotFoundException>(() => MintLintAnalyzer.AnalyzePath(Path.Combine(_root, "missing-dir")));

        // A path that exists but is not a supported source file is simply not analyzed.
        Assert.Empty(MintLintAnalyzer.AnalyzePath(unsupported));
    }

    // ------------------------------------------------------- scorer behavior

    [Theory]
    [InlineData(4, 20.0, "Clean")]
    [InlineData(10, 50.0, "Okay")]
    [InlineData(13, 65.0, "NeedsWork")]
    [InlineData(20, 100.0, "AtRisk")]
    public void Scorer_MapsCyclomaticComplexityToRatingBands(int cyclomatic, double expectedScore, string expectedRating)
    {
        FileScore score = MintLintScorer.Score(MetricsWithCyclomatic(cyclomatic));

        Assert.Equal(expectedScore, score.Overall.Score);
        Assert.Equal(expectedRating, score.Overall.Rating);
    }

    [Fact]
    public void Scorer_EmptySet_ScoresZero()
    {
        ScanResult result = MintLintScorer.Score([]);

        Assert.Empty(result.Files);
        Assert.Equal(0.0, result.Overall.Score);
        Assert.Equal("Clean", result.Overall.Rating);
    }

    // --------------------------------------------------------------- helpers

    private string WriteFixture(string relativePath, string content)
    {
        string fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private static FunctionMetrics FunctionNamed(FileMetrics metrics, string name)
    {
        List<FunctionMetrics> matches = [.. metrics.Functions.Where(f => f.Name == name)];
        Assert.Single(matches);
        return matches[0];
    }

    private static double CategoryScoreOf(FileScore score, string category)
    {
        List<CategoryScore> matches = [.. score.Categories.Where(c => c.Name == category)];
        Assert.Single(matches);
        return matches[0].Score;
    }

    private static void AssertGradedClean(FileScore score)
    {
        Assert.Equal("Clean", score.Overall.Rating);
        Assert.True(score.Overall.Score < 30, $"Expected a clean score (< 30) but got {score.Overall.Score}.");
    }

    private static void AssertGradedAtRisk(FileScore score)
    {
        Assert.Equal("AtRisk", score.Overall.Rating);
        Assert.Equal(100.0, score.Overall.Score);
    }

    private static FileMetrics MetricsWithCyclomatic(int cyclomatic)
    {
        return new FileMetrics(
            File: "sample.cs",
            Loc: 10,
            Functions: [new FunctionMetrics("f", 1, 5, cyclomatic, 0, 1, 0.0, 0.0)],
            Classes: [],
            FanOut: 0,
            DuplicationRatio: 0.0,
            CyclomaticSum: cyclomatic,
            CognitiveSum: 0,
            NPathMax: 1,
            HalsteadVolume: 0.0,
            MaintainabilityIndex: 100.0,
            ComplexityLevel: "ok",
            MaxNestingDepth: 0,
            MaxParameterCount: 0,
            HardCodedDependencies: 0,
            AmbientDependencies: 0);
    }
}
