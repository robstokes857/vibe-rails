namespace VibeRails.Utils;

/// <summary>
/// Length caps that never split a UTF-16 surrogate pair.
///
/// <para>C# string indexing counts code units, not characters, so <c>text[..max]</c> can cut an
/// astral character (emoji, CJK extension, many mathematical symbols) in half and leave a lone
/// surrogate at the end. A lone surrogate is not valid UTF-16: it round-trips through UTF-8 as
/// U+FFFD, and depending on the consumer it either corrupts the tail of the text or throws
/// outright. Every cap applied to user- or command-supplied text goes through here.</para>
/// </summary>
public static class TextTruncation
{
    /// <summary>
    /// Returns <paramref name="text"/> unchanged when it already fits, otherwise the longest
    /// prefix of at most <paramref name="maxChars"/> code units that ends on a whole character.
    /// The result is one code unit short of the cap when the cap would have landed mid-pair.
    /// </summary>
    public static string Truncate(string text, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);

        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;

        // A high surrogate at the last kept position owns the low surrogate we are about to
        // drop, so it has to go with it.
        var end = maxChars;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
            end--;

        return text[..end];
    }

    /// <summary>
    /// Caps <paramref name="text"/> and appends <paramref name="marker"/> when anything was
    /// dropped. The marker is outside the budget: the result can be up to
    /// <paramref name="maxChars"/> + marker length.
    /// </summary>
    public static string TruncateWithMarker(string text, int maxChars, string marker)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;

        return Truncate(text, maxChars) + marker;
    }
}
