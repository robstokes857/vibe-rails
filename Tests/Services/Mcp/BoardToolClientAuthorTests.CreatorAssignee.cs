using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// VIBE-185: an agent that makes a card from a VibeRails terminal is assigned it, as the picker
/// would name its LLM. Clients VibeRails did not launch keep the card unassigned unless they name
/// a provider themselves, which a bare provider name now does.
/// </summary>
public sealed partial class BoardToolClientAuthorTests
{
    private const string TerminalSession = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

    [Fact]
    public async Task CreateBoardCard_AssignsTheBoardLaunchSelection()
    {
        await LinkLaunchAsync(TerminalSession, "base:codex", "codex");
        _resolver.CurrentSessionId = TerminalSession;
        var client = await ConnectAsync("codex-mcp-client");

        var reply = await CallAsync(client, "create_board_card", new() { ["title"] = "Follow-up" });

        Assert.Equal("base:codex", (await CreatedCardAsync("Follow-up")).Assignee);
        Assert.Contains("\nAssigned to base:codex, the LLM this VibeRails terminal runs. Pass assignee=\"\" to create a card unassigned.", reply);
    }

    [Fact]
    public async Task CreateBoardCard_AssignsAnOfferedEnvironment_AndTheBareCliForAWorker()
    {
        await StateAsync("""
            INSERT INTO Environments (Id, CustomName, LLM, Hidden, AutomationWorker) VALUES (12, 'Careful Claude', 2, 0, 0);
            INSERT INTO Environments (Id, CustomName, LLM, Hidden, AutomationWorker) VALUES (35, 'codex_code_review', 1, 0, 1);
            INSERT INTO Sessions (Id, Cli, EnvironmentName) VALUES ('11111111-1111-1111-1111-111111111111', 'Claude', 'Careful Claude');
            """);
        _resolver.CurrentSessionId = "11111111-1111-1111-1111-111111111111";
        var client = await ConnectAsync("claude-code");
        await CallAsync(client, "create_board_card", new() { ["title"] = "From an environment terminal" });
        Assert.Equal("env:12:claude", (await CreatedCardAsync("From an environment terminal")).Assignee);

        // A lane Worker is hidden from the picker, so its cards go to the bare CLI it runs.
        await LinkLaunchAsync(TerminalSession, "env:35:codex", "codex");
        _resolver.CurrentSessionId = TerminalSession;
        await CallAsync(client, "create_board_card", new() { ["title"] = "From the review worker" });
        Assert.Equal("base:codex", (await CreatedCardAsync("From the review worker")).Assignee);
    }

    [Fact]
    public async Task CreateBoardCard_LeavesItUnassigned_WithoutAKnownTerminal()
    {
        var client = await ConnectAsync("claude-code");
        var reply = await CallAsync(client, "create_board_card", new() { ["title"] = "From a desktop app" });
        Assert.Null((await CreatedCardAsync("From a desktop app")).Assignee);
        Assert.DoesNotContain("Assigned to", reply);

        await StateAsync("INSERT INTO Sessions (Id, Cli, EnvironmentName) VALUES ('22222222-2222-2222-2222-222222222222', 'Shell', NULL);");
        _resolver.CurrentSessionId = "22222222-2222-2222-2222-222222222222";
        await CallAsync(client, "create_board_card", new() { ["title"] = "From a shell" });
        Assert.Null((await CreatedCardAsync("From a shell")).Assignee);
    }

    [Fact]
    public async Task CreateBoardCard_AnExplicitAssigneeWins_AndABareProviderIsShorthand()
    {
        await LinkLaunchAsync(TerminalSession, "base:codex", "codex");
        _resolver.CurrentSessionId = TerminalSession;
        var client = await ConnectAsync("codex-mcp-client");

        var unassigned = await CallAsync(client, "create_board_card", new() { ["title"] = "For later", ["assignee"] = "" });
        Assert.Null((await CreatedCardAsync("For later")).Assignee);
        Assert.DoesNotContain("Assigned to", unassigned);

        await CallAsync(client, "create_board_card", new() { ["title"] = "For Claude", ["assignee"] = " Claude " });
        var forClaude = await CreatedCardAsync("For Claude");
        Assert.Equal("base:claude", forClaude.Assignee);

        await CallAsync(client, "update_board_card", new() { ["card"] = forClaude.Key, ["assignee"] = "grok" });
        Assert.Equal("base:grok", (await _store.FindCardAsync(_project, forClaude.Id, Ct))!.Assignee);
        Assert.StartsWith("FAIL:", await CallAsync(client, "create_board_card", new() { ["title"] = "Nobody", ["assignee"] = "nobody" }));
    }

    private async Task LinkLaunchAsync(string sessionId, string selection, string cli)
    {
        var holder = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Launched for " + selection), Ct, BoardAuthor.User());
        await _store.LinkSessionAsync(_project, holder.Id, sessionId, "tab-" + selection, selection, cli, cli, BoardSessionRecord.LaunchOrigin, Ct);
    }

    /// <summary>The slice of state.db the lookup reads, in this disposable fixture.</summary>
    private async Task StateAsync(string sql)
    {
        await using var db = new SqliteConnection($"Data Source={Path.Combine(_root, "state.db")};Pooling=False");
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Sessions (Id TEXT PRIMARY KEY, Cli TEXT NOT NULL, EnvironmentName TEXT);
            CREATE TABLE IF NOT EXISTS Environments (Id INTEGER PRIMARY KEY, CustomName TEXT NOT NULL, LLM INTEGER NOT NULL,
                Hidden INTEGER NOT NULL DEFAULT 0, AutomationWorker INTEGER NOT NULL DEFAULT 0);
            """ + sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
