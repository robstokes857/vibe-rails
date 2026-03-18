namespace VibeRails.Services.Terminal;

internal static class TerminalReplayPolicy
{
    public static bool ShouldUseRedrawAttach(string? cliName, bool syncOutputActive)
    {
        if (syncOutputActive)
            return true;

        return NormalizeCli(cliName) switch
        {
            "codex" => true,
            _ => false
        };
    }

    public static string DescribeReason(string? cliName, bool syncOutputActive)
    {
        if (syncOutputActive)
            return "synchronized-output-active";

        return NormalizeCli(cliName) switch
        {
            "codex" => "codex-redraw-fallback",
            _ => "replay"
        };
    }

    private static string NormalizeCli(string? cliName)
        => cliName?.Trim().ToLowerInvariant() ?? string.Empty;
}
