using System.Text;
using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services.VCA.Hooks;

public interface ICommitMessageCoAuthorCleaner
{
    Task<int> RemoveAsync(string? commitMessagePath, CancellationToken cancellationToken);
}

/// <summary>
/// Removes Git/GitHub <c>Co-authored-by:</c> trailers from the proposed commit message when the
/// default-on Git Guard policy is enabled. The hook edits Git's message file before any VCA rule
/// reads it, so both the policy and commit-message validation see the text Git will record.
///
/// The setting promises to remove <em>trailers</em>, so only the message's terminal trailer block is
/// searched — the shape Git itself recognizes in <c>interpret-trailers</c>. A body line that happens
/// to start with the token ("Co-authored-by: is what GitHub reads for attribution…") is prose, and
/// deleting it would silently rewrite the author's paragraph.
/// </summary>
public sealed class CommitMessageCoAuthorCleaner : ICommitMessageCoAuthorCleaner
{
    private const string TrailerToken = "Co-authored-by";

    /// <summary>Git's default <c>core.commentChar</c>. Comment lines never survive into the commit.</summary>
    private const char CommentChar = '#';

    /// <summary>
    /// The body of the line <c>git commit --verbose</c> and <c>--cleanup=scissors</c> insert above
    /// the diff they append. Git drops the line and everything after it, so nothing below it is part
    /// of the message.
    /// </summary>
    private const string ScissorsMarker = "------------------------ >8 ------------------------";

    /// <summary>
    /// Latin-1 maps every byte to exactly one char and back, so bytes this class does not delete are
    /// written back unchanged whatever <c>i18n.commitEncoding</c> is set to, and a byte-order mark
    /// rides along as ordinary leading bytes. Reading the file as text instead would re-encode the
    /// whole message as UTF-8 and drop its BOM.
    /// </summary>
    private static readonly Encoding ByteTransparentEncoding = Encoding.Latin1;

    private readonly Func<bool> _isEnabled;

    public CommitMessageCoAuthorCleaner()
        : this(ReadEnabledSetting)
    {
    }

    internal CommitMessageCoAuthorCleaner(Func<bool> isEnabled)
    {
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
    }

    public async Task<int> RemoveAsync(
        string? commitMessagePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isEnabled() ||
            string.IsNullOrWhiteSpace(commitMessagePath) ||
            !File.Exists(commitMessagePath))
        {
            return 0;
        }

        var original = await File.ReadAllBytesAsync(commitMessagePath, cancellationToken);
        if (HasWideEncodingMark(original))
        {
            // The byte-transparent round trip below holds for every ASCII-compatible encoding Git
            // can be pointed at, which is all of them in practice — but not for UTF-16/32, where it
            // would corrupt the message. Removing a trailer is not worth that risk.
            Log.Warning(
                "Skipping Co-authored-by cleanup: {Path} is UTF-16/UTF-32 encoded",
                commitMessagePath);
            return 0;
        }

        // Trailer syntax is ASCII, and an ASCII byte means the same thing in every encoding that
        // reaches this point, so the scan does not need to know which one it is reading.
        var cleaned = RemoveTrailers(ByteTransparentEncoding.GetString(original), out var removedCount);
        if (removedCount == 0)
        {
            return 0;
        }

