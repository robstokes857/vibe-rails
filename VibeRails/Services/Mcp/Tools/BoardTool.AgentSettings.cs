using VibeRails.DTOs;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    private static void RequireBaseAssignee(string? assignee, BaseLlmOptions? options)
    {
        if (options is null) return;
        if (!BoardSelection.TryParse(assignee, out var selection) || selection is null || selection.IsEnvironment)
            throw new BoardValidationException("baseLlmOptions requires a base CLI assignee such as base:codex. Saved environments use their own launch settings.");
    }

    private static string DescribeAgentSettings(string? assignee, BaseLlmOptions? options)
    {
        if (string.IsNullOrWhiteSpace(assignee)) return "Saved agent: unassigned.";
        if (BoardSelection.TryParse(assignee, out var selection) && selection!.IsEnvironment)
            return $"Saved agent: {assignee}; uses the saved environment's launch settings.";
        return $"Saved agent: {assignee}; model={Setting(options?.Model)}; effort={Setting(options?.Effort)}; "
            + $"speed={Setting(options?.Speed)}; mode={Setting(options?.Mode)}; yolo={(options?.Yolo == true ? "true" : "false")}."
            + " Default values inherit the CLI configuration; these are saved launch settings, not a running session's effective settings.";
    }

    private static string Setting(string? value) => string.IsNullOrEmpty(value) ? "default" : value;
}
