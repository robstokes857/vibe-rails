using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    [McpServerTool, Description("Recheck only this card's ghost after linked code has merged. Local or remote-tracking default-branch commits count, including unpushed local commits; a PR must be merged into this repository's default branch. No recurring monitoring is started. Unavailable verification keeps the existing marker. " + GhostGuidance)]
    public async Task<string> RecheckBoardGhost(
        [Description(CardArgumentHelp)] string? card = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            var result = await service.RecheckGhostAsync(target.Project, target.CardId!, cancellationToken);
            return result?.Message ?? $"FAIL: card not found: {card}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("recheck the card's ghost", ex); }
    }
}
