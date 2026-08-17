using VibeRails.Services;
using VibeRails.Services.VCA;
using Xunit;

namespace Tests.Services;

public class RulesServiceTests
{
    private readonly RulesService _service = new();

    [Fact]
    public void AllowedRules_IncludesBothPathLockTemplates()
    {
        var rules = _service.AllowedRules();

        Assert.Contains(PathLockRule.FileTemplate, rules);
        Assert.Contains(PathLockRule.DirectoryTemplate, rules);
    }

    [Theory]
    [InlineData("File Lock('src/app.cs')", Rule.FileLock)]
    [InlineData("Directory Lock('src/generated')", Rule.DirectoryLock)]
    [InlineData("Check commit message for: wip, temporary", Rule.CheckCommitMessageForWords)]
    public void TryParse_RecognizesParameterizedRules(string text, Rule expectedRule)
    {
        Assert.True(_service.TryParse(text, out var parsed));
        Assert.Equal(expectedRule, parsed);
    }

    [Theory]
    [InlineData("File Lock(src/app.cs)")]
    [InlineData("Directory Lock('')")]
    public void TryParse_RejectsMalformedPathLocks(string text)
    {
        Assert.False(_service.TryParse(text, out _));
    }
}
