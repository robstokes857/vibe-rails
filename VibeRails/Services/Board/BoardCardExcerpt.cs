using System.Text.RegularExpressions;

namespace VibeRails.Services.Board;

/// <summary>
/// The start of a card description, long enough for a lane tile's plain-text preview (VIBE-321).
/// A tile shows 110 characters of <c>toPlainPreview</c> (board-text.js), but the summaries the Board
/// polls every ten seconds carried whole descriptions: 132-616 KB for 90 cards. The editor always
/// reads the full card, so a tile needs only enough text for its preview to come out the same:
/// <list type="bullet">
/// <item>at least <see cref="VisibleCharacters"/> non-space characters, counting a fenced code block
/// and an attachment image as the short placeholders they become;</item>
/// <item>cut only at a line break, never inside a fenced block: inline code and images never span a
/// line, so every construct the preview pairs before the cut pairs the same way in the whole text;</item>
/// <item>the whole description when that line runs past <see cref="LineAllowance"/>, or when it holds
/// control characters, which the preview strips before pairing anything.</item>
/// </list>
/// BoardCardExcerptTests compares the previews of excerpts and whole texts.
/// </summary>
public static partial class BoardCardExcerpt
{
    /// <summary>Non-space characters kept before the cut: several times the 110 a tile shows.</summary>
    public const int VisibleCharacters = 600;
    /// <summary>How far past that point the cut may move to reach the end of the line.</summary>
    public const int LineAllowance = 20_000;

    /// <summary>The description itself when it is short, else the prefix described on the class.</summary>
    public static string For(string? description)
    {
        if (string.IsNullOrEmpty(description)) return description ?? "";
        if (description.Length <= VisibleCharacters || ControlCharacter().IsMatch(description)) return description;
        var visible = 0;
        var position = 0;
        while (position < description.Length && visible < VisibleCharacters)
        {
            if (Skip(description, ref position, out var placeholder)) visible += placeholder;
            else
            {
                if (!char.IsWhiteSpace(description[position])) visible++;
                position++;
            }
        }
        // Finish the line, still stepping over whole fences, which may span lines.
        var limit = position + LineAllowance;
        while (position < description.Length && description[position] != '\n')
        {
            if (position > limit) return description;
            if (!Skip(description, ref position, out _)) position++;
        }
        return position >= description.Length ? description : description[..position];
    }

    /// <summary>Steps over a closed fenced block or an attachment image, as the preview replaces them.</summary>
    private static bool Skip(string text, ref int position, out int placeholder)
    {
        placeholder = 0;
        if (string.CompareOrdinal(text, position, "```", 0, 3) == 0)
        {
            // The preview pairs fences left to right (FENCE_RE is lazy); an unclosed fence stays text.
            var close = text.IndexOf("```", position + 3, StringComparison.Ordinal);
            if (close < 0) return false;
            position = close + 3;
            placeholder = "[code]".Length;
            return true;
        }
        // FENCE_RE runs first: a fence opener inside an image caption wins over the image.
        if (text[position] == '!' && Image().Match(text, position) is { Success: true } image
            && !image.Value.Contains("```", StringComparison.Ordinal))
        {
            position += image.Length;
            placeholder = "[image]".Length;
            return true;
        }
        return false;
    }

    // The same patterns as stripControlChars and IMAGE_RE in board-text.js.
    [GeneratedRegex("[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlCharacter();
    [GeneratedRegex(@"\G!\[[^\]\n]*\]\(attachment:[A-Za-z0-9_-]+\)")]
    private static partial Regex Image();
}
