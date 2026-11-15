namespace VibeRails.Services.Terminal;

/// <summary>Optional default prompt for LLM launches without an initial message or summary.</summary>
internal static class VibeStoryTrackingPrompt
{
    internal const string Guidance =
        "VibeRails: When useful, consider creating a Vibe Story (Board card) with create_board_card to track work requested in this terminal, including one-off changes started outside the Board. " +
        "Creating a story is optional and at your discretion. " +
        "If you choose to track it, record progress and results with add_board_comment and link this terminal with attach_board_session as needed. " +
        "Before committing, call validate_vca. For intentional changes blocked by STOP, request human approval using request_stop_bypass with a reason and intended commit message. " +
        "Check get_stop_bypass_status; retry only after approval, without changing the staged snapshot. Never substitute --no-verify. If these tools are missing, report the runtime/version mismatch. " +
        "Follow existing Board instructions and tool approval rules. This message is not a task. Your only reply is to tell the user you're ready to help. Don't call tools, start work, or write a request on their behalf until they send one.";
}
