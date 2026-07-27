using MintLint;
using Xunit;

namespace Tests.MintLintTests;

public sealed class MintLintSourceInputTests
{
    private const string SharedMethod = """
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

    public static TheoryData<string, string> SupportedLanguageSources => new()
    {
        { "sample.cs", "public static class Sample { public static int Add(int a, int b) { return a + b; } }" },
        { "sample.js", "export function add(a, b) { return a + b; }" },
        { "sample.ts", "export function add(a: number, b: number): number { return a + b; }" },
        { "sample.py", "def add(a, b):\n    return a + b\n" },
        { "sample.go", "package sample\nfunc add(a int, b int) int { return a + b }" },
        { "sample.rs", "fn add(a: i32, b: i32) -> i32 { a + b }" },
        { "sample.c", "int add(int a, int b) { return a + b; }" },
        { "sample.java", "class Sample { int add(int a, int b) { return a + b; } }" },
        { "sample.cpp", "class Sample { public: int add(int a, int b) { return a + b; } };" },
        { "sample.php", "<?php function add($a, $b) { return $a + $b; }" },
        { "sample.rb", "def add(a, b)\n  a + b\nend\n" },
        { "sample.sh", "add() { echo $(( $1 + $2 )); }" },
        { "sample.ps1", "function Add-Numbers([int]$A, [int]$B) { return $A + $B }" },
    };

    [Theory]
    [MemberData(nameof(SupportedLanguageSources))]
    public void AnalyzeSources_ParsesEverySupportedLanguageFromMemory(string path, string content)
    {
        FileMetrics result = Assert.Single(MintLintAnalyzer.AnalyzeSources([new SourceInput(path, content)]));

        Assert.Equal(path, result.File);
        Assert.True(result.Loc > 0);
        Assert.NotEmpty(result.Functions);
    }

    [Theory]
    [InlineData("source.cs")]
    [InlineData("source.JSX")]
    [InlineData("source.mts")]
    [InlineData("source.py")]
    [InlineData("source.go")]
    [InlineData("source.rs")]
    [InlineData("source.c")]
    [InlineData("source.java")]
    [InlineData("source.c++")]
    [InlineData("source.hpp")]
    [InlineData("source.cppm")]
    [InlineData("source.php")]
    [InlineData("source.PHTML")]
    [InlineData("source.rb")]
    [InlineData("source.gemspec")]
    [InlineData("source.SH")]
    [InlineData("source.bash")]
    [InlineData("source.ps1")]
    [InlineData("source.PSM1")]
    public void SupportsFile_RecognizesSupportedExtensions(string path)
    {
        Assert.True(MintLintAnalyzer.SupportsFile(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("README.md")]
    [InlineData("source.cs.txt")]
    public void SupportsFile_RejectsMissingOrUnsupportedExtensions(string? path)
    {
        Assert.False(MintLintAnalyzer.SupportsFile(path));
    }

    [Fact]
    public void AnalyzeSources_SkipsUnsupportedAndExcludedInputs()
    {
        IReadOnlyList<FileMetrics> results = MintLintAnalyzer.AnalyzeSources(
            [
                new SourceInput("notes.md", "# not source"),
                new SourceInput("generated/skip.cs", "class Skip { }"),
                new SourceInput("src/keep.cs", "class Keep { }")
            ],
            new MintLintOptions { ExtraExcludes = ["generated/*"] });

        FileMetrics result = Assert.Single(results);
        Assert.Equal("src/keep.cs", result.File);
    }

    [Fact]
    public void AnalyzeSources_NormalizesAndDeterministicallyOrdersLogicalPaths()
    {
        IReadOnlyList<FileMetrics> results = MintLintAnalyzer.AnalyzeSources(
            [
                new SourceInput("zeta.cs", "class Zeta { }"),
                new SourceInput("./Alpha.cs", "class UpperAlpha { }"),
                new SourceInput("src\\beta.cs", "class Beta { }")
            ]);

        Assert.Equal(["Alpha.cs", "src/beta.cs", "zeta.cs"], results.Select(result => result.File));
    }

    [Fact]
    public void AnalyzeSources_ComputesDuplicationAcrossInMemoryFiles()
    {
        IReadOnlyList<FileMetrics> results = MintLintAnalyzer.AnalyzeSources(
            [
                new SourceInput("beta.cs", "class Beta { " + SharedMethod + " }"),
                new SourceInput("alpha.cs", "class Alpha { " + SharedMethod + " }")
            ]);

        Assert.Equal(["alpha.cs", "beta.cs"], results.Select(result => result.File));
        Assert.All(results, result => Assert.True(result.DuplicationRatio > 0.1));
    }

    [Fact]
    public void AnalyzeSources_AppliesComplexityOptions()
    {
        const string source = "class Sample { int Choose(bool first, bool second) { if (first) return 1; if (second) return 2; return 0; } }";

        FileMetrics result = Assert.Single(MintLintAnalyzer.AnalyzeSources(
            [new SourceInput("sample.cs", source)],
            new MintLintOptions { WarningThreshold = 2, ErrorThreshold = 3 }));

        Assert.Equal("error", result.ComplexityLevel);
    }

    [Fact]
    public void ScanSources_AppliesProfileAndOrdersEqualScoresByPath()
    {
        ScoringProfile profile = ScoringProfile.Default with
        {
            Thresholds = new Dictionary<string, MetricThreshold>(ScoringProfile.Default.Thresholds)
            {
                ["cyclomatic_complexity"] = new(1, 2)
            }
        };

        ScanResult result = MintLintAnalyzer.ScanSources(
            [
                new SourceInput("zeta.cs", "class Zeta { int Pick(bool value) { if (value) return 1; return 0; } }"),
                new SourceInput("alpha.cs", "class Alpha { int Pick(bool value) { if (value) return 1; return 0; } }")
            ],
            profile: profile);

        Assert.Equal(["alpha.cs", "zeta.cs"], result.Files.Select(file => file.File));
        // The tightened profile saturates Complexity, but only that one category — the
        // breadth-gated overall lands on the depth floor (0.3 × 100 = 30, "Okay").
        Assert.All(result.Files, file => Assert.Equal("Okay", file.Overall.Rating));
        Assert.All(result.Files, file => Assert.Equal(30.0, file.Overall.Score));
    }

    [Fact]
    public void SourceInput_ValidatesRequiredValues()
    {
        Assert.Throws<ArgumentException>(() => new SourceInput(" ", "content"));
        Assert.Throws<ArgumentNullException>(() => new SourceInput("source.cs", null!));
    }
}
