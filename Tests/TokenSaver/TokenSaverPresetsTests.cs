using TokenSaver.Minify;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Pins the level → (flags, condense, allowlist) preset table. These are user-facing contracts:
/// a preset drifting silently would change what gets rewritten on the wire for every install on
/// that level, so any intentional change must show up here.
/// </summary>
public class TokenSaverPresetsTests
{
    [Theory]
    [InlineData(null, TokenSaverLevel.Custom)]
    [InlineData("", TokenSaverLevel.Custom)]
    [InlineData("custom", TokenSaverLevel.Custom)]
    [InlineData("bogus", TokenSaverLevel.Custom)]
    [InlineData("off", TokenSaverLevel.Off)]
    [InlineData("OFF", TokenSaverLevel.Off)]
    [InlineData("safest", TokenSaverLevel.Safest)]
    [InlineData(" Safest ", TokenSaverLevel.Safest)]
    [InlineData("safe", TokenSaverLevel.Safe)]
    [InlineData("medium", TokenSaverLevel.Medium)]
    [InlineData("high", TokenSaverLevel.High)]
    public void Normalize_MapsStringsToLevels(string? input, TokenSaverLevel expected)
    {
        Assert.Equal(expected, TokenSaverPresets.Normalize(input));
    }

    [Theory]
    [InlineData(TokenSaverLevel.Custom, "custom")]
    [InlineData(TokenSaverLevel.Off, "off")]
    [InlineData(TokenSaverLevel.Safest, "safest")]
    [InlineData(TokenSaverLevel.Safe, "safe")]
    [InlineData(TokenSaverLevel.Medium, "medium")]
    [InlineData(TokenSaverLevel.High, "high")]
    public void ToSettingsString_RoundTripsThroughNormalize(TokenSaverLevel level, string expected)
    {
        var s = TokenSaverPresets.ToSettingsString(level);
        Assert.Equal(expected, s);
        Assert.Equal(level, TokenSaverPresets.Normalize(s));
    }

    [Fact]
    public void Safest_IsTodaysDefaultBehavior()
    {
        var (flags, condense, allowlist) = TokenSaverPresets.For(TokenSaverLevel.Safest);
        Assert.Equal(MinifyFlags.Default, flags);
        Assert.True(condense.IsNoOp);
        Assert.Equal(["Bash", "PowerShell"], allowlist);
    }

    [Fact]
    public void Safe_AddsBlankRunCollapseAndBackgroundShell()
    {
        var (flags, condense, allowlist) = TokenSaverPresets.For(TokenSaverLevel.Safe);
        Assert.Equal(MinifyFlags.Default with { CollapseBlankLineRuns = true }, flags);
        Assert.True(condense.IsNoOp);
        Assert.Equal(["Bash", "PowerShell", "BashOutput"], allowlist);
    }

    [Fact]
    public void Medium_AddsDedupOnly()
    {
        var (flags, condense, allowlist) = TokenSaverPresets.For(TokenSaverLevel.Medium);
        Assert.Equal(MinifyFlags.Default with { CollapseBlankLineRuns = true }, flags);
        Assert.Equal(new CondenseOptions(DedupeConsecutiveLines: true, TruncateLongOutput: false), condense);
        Assert.Equal(["Bash", "PowerShell", "BashOutput"], allowlist);
    }

    [Fact]
    public void High_AddsDedupAndTruncation()
    {
        var (flags, condense, allowlist) = TokenSaverPresets.For(TokenSaverLevel.High);
        Assert.Equal(MinifyFlags.Default with { CollapseBlankLineRuns = true }, flags);
        Assert.Equal(new CondenseOptions(DedupeConsecutiveLines: true, TruncateLongOutput: true), condense);
        Assert.Equal(["Bash", "PowerShell", "BashOutput"], allowlist);
    }

    [Fact]
    public void Off_IsAllNoOp()
    {
        var (flags, condense, _) = TokenSaverPresets.For(TokenSaverLevel.Off);
        Assert.True(flags.IsNoOp);
        Assert.True(condense.IsNoOp);
    }

    [Fact]
    public void Custom_HasNoPreset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenSaverPresets.For(TokenSaverLevel.Custom));
    }

    [Fact]
    public void EveryCondenseEnabledPreset_AlsoCollapsesCrRedraws()
    {
        // The condenser aborts whole strings containing CR; CR-collapse (which also normalizes
        // CRLF→LF) is what keeps that abort rare. A preset with condensing but no CR-collapse
        // would silently forfeit most savings on Windows output.
        foreach (var level in new[] { TokenSaverLevel.Medium, TokenSaverLevel.High })
        {
            var (flags, condense, _) = TokenSaverPresets.For(level);
            Assert.False(condense.IsNoOp);
            Assert.True(flags.CollapseCrRedraws);
        }
    }
}
