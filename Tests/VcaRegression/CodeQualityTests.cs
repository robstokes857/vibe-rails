using Moq;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.VcaRegression;

public sealed class CodeQualityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task McpQualityValidationUsesItsInjectedSnapshotProvider()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("code-quality/minimum-c");
        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(repo.RepositoryPath, Ct);
        var provider = new Moq.Mock<IGitStagedSnapshotProvider>(Moq.MockBehavior.Strict);
        provider.Setup(p => p.CaptureAsync(repo.RepositoryPath, Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        var report = await RulesTool.ValidateVcaReportAsync(provider.Object, repo.RepositoryPath);
        var output = report.Output;

        Assert.Contains("Code quality minimum C", output);
        provider.Verify(p => p.CaptureAsync(repo.RepositoryPath, Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
    }

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
        var preview = await RulesTool.ValidateVcaReportAsync(new VibeRails.Services.GitPreflight.GitStagedSnapshotProvider(), stagedSnapshot: workingTree, workingTreeScope: true, cancellationToken: Ct);
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
    public async Task SmallCleanFilesCannotAverageABadFileUp()
    {
        // Twenty one-line files beside the 105-line Messy.cs. The overall grade is weighted by added
        // lines (VIBE-274); as a plain mean of file grades this commit passed.
        await using var repo = await VcaRegressionRepository.CreateAsync("code-quality/minimum-c");
        for (var i = 0; i < 20; i++)
            await System.IO.File.WriteAllTextAsync(Path.Combine(repo.RepositoryPath, $"Good{i}.cs"), $"public class Good{i} {{ public int Value => {i}; }}\n", Ct);
        await repo.GitAsync("add", ".");
        Expect.Blocked(await repo.RunPreCommitAndCrossCheckAsync(), "Code quality minimum C");
    }

    [Fact]
    public async Task AggregatePassesWhenCleanCodeOutweighsOneBadFile()
    {
        // The rule grades the change as a whole, never each file: twenty clean files of 27 lines
        // outweigh Messy.cs, so the commit passes while Messy.cs is still reported as an F.
        await using var repo = await VcaRegressionRepository.CreateAsync("code-quality/minimum-c");
        for (var i = 0; i < 20; i++)
        {
            var methods = string.Concat(Enumerable.Range(0, 6).Select(method =>
                $"    public int Step{i}x{method}(int value)\n    {{\n        return value + {i * 10 + method};\n    }}\n"));
            await System.IO.File.WriteAllTextAsync(Path.Combine(repo.RepositoryPath, $"Good{i}.cs"), $"public class Good{i}\n{{\n{methods}}}\n", Ct);
        }
        await repo.GitAsync("add", ".");
        Expect.Passed(await repo.RunPreCommitAndCrossCheckAsync(), 1);
    }

    [Fact]
    public async Task AFailedGradeTellsTheCommitterWhatToFix()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("code-quality/minimum-c");
        var report = await RulesTool.ValidateVcaReportAsync(new GitStagedSnapshotProvider(), repo.RepositoryPath);

        Assert.Contains("is below minimum C (70/100). Fix these first:", report.Output);
        Assert.Contains("F Messy.cs (health ", report.Output);
        Assert.Contains(" in Run, line 3 (critical at ", report.Output);
        Assert.Contains("Fix: ", report.Output);
    }
}
