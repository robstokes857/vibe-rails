using Moq;
using VibeRails.Services;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using Xunit;

namespace Tests.Services.VCA;

public sealed class SessionShareCommitRuleTests
{
    public const string Url = "https://viberails.ai/shared/session?key=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("vibe-share:{0}")]
    [InlineData("Fix bug\r\n\r\n  VIBE-SHARE: {0}  \r\n")]
    [InlineData("vibe-share:invalid\nvibe-share:{0}\nvibe-share:{0}")]
    [InlineData("Replay: {0}.")]
    [InlineData("[Session]({0})")]
    [InlineData("See <{0}> for context")]
    [InlineData("Session `{0}`")]
    public void FindsTrailersAndFallbackLinks(string template) =>
        Assert.True(SessionShareCommitRule.HasShareLink(string.Format(template, Url)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("vibe-share:url1")]
    [InlineData("vibe-share:https://viberails.ai")]
    [InlineData("https://viberails.ai/api/v1/session-sharing-links")]
    [InlineData("http://viberails.ai/shared/session?key={0}")]
    [InlineData("https://viberails.ai.evil.test/shared/session?key={0}")]
    [InlineData("https://viberails.ai@evil.test/shared/session?key={0}")]
    [InlineData("https://user@viberails.ai/shared/session?key={0}")]
    [InlineData("https://viberails.ai:444/shared/session?key={0}")]
    [InlineData("https://viberails.ai/shared/board?key={0}")]
    [InlineData("https://viberails.ai/shared/session?key=short")]
    [InlineData("https://viberails.ai/shared/session?key={0}a")]
    [InlineData("https://viberails.ai/shared/session?other={0}")]
    public void RejectsMissingInvalidAndUnrelatedLinks(string? template) =>
        Assert.False(SessionShareCommitRule.HasShareLink(template?.Replace("{0}", new string('a', 64))));

    [Fact]
    public void CatalogIncludesTheRuleAndActionableInstructions()
    {
        var service = new RulesService();
        Assert.True(service.TryParse(" require viberails session link ", out var rule));
        Assert.Equal(Rule.RequireVibeRailsSessionLink, rule);
        var entry = Assert.Single(service.AllowedRulesWithDescriptions(), r => r.Name == SessionShareCommitRule.Name);
        Assert.Contains("create_session_share_link", entry.Description);
        Assert.Contains("vibe-share:", entry.Description);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RulesPageDefersAndExplainsHowToAddLink(bool withSource)
    {
        var service = new RuleValidationService(new RulesService(), Mock.Of<IAgentFileService>(), Moq.Mock.Of<VibeRails.Services.GitPreflight.IGitWorkingTreeSnapshotProvider>());
        var rule = new RuleWithEnforcement(SessionShareCommitRule.Name, Enforcement.STOP);
        var root = Path.GetFullPath(Path.GetTempPath());
        var result = withSource
            ? await service.ValidateWithSourceAsync(["app.cs"], [new(rule, Path.Combine(root, "vc.rules.md"))], root, TestContext.Current.CancellationToken)
            : await service.ValidateAsync(["app.cs"], [rule], root, TestContext.Current.CancellationToken);
        var finding = Assert.Single(result.Results);
        Assert.True(finding.Passed);
        Assert.Equal(Enforcement.STOP, finding.Enforcement);
        Assert.Equal(SessionShareCommitRule.DeferredMessage, finding.Message);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Fix bug", false)]
    [InlineData(Url, true)]
    public async Task LegacyValidatorUsesTheSameMatcher(string? message, bool valid)
    {
        var result = await new SessionShareCommitValidator().ValidateAsync("app.cs",
            new(SessionShareCommitRule.Name, Enforcement.STOP), "vc.rules.md", "/repo",
            new ValidationContext(CommitMessage: message), TestContext.Current.CancellationToken);
        Assert.Equal(valid, result.IsValid);
    }
}
