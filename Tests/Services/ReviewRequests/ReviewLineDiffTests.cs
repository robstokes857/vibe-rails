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
}
