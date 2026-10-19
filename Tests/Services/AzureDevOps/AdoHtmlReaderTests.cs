using VibeRails.Services.AzureDevOps;
using Xunit;

namespace Tests.Services.AzureDevOps;

public sealed class AdoHtmlReaderTests
{
    [Fact]
    public void BlockTagsBecomeLines_ListsBecomeDashes_AndEntitiesAreDecodedLast()
    {
        const string html = """
            <div>Sign-in fails&nbsp;on <b>Safari</b>.</div><div><br></div>
            <ul><li>Open   the
               page</li><li>Use &lt;script&gt; &amp; wait</li></ul>
            <p>Done.</p>
            """;

        Assert.Equal("Sign-in fails on Safari.\n\n- Open the page\n- Use <script> & wait\nDone.", AdoHtmlReader.ToText(html));
    }

    [Fact]
    public void ScriptsStylesImagesAndCommentsAreDropped_AndAStrayLessThanIsText()
    {
        const string html = "<style>p { color: red }</style><p>Keep 1 < 2</p><!-- note --><img src=\"data:image/png;base64,AAAA\">"
            + "<script>alert('x')</script><p>and <a href=\"https://example.com\">this link</a></p>";

        Assert.Equal("Keep 1 < 2\nand this link", AdoHtmlReader.ToText(html));
    }

    [Fact]
    public void PreformattedTextKeepsItsLines()
    {
        Assert.Equal("Steps:\nline one\n  indented", AdoHtmlReader.ToText("<p>Steps:</p><pre>line one\n  indented</pre>"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<div><br></div>")]
    public void EmptyBodiesAreEmpty(string? html) => Assert.Equal(string.Empty, AdoHtmlReader.ToText(html));

    [Fact]
    public void PlainTextWithNoTagsIsKept() => Assert.Equal("Just text", AdoHtmlReader.ToText("  Just text  "));

    [Fact]
    public void AnUnclosedTagIsText() => Assert.Equal("a <b", AdoHtmlReader.ToText("a <b"));

    /// <summary>
    /// Review R3: each '&lt;' used to search the rest of the body for a '&gt;', so a long run of
    /// unclosed tags was quadratic. A million characters now take one pass.
    /// </summary>
    [Fact]
    public void ARunOfUnclosedTagsIsReadInOnePass()
    {
        var html = string.Concat(Enumerable.Repeat("<a", 500_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var text = AdoHtmlReader.ToText(html);

        Assert.Equal(html, text);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }
}
