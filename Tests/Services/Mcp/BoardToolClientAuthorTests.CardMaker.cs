using ModelContextProtocol.Protocol;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// VIBE-96: create_board_card asks the agent for its name and, when it has one, its VibeRails
/// session, and the card keeps both beside its agent mark. Run over the real MCP registration, so
/// the argument names an agent sees are the ones under test.
/// </summary>
public sealed partial class BoardToolClientAuthorTests
{
    private const string KnownSession = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string LaunchSession = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    [Fact]
    public async Task CreateBoardCard_RecordsTheAgentsName_AndAKnownSession()
    {
        await KnowSessionAsync(KnownSession, "Review worker");
        var client = await ConnectAsync("claude-code");

        var reply = await CallAsync(client, "create_board_card", new()
        {
            ["title"] = "Filed with provenance",
            ["agentName"] = " Claude\u200B Opus\n5.5 ",
            ["sessionId"] = KnownSession.ToUpperInvariant(),
        });

        var card = await CreatedCardAsync("Filed with provenance");
        Assert.True(card.AgentMade);
        Assert.Equal("Claude Opus 5.5", card.AgentMadeBy);
        Assert.Equal(KnownSession, card.AgentMadeSessionId);
        Assert.Contains($"\nMade by Claude Opus 5.5 in session {KnownSession} at {card.CreatedUtc:u}", reply);
        Assert.DoesNotContain("not recorded", reply);

        var response = (await _service.GetCardAsync(_project, card.Id, Ct))!;
        Assert.Equal("Claude Opus 5.5", response.AgentMadeBy);
        Assert.Equal(KnownSession, response.AgentMadeSessionId);
        var summary = Assert.Single((await _service.GetCardsAsync(_project, Ct)).Cards, c => c.Id == card.Id);
        Assert.Equal("Claude Opus 5.5", summary.AgentMadeBy);
        Assert.Equal(KnownSession, summary.AgentMadeSessionId);

        var read = await CallAsync(client, "get_board_card", new() { ["card"] = card.Key });
        Assert.Contains($" · Agent-made by Claude Opus 5.5 in session {KnownSession}", read);
        Assert.Contains($"Created {card.CreatedUtc:u}", read);
        Assert.Contains("agent-made by Claude Opus 5.5", await CallAsync(client, "list_board_cards", new()));

        // Provenance is written once: no edit path names or renames the maker.
        await CallAsync(client, "update_board_card", new() { ["card"] = card.Key, ["title"] = "Renamed by the agent" });
        var edited = (await _store.FindCardAsync(_project, card.Id, Ct))!;
        Assert.Equal("Claude Opus 5.5", edited.AgentMadeBy);
        Assert.Equal(KnownSession, edited.AgentMadeSessionId);
    }

    [Fact]
    public async Task CreateBoardCard_WithoutAName_UsesTheResolvedAuthor()
    {
        var client = await ConnectAsync("codex-mcp-client");

        var reply = await CallAsync(client, "create_board_card", new() { ["title"] = "Nameless", ["agentName"] = "Agent" });

        var card = await CreatedCardAsync("Nameless");
        Assert.Equal("Codex", card.AgentMadeBy);
        Assert.Null(card.AgentMadeSessionId);
        Assert.Contains($"\nMade by Codex at {card.CreatedUtc:u}", reply);
    }

    [Theory]
    [InlineData("not-a-session", "it is not a VibeRails session id (a GUID)")]
    [InlineData("11111111-2222-3333-4444-555555555555", "sessionId 11111111-2222-3333-4444-555555555555 was not recorded: VibeRails has no such session.")]
    public async Task CreateBoardCard_DropsASessionVibeRailsDoesNotKnow_AndSaysSo(string sessionId, string note)
    {
        var client = await ConnectAsync("claude-code");

        var reply = await CallAsync(client, "create_board_card", new()
        {
            ["title"] = "Guessed session", ["agentName"] = "Claude Opus 5.5", ["sessionId"] = sessionId,
        });

        Assert.StartsWith("Created ", reply);
        Assert.Contains(note, reply);
        var card = await CreatedCardAsync("Guessed session");
        Assert.Equal("Claude Opus 5.5", card.AgentMadeBy);
        Assert.Null(card.AgentMadeSessionId);
    }

    [Fact]
    public async Task CreateBoardCard_TheLaunchingSessionOutranksASuppliedOne()
    {
        await KnowSessionAsync(KnownSession, "Somebody else");
        await KnowSessionAsync(LaunchSession, "Review worker");
        _resolver.CurrentSessionId = LaunchSession;
        var client = await ConnectAsync("claude-code");

        await CallAsync(client, "create_board_card", new() { ["title"] = "From the launch", ["sessionId"] = KnownSession });

        var card = await CreatedCardAsync("From the launch");
        Assert.Equal(LaunchSession, card.AgentMadeSessionId);
        Assert.Equal("Review worker", card.AgentMadeBy);
    }

    [Fact]
    public async Task CreateBoardCard_AsksForBothOptionally()
    {
        var client = await ConnectAsync("claude-code");
        var tool = Assert.Single(await client.ListToolsAsync(cancellationToken: Ct), t => t.Name == "create_board_card");

        var properties = tool.JsonSchema.GetProperty("properties");
        Assert.Contains("model name", properties.GetProperty("agentName").GetProperty("description").GetString());
        Assert.Contains("VIBERAILS_TOOL_CURRENT_SESSION_ID", properties.GetProperty("sessionId").GetProperty("description").GetString());
        var required = tool.JsonSchema.TryGetProperty("required", out var names)
            ? names.EnumerateArray().Select(name => name.GetString()).ToList() : [];
        Assert.Equal("title", Assert.Single(required));
    }

    [Fact]
    public async Task OnlyAnAgentCardCarriesAMaker()
    {
        // The REST path is a person: a request naming a maker still makes a plain human card.
        var person = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Typed in the board",
            AgentMade: true, AgentMadeBy: "Claude Opus 5.5", AgentMadeSessionId: KnownSession), Ct, BoardAuthor.User());
        Assert.False(person.AgentMade);
        Assert.Null(person.AgentMadeBy);
        Assert.Null(person.AgentMadeSessionId);

        // The store keeps the invariant itself: no mark, no maker.
        var stored = await _store.CreateCardAsync(_project, new NewBoardCard(null, "Store only", "", null, "medium", null, [], false,
            AgentMadeBy: "Claude", AgentMadeSessionId: KnownSession), Ct, BoardAuthor.Agent("Claude", "claude", KnownSession));
        var reread = (await _store.FindCardAsync(_project, stored.Id, Ct))!;
        Assert.Null(reread.AgentMadeBy);
        Assert.Null(reread.AgentMadeSessionId);
    }

    /// <summary>A session VibeRails has recorded: linked to a card the user made.</summary>
    private async Task KnowSessionAsync(string sessionId, string displayName)
    {
        var holder = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Holder " + displayName), Ct, BoardAuthor.User());
        await _store.LinkSessionAsync(_project, holder.Id, sessionId, "tab-" + displayName, "env:7:claude", "claude", displayName,
            BoardSessionRecord.LaunchOrigin, Ct);
    }

    private async Task<BoardCardRecord> CreatedCardAsync(string title) =>
        Assert.Single(await _store.GetCardsAsync(_project, Ct), card => card.Title == title);
}
