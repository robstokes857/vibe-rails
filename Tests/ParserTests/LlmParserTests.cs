using VibeRails.Services;
using Xunit;

namespace Tests.ParserTests;

public class LlmParserTests
{
    private readonly LlmParser _parser = new();

    [Fact]
    public void Parse_ReturnsMatchingEnum_ForKnownValue()
    {
        var result = _parser.Parse(" codex ");

        Assert.Equal(LLM.Codex, result);
    }

    [Fact]
    public void Parse_ReturnsShell_ForShellValue()
    {
        var result = _parser.Parse("shell");

        Assert.Equal(LLM.Shell, result);
    }

    [Fact]
    public void Parse_ReturnsNotSet_ForUnknownValue()
    {
        var result = _parser.Parse("unknown");

        Assert.Equal(LLM.NotSet, result);
    }

    [Fact]
    public void Normalize_ReturnsCanonicalEnumName_ForKnownValue()
    {
        var result = _parser.Normalize(" claude ");

        Assert.Equal("Claude", result);
    }

    [Fact]
    public void Normalize_ReturnsEmptyString_ForUnknownValue()
    {
        var result = _parser.Normalize("unknown");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void All_ExcludesNotSet()
    {
        Assert.DoesNotContain(LLM.NotSet, _parser.All);
        Assert.Contains(LLM.Codex, _parser.All);
        Assert.Contains(LLM.Claude, _parser.All);
        Assert.Contains(LLM.Gemini, _parser.All);
        Assert.Contains(LLM.Copilot, _parser.All);
    }
}
