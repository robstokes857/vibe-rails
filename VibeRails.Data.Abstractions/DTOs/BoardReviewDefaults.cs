namespace VibeRails.DTOs;

/// <summary>The local new-board review recipe. Existing recipes and user edits are never refreshed.</summary>
public static class BoardReviewDefaults
{
    public const string RecipeId = "viberails.board.switch-reviewer.v1";
    public const string ReviewScope = "unpushed";
    public const string Name = "Switch reviewer";
    public const string Description = "Code quality and VCA check unpushed commits, then the selected reviewer reviews the card. Choose the reviewer and edit provider mappings in Lane agents. Saves Checks evidence and a Code review report on the card. The agent follows your Board instructions to choose the next action.";
    public const string Prompt = """
        Review the originating card's code in the stated project checkout. Read its description, comments,
        linked commits and explicit coding attribution. Use get_board_reviews to inspect earlier reviews;
        poll pending reviews and follow the severity and follow-up policy in your launch instructions.
        Use read_board_check to inspect saved Code quality and VCA evidence, including scope,
        coverage, failures and limitations.
        Review the intended changes and tests; record actionable findings with file/line references and
        publish the canonical Code review report using the Board review tools. A successful process exit
        is not review approval. Save your evidence and handoff in the card before choosing the next action.
        Read the lane context and the user's Board workflow instructions. Humans and agents choose card
        movement through MCP; lane names and the meaning of Done are user-defined. Before moving, call
        list_board_columns to inspect the destination's Automations. Report completion with complete_board_agent.
        """;
}
