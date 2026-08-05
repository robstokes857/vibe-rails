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
    public void Parse_ReturnsGlm52_ForGlm52String()
    {
        // C# enum names can't contain hyphens/periods, so "glm-5.2" can't round-trip
        // through Enum.TryParse. LlmParser special-cases it via SpecialCaseMap.
        var result = _parser.Parse("glm-5.2");

        Assert.Equal(LLM.Glm52, result);
    }

    [Theory]
    [InlineData("GLM-5.2")]
    [InlineData("Glm-5.2")]
    [InlineData(" glm-5.2 ")]
    public void Parse_HandlesGlm52CaseInsensitiveAndWhitespace(string input)
    {
        var result = _parser.Parse(input);

        Assert.Equal(LLM.Glm52, result);
    }

    [Fact]
    public void Normalize_ReturnsWireFormat_ForGlm52()
    {
        // Normalize must return "glm-5.2" (not "Glm52") so the wire format callers
        // expect is preserved through the round-trip.
        var result = _parser.Normalize("glm-5.2");

        Assert.Equal("glm-5.2", result);
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
        Assert.Contains(LLM.Antigravity, _parser.All);
        Assert.Contains(LLM.Copilot, _parser.All);
        Assert.Contains(LLM.OpenCode, _parser.All);
        Assert.Contains(LLM.Glm52, _parser.All);
    }

    [Theory]
    [InlineData(LLM.Glm52, "glm-5.2")]
    [InlineData(LLM.Claude, "Claude")]
    [InlineData(LLM.Codex, "Codex")]
    public void ToWireName_ReturnsWireFormat_ForEnumValue(LLM llm, string expected)
    {
        // Outbound boundaries persist session Cli, the environments API, etc. through ToWireName /
        // Normalize(LLM); both must emit the hyphenated wire name for the pseudo-CLIs, not "Glm52".
        Assert.Equal(expected, LlmParser.ToWireName(llm));
        Assert.Equal(expected, _parser.Normalize(llm));
    }

    [Fact]
    public void WireName_RoundTripsForEveryLlm()
    {
        // Regression guard for the enum-name/wire-name divergence (the Antigravity bug class): every
        // outbound wire name must Parse back to the same enum. A future pseudo-CLI that adds a
        // hyphenated wire name without a SpecialCaseMap reverse entry would fail here.
        foreach (var llm in _parser.All)
        {
            var wire = LlmParser.ToWireName(llm);
            Assert.Equal(llm, _parser.Parse(wire));
        }
    }
}
