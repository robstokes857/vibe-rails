using Moq;
using VibeRails.Services;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using Xunit;

namespace Tests.Services.VCA;

public class CommitMessageWordRuleTests
{
    [Fact]
    public void CatalogKeepsTheTemplateButOnlyPopulatedSyntaxParses()
    {
        var service = new RulesService();
        Assert.Contains(service.AllowedRulesWithDescriptions(), rule => rule.Name == CommitMessageWordRule.Template);
        Assert.False(service.TryParse(CommitMessageWordRule.Template, out _));
        Assert.True(service.TryParse(" check COMMIT message for: WIP, temporary ", out var parsed));
        Assert.Equal(Rule.CheckCommitMessageForWords, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Check commit message for")]
    [InlineData("Check commit message for:")]
    [InlineData("Check commit message for: , ")]
    [InlineData("Check commit message for: wip,,todo")]
    [InlineData("Check commit message for: wip,")]
    [InlineData("Check commit message for wip")]
    [InlineData("Check commit message for: \"wip\"")]
    [InlineData("Check commit message for: 'wip'")]
    [InlineData("Check commit message for: do\t not merge")]
    [InlineData("Check commit message for: wip\nTODO")]
    public void RejectsIncompleteOrMalformedLists(string? text)
    {
        Assert.False(CommitMessageWordRule.TryParse(text, out _));
        Assert.False(new RulesService().TryParse(text!, out _));
    }

    [Fact]
    public void TrimsAndDeduplicatesEntriesWithoutLosingPhrasesOrApostrophes()
    {
        Assert.True(CommitMessageWordRule.TryParse(" Check commit message for:  WIP , do not merge , wip, don't ship  ", out var rule));
        Assert.Equal(["WIP", "do not merge", "don't ship"], rule.Words);
    }

    [Theory]
    [InlineData("WIP: fix parser", "wip")]
    [InlineData("Please DO NOT MERGE this", "do not merge")]
    [InlineData("Don't ship this change", "don't ship")]
    [InlineData("Fix a.b now", "a.b")]
    [InlineData("Fix axb now", "")]
    [InlineData("Fix swipe handler and temporaryFiles", "")]
    [InlineData("Add wip_value", "")]
    [InlineData("Add implementation", "")]
    [InlineData("TODO: replace C++ [skip]", "TODO:, C++, [skip]")]
    public async Task SharedAndLegacyMatchersKeepLiteralWholeWordCaseInsensitiveSemantics(string message, string expected)
    {
        const string text = "Check commit message for: wip, do not merge, don't ship, a.b, temporary, TODO:, C++, [skip]";
        Assert.True(CommitMessageWordRule.TryParse(text, out var rule));
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(", "), rule.FindMatches(message));
        var result = await new CommitMessageWordValidator().ValidateAsync("app.cs",
            new RuleWithEnforcement(text, Enforcement.STOP), "vc.rules.md", "/repo", new ValidationContext(message), TestContext.Current.CancellationToken);
        Assert.Equal(expected.Length == 0, result.IsValid);
        if (expected.Length > 0) Assert.Contains(expected, result.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RulesPageReportsMalformedConfigurationAsWarningAndValidRuleAsDeferred(bool withSource)
    {
        var service = new RuleValidationService(new RulesService(), Mock.Of<IAgentFileService>());
        var rules = new List<RuleWithEnforcement> {
            new(CommitMessageWordRule.Template, Enforcement.STOP),
            new("Check commit message for: wip", Enforcement.COMMIT)
        };
        var result = withSource
            ? await service.ValidateWithSourceAsync(["app.cs"], rules.Select(rule =>
                new VibeRails.Services.RuleWithSource(rule, "vc.rules.md")).ToList(), "/repo", TestContext.Current.CancellationToken)
            : await service.ValidateAsync(["app.cs"], rules, "/repo", TestContext.Current.CancellationToken);
        Assert.False(result.Results[0].Passed);
        Assert.Equal(Enforcement.WARN, result.Results[0].Enforcement);
        Assert.Contains("UNSUPPORTED", result.Results[0].Message);
        Assert.True(result.Results[1].Passed);
        Assert.Equal(Enforcement.COMMIT, result.Results[1].Enforcement);
        Assert.Contains("Deferred", result.Results[1].Message);
    }

    [Fact]
    public async Task LegacyValidatorExplainsMissingListAndMissingCommitMessage()
    {
        var validator = new CommitMessageWordValidator();
        var invalid = await validator.ValidateAsync("app.cs", new(CommitMessageWordRule.Template, Enforcement.STOP), "vc.rules.md", "/repo", ct: TestContext.Current.CancellationToken);
        Assert.False(invalid.IsValid);
        Assert.Contains("UNSUPPORTED", invalid.Message);
        var deferred = await validator.ValidateAsync("app.cs", new("Check commit message for: wip", Enforcement.STOP), "vc.rules.md", "/repo", ct: TestContext.Current.CancellationToken);
        Assert.True(deferred.IsValid);
        Assert.Contains("Deferred", deferred.Message);
    }

    [Fact]
    public async Task LegacyOrchestratorReportsMalformedStopListAsUnsupportedWarning()
    {
        var parser = new Mock<IFileAndRuleParser>();
        parser.Setup(value => value.GetFilesAndRulesAsync("/repo", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, List<VibeRails.Services.VCA.RuleWithSource>> {
                ["app.cs"] = [new(new(CommitMessageWordRule.Template, Enforcement.STOP), "vc.rules.md")]
            });
        var service = new ValidationService(Mock.Of<IValidatorList>(), parser.Object);
        var report = await service.ValidateAsync("/repo", true, cancellationToken: TestContext.Current.CancellationToken);
        var finding = Assert.Single(report.Results);
        Assert.False(finding.Passed);
        Assert.Equal(Enforcement.WARN, finding.Enforcement);
        Assert.Contains("UNSUPPORTED", finding.Message);
    }
}
