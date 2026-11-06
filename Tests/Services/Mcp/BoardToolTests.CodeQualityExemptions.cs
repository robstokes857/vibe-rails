using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task QualityExemptionToolsDefaultToCardAndCanListAndRevoke()
    {
        await _tool.CreateBoardCard("Exemptions", cancellationToken: Ct);
        var card = (await _store.GetCardsAsync(_project, Ct)).Single();
        _resolver.CurrentSessionId = "quality-session";
        await _store.LinkSessionAsync(_project, card.Id, "quality-session", null, "base:codex", "codex", "Codex", BoardSessionRecord.LaunchOrigin, Ct);
        Assert.StartsWith("Exemption ", await _tool.ExemptCodeQualityFile("Tests/generated.cs", "Generated fixture", cancellationToken: Ct));
        var entry = Assert.Single(await _store.GetCodeQualityExemptionsAsync(_project, card.Id, Ct));
        Assert.Equal(card.Id, entry.CardId);
        Assert.Equal("quality-session", entry.Author.SessionId);
        var list = await _tool.ListCodeQualityExemptions(cancellationToken: Ct);
        Assert.Contains(entry.Id, list);
        Assert.Contains("Generated fixture", list);
        Assert.Contains(" · card · ", list);
        Assert.StartsWith("Revoked ", await _tool.RevokeCodeQualityExemption(entry.Id, cancellationToken: Ct));
        Assert.Contains("(none)", await _tool.ListCodeQualityExemptions(cancellationToken: Ct));
        Assert.StartsWith("FAIL:", await _tool.ExemptCodeQualityFile("a.cs", "Reason", "forever", cancellationToken: Ct));
        Assert.StartsWith("FAIL:", await _tool.ExemptCodeQualityFile("../a.cs", "Reason", cancellationToken: Ct));
    }

    [Fact]
    public async Task SharedBoardCannotChangeExemptions()
    {
        var board = await _store.ImportSharedBoardAsync(_project,
            new(Guid.NewGuid().ToString("D"), "Shared", "VB", "VB", [new("quality_shared_lane", "Todo", null, 0)], false, 0), "other-account", Ct);
        var card = (await _store.CreateSyncedCardAsync(_project, "quality_shared_card", "VB-QWERT-1",
            new("quality_shared_lane", "Shared task", "", null, "medium", null, [], false, BoardId: board.Id),
            BoardAuthor.User(), new("quality_shared_created", 1, DateTime.UtcNow, BoardId: board.Id), Ct))!;
        Assert.Contains(BoardAccess.ReadOnlyMessage, await _tool.ExemptCodeQualityFile("a.cs", "Reason", card: card.Key, cancellationToken: Ct));
        Assert.Contains(BoardAccess.ReadOnlyMessage, await _tool.RevokeCodeQualityExemption("any", card.Key, Ct));
        Assert.Empty(await _store.GetCodeQualityExemptionsAsync(_project, card.Id, Ct));
    }
}
