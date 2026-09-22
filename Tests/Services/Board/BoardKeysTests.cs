using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// The card key format and the folder-name prefix derivation (VB-32). Which prefix a project
/// actually gets, including collisions and the legacy VB rule, is the store's job: see
/// <c>Keys_*</c> in <see cref="BoardStoreTests"/>.
/// </summary>
public sealed class BoardKeysTests
{
    [Theory]
    [InlineData("VB-12", true, "VB", 12)]
    [InlineData("vb-1", true, "VB", 1)]
    [InlineData(" VB-7 ", true, "VB", 7)]
    [InlineData("vr-3", true, "VR", 3)]
    [InlineData("KBB-12", true, "KBB", 12)]
    [InlineData("A1B2-9", true, "A1B2", 9)]
    [InlineData("VB-0", false, "", 0)]
    [InlineData("VB-", false, "", 0)]
    [InlineData("-12", false, "", 0)]
    [InlineData("1A-12", false, "", 0)]
    [InlineData("VB-1-2", false, "", 0)]
    [InlineData("VB-x", false, "", 0)]
    [InlineData("TOOLONGPRE-1", false, "", 0)]
    [InlineData("card_abc", false, "", 0)]
    [InlineData("", false, "", 0)]
    [InlineData(null, false, "", 0)]
    public void TryParse(string? input, bool expected, string prefix, int number)
    {
        Assert.Equal(expected, BoardKeys.TryParse(input, out var parsedPrefix, out var parsed));
        Assert.Equal(prefix, parsedPrefix);
        Assert.Equal(number, parsed);
        Assert.Equal(expected, BoardKeys.TryParse(input, out var numberOnly));
        Assert.Equal(number, numberOnly);
    }

    [Fact]
    public void Format_JoinsPrefixAndNumber()
    {
        Assert.Equal("VB-32", BoardKeys.Format(BoardKeys.LegacyPrefix, 32));
        Assert.Equal("VR-1", BoardKeys.Format("VR", 1));
    }

    [Theory]
    [InlineData("vibe-rails", "VR,VIBE")]                 // dash-separated words: initials, then first letters
    [InlineData("VibeRails-Front", "VRF,VIBE")]           // camelCase and dashes both split
    [InlineData("my_cool_project", "MCP,MYCO")]
    [InlineData("my.cool project", "MCP,MYCO")]           // dots and spaces separate too
    [InlineData("project", "PROJ")]                       // one word: no initials, first four letters
    [InlineData("vb", "")]                                // the legacy prefix is never derived
    [InlineData("vibe-board", "VIBE")]                    // ...even when the initials would spell it
    [InlineData("my-super-long-repo-name", "MSLR,MYSU")] // capped at four
    [InlineData("3d-engine", "DE,DENG")]         // digits are not letters and do not split
    [InlineData("vb2Api", "VA,VBAP")]                     // an upper-case letter after a digit starts a word
    [InlineData("HTTPServer", "HTTP")]                    // consecutive capitals stay one word
    [InlineData("café-menu", "CM,CAFE")]                  // accents fall back to the base letter
    [InlineData("x", "")]                                 // fewer than two letters: nothing to offer
    [InlineData("1234", "")]
    [InlineData("プロジェクト", "")]
    [InlineData("", "")]
    public void DerivePrefixCandidates_FromTheProjectFolderName(string folder, string expected)
    {
        var path = folder.Length == 0 ? Path.GetPathRoot(Path.GetTempPath())! : Path.Combine(Path.GetTempPath(), folder);
        var candidates = BoardKeys.DerivePrefixCandidates(path);
        Assert.Equal(expected, string.Join(',', candidates));
        Assert.All(candidates, candidate =>
        {
            Assert.InRange(candidate.Length, BoardKeys.MinPrefixLength, BoardKeys.MaxPrefixLength);
            Assert.All(candidate, c => Assert.True(char.IsAsciiLetterUpper(c)));
        });
    }

    [Fact]
    public void DerivePrefixCandidates_IgnoresATrailingSeparator()
    {
        Assert.Equal(["VR", "VIBE"], BoardKeys.DerivePrefixCandidates(Path.Combine(Path.GetTempPath(), "vibe-rails") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void RandomPrefix_IsFourUpperCaseLetters_AndVaries()
    {
        var prefixes = Enumerable.Range(0, 32).Select(_ => BoardKeys.RandomPrefix()).ToList();
        Assert.All(prefixes, prefix =>
        {
            Assert.Equal(BoardKeys.MaxPrefixLength, prefix.Length);
            Assert.All(prefix, c => Assert.True(char.IsAsciiLetterUpper(c)));
            Assert.True(BoardKeys.TryParse(prefix + "-1", out var parsed, out _));
            Assert.Equal(prefix, parsed);
        });
        Assert.True(prefixes.Distinct().Count() > 1);
    }
}
