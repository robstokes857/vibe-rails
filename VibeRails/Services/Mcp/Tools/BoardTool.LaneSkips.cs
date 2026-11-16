using System.ComponentModel;
using ModelContextProtocol.Server;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    [McpServerTool, Description("Skip one lane step (Agent or Action) on a card, with your reason, when it does not apply to this card: for example a Code quality check on a card that changes no code. Later steps carry on at once. The skip and your comment are saved together, and the card shows \"Skipped by agent\" with your reason until the user dismisses it, so only skip for a reason you would defend to the owner. A step whose run has started cannot be skipped by an agent. Find the eventKey with get_board_agent_status. Does not move the card.")]
    public async Task<string> SkipLaneStep(
        [Description("The step's eventKey (lane entry id) from get_board_agent_status or get_board_card's Lane runs.")] string eventKey,
        [Description("Why this step does not apply to this card, 20–2,000 characters. The owner reads it on the card.")] string reason,
        [Description(CardArgumentHelp)] string? card = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null)
                return target.Error;
            if (await ResolveAuthorAsync(server, cancellationToken) is not { } author)
                return UnnamedClientHint;
            var skip = await BoardAgentSkips.SkipAsync(store, launches, target.Project, target.CardId!, eventKey, reason, author, cancellationToken);
            await TrackDesktopActivityAsync(server, target.Project, target.CardId!, author.Label, cancellationToken);
            return $"Skipped \"{skip.StepName}\" on {target.CardKey}. Later steps carry on. Your reason is saved in a comment, "
                + "and the card shows \"Skipped by agent\" until the user dismisses it.";
        }
        catch (Exception ex) when (ex is BoardValidationException or BoardConflictException) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("skip the lane step", ex); }
    }
}
