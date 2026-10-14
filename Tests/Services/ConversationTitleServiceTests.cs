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
    public async Task RebuildsFullTranscriptTwiceAndKeepsTitleSeparateFromResumeSummary()
    {
        var repository = await Seed();
        await repository.SaveChatSummaryAsync(new ChatSummary { SessionId = "session", SummaryText = "Existing resume recap", Date = DateTime.UtcNow }, Ct);
        var transcript = "User: opening\nAgent: answer";
        var transcripts = new Mock<ISessionTranscriptService>();
        transcripts.Setup(x => x.BuildCurrentAsync("session", It.IsAny<CancellationToken>())).ReturnsAsync(() => transcript);
        var requests = 0;
        var handler = new Handler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(transcript, json.RootElement.GetProperty("text").GetString());
            return Reply(++requests == 1 ? "First title" : "Updated title");
        });
        var service = Service(transcripts.Object, EmptyBoard(), handler);
        await service.ProcessOneAsync(Ct);
        Assert.Equal("First title", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
        await service.ProcessOneAsync(Ct);
        Assert.Equal(1, requests);
        transcript += "\nUser: follow-up\nAgent: later answer";
        await repository.InsertUserInputAsync("session", 2, new string('b', 450), null);
        await service.ProcessOneAsync(Ct);
        Assert.Equal("Updated title", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
        await repository.InsertUserInputAsync("session", 3, new string('c', 1000), null);
        await service.ProcessOneAsync(Ct);
        Assert.Equal(2, requests);
        Assert.Equal("Existing resume recap", Assert.Single((await repository.GetChatSummariesBySessionAsync("session", Ct))!).SummaryText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedOpeningSkipsRefreshAcrossRestartAndEndsFutureWork(bool messageMilestone)
    {
        var repository = await Seed();
        var transcript = "User: opening\nAgent: " + new string('a', 1500);
        var transcripts = new Mock<ISessionTranscriptService>();
        transcripts.Setup(x => x.BuildCurrentAsync("session", It.IsAny<CancellationToken>())).ReturnsAsync(() => transcript);
        var handler = new Handler((_, _) => Task.FromResult(Reply("First title")));
        var events = new Mock<IAppEventBus>();
        await Service(transcripts.Object, EmptyBoard(), handler, events: events.Object).ProcessOneAsync(Ct);
        var hash = Assert.IsType<string>(Scalar("SELECT LastExcerptHash FROM ConversationTitleRequests"));
        var stamp = await new ConversationTitleStore(Connection).GetChangeStampAsync(Ct);
        Assert.Equal(64, hash.Length);
        events.Invocations.Clear();

        transcript += "\nUser: follow-up\nAgent: new answer";
        if (messageMilestone)
        {
            for (var message = 2; message <= 10; message++)
                await repository.InsertUserInputAsync("session", message, "More", null);
        }
        else
            await repository.InsertUserInputAsync("session", 2, new string('b', 450), null);
        // Reconstruct both service and store, as another root/restarted process would.
        await Service(transcripts.Object, EmptyBoard(), handler, events: events.Object).ProcessOneAsync(Ct);

        Assert.Equal(1, handler.Count);
        Assert.Equal("First title", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
        Assert.True((await repository.GetChatHistoryItemAsync("session", Ct))!.SessionTitleGenerated);
        Assert.Equal(hash, Scalar("SELECT LastExcerptHash FROM ConversationTitleRequests"));
        Assert.Equal(stamp, await new ConversationTitleStore(Connection).GetChangeStampAsync(Ct));
        Assert.Empty(events.Invocations);
        await repository.InsertUserInputAsync("session", 11, new string('c', 1000), null);
        Assert.Null(await new ConversationTitleStore(Connection).ClaimAsync(DateTime.UtcNow.AddDays(1), Ct));
    }

    [Fact]
    public async Task FailedOpeningIsRetriedAndOnlyAppliedTitleSavesHash()
    {
        await Seed();
        var handler = new Handler((_, _) => Task.FromResult(Reply("password=hunter2")));
        await Service(Transcript(), EmptyBoard(), handler).ProcessOneAsync(Ct);
        Assert.Equal(DBNull.Value, Scalar("SELECT LastExcerptHash FROM ConversationTitleRequests"));
        Scalar("UPDATE ConversationTitleRequests SET RetryAfterUTC='2000-01-01T00:00:00.0000000Z'");

        var retry = new Handler((_, _) => Task.FromResult(Reply("Valid title")));
        await Service(Transcript(), EmptyBoard(), retry).ProcessOneAsync(Ct);

        Assert.Equal(1, retry.Count);
        Assert.IsType<string>(Scalar("SELECT LastExcerptHash FROM ConversationTitleRequests"));
    }

    [Fact]
    public async Task LegacyTitleWithoutHashStillRefreshes()
    {
        var repository = await Seed();
        var store = new ConversationTitleStore(Connection);
        var work = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(DateTime.UtcNow, Ct));
        await store.CompleteAsync(work, "Legacy title", DateTime.UtcNow, Ct);
        await repository.InsertUserInputAsync("session", 2, new string('b', 450), null);
        var handler = new Handler((_, _) => Task.FromResult(Reply("Refreshed title")));

        await Service(Transcript(), EmptyBoard(), handler).ProcessOneAsync(Ct);

        Assert.Equal(1, handler.Count);
        Assert.Equal("Refreshed title", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
        Assert.IsType<string>(Scalar("SELECT LastExcerptHash FROM ConversationTitleRequests"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoardAttachmentBeforeOrDuringRequestPreservesExistingLabel(bool duringRequest)
    {
        var repository = await Seed();
        var attached = !duringRequest;
        var board = new Mock<IBoardStore>();
        board.Setup(x => x.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => attached ? new[] { new BoardSessionCard("session", "card", "VB-1", "Board task") } : []);
        var handler = new Handler((_, _) =>
        {
            attached = true;
            return Task.FromResult(Reply("Late title"));
        });
        await Service(Transcript(), board.Object, handler).ProcessOneAsync(Ct);
        Assert.Equal("", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
        Assert.Equal(duringRequest ? 1 : 0, handler.Count);
        Assert.Null(await new ConversationTitleStore(Connection).ClaimAsync(DateTime.UtcNow.AddDays(1), Ct));
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
        await Service(Transcript(), EmptyBoard(), handler).ProcessOneAsync(Ct);
        Assert.Equal("My chosen name", (await repository.GetSessionDisplayInfoAsync("session")).DisplayName);
    }

    [Fact]
    public async Task DisabledSettingsDoNotClaimOrBuildTranscript()
    {
        await Seed();
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        await Service(new Mock<ISessionTranscriptService>(MockBehavior.Strict).Object, EmptyBoard(), handler,
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

        await Service(Transcript(), EmptyBoard(), handler).ProcessOneAsync(Ct);

        Assert.Equal(1, handler.Count);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ConversationTitleApiBackoff"));
        var next = await new ConversationTitleStore(Connection).ClaimAsync(DateTime.UtcNow, Ct, Fingerprint);
        Assert.Equal("other", next?.SessionId);
    }

    [Fact]
    public async Task ConversationThatKeepsFailingStopsAsking()
    {
        await Seed();
        var handler = new Handler((_, _) => Task.FromResult(Reply("password=hunter2")));
        var service = Service(Transcript(), EmptyBoard(), handler);

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

        await Service(Transcript(), EmptyBoard(), handler).ProcessOneAsync(Ct);

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

    private ConversationTitleService Service(ISessionTranscriptService transcripts, IBoardStore board,
        Handler handler, ConversationTitleSettings? settings = null, IAppEventBus? events = null) => new(new ConversationTitleStore(Connection),
            transcripts, board, new ConversationTitleClient(new HttpClient(handler), () => settings ?? new(true, "synthetic-key")),
            events ?? Mock.Of<IAppEventBus>());

    private static ISessionTranscriptService Transcript() => Mock.Of<ISessionTranscriptService>(x =>
        x.BuildCurrentAsync("session", It.IsAny<CancellationToken>()) == Task.FromResult("User: task\nAgent: response"));

    private static IBoardStore EmptyBoard() => Mock.Of<IBoardStore>(x =>
        x.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<BoardSessionCard>>(Array.Empty<BoardSessionCard>()));

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
