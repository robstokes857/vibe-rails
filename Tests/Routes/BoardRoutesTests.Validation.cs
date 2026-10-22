using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Routes;

public sealed partial class BoardRoutesTests
{
    [Fact]
    public async Task ManualSessionLinksRejectForeignAndUnknownRecordingsBeforeWriting()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var card = await store.CreateCardAsync(_project, new(null, "Session scope", "", null, "medium", null, [], false), ct);
        var foreign = Guid.NewGuid().ToString();
        var local = Guid.NewGuid().ToString();
        await SeedRecordedSessionAsync(foreign, _project + "-other");
        await SeedRecordedSessionAsync(local, _project + Path.DirectorySeparatorChar);
        var path = $"/api/v1/board/cards/{card.Id}/sessions";
        foreach (var credentials in new[] { (Session: (string?)null, Tab: (string?)null), ("test-session", null), (null, "test-tab") })
        {
            using var denied = await SendAsync(HttpMethod.Post, path, credentials.Session, credentials.Tab, new AddBoardSessionRequest(local, ""));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        foreach (var id in new[] { foreign, Guid.NewGuid().ToString(), "" })
        {
            using var rejected = await PostJsonAsync(path, new { id });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        Assert.Empty((await store.GetCardDetailAsync(_project, card.Id, ct))!.Sessions);
        using var accepted = await PostJsonAsync(path, new { id = local });
        accepted.EnsureSuccessStatusCode();
        Assert.Equal(local, Assert.Single((await store.GetCardDetailAsync(_project, card.Id, ct))!.Sessions).SessionId);

        await store.EnsureDefaultColumnsAsync(_project + "-other", ct);
        var outside = await store.CreateCardAsync(_project + "-other", new(null, "Foreign", "", null, "medium", null, [], false), ct);
        using var missingCard = await PostJsonAsync($"/api/v1/board/cards/{outside.Id}/sessions", new { id = local });
        Assert.Equal(HttpStatusCode.NotFound, missingCard.StatusCode);
    }

    [Fact]
    public async Task UnexpectedOperationFailuresDoNotExposeExceptionText()
    {
        var commits = Mock.Get(_app.Services.GetRequiredService<IBoardCommitService>());
        commits.Setup(c => c.DescribeAsync(It.IsAny<string>(), "deadbee", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Private path C:\\secrets\\state.db; token=do-not-return"));
        using var created = await PostJsonAsync("/api/v1/board/cards", new { title = "Error handling" });
        created.EnsureSuccessStatusCode();
        using var failed = await PostJsonAsync("/api/v1/board/cards/PROJ-1/commits", new { sha = "deadbee" });
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        using var body = await ReadJsonAsync(failed);
        Assert.Equal("Could not complete the Board request. Try again or check the application log.", body.RootElement.GetProperty("error").GetString());
    }

    private async Task SeedRecordedSessionAsync(string id, string directory)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlStrings.CreateSessionsTable;
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "INSERT INTO Sessions (Id, Cli, WorkingDirectory, StartedUTC) VALUES ($id, 'codex', $directory, $started);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$directory", directory);
        command.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }
}
