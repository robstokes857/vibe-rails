using System.Collections.Generic;
using MintLint;
using Xunit;

namespace Tests.MintLintTests;

/// <summary>
/// Pins the <see cref="IScoreCalculator"/> seam introduced by the 2026-08-20 refactor:
/// <see cref="MintLintScorer"/> must delegate ALL grading to the supplied calculator
/// (per-file and scan roll-up), and fall back to <see cref="ScoreCalculator.Instance"/>
/// when none is given. The numeric behavior of the default calculator itself is pinned
/// by MintLintGradingTests; these tests only guard the seam.
/// </summary>
public sealed class ScoreCalculatorSeamTests
{
    private sealed class StubCalculator : IScoreCalculator
    {
        public int FileCalls { get; private set; }

        public int ScanCalls { get; private set; }

        public FileScore ScoreFile(MeasuredFile file, ScoringProfile profile)
        {
            FileCalls++;
            return new FileScore(file.File, file.Metrics, [], new OverallScore(42.0, "Stub"));
        }

        public OverallScore ScoreScan(IReadOnlyList<FileScore> files)
        {
            ScanCalls++;
            return new OverallScore(41.0, "StubScan");
        }
    }

    private static FileMetrics EmptyMetrics(string file)
    {
        return new FileMetrics(
            file, 10, [], [], 0, 0, 0, 0, 0, 0, 100.0, "ok", 0, 0, 0, 0);
    }

    [Fact]
    public void Score_SingleFile_UsesSuppliedCalculator()
    {
        var stub = new StubCalculator();

        FileScore score = MintLintScorer.Score(EmptyMetrics("a.cs"), profile: null, calculator: stub);

        Assert.Equal(1, stub.FileCalls);
        Assert.Equal("Stub", score.Overall.Rating);
        Assert.Equal(42.0, score.Overall.Score);
    }

    [Fact]
    public void Score_Files_UsesSuppliedCalculatorForEveryFileAndTheScanRollUp()
    {
        var stub = new StubCalculator();

        ScanResult result = MintLintScorer.Score(
            [EmptyMetrics("a.cs"), EmptyMetrics("b.cs")], profile: null, calculator: stub);

        Assert.Equal(2, stub.FileCalls);
        Assert.Equal(1, stub.ScanCalls);
        Assert.Equal("StubScan", result.Overall.Rating);
        Assert.Equal(2, result.Files.Count);
    }

    [Fact]
    public void Score_WithoutCalculator_UsesTheDefaultInstance()
    {
        // No calculator supplied -> ScoreCalculator.Instance grades; an empty scan is Clean at 0.
        ScanResult result = MintLintScorer.Score([]);

        Assert.Equal(0, result.Overall.Score);
        Assert.Equal("Clean", result.Overall.Rating);
    }
}
