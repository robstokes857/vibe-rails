using MintLint;
using Moq;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using Xunit;

namespace Tests.Services.VCA;

public sealed class CodeQualityRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-snapshot"));
    private static readonly string Messy = "public class Messy { public int Run(int a, int b) { "
        + string.Join("\n", Enumerable.Range(0, 8).Select(i => $"var dependency{i} = new Repository();"))
        + string.Join("\n", Enumerable.Range(0, 90).Select(i => $"if (a == {i} && b > 0) {{ a += b; }}"))
        + " return a; } }";
    private const string Clean = "public class Clean { public int Value => 1; }";

    [Theory]
    [InlineData('A', 10, true, "A")]
    [InlineData('A', 10.01, false, "B")]
    [InlineData('B', 20, true, "B")]
    [InlineData('B', 20.01, false, "C")]
    [InlineData('C', 30, true, "C")]
    [InlineData('C', 30.01, false, "D")]
    [InlineData('C', 45.01, false, "F")]
    public void BoundariesMatchQualityViewer(char minimum, double concern, bool pass, string actual)
    {
        var result = CodeQualityRule.EvaluateScore(minimum, concern);
        Assert.Equal(pass, result.IsValid);
        Assert.Contains($"grade {actual}", result.Message);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteScoreDoesNotPass(double concern) =>
        Assert.False(CodeQualityRule.EvaluateScore('C', concern).IsValid);

    [Theory]
    [InlineData("A", Rule.CodeQualityMinimumA)]
    [InlineData("B", Rule.CodeQualityMinimumB)]
    [InlineData("C", Rule.CodeQualityMinimumC)]
    public void CatalogOffersOnlySupportedThresholds(string grade, Rule expected)
    {
        var service = new RulesService();
        Assert.Equal(3, service.AllowedRules().Count(CodeQualityRule.LooksLike));
        Assert.True(service.TryParse($" code quality minimum {grade.ToLowerInvariant()} ", out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Contains("overall", service.GetDescription(parsed), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("D")]
    [InlineData("F")]
    [InlineData("C+")]
    [InlineData("")]
    public async Task InvalidThresholdCannotBeAuthoredAndOnlyWarnsWhenHandWritten(string grade)
    {
        var rule = $"Code quality minimum {grade}";
        Assert.False(new RulesService().TryParse(rule, out _));
        var report = await RulesTool.ValidateVcaReportAsync(stagedSnapshot: Snapshot(rule, File("a.cs", Clean)), cancellationToken: Ct);
        Assert.False(report.HasStopViolation);
        Assert.Contains("[WARN]", report.Output);
        Assert.Contains("UNSUPPORTED:", report.Output);
    }

    [Fact]
    public void GradesTheOverallScanRatherThanWorstFile()
    {
        var files = new[] { File("bad.cs", Messy) }
            .Concat(Enumerable.Range(0, 20).Select(i => File($"good{i}.cs", $"public class Good{i} {{ public int Value => {i}; }}")))
            .ToArray();
        Assert.False(CodeQualityRule.Evaluate('C', files.Take(1), Ct).IsValid);
        Assert.True(CodeQualityRule.Evaluate('C', files, Ct).IsValid);
        var scan = MintLintScorer.Score(MintLintAnalyzer.AnalyzeSources(
            files.Select(f => new SourceInput(f.RelativePath, f.AddedContent!)).ToList()));
        Assert.Equal(CodeQualityRule.EvaluateScore('C', scan.Overall.Score), CodeQualityRule.Evaluate('C', files, Ct));
    }

    [Fact]
    public void ScoresAddedLinesRatherThanExistingFile()
    {
        Assert.False(CodeQualityRule.Evaluate('C', [File("bad.cs", Messy)], Ct).IsValid);
        Assert.True(CodeQualityRule.Evaluate('C', [File("bad.cs", Messy) with { AddedContent = "public int Value => 1;" }], Ct).IsValid);
    }

    [Fact]
    public void NoAddedSupportedCodeIsExplicitlySkipped()
    {
        var result = CodeQualityRule.Evaluate('A',
        [
            File("old.cs", Messy) with { AddedContent = "" },
            File("README.md", Messy),
            File("deleted.cs", Messy) with { ExistsInIndex = false, Content = null },
            File("binary.cs", Messy) with { IsBinary = true }
        ], Ct);
        Assert.True(result.IsValid);
        Assert.Contains("skipped", result.Message);
    }

    [Fact]
    public void MissingSourceDoesNotSilentlyPass() =>
        Assert.False(CodeQualityRule.Evaluate('C', [File("bad.cs", Messy) with { Content = null }], Ct).IsValid);

    [Fact]
    public async Task RuleScopeDoesNotIncludeSiblingPrefix()
    {
        var snapshot = Snapshot("Code quality minimum C", File("src/good.cs", Clean), File("src-other/bad.cs", Messy))
            with { AgentFiles = [new("src/vc.rules.md", "## Vibe Rails Rules\n- Code quality minimum C (STOP)\n")] };
        var report = await RulesTool.ValidateVcaReportAsync(stagedSnapshot: snapshot, cancellationToken: Ct);
        Assert.False(report.HasError, report.Output);
        Assert.False(report.HasStopViolation, report.Output);
        Assert.Contains("Overall Code quality grade", report.Output);
    }

    [Fact]
    public async Task RulesPageUsesOneWorkingTreeSnapshotForAllThresholds()
    {
        var snapshot = Snapshot("Code quality minimum C", File("bad.cs", Messy));
        var provider = new Mock<IGitWorkingTreeSnapshotProvider>();
        provider.Setup(p => p.CaptureWorkingTreeAsync(Root, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var service = new RuleValidationService(new RulesService(), Mock.Of<IAgentFileService>(), provider.Object);
        var result = await service.ValidateAsync(["bad.cs"],
            "ABC".Select(g => new RuleWithEnforcement($"Code quality minimum {g}", Enforcement.STOP)).ToList(),
            Root, TestContext.Current.CancellationToken);
        Assert.All(result.Results, r => Assert.False(r.Passed, r.Message));
        provider.Verify(p => p.CaptureWorkingTreeAsync(Root, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RulesPageScopesTheAggregateToTheDeclaringDirectory()
    {
        var snapshot = Snapshot("Code quality minimum C", File("src/good.cs", Clean), File("other/bad.cs", Messy));
        var provider = new Mock<IGitWorkingTreeSnapshotProvider>();
        provider.Setup(p => p.CaptureWorkingTreeAsync(Root, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var service = new RuleValidationService(new RulesService(), Mock.Of<IAgentFileService>(), provider.Object);
        var result = await service.ValidateWithSourceAsync(["src/good.cs", "other/bad.cs"],
            [new VibeRails.Services.RuleWithSource(new("Code quality minimum C", Enforcement.STOP), Path.Combine(Root, "src", "vc.rules.md"))],
            Root, TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(result.Results).Passed);
    }

    [Fact]
    public async Task RulesPageReportsSnapshotFailureAtDeclaredLevel()
    {
        var provider = new Mock<IGitWorkingTreeSnapshotProvider>();
        provider.Setup(p => p.CaptureWorkingTreeAsync(Root, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("snapshot unavailable"));
        var service = new RuleValidationService(new RulesService(), Mock.Of<IAgentFileService>(), provider.Object);
        var result = Assert.Single((await service.ValidateAsync(["bad.cs"],
            [new("Code quality minimum C", Enforcement.STOP)], Root, TestContext.Current.CancellationToken)).Results);
        Assert.False(result.Passed);
        Assert.Equal(Enforcement.STOP, result.Enforcement);
        Assert.Contains("UNSUPPORTED:", result.Message);
    }

    [Theory]
    [InlineData("SKIP")]
    [InlineData("DISABLED")]
    public async Task DisabledRulesDoNotRun(string level)
    {
        var snapshot = Snapshot("Code quality minimum C", File("bad.cs", Messy))
            with { AgentFiles = [new("vc.rules.md", $"## Vibe Rails Rules\n- Code quality minimum C ({level})\n")] };
        var report = await RulesTool.ValidateVcaReportAsync(stagedSnapshot: snapshot, cancellationToken: Ct);
        Assert.False(report.HasStopViolation);
        Assert.Equal(0, report.ApplicableRuleCount);
    }

    [Fact]
    public async Task LegacyValidatorRequiresAndUsesAggregateContext()
    {
        var validator = new CodeQualityValidator(Rule.CodeQualityMinimumC);
        var rule = new RuleWithEnforcement("Code quality minimum C", Enforcement.STOP);
        var source = Path.Combine(Root, "vc.rules.md");
        Assert.False((await validator.ValidateAsync("good.cs", rule, source, Root, ct: Ct)).IsValid);
        var snapshot = Snapshot(rule.RuleText, File("bad.cs", Messy));
        var context = new ValidationContext(AdditionalData: new() { [CodeQualityValidator.SnapshotKey] = snapshot });
        Assert.False((await validator.ValidateAsync("good.cs", rule, source, Root, context, Ct)).IsValid);
    }

    private static GitStagedFileSnapshot File(string path, string content) =>
        new(path, Path.GetFullPath(path, Root), GitStagedChangeKind.Modified, true, false, 1, content, AddedContent: content);

    private static GitStagedSnapshot Snapshot(string rule, params GitStagedFileSnapshot[] files) =>
        new(Root, files, [new("vc.rules.md", $"## Vibe Rails Rules\n- {rule} (STOP)\n")]);
}
