namespace TokenSaver.Minify;

/// <summary>
/// The token saver's one user-facing knob. Each level maps to a fixed preset of minify flags,
/// condense options, and tool allowlist (<see cref="TokenSaverPresets.For"/>); the per-transform
/// Config bools are honored only at <see cref="Custom"/>, which exists as the settings.json-only
/// bisection escape hatch. Switching level can bust a provider prompt cache once — same semantics
/// as flipping an individual transform flag.
/// </summary>
public enum TokenSaverLevel
{
    /// <summary>Unrecognized or explicitly "custom": the legacy per-transform bools apply.</summary>
    Custom = 0,
    Off,
    Safest,
    Safe,
    Medium,
    High,
}

public static class TokenSaverPresets
{
    /// <summary>
    /// Claude Code's foreground shell tools — Bash everywhere, PowerShell on Windows sessions.
    /// Read/Grep are deliberately excluded at every level: the model builds Edit old_string
    /// values from their output, so touching them risks failed edits, not just lost savings.
    /// </summary>
    public static readonly IReadOnlyList<string> ShellTools = ["Bash", "PowerShell"];

    /// <summary>Safe and up: adds background-shell reads (same content class as Bash).</summary>
    public static readonly IReadOnlyList<string> ShellToolsWithBackground =
        ["Bash", "PowerShell", "BashOutput"];

    private static readonly MinifyFlags SafeFlags = MinifyFlags.Default with
    {
        CollapseBlankLineRuns = true,
    };

    /// <summary>Maps the persisted settings string to a level; anything unrecognized is Custom
    /// (fail toward the legacy per-flag behavior, never toward a lossier preset).</summary>
    public static TokenSaverLevel Normalize(string? level) =>
        (level ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "off" => TokenSaverLevel.Off,
            "safest" => TokenSaverLevel.Safest,
            "safe" => TokenSaverLevel.Safe,
            "medium" => TokenSaverLevel.Medium,
            "high" => TokenSaverLevel.High,
            _ => TokenSaverLevel.Custom,
        };

    /// <summary>The canonical settings string for a level (what the API returns to the UI).</summary>
    public static string ToSettingsString(TokenSaverLevel level) => level switch
    {
        TokenSaverLevel.Off => "off",
        TokenSaverLevel.Safest => "safest",
        TokenSaverLevel.Safe => "safe",
        TokenSaverLevel.Medium => "medium",
        TokenSaverLevel.High => "high",
        _ => "custom",
    };

    /// <summary>
    /// The preset table. <see cref="TokenSaverLevel.Off"/> resolves to all-no-op (the route builds
    /// no transform); <see cref="TokenSaverLevel.Custom"/> has no preset by definition — the
    /// settings service maps the legacy bools itself — so asking for one is a programming error.
    /// </summary>
    public static (MinifyFlags Flags, CondenseOptions Condense, IReadOnlyList<string> Allowlist)
        For(TokenSaverLevel level) => level switch
    {
        TokenSaverLevel.Off => (default, default, ShellTools),
        TokenSaverLevel.Safest => (MinifyFlags.Default, default, ShellTools),
        TokenSaverLevel.Safe => (SafeFlags, default, ShellToolsWithBackground),
        TokenSaverLevel.Medium => (
            SafeFlags,
            new CondenseOptions(DedupeConsecutiveLines: true, TruncateLongOutput: false),
            ShellToolsWithBackground),
        TokenSaverLevel.High => (
            SafeFlags,
            new CondenseOptions(DedupeConsecutiveLines: true, TruncateLongOutput: true),
            ShellToolsWithBackground),
        _ => throw new ArgumentOutOfRangeException(
            nameof(level), level, "Custom has no preset — map the per-transform settings instead."),
    };
}
