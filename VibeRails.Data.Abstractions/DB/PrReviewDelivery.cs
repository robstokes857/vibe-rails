namespace VibeRails.DB;

/// <summary>Machine-wide switch shared by root backends and MCP hosts.</summary>
public sealed record PrReviewerSettings(bool Enabled);

/// <summary>An immutable outbound snapshot with a separate, durable delivery receipt.</summary>
public sealed record PrReviewDelivery(long ReviewId, string Repository, int PrNumber, string HeadSha,
    string Event, string Body, string Marker, string Status, string? Login, long? GitHubId, string? Error);
