using System.Text;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>Budgets already-redacted messages without letting recent bookkeeping erase the original task.</summary>
internal static class ConversationTitleContext
{
    private const int MaxSampleMessages = 24;
    private const int MaxExcerptChars = 4_000;
    private const string ExcerptNotice = "Excerpts in chronological order; gaps in message numbers indicate omitted messages.\n";
    private const string Shortened = "\n[… message shortened …]\n";

    internal static string Build(IReadOnlyList<string> messages, int maxChars)
    {
        if (messages.Count == 0) return "";
        var fullLength = messages.Sum(text => (long)text.Length) + messages.Count - 1;
        if (fullLength <= maxChars) return string.Join('\n', messages);

        string Heading(int index) => $"[User message {index + 1} of {messages.Count}]\n";
        // One giant paste should not force us to sample otherwise short messages. First check
        // whether capping individual messages leaves room for the entire framed history.
        var cappedLength = ExcerptNotice.Length + 2L * (messages.Count - 1)
            + messages.Select((text, index) => (long)Heading(index).Length + Math.Min(text.Length, MaxExcerptChars)).Sum();
        var keepAll = cappedLength <= maxChars;
        var selected = keepAll ? Enumerable.Range(0, messages.Count).ToArray() : SelectMessages(messages.Count);
        var headings = selected.Select(Heading).ToArray();
        // Reserve framing first, then give each selected message an equal allowance. A huge paste
        // cannot take another message's space. Both ends of each oversized message survive.
        var framing = ExcerptNotice.Length + headings.Sum(heading => heading.Length) + 2 * (selected.Length - 1);
        var allowance = keepAll ? MaxExcerptChars : Math.Min(MaxExcerptChars, (maxChars - framing) / selected.Length);
        var builder = new StringBuilder(ExcerptNotice);
        for (var i = 0; i < selected.Length; i++)
        {
            if (i > 0) builder.Append("\n\n");
            builder.Append(headings[i]).Append(Excerpt(messages[selected[i]], allowance));
        }
        return builder.ToString();
    }

    private static int[] SelectMessages(int count)
    {
        if (count <= MaxSampleMessages) return Enumerable.Range(0, count).ToArray();
        // Keep the first two and last four, plus evenly spaced samples covering the middle.
        var selected = new SortedSet<int> { 0, 1, count - 4, count - 3, count - 2, count - 1 };
        const int middleSamples = MaxSampleMessages - 6;
        var middleCount = count - 6;
        for (var i = 0; i < middleSamples; i++)
            selected.Add(2 + (int)((long)i * (middleCount - 1) / (middleSamples - 1)));
        return selected.ToArray();
    }

    private static string Excerpt(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var available = maxChars - Shortened.Length;
        var headLength = available * 2 / 3;
        var tailStart = text.Length - (available - headLength);
        if (headLength > 0 && char.IsHighSurrogate(text[headLength - 1])) headLength--;
        if (tailStart < text.Length && char.IsLowSurrogate(text[tailStart])) tailStart++;
        return text[..headLength] + Shortened + text[tailStart..];
    }
}
