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
    /// Trims and caps at <see cref="MaxLength"/> without splitting a surrogate pair (a dangling
    /// high surrogate would render as a replacement char). A null value normalizes to empty.
    /// </summary>
    public static string Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
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
