namespace VibeRails.DTOs;

/// <summary>The originating card is part of the immutable run trigger, never a mutable job setting.</summary>
public static class JobBoardContext
{
    public const string ManualPrefix = "board-card:";
    /// <summary>Review retries retain their card while continuing to open in a native terminal.</summary>
    public const string ReviewRetryPrefix = "board-review-retry:";
    /// <summary>A deterministic lane retry keeps its entry identity and opens in a native terminal.</summary>
    public const string LaneRetryPrefix = "board-lane-retry:";

    public static bool OpensTerminalTab(JobTriggerKind kind, string triggerKey) =>
        kind == JobTriggerKind.BoardLane || kind == JobTriggerKind.Manual
            && triggerKey.StartsWith(ManualPrefix, StringComparison.Ordinal) && GetCardKey(kind, triggerKey) is not null;

    public static string? GetCardKey(JobTriggerKind kind, string triggerKey)
    {
        var prefix = kind switch
        {
            JobTriggerKind.BoardLane => "board-lane:",
            JobTriggerKind.Manual => triggerKey.StartsWith(ReviewRetryPrefix, StringComparison.Ordinal) ? ReviewRetryPrefix
                : triggerKey.StartsWith(LaneRetryPrefix, StringComparison.Ordinal) ? LaneRetryPrefix : ManualPrefix,
            _ => null
        };
        if (prefix is null || !triggerKey.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var remainder = triggerKey.AsSpan(prefix.Length);
        var separator = remainder.IndexOf(':');
        return separator > 0 && separator < remainder.Length - 1 ? remainder[..separator].ToString() : null;
    }

    /// <summary>Resolve only explicitly retained lane identity, never a card's latest run.</summary>
    public static string? GetLaneTriggerKey(JobTriggerKind kind, string triggerKey)
    {
        var card = GetCardKey(kind, triggerKey);
        if (card is null) return null;
        var parts = triggerKey.Split(':');
        if (kind == JobTriggerKind.BoardLane)
            return parts.Length == 4 && parts.All(p => p.Length > 0) ? triggerKey : null;
        return parts.Length >= 6 && parts[2] == "lane" && parts[3].Length > 0 && parts[4].Length > 0
            ? $"board-lane:{card}:{parts[3]}:{parts[4]}" : null;
    }
}
