namespace VibeRails.Services.Terminal;

/// <summary>Optional Board tracking guidance shared by base and saved-environment LLM launches.</summary>
internal static class VibeStoryTrackingPrompt
{
    private const string Guidance =
        "VibeRails: When useful, consider creating a Vibe Story (Board card) with create_board_card to track work requested in this terminal, including one-off changes started outside the Board. " +
        "Creating a story is optional and at your discretion. Reuse a story already tracking this work instead of creating a duplicate. " +
        "If you choose to track it, record progress and results with add_board_comment and link this terminal with attach_board_session as needed. " +
        "Follow existing Board instructions and tool approval rules. This guidance alone is not a task; if no user task has been provided, wait for the user's request.";

    internal static string Compose(string? prompt) => string.IsNullOrWhiteSpace(prompt)
        ? Guidance
        : Guidance + "\n\n" + prompt;
}
