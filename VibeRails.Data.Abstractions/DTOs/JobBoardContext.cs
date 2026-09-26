namespace VibeRails.DTOs;

/// <summary>The originating card is part of the immutable run trigger, never a mutable job setting.</summary>
public static class JobBoardContext
{
    public const string ManualPrefix = "board-card:";

    public static bool OpensTerminalTab(JobTriggerKind kind, string triggerKey) =>
        kind == JobTriggerKind.BoardLane || GetCardKey(kind, triggerKey) is not null;

    public static string? GetCardKey(JobTriggerKind kind, string triggerKey)
    {
        var prefix = kind switch
        {
            JobTriggerKind.BoardLane => "board-lane:",
            JobTriggerKind.Manual => ManualPrefix,
            _ => null
        };
        if (prefix is null || !triggerKey.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var remainder = triggerKey.AsSpan(prefix.Length);
        var separator = remainder.IndexOf(':');
        return separator > 0 && separator < remainder.Length - 1 ? remainder[..separator].ToString() : null;
    }
}
