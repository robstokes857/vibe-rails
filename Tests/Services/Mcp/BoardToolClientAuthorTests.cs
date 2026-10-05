using System.IO.Pipelines;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// VIBE-61: an agent VibeRails did not launch has no session to name it, so Board writes take the
/// name its MCP client sent in the handshake instead of the anonymous "Agent". The protocol cases
/// run the real tool registration over a stream transport (the stdio host's shape) with the
/// initialize handshake Claude Code and Codex use, against a real temporary Board store.
/// </summary>
public sealed class BoardToolClientAuthorTests : IAsyncDisposable
{
    private const string LegacyHandshake = "2025-06-18";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-author-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly string _project;
    private readonly BoardStore _store;
    private readonly BoardService _service;
    private readonly Resolver _resolver;
    private readonly CancellationTokenSource _serverStop = new();
    private ServiceProvider? _provider;
    private Task? _serverRun;
    private McpClient? _client;

    public BoardToolClientAuthorTests()
    {
        Directory.CreateDirectory(_root);
        _project = BoardStore.NormalizeProjectPath(Path.Combine(_root, "project"));
        _connectionString = $"Data Source={Path.Combine(_root, "board.db")};Pooling=False";
        _store = new BoardStore(_connectionString, $"Data Source={Path.Combine(_root, "state.db")};Pooling=False");
        _service = new BoardService(_store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        _resolver = new Resolver(_project);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The names the installed CLIs send (read from their binaries, 2026-10-05): Claude Code 2.x,
    // Codex 0.160.0, Grok's xai-grok-mcp client, Copilot CLI, Antigravity (agy) and OpenCode.
    [Theory]
    [InlineData("claude-code", "Claude Code", "Claude")]
    [InlineData("codex-mcp-client", null, "Codex")]
    [InlineData("xai-grok-cli", null, "grok")]
    [InlineData("grok-cli", null, "grok")]
    [InlineData("copilot-cli", null, "Copilot")]
    [InlineData("antigravity-client", "Google Antigravity", "Antigravity")]
    [InlineData("opencode", null, "OpenCode")]
    [InlineData("some-wrapper", "Claude Code", "Claude")]
    [InlineData("clau\u200Bde-code", null, "Claude")]
    public void ClientAuthor_NamesARecognisedCli_WithItsLogo(string name, string? title, string expected)
    {
        var author = BoardTool.ClientAuthor(new Implementation { Name = name, Title = title, Version = "1.0.0" }, "sess-1");

        Assert.NotNull(author);
        Assert.Equal(BoardAuthor.AgentKind, author.Kind);
        Assert.Equal(expected, author.Label);
        Assert.Equal(expected, author.Cli);
        Assert.Equal("sess-1", author.SessionId);
    }

    [Fact]
    public void ClientAuthor_KeepsAnotherClientsOwnName_AsOneBoundedLine()
    {
        var titled = BoardTool.ClientAuthor(new Implementation { Name = "acme-runner", Title = "Acme\u0007 Runner\nPro", Version = "1" }, null);
        Assert.Equal("Acme Runner Pro", titled!.Label);
        Assert.Null(titled.Cli);

        Assert.Equal("acme-runner", BoardTool.ClientAuthor(new Implementation { Name = " acme-runner ", Version = "1" }, null)!.Label);
        Assert.Equal("Acme Runner Pro", BoardTool.ClientAuthor(new Implementation { Name = "Acme\u2028Runner\u00A0\u2029Pro\u202E", Version = "1" }, null)!.Label);
        Assert.Equal(60, BoardTool.ClientAuthor(new Implementation { Name = new string('x', 200), Version = "1" }, null)!.Label.Length);

        // A cut never splits a surrogate pair: the emoji straddling the limit is dropped whole.
        var cut = BoardTool.ClientAuthor(new Implementation { Name = new string('x', 59) + "\U0001F600tail", Version = "1" }, null)!.Label;
        Assert.Equal(new string('x', 59), cut);
    }

    [Theory]
    [InlineData("Café Runner ☕")]
    [InlineData("日本語クライアント")]
    [InlineData("Ünïcödé \U0001F680 Tool")]
    public void ClientAuthor_KeepsLegitimateUnicodeNames(string name)
    {
        Assert.Equal(name, BoardTool.ClientAuthor(new Implementation { Name = name, Version = "1" }, null)!.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Agent")]
    [InlineData(" Agent\u00A0")]
    [InlineData("Agent\u200B session")]
    [InlineData("\u0007")]
    [InlineData("\u200B")]
    [InlineData("\u202E")]
    [InlineData("\uFEFF\u200D\u2060")]
    [InlineData("\u2028\u2029\u00A0\u3000")]
    [InlineData("\U000E0041\U000E0042")]
    public void ClientAuthor_IsNullWithoutAUsableName(string name)
    {
        Assert.Null(BoardTool.ClientAuthor(new Implementation { Name = name, Version = "1.0.0" }, null));
        Assert.Null(BoardTool.ClientAuthor(null, null));
    }

    [Theory]
    [InlineData("claude-code", "Claude")]
    [InlineData("codex-mcp-client", "Codex")]
    [InlineData("xai-grok-cli", "grok")]
    public async Task UnlaunchedAgent_IsRecordedUnderItsClientName(string clientName, string expected)
    {
        // No VibeRails session: the card that started VIBE-61 was made from a terminal like this.
        var client = await ConnectAsync(clientName);

        var created = await CallAsync(client, "create_board_card", new() { ["title"] = "Track my own work" });
        Assert.StartsWith("Created ", created);
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct));
        Assert.True(card.AgentMade);

        var comment = await CallAsync(client, "add_board_comment", new() { ["body"] = "Started", ["card"] = card.Key });
        Assert.Contains($" as {expected} at ", comment);
        var author = Assert.Single((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments).Author;
        Assert.Equal(BoardAuthor.AgentKind, author.Kind);
        Assert.Equal(expected, author.Label);
        Assert.Equal(expected, author.Cli);
        Assert.Null(author.SessionId);

        // The card's own creation entry in History names the agent too.
        var history = await _service.GetHistoryAsync(_project, card.BoardId!, card.Id, 0, Ct);
        Assert.Equal(expected, Assert.Single(history!, entry => entry.Kind == "created").Author);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("Agent")]
    [InlineData("\u200B")]
    public async Task ClientWithoutAName_IsRefused_AndNothingIsWritten(string clientName)
    {
        var client = await ConnectAsync(clientName);

        var created = await CallAsync(client, "create_board_card", new() { ["title"] = "Anonymous" });
        Assert.StartsWith("FAIL: this MCP client did not say which agent it is, so nothing was written.", created);
        Assert.Empty(await _store.GetCardsAsync(_project, Ct));

        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Typed in the board"), Ct, BoardAuthor.User());
        foreach (var (tool, arguments) in new (string, Dictionary<string, object?>)[]
                 {
                     ("add_board_comment", new() { ["body"] = "hello", ["card"] = card.Key }),
                     ("append_board_note", new() { ["body"] = "hello", ["card"] = card.Key }),
                     ("update_board_card", new() { ["card"] = card.Key, ["title"] = "Renamed" }),
                     ("move_board_card", new() { ["card"] = card.Key, ["column"] = "Review" }),
                     ("add_board_attachment", new() { ["name"] = "notes.md", ["text"] = "x", ["card"] = card.Key }),
                 })
            Assert.StartsWith("FAIL: this MCP client did not say which agent it is", await CallAsync(client, tool, arguments));

        var detail = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!;
        Assert.Equal("Typed in the board", detail.Card.Title);
        Assert.Empty(detail.Comments);
        Assert.Empty(detail.Attachments);
        Assert.Equal(card.ColumnId, detail.Card.ColumnId);
    }

    [Fact]
    public async Task LaunchingSession_OutranksTheClientName()
    {
        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Launched"), Ct, BoardAuthor.User());
        _resolver.CurrentSessionId = "sess-launch";
        await _store.LinkSessionAsync(_project, card.Id, "sess-launch", "tab-1", "env:7:claude", "claude", "Review worker",
            BoardSessionRecord.LaunchOrigin, Ct);
        var client = await ConnectAsync("claude-code");

        Assert.Contains(" as Review worker at ", await CallAsync(client, "add_board_comment", new() { ["body"] = "on it" }));
        var author = Assert.Single((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments).Author;
        Assert.Equal("Review worker", author.Label);
        Assert.Equal("sess-launch", author.SessionId);
    }

    [Fact]
    public async Task TheServerParameter_IsInNoToolSchema()
    {
        var client = await ConnectAsync("claude-code");
        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Contains(tools, tool => tool.Name == "add_board_comment");
        foreach (var tool in tools)
        {
            if (tool.JsonSchema.TryGetProperty("properties", out var properties))
                Assert.False(properties.TryGetProperty("server", out _), $"{tool.Name} exposes a server argument");
        }
    }

    private async Task<McpClient> ConnectAsync(string clientName)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBoardService>(_service);
        services.AddSingleton<IBoardProjectResolver>(_resolver);
        services.AddSingleton<IBoardStore>(_store);
        services.AddScoped<BoardTool>();
        services.AddMcpServer(options => options.ServerInfo = new() { Name = "viberails-mcp-test", Version = "1.0.0" })
            .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
            .WithVibeRailsTools();
        _provider = services.BuildServiceProvider();
        _serverRun = _provider.GetRequiredService<McpServer>().RunAsync(_serverStop.Token);

        _client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            new McpClientOptions
            {
                ClientInfo = new() { Name = clientName, Version = "1.0.0" },
                ProtocolVersion = LegacyHandshake,
            },
            cancellationToken: Ct);
        return _client;
    }

    private static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Ct);
        return Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
    }

    private sealed class Resolver(string project) : IBoardProjectResolver
    {
        public string GitWorkingDirectory => project;
        public string? CurrentSessionId { get; set; }
        public Task<string> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(project);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        await _serverStop.CancelAsync();
        if (_serverRun is not null)
        {
            try { await _serverRun; }
            catch (OperationCanceledException) { }
        }
        if (_provider is not null) await _provider.DisposeAsync();
        _serverStop.Dispose();
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
