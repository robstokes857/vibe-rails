using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.VcaRegression;

public sealed class CodeQualityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("a", "WARN")]
    [InlineData("a", "COMMIT")]
    [InlineData("a", "STOP")]
    [InlineData("b", "WARN")]
    [InlineData("b", "COMMIT")]
    [InlineData("b", "STOP")]
    [InlineData("c", "WARN")]
    [InlineData("c", "COMMIT")]
    [InlineData("c", "STOP")]
    public Task MinimumGradeHonorsEnforcement(string grade, string level) =>
        EnforcementLevels.AssertPreCommitViolationHonorsLevelAsync(
            $"code-quality/minimum-{grade}", level, $"Code quality minimum {grade.ToUpperInvariant()}");

    [Fact]
    public async Task UnstagedFixCannotPassStagedGrade()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("code-quality/minimum-c");
        await System.IO.File.WriteAllTextAsync(Path.Combine(repo.RepositoryPath, "Messy.cs"), "public class Clean { }\n", Ct);
        Expect.Blocked(await repo.RunPreCommitAndCrossCheckAsync(), "Code quality minimum C");
        var workingTree = await new GitStagedSnapshotProvider().CaptureWorkingTreeAsync(repo.RepositoryPath, TestContext.Current.CancellationToken);
        var preview = await RulesTool.ValidateVcaReportAsync(stagedSnapshot: workingTree, workingTreeScope: true, cancellationToken: Ct);
        Assert.False(preview.HasStopViolation, preview.Output);
    }

    [Fact]
    public async Task ExistingBadCodeDoesNotFailCleanAddedCode()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("code-quality/minimum-c");
        await repo.GitAsync("commit", "--no-verify", "-m", "baseline");
        await System.IO.File.AppendAllTextAsync(Path.Combine(repo.RepositoryPath, "Messy.cs"), "\npublic class CleanAddition { }\n", Ct);
        await repo.GitAsync("add", "Messy.cs");
        Expect.Passed(await repo.RunPreCommitAndCrossCheckAsync(), 1);
    }

    [Fact]
    public async Task AggregatePassesEvenWhenOneFileFails()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("code-quality/minimum-c");
        for (var i = 0; i < 20; i++)
            await System.IO.File.WriteAllTextAsync(Path.Combine(repo.RepositoryPath, $"Good{i}.cs"), $"public class Good{i} {{ public int Value => {i}; }}\n", Ct);
        await repo.GitAsync("add", ".");
        Expect.Passed(await repo.RunPreCommitAndCrossCheckAsync(), 1);
    }
}