        await File.WriteAllBytesAsync(
            commitMessagePath,
            ByteTransparentEncoding.GetBytes(cleaned),
            cancellationToken);
        return removedCount;
    }

    internal static string RemoveTrailers(string commitMessage, out int removedCount)
    {
        removedCount = 0;
        if (string.IsNullOrEmpty(commitMessage))
        {
            return commitMessage;
        }

        var lines = SplitLines(commitMessage);
        var (blockStart, blockEnd) = FindTrailerBlock(commitMessage, lines);
        if (blockStart == blockEnd)
        {
            return commitMessage;
        }

        var removed = new bool[lines.Count];
        var index = blockStart;
        while (index < blockEnd)
        {
            if (!IsCoAuthorTrailer(LineText(commitMessage, lines[index])))
            {
                index++;
                continue;
            }

            removed[index++] = true;
            removedCount++;

            // A trailer's value may wrap onto following indented lines, which Git folds back into
            // the value above. They go with the trailer they belong to, or removal would leave the
            // tail of a co-author's name stranded as a line of its own. An indented line that is
            // itself a co-author trailer is handled by the outer loop instead: the match below
            // tolerates leading whitespace, because that is how these are written in the wild.
            while (index < blockEnd)
            {
                var continuation = LineText(commitMessage, lines[index]);
                if (!IsContinuation(continuation) || IsCoAuthorTrailer(continuation))
                {
                    break;
                }

                removed[index++] = true;
            }
        }

        if (removedCount == 0)
        {
            return commitMessage;
        }

        var cleaned = new StringBuilder(commitMessage.Length);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!removed[i])
            {
                cleaned.Append(commitMessage, lines[i].Start, lines[i].End - lines[i].Start);
            }
        }

        return TrimOrphanedTrailingBlankLines(cleaned.ToString());
    }

    /// <summary>
    /// Locates the terminal trailer block as a half-open line range, or an empty range when the
    /// message has none. Modeled on Git's own <c>find_trailer_block_start</c>, conservatively: where
    /// Git will also accept a mixed block that contains a trailer it generated itself, this takes
    /// only the run of trailer lines at the end. That keeps the "🤖 Generated with …" line AI tools
    /// emit directly above their trailer from dragging the paragraph above it into scope.
    /// </summary>
    private static (int Start, int End) FindTrailerBlock(string message, List<MessageLine> lines)
    {
        var end = FindEndOfMessage(message, lines);

        // Trailing blank lines are separators, not content.
        while (end > 0 && IsBlank(LineText(message, lines[end - 1])))
        {
            end--;
        }

        if (end == 0)
        {
            return (0, 0);
        }

        // Walk back to the blank line that opens the final paragraph. Git treats the first paragraph
        // as the description, so a message without a blank line has no trailer block at all: its one
        // paragraph is the subject.
        var paragraphStart = end;
        while (paragraphStart > 0 && !IsBlank(LineText(message, lines[paragraphStart - 1])))
        {
            paragraphStart--;
        }

        if (paragraphStart == 0)
        {
            return (0, 0);
        }

        var start = end;
        while (start > paragraphStart)
        {
            var text = LineText(message, lines[start - 1]);

            // Comment lines are Git's own and are stripped before the message is recorded, so they
            // neither qualify nor disqualify the block they sit in.
            if (!IsComment(text) && !IsTrailer(text) && !IsContinuation(text))
            {
                break;
            }

            start--;
        }

        return (start, end);
    }

    /// <summary>
    /// The end of the message proper: the scissors line, if present, or the end of the file. Below
    /// the scissors sits the diff <c>git commit --verbose</c> appends, which is ordinary uncommented
    /// text and would otherwise read as the message's final paragraph.
    /// </summary>
    private static int FindEndOfMessage(string message, List<MessageLine> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var text = LineText(message, lines[i]);
            if (IsComment(text) && text[1..].Trim().SequenceEqual(ScissorsMarker))
            {
                return i;
            }
        }

        return lines.Count;
    }

    /// <summary>
    /// The tolerant match for what this policy removes: the token, optional horizontal whitespace on
    /// either side, then a colon. Leading whitespace is allowed because an indented co-author line is
    /// common in hand-edited messages, even though Git would read it as a continuation.
    /// </summary>
    private static bool IsCoAuthorTrailer(ReadOnlySpan<char> line)
    {
        line = TrimStartHorizontalWhitespace(line);
        if (!line.StartsWith(TrailerToken, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        line = TrimStartHorizontalWhitespace(line[TrailerToken.Length..]);
        return !line.IsEmpty && line[0] == ':';
    }

    /// <summary>
    /// Git's test for a trailer line: a token of letters, digits and dashes, optional horizontal
    /// whitespace, then a colon — and no leading whitespace, which is exactly what separates a
    /// trailer from a continuation of the trailer above it.
    /// </summary>
    private static bool IsTrailer(ReadOnlySpan<char> line)
    {
        var sawWhitespace = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == ':')
            {
                return i >= 1;
            }

            if (!sawWhitespace && (char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                continue;
            }

            if (i >= 1 && c is ' ' or '\t')
            {
                sawWhitespace = true;
                continue;
            }

            return false;
        }

        return false;
    }

    private static bool IsContinuation(ReadOnlySpan<char> line) =>
        !line.IsEmpty && line[0] is ' ' or '\t';

    private static bool IsComment(ReadOnlySpan<char> line) =>
        !line.IsEmpty && line[0] == CommentChar;

    private static bool IsBlank(ReadOnlySpan<char> line) => line.IsWhiteSpace();

    private static ReadOnlySpan<char> TrimStartHorizontalWhitespace(ReadOnlySpan<char> value)
    {
        var index = 0;
        while (index < value.Length && value[index] is ' ' or '\t')
        {
            index++;
        }

        return value[index..];
    }

    private static ReadOnlySpan<char> LineText(string message, MessageLine line) =>
        message.AsSpan(line.Start, line.TextEnd - line.Start);

    private static List<MessageLine> SplitLines(string message)
    {
        var lines = new List<MessageLine>();
        var start = 0;
        while (start < message.Length)
        {
            var textEnd = start;
            while (textEnd < message.Length && message[textEnd] is not '\r' and not '\n')
            {
                textEnd++;
            }

            var end = textEnd;
            if (end < message.Length && message[end] == '\r')
            {
                end++;
            }
            if (end < message.Length && message[end] == '\n')
            {
                end++;
            }

            lines.Add(new MessageLine(start, textEnd, end));
            start = end;
        }

        return lines;
    }

    // Removing a final trailer block can leave its separator as two or more line endings at EOF.
    // Keep one final line ending (Git's conventional message shape) while removing the orphaned
    // blank lines. Interior spacing and the repository's CRLF/LF convention remain untouched.
    private static string TrimOrphanedTrailingBlankLines(string value)
    {
        var end = value.Length;
        var lineEndingCount = 0;
        string? finalLineEnding = null;

        while (end > 0)
        {
            if (value[end - 1] == '\n')
            {
                var start = end - 1;
                if (start > 0 && value[start - 1] == '\r')
                {
                    start--;
                }

                finalLineEnding ??= value[start..end];
                end = start;
                lineEndingCount++;
                continue;
            }

            if (value[end - 1] == '\r')
            {
                finalLineEnding ??= "\r";
                end--;
                lineEndingCount++;
                continue;
            }

            break;
        }

        if (end == 0)
        {
            return string.Empty;
        }

        if (lineEndingCount <= 1)
        {
            return value;
        }

        return value[..end] + finalLineEnding;
    }

    private static bool HasWideEncodingMark(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 2 &&
            ((content[0] == 0xFF && content[1] == 0xFE) || (content[0] == 0xFE && content[1] == 0xFF)))
        {
            return true; // UTF-16 LE/BE, and UTF-32 LE, which opens with the same two bytes.
        }

        return content.Length >= 4 && content[0] == 0x00 && content[1] == 0x00; // UTF-32 BE.
    }

    private static bool ReadEnabledSetting() =>
        ReadEnabledSetting(static () => Config.LoadFresh().RemoveCoAuthorTrailers);

    internal static bool ReadEnabledSetting(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            // Fail closed. The documented default is on, but this is the path where the setting
            // could not be read at all — and the operation it guards edits the author's message
            // irreversibly. Skipping cleanup for one commit is recoverable; rewriting a message for
            // someone who had switched the policy off is not.
            Log.Warning(ex, "Unable to read the co-author trailer setting; skipping commit-message cleanup");
            return false;
        }
    }

    /// <summary>A line as three offsets: its text, and its text plus whatever line ending followed.</summary>
    private readonly record struct MessageLine(int Start, int TextEnd, int End);
}
