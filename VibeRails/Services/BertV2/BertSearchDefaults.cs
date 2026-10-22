namespace VibeRails.Services.BertV2;

internal static class BertSearchDefaults
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;
    public const int DefaultTopK = 10;
    public const int MaxTopK = 50;
    public const int PreviewLength = 220;

    public const int MaxQueryLength = 1000;

    public static void ValidateQuery(string query)
    {
        // Check length before whitespace scanning, model construction or database work.
        if (query is { Length: > MaxQueryLength })
            throw new ArgumentException($"Query must be at most {MaxQueryLength} characters.", nameof(query));
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
    }
}
