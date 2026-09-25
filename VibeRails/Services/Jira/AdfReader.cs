using System.Text;
using System.Text.Json;

namespace VibeRails.Services.Jira;

/// <summary>
/// Flattens an Atlassian Document Format document to plain text. Phase 1 only reads: a lossy
/// result is acceptable because the description is not pushed back. Unknown nodes are skipped.
/// Media, mentions, panels, tables and inline cards become a one-line placeholder.
/// </summary>
public static class AdfReader
{
    public static string ToText(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
            return string.Empty;
        var builder = new StringBuilder();
        AppendNode(builder, document, new Context(ListKind.None, 0));
        return builder.ToString().Trim();
    }

    public static string ToText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            return ToText(document.RootElement);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private enum ListKind { None, Bullet, Ordered }

    private readonly record struct Context(ListKind List, int Index);

    private static void AppendNode(StringBuilder builder, JsonElement node, Context context)
    {
        var type = Text(node, "type");
        switch (type)
        {
            case "doc":
                AppendChildren(builder, node, context, "\n\n");
                break;
            case "paragraph":
                AppendInline(builder, node);
                break;
            case "heading":
                var level = Math.Clamp(Int(node, "attrs", "level") ?? 1, 1, 6);
                builder.Append(new string('#', level)).Append(' ');
                AppendInline(builder, node);
                break;
            case "bulletList":
                AppendList(builder, node, ListKind.Bullet);
                break;
            case "orderedList":
                AppendList(builder, node, ListKind.Ordered);
                break;
            case "listItem":
                builder.Append(context.List == ListKind.Ordered ? $"{context.Index}. " : "- ");
                AppendChildren(builder, node, new Context(ListKind.None, 0), "\n");
                break;
            case "codeBlock":
                builder.Append("```");
                var language = Text(node, "attrs", "language");
                if (!string.IsNullOrEmpty(language))
                    builder.Append(language);
                builder.Append('\n');
                AppendInline(builder, node);
                if (builder.Length == 0 || builder[^1] != '\n')
                    builder.Append('\n');
                builder.Append("```");
                break;
            case "blockquote":
                AppendQuoted(builder, node);
                break;
            case "rule":
                builder.Append("---");
                break;
            case "text":
                AppendText(builder, node);
                break;
            case "hardBreak":
                builder.Append('\n');
                break;
            case "media" or "mediaSingle" or "mediaGroup":
                builder.Append("[image]");
                break;
            case "mention":
                var mention = Text(node, "attrs", "text");
                builder.Append(string.IsNullOrEmpty(mention) ? "@mention" : mention);
                break;
            case "table":
                builder.Append("[table]");
                break;
            case "panel":
                builder.Append("[panel]");
                break;
            case "inlineCard" or "blockCard" or "embedCard":
                builder.Append("[link card]");
                break;
            case "emoji":
                var emoji = Text(node, "attrs", "shortName") ?? Text(node, "attrs", "text");
                if (!string.IsNullOrEmpty(emoji))
                    builder.Append(emoji);
                break;
            default:
                break;
        }
    }

    private static void AppendList(StringBuilder builder, JsonElement node, ListKind kind)
    {
        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;
        var index = kind == ListKind.Ordered ? Int(node, "attrs", "order") ?? 1 : 1;
        var first = true;
        foreach (var child in content.EnumerateArray())
        {
            if (!first)
                builder.Append('\n');
            first = false;
            AppendNode(builder, child, new Context(kind, index));
            index++;
        }
    }

    private static void AppendChildren(StringBuilder builder, JsonElement node, Context context, string separator)
    {
        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;
        var first = true;
        foreach (var child in content.EnumerateArray())
        {
            if (!first && builder.Length > 0 && !EndsWith(builder, separator))
                builder.Append(separator);
            first = false;
            var start = builder.Length;
            AppendNode(builder, child, context);
            if (builder.Length == start)
                first = builder.Length == 0;
        }
    }

    private static void AppendInline(StringBuilder builder, JsonElement node)
    {
        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;
        foreach (var child in content.EnumerateArray())
            AppendNode(builder, child, new Context(ListKind.None, 0));
    }

    private static void AppendQuoted(StringBuilder builder, JsonElement node)
    {
        var inner = new StringBuilder();
        AppendChildren(inner, node, new Context(ListKind.None, 0), "\n");
        var lines = inner.ToString().Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                builder.Append('\n');
            builder.Append("> ").Append(lines[i]);
        }
    }

    private static void AppendText(StringBuilder builder, JsonElement node)
    {
        var text = Text(node, "text") ?? string.Empty;
        var link = LinkHref(node);
        var code = HasMark(node, "code");
        var strong = HasMark(node, "strong");
        var em = HasMark(node, "em");
        if (code)
            builder.Append('`').Append(text).Append('`');
        else if (link is not null)
            builder.Append('[').Append(text).Append("](").Append(link).Append(')');
        else if (strong)
            builder.Append("**").Append(text).Append("**");
        else if (em)
            builder.Append('*').Append(text).Append('*');
        else
            builder.Append(text);
    }

    private static bool HasMark(JsonElement node, string markType)
    {
        if (!node.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var mark in marks.EnumerateArray())
        {
            if (string.Equals(Text(mark, "type"), markType, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string? LinkHref(JsonElement node)
    {
        if (!node.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var mark in marks.EnumerateArray())
        {
            if (!string.Equals(Text(mark, "type"), "link", StringComparison.Ordinal))
                continue;
            var href = Text(mark, "attrs", "href");
            if (!string.IsNullOrEmpty(href))
                return href;
        }
        return null;
    }

    private static string? Text(JsonElement node, string property) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Text(JsonElement node, string property, string nested)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;
        return Text(value, nested);
    }

    private static int? Int(JsonElement node, string property, string nested)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;
        return value.TryGetProperty(nested, out var number) && number.TryGetInt32(out var parsed) ? parsed : null;
    }

    private static bool EndsWith(StringBuilder builder, string suffix)
    {
        if (builder.Length < suffix.Length)
            return false;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (builder[builder.Length - suffix.Length + i] != suffix[i])
                return false;
        }
        return true;
    }
}
