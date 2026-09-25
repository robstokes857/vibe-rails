using System.Text.Json;
using VibeRails.Services.Jira;
using Xunit;

namespace Tests.Services.Jira;

public sealed class AdfReaderTests
{
    [Fact]
    public void FlattensTheNodesPhaseOneKeeps()
    {
        const string json = """
            {
              "type": "doc",
              "content": [
                {"type": "heading", "attrs": {"level": 2}, "content": [{"type": "text", "text": "Plan"}]},
                {"type": "paragraph", "content": [
                  {"type": "text", "text": "Ship "},
                  {"type": "text", "text": "the pull", "marks": [{"type": "strong"}]},
                  {"type": "text", "text": " and read "},
                  {"type": "text", "text": "the docs", "marks": [{"type": "link", "attrs": {"href": "https://example.com"}}]},
                  {"type": "text", "text": " before "},
                  {"type": "text", "text": "code", "marks": [{"type": "code"}]}
                ]},
                {"type": "bulletList", "content": [
                  {"type": "listItem", "content": [{"type": "paragraph", "content": [{"type": "text", "text": "One"}]}]},
                  {"type": "listItem", "content": [{"type": "paragraph", "content": [{"type": "text", "text": "Two"}]}]}
                ]},
                {"type": "orderedList", "attrs": {"order": 1}, "content": [
                  {"type": "listItem", "content": [{"type": "paragraph", "content": [{"type": "text", "text": "First"}]}]}
                ]},
                {"type": "codeBlock", "attrs": {"language": "js"}, "content": [{"type": "text", "text": "const n = 1;"}]},
                {"type": "blockquote", "content": [{"type": "paragraph", "content": [{"type": "text", "text": "Noted"}]}]},
                {"type": "rule"},
                {"type": "mediaSingle", "content": [{"type": "media", "attrs": {"id": "abc"}}]},
                {"type": "paragraph", "content": [
                  {"type": "mention", "attrs": {"text": "@Ada"}},
                  {"type": "text", "text": " owns the "},
                  {"type": "table"},
                  {"type": "text", "text": " "},
                  {"type": "inlineCard", "attrs": {"url": "https://example.com"}}
                ]},
                {"type": "futureNode", "content": [{"type": "text", "text": "ignored"}]}
              ]
            }
            """;

        var text = AdfReader.ToText(json);

        Assert.Contains("## Plan", text);
        Assert.Contains("Ship **the pull** and read [the docs](https://example.com) before `code`", text);
        Assert.Contains("- One\n- Two", text);
        Assert.Contains("1. First", text);
        Assert.Contains("```js\nconst n = 1;\n```", text);
        Assert.Contains("> Noted", text);
        Assert.Contains("---", text);
        Assert.Contains("[image]", text);
        Assert.Contains("@Ada", text);
        Assert.Contains("[table]", text);
        Assert.Contains("[link card]", text);
        Assert.DoesNotContain("ignored", text);
        Assert.DoesNotContain("futureNode", text);
    }

    [Fact]
    public void EmptyAndBrokenDocumentsBecomeEmptyText()
    {
        Assert.Equal(string.Empty, AdfReader.ToText((string?)null));
        Assert.Equal(string.Empty, AdfReader.ToText("   "));
        Assert.Equal(string.Empty, AdfReader.ToText("{"));
        using var number = JsonDocument.Parse("1");
        Assert.Equal(string.Empty, AdfReader.ToText(number.RootElement));
    }
}
