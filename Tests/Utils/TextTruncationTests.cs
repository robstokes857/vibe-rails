using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public class TextTruncationTests
{
    // U+1F600 GRINNING FACE — two UTF-16 code units, so C# sees "😀" as Length 2.
    private const string Emoji = "\U0001F600";

    [Fact]
    public void ShorterThanTheCap_IsReturnedUnchanged()
    {
        Assert.Equal("hello", TextTruncation.Truncate("hello", 10));
        Assert.Equal("hello", TextTruncation.Truncate("hello", 5));
        Assert.Equal("", TextTruncation.Truncate("", 5));
        Assert.Equal("", TextTruncation.Truncate("anything", 0));
    }

    [Fact]
    public void CutLandingBetweenSurrogates_DropsTheWholeCharacter()
    {
        // "ab😀" is 4 code units. Cutting at 3 would keep the high surrogate and drop its low
        // half, leaving an unpaired code unit that renders as U+FFFD and can break a UTF-8
        // encode on the way to the PTY.
        var truncated = TextTruncation.Truncate("ab" + Emoji, 3);

        Assert.Equal("ab", truncated);
        Assert.DoesNotContain(truncated, char.IsSurrogate);
    }

    [Fact]
    public void CutLandingAfterAFullPair_KeepsIt()
    {
        Assert.Equal("ab" + Emoji, TextTruncation.Truncate("ab" + Emoji + "cd", 4));
    }

    [Fact]
    public void EveryCutPositionInASurrogateHeavyString_LeavesWellFormedText()
    {
        var text = string.Concat(Enumerable.Repeat("x" + Emoji, 20));

        for (var max = 0; max <= text.Length; max++)
        {
            var truncated = TextTruncation.Truncate(text, max);

            Assert.True(truncated.Length <= max);
            // A lone surrogate anywhere means the cut split a pair. Encoding is the practical
            // test: UTF-8 turns unpaired surrogates into replacement characters.
            Assert.Equal(
                truncated,
                System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(truncated)));
        }
    }

    [Fact]
    public void Marker_IsAppendedOnlyWhenSomethingWasActuallyDropped()
    {
        Assert.Equal("hello", TextTruncation.TruncateWithMarker("hello", 10, "…more"));
        Assert.Equal("hello", TextTruncation.TruncateWithMarker("hello", 5, "…more"));
        Assert.Equal("hell…more", TextTruncation.TruncateWithMarker("hello", 4, "…more"));
    }

    [Fact]
    public void MarkerCase_AlsoRespectsSurrogatePairs()
    {
        Assert.Equal("ab…more", TextTruncation.TruncateWithMarker("ab" + Emoji + "cd", 3, "…more"));
    }

    [Fact]
    public void NegativeCap_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextTruncation.Truncate("hello", -1));
    }
}
