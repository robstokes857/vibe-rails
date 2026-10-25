using VibeRails.Services.ReviewRequests;
using Xunit;

namespace Tests.Services.ReviewRequests;

/// <summary>The +/− counts the card shows beside each staged file (VB-50 slice E).</summary>
public sealed class ReviewLineDiffTests
{
    [Fact]
    public void CountsAddedAndRemovedLinesLikeGitNumstat()
    {
        Assert.Equal((1, 1), ReviewLineDiff.Count("class Db { }\n", "class Db { int Version = 2; }\n"));
        Assert.Equal((0, 0), ReviewLineDiff.Count("same\nlines\n", "same\nlines\n"));
        Assert.Equal((2, 0), ReviewLineDiff.Count("a\nb\n", "a\nx\ny\nb\n"));
        Assert.Equal((0, 2), ReviewLineDiff.Count("a\nx\ny\nb\n", "a\nb\n"));
        Assert.Equal((1, 2), ReviewLineDiff.Count("a\nb\nc\nd\n", "a\nz\nd\n"));
        // A trailing newline is not an extra line; a missing one changes the last line only through its text.
        Assert.Equal((0, 0), ReviewLineDiff.Count("a\nb", "a\nb"));
        Assert.Equal((1, 1), ReviewLineDiff.Count("a\nb\r\n", "a\nb\n"));
    }

    [Fact]
    public void AddedAndDeletedFilesCountEveryLineOfTheOtherSide()
    {
        Assert.Equal((3, 0), ReviewLineDiff.Count(null, "one\ntwo\nthree\n"));
        Assert.Equal((0, 2), ReviewLineDiff.Count("one\ntwo\n", null));
        Assert.Equal((0, 0), ReviewLineDiff.Count("", ""));
        Assert.Null(ReviewLineDiff.Count(null, null));
    }

    [Fact]
    public void StopsAtTheEditBoundInsteadOfStalling()
    {
        var before = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"old {i}")) + "\n";
        var after = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"new {i}")) + "\n";
        Assert.Equal((300, 300), ReviewLineDiff.Count(before, after));
        Assert.Null(ReviewLineDiff.Count(before, after, maxEdits: 100));
    }

    // VIBE-213 item 8: a rewrite well inside the edit bound still stops at the per-file work budget.
    [Fact]
    public void StopsAtTheWorkBudgetWellInsideTheEditBound()
    {
        var before = string.Join('\n', Enumerable.Range(0, 2_000).Select(i => $"old {i}")) + "\n";
        var after = string.Join('\n', Enumerable.Range(0, 2_000).Select(i => $"new {i}")) + "\n";
        Assert.True(4_000 < ReviewLineDiff.MaxEdits);
        Assert.Null(ReviewLineDiff.Count(before, after));
        Assert.Equal((2_000, 2_000), ReviewLineDiff.Count(before, after, maxWork: long.MaxValue));
        // A small change in a large file never comes near the budget: prefix and suffix are trimmed first.
        var large = string.Join('\n', Enumerable.Range(0, 200_000).Select(i => $"line {i}")) + "\n";
        Assert.Equal((1, 1), ReviewLineDiff.Count(large, large.Replace("line 100000\n", "changed\n")));
    }

    // VIBE-213 item 18: ReviewBundleCapture.WireSize counts the escaped bytes without building them; it
    // must stay equal to the encoder's own output, or a bundle could pass the local bound and fail the host's.
    [Fact]
    public void WireSizeMatchesTheEscapedEncoding()
    {
        static long Escaped(string text) => System.Text.Json.JsonEncodedText.Encode(text).EncodedUtf8Bytes.Length + 2L;
        for (var character = 0; character < 128; character++)
        {
            var text = ((char)character).ToString();
            Assert.Equal(Escaped(text), ReviewBundleCapture.WireSize(text));
        }
        string[] samples = ["", "class Db { int Version = 2; }\n", "<a href=\"x\">&'+`</a>\\", "é中\u2028\uFFFD", "emoji 😀 pair", "tab\tcr\rnul\0"];
        foreach (var text in samples)
            Assert.Equal(Escaped(text), ReviewBundleCapture.WireSize(text));
        var random = new Random(213);
        for (var round = 0; round < 200; round++)
        {
            var chars = new char[random.Next(0, 64)];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = random.Next(4) == 0 ? (char)random.Next(0x80, 0xD800) : (char)random.Next(0, 0x80);
            var text = new string(chars);
            Assert.Equal(Escaped(text), ReviewBundleCapture.WireSize(text));
        }
        Assert.Equal(0, ReviewBundleCapture.WireSize(null));
    }
}
