using System.Text;
using System.Text.RegularExpressions;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>VIBE-321 #6: a lane tile's preview comes out the same from the excerpt as from the whole description.</summary>
public sealed partial class BoardCardExcerptTests
{
    [Fact]
    public void ShortDescriptionsAreReturnedWhole()
    {
        Assert.Equal("", BoardCardExcerpt.For(null));
        Assert.Equal("", BoardCardExcerpt.For(""));
        var text = string.Join(' ', Enumerable.Repeat("word", 119));
        Assert.Same(text, BoardCardExcerpt.For(text));
    }

    [Fact]
    public void ControlCharactersOrAVeryLongLineKeepTheWholeDescription()
    {
        // The preview strips control characters before it pairs fences: "``\u0007`" is a fence there.
        var control = "Escape \u001b[31mred\u001b[0m\n" + string.Join('\n', Enumerable.Repeat("filler words on a line", 500));
        Assert.Same(control, BoardCardExcerpt.For(control));
        var oneLine = string.Concat(Enumerable.Repeat("😀 word ", 5000));
        Assert.Same(oneLine, BoardCardExcerpt.For(oneLine));
    }

    [Fact]
    public void LongTextIsCutAtTheEndOfALineAfterEnoughVisibleCharacters()
    {
        var text = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"line {i} with some words"));
        var excerpt = BoardCardExcerpt.For(text);
        Assert.True(excerpt.Length < text.Length / 4, $"kept {excerpt.Length} of {text.Length}");
        Assert.True(excerpt.Count(c => !char.IsWhiteSpace(c)) >= BoardCardExcerpt.VisibleCharacters);
        Assert.StartsWith(excerpt + "\n", text, StringComparison.Ordinal);
        Assert.Equal(Preview(text), Preview(excerpt));
    }

    [Fact]
    public void AFencedBlockIsNeverCutAndCountsAsItsPlaceholder()
    {
        var log = "```text\n" + new string('x', 50_000) + "\n```";
        var text = "Intro\n" + log + "\nAfter the log\n" + string.Join('\n', Enumerable.Repeat("tail words here", 2000));
        var excerpt = BoardCardExcerpt.For(text);
        Assert.Contains(log, excerpt, StringComparison.Ordinal);
        Assert.Contains("After the log", excerpt, StringComparison.Ordinal);
        Assert.True(excerpt.Length < text.Length);
        Assert.Equal(Preview(text), Preview(excerpt));
    }

    [Fact]
    public void GeneratedDescriptionsKeepTheirTilePreview()
    {
        var random = new Random(321);
        string[] pieces = ["word", "Word ", "  ", "\n", "\n\n", "\t", "`inline code`", "`", "```", "```js\n", "```\n",
            "![shot](attachment:att_1)", "![", "](attachment:", "![a ``` b](attachment:x)", "https://example.com/a.", "😀", "é",
            new string(' ', 300), new string('x', 700), "\r\n", " ", "word word word word\n"];
        var shortened = 0;
        for (var sample = 0; sample < 3000; sample++)
        {
            var text = new StringBuilder();
            var length = random.Next(1, 8000);
            while (text.Length < length) text.Append(pieces[random.Next(pieces.Length)]);
            var description = text.ToString();
            var excerpt = BoardCardExcerpt.For(description);
            Assert.StartsWith(excerpt, description, StringComparison.Ordinal);
            Assert.Equal(Preview(description), Preview(excerpt));
            if (excerpt.Length < description.Length) shortened++;
        }
        Assert.True(shortened > 500, $"only {shortened} samples were shortened");
    }

    /// <summary>A port of <c>toPlainPreview(text, 110)</c> in board-text.js, for this comparison only.</summary>
    private static string Preview(string text, int maxLength = 110)
    {
        var flat = ControlCharacters().Replace(text, "");
        flat = Fence().Replace(flat, " [code] ");
        flat = Image().Replace(flat, " [image] ");
        flat = InlineCode().Replace(flat, "$1");
        flat = Whitespace().Replace(flat, " ").Trim();
        return flat.Length > maxLength ? flat[..(maxLength - 1)] + "…" : flat;
    }

    [GeneratedRegex("[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlCharacters();
    [GeneratedRegex(@"```([a-zA-Z0-9_+#.-]*)[ \t]*\r?\n?([\s\S]*?)```")]
    private static partial Regex Fence();
    [GeneratedRegex(@"!\[([^\]\n]*)\]\(attachment:([A-Za-z0-9_-]+)\)")]
    private static partial Regex Image();
    [GeneratedRegex("`([^`\n]+)`")]
    private static partial Regex InlineCode();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
