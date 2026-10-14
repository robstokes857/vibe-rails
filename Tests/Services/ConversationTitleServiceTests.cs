using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.Board;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Services;

public sealed class ConversationTitleServiceTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"title-service-{Guid.NewGuid():N}.db");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string Connection => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    [Fact]
    public async Task SendsTheUserTextOnceAndKeepsTitleSeparateFromResumeSummary()
    {
        var repository = await Seed();
        await repository.SaveChatSummaryAsync(new ChatSummary { SessionId = "session", SummaryText = "Existing resume recap", Date = DateTime.UtcNow }, Ct);
        string? sent = null;
        var handler = new Handler(async (request, ct) =>
        {
            sent = await SentText(request, ct);
            return Reply("First title");
        });
        var service = Service(repository, EmptyBoard(), handler);
        await service.ProcessOneAsync(Ct);
        Assert.Equal($"{ConversationTitleClient.UserTextHeading}\n{new string('a', 300)}", sent);
        Assert.Equal("First title", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);

        // However much the user writes afterwards, the conversation has had its one request.
        for (var message = 2; message <= 12; message++)
            await repository.InsertUserInputAsync("session", message, new string('b', 500), null);
        Scalar("UPDATE Sessions SET EndedUTC='2026-10-12T13:00:00.0000000Z'");
        await service.ProcessOneAsync(Ct);
        Assert.Equal(1, handler.Count);
        Assert.Equal("First title", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
        Assert.Equal("Existing resume recap", Assert.Single((await repository.GetChatSummariesBySessionAsync("session", Ct))!).SummaryText);
    }

    [Fact]
    public async Task UserTextFollowsInputOrderAndLeavesOutBareCommands()
    {
        var repository = new Repository(Connection);
        await repository.CreateSessionAsync("session", "claude", null, ".", 1);
        await repository.InsertUserInputAsync("session", 1, "/model", null);
        await repository.InsertUserInputAsync("session", 2, "  Fix the login redirect  ", null);
        await repository.InsertUserInputAsync("session", 3, "/clear", null);
        await repository.InsertUserInputAsync("session", 4, "/review the auth module", null);
        string? sent = null;
        var handler = new Handler(async (request, ct) =>
        {
            sent = await SentText(request, ct);
            return Reply("Fix login redirect");
        });

        await Service(repository, EmptyBoard(), handler).ProcessOneAsync(Ct);

        Assert.Equal($"{ConversationTitleClient.UserTextHeading}\nFix the login redirect\n/review the auth module", sent);
    }

    [Fact]
    public void CommandsAreKeptWhenTheyAreAllTheUserWrote()
    {
        UserInputRecord Input(int sequence, string text) => new(sequence, "session", sequence, text, null, DateTime.UtcNow);
        Assert.Equal("/login\n/model", ConversationTitleService.UserText([Input(2, "/model"), Input(1, "/login")]));
        Assert.Equal("", ConversationTitleService.UserText([Input(1, "   ")]));
    }

    [Fact]
    public async Task BoardLinkedConversationIsTitledWithItsStoryAsBackground()
    {
        var repository = await Seed();
        var board = new Mock<IBoardStore>();
        board.Setup(x => x.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BoardSessionCard("session", "card", "VB-M66G2-64", "Better filters", "VIBE-7")]);
        board.Setup(x => x.FindLocalCardAsync("card", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Card("Better filters", "Filter chat history by card and provider."));
        string? sent = null;
        var handler = new Handler(async (request, ct) =>
        {
            sent = await SentText(request, ct);
            return Reply("Add provider filter to history");
        });

        await Service(repository, board.Object, handler).ProcessOneAsync(Ct);

        Assert.Equal($"""
            {ConversationTitleClient.StoryHeading}
            VIBE-7: Better filters
            Filter chat history by card and provider.

            {ConversationTitleClient.UserTextHeading}
            {new string('a', 300)}
            """.ReplaceLineEndings("\n"), sent);
        Assert.Equal("Add provider filter to history", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
        Assert.True((await repository.GetChatHistoryItemAsync("session", Ct))!.SessionTitleGenerated);
    }

    [Fact]
    public async Task BoardReadFailureStillTitlesWithoutTheStory()
    {
        var repository = await Seed();
        var board = new Mock<IBoardStore>();
        board.Setup(x => x.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SqliteException("board.db is busy", 5));
        string? sent = null;
        var handler = new Handler(async (request, ct) =>
        {
            sent = await SentText(request, ct);
            return Reply("Valid title");
        });

        await Service(repository, board.Object, handler).ProcessOneAsync(Ct);

        Assert.StartsWith(ConversationTitleClient.UserTextHeading, sent);
        Assert.Equal("Valid title", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
    }

    [Fact]
    public async Task FallbackTitleSpendsTheOneRequestWithoutNamingTheConversation()
    {
        var repository = await Seed();
        var handler = new Handler((_, _) => Task.FromResult(Reply("New conversation")));

        await Service(repository, EmptyBoard(), handler).ProcessOneAsync(Ct);
        Scalar("UPDATE ConversationTitleRequests SET RetryAfterUTC='2000-01-01T00:00:00.0000000Z'");
        await Service(repository, EmptyBoard(), handler).ProcessOneAsync(Ct);

        Assert.Equal(1, handler.Count);
        Assert.Equal("", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
        Assert.Equal(1L, Scalar("SELECT Cancelled FROM ConversationTitleRequests"));
    }

    [Fact]
    public async Task ManualRenameDuringRemoteCallWins()
    {
        var repository = await Seed();
        var handler = new Handler(async (_, _) =>
        {
            await repository.SetSessionDisplayNameAsync("session", "My chosen name");
            return Reply("Late title");
        });
        await Service(repository, EmptyBoard(), handler).ProcessOneAsync(Ct);
        Assert.Equal("My chosen name", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
    }

    [Fact]
    public async Task DisabledSettingsDoNotClaimOrReadInput()
    {
        await Seed();
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        await Service(new Mock<IUserInputStore>(MockBehavior.Strict).Object, EmptyBoard(), handler,
            new(false, "synthetic-key")).ProcessOneAsync(Ct);
        Assert.Equal(0, handler.Count);
        var work = Assert.IsType<ConversationTitleWork>(await new ConversationTitleStore(Connection).ClaimAsync(DateTime.UtcNow, Ct));
        Assert.Equal(1, work.Attempts);
    }

    [Fact]
    public async Task RejectedTitleNeverPausesOtherConversations()
    {
        var repository = await Seed();
        await repository.CreateSessionAsync("other", "codex", null, ".", 1);
        await repository.InsertUserInputAsync("other", 1, new string('o', 300), null);
        var handler = new Handler((_, _) => Task.FromResult(Reply("password=hunter2")));

        await Service(repository, EmptyBoard(), handler).ProcessOneAsync(Ct);

        Assert.Equal(1, handler.Count);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ConversationTitleApiBackoff"));
        var next = await new ConversationTitleStore(Connection).ClaimAsync(DateTime.UtcNow, Ct, Fingerprint);
        Assert.Equal("other", next?.SessionId);
    }

    [Fact]
    public async Task ConversationThatKeepsFailingStopsAsking()
    {
        var repository = await Seed();
        var handler = new Handler((_, _) => Task.FromResult(Reply("password=hunter2")));
        var service = Service(repository, EmptyBoard(), handler);

        for (var attempt = 0; attempt < ConversationTitleService.MaxSessionAttempts + 2; attempt++)
        {
            await service.ProcessOneAsync(Ct);
            Scalar("UPDATE ConversationTitleRequests SET RetryAfterUTC='2000-01-01T00:00:00.0000000Z'");
        }

        Assert.Equal(ConversationTitleService.MaxSessionAttempts, handler.Count);
        Assert.Equal(1L, Scalar("SELECT Cancelled FROM ConversationTitleRequests WHERE SessionId='session'"));
    }

    [Fact]
    public async Task KeyWideFailurePausesEveryConversation()
    {
        var repository = await Seed();
        await repository.CreateSessionAsync("other", "codex", null, ".", 1);
        await repository.InsertUserInputAsync("other", 1, new string('o', 300), null);
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));

        await Service(repository, EmptyBoard(), handler).ProcessOneAsync(Ct);

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ConversationTitleApiBackoff"));
        Assert.Null(await new ConversationTitleStore(Connection).ClaimAsync(DateTime.UtcNow, Ct, Fingerprint));
    }

    private static string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-key")));

    private object? Scalar(string sql)
    {
        using var db = new SqliteConnection(Connection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private async Task<Repository> Seed()
    {
        var repository = new Repository(Connection);
        await repository.CreateSessionAsync("session", "codex", null, ".", 1);
        await repository.InsertUserInputAsync("session", 1, new string('a', 300), null);
        return repository;
    }

    private ConversationTitleService Service(IUserInputStore inputs, IBoardStore board,
        Handler handler, ConversationTitleSettings? settings = null, IAppEventBus? events = null) => new(new ConversationTitleStore(Connection),
            inputs, board, new ConversationTitleClient(new HttpClient(handler), () => settings ?? new(true, "synthetic-key")),
            events ?? Mock.Of<IAppEventBus>());

    private static IBoardStore EmptyBoard() => Mock.Of<IBoardStore>(x =>
        x.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<BoardSessionCard>>(Array.Empty<BoardSessionCard>()));

    private static BoardCardRecord Card(string title, string description) => new("card", ".", 64, "todo", 0, title, description,
        null, "medium", null, [], false, 0, DateTime.UtcNow, DateTime.UtcNow);

    private static async Task<string?> SentText(HttpRequestMessage request, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("text").GetString();
    }

    private static HttpResponseMessage Reply(string title) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"title\":\"" + title + "\"}")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return send(request, cancellationToken);
        }
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }
}
