namespace VibeRails.Utils;

/// <summary>
/// One definition of a computer-name string for every caller that sends one off-machine
/// (settings payloads, push notifications, token-savings telemetry). Remote records are keyed by
/// API key + computer name, so a name truncated by one caller and sent whole by another would
/// split a single machine into two records.
/// </summary>
public static class ComputerNameFormatter
{
    public const int MaxLength = 80;

    /// <summary>
    /// Strips control characters, then trims and caps at <see cref="MaxLength"/> without splitting
    /// a surrogate pair (a dangling high surrogate would render as a replacement char). A null
    /// value normalizes to empty.
    /// </summary>
    /// <remarks>
    /// Control characters have to go before the value is stored, not just before it is displayed.
    /// <c>Trim()</c> leaves an interior one in place and <c>Uri.EscapeDataString</c> encodes it
    /// into a perfectly legal header value, but the receiving API rejects any control character
    /// with a 400 — which is not a transient status, so nothing retries and no session from this
    /// machine can be exported until the name is corrected by hand.
    /// </remarks>
    public static string Normalize(string? value)
    {
        var source = value ?? string.Empty;
        var trimmed = source.Any(char.IsControl)
            ? new string(source.Where(c => !char.IsControl(c)).ToArray()).Trim()
            : source.Trim();
        if (trimmed.Length <= MaxLength)
            return trimmed;

        var cut = char.IsHighSurrogate(trimmed[MaxLength - 1])
            ? MaxLength - 1
            : MaxLength;
        return trimmed[..cut];
    }

    /// <summary>
    /// The live machine name, normalized. Returns empty rather than throwing: this feeds
    /// display strings and background telemetry, neither of which is worth failing over.
    /// </summary>
    public static string Machine()
    {
        try
        {
            return Normalize(Environment.MachineName);
        }
        catch
        {
            return string.Empty;
        }
    }
}
