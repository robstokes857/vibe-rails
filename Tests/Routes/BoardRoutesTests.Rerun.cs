using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Routes;

public sealed partial class BoardRoutesTests
{
    [Fact]
    public async Task LaneRerunRequiresCredentialsAndExactFailedEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var boards = _app.Services.GetRequiredService<IBoardStore>();
        var jobs = _app.Services.GetRequiredService<IJobStore>();
        await boards.EnsureDefaultColumnsAsync(_project, ct);
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = SqlStrings.CreateEnvironmentsTable;
            await command.ExecuteNonQueryAsync(ct);
        }
        var job = await jobs.CreateJobAsync(new("Checks", _project, LLM.NotSet, null, "", null, true, [],
            Actions: [new(null, JobActionKind.Vca, Arguments: ["working-tree"])]), ct);
        var lane = (await boards.GetColumnsAsync(_project, ct))[0].Id;
        await boards.SaveLaneAutomationAsync(_project, lane, [job.Id], 0, ct);
        var card = await boards.CreateCardAsync(_project, new(lane, "Retry me", "", null, "medium", null, [], false), ct);
        var source = Assert.Single(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(2), ct));
        await jobs.CompleteRunAsync(source, JobRunStatus.Failed, 1, "Failed", ct);
        var entry = Assert.Single(await boards.GetLaneAutomationStatusesAsync(_project, card.Id, ct));
        var path = $"/api/v1/board/cards/{card.Id}/automations/rerun";
        var body = new RerunBoardCardAutomationRequest(job.Id, entry.EventKey);
        foreach (var credentials in new[] { (Session: (string?)null, Tab: (string?)null), ("test-session", null), (null, "test-tab") })
        {
            using var denied = await SendAsync(HttpMethod.Post, path, credentials.Session, credentials.Tab, body);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var invalid = await SendJsonAsync(HttpMethod.Post, path, new { jobId = job.Id, eventKey = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var stale = await SendJsonAsync(HttpMethod.Post, path, new { jobId = job.Id, eventKey = "old-entry" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var foreignProject = _project + "-foreign";
        await boards.EnsureDefaultColumnsAsync(foreignProject, ct);
        var foreign = await boards.CreateCardAsync(foreignProject, new(null, "Foreign", "", null, "medium", null, [], false), ct);
        using var outside = await SendJsonAsync(HttpMethod.Post, $"/api/v1/board/cards/{foreign.Id}/automations/rerun", body);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        using var wrongJob = await SendJsonAsync(HttpMethod.Post, path, new { jobId = job.Id + 1, eventKey = entry.EventKey });
        Assert.Equal(HttpStatusCode.Conflict, wrongJob.StatusCode);
        using var queued = await SendJsonAsync(HttpMethod.Post, path, body);
        queued.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(queued);
        var rerunId = json.RootElement.GetProperty("runId").GetString()!;
        Assert.NotEqual(source, rerunId);
        Assert.True((await jobs.GetRunAsync(rerunId, ct))!.LaunchInTerminalTab);
        using var duplicate = await SendJsonAsync(HttpMethod.Post, path, body);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var activity = await GetJsonAsync($"/api/v1/board/cards/{card.Id}/automations");
        var step = activity.RootElement.GetProperty("laneEntries")[0];
        Assert.Equal("Queued", step.GetProperty("stepStatus").GetString());
        Assert.False(step.GetProperty("canRerun").GetBoolean());
        Assert.Equal(lane, (await boards.FindCardAsync(_project, card.Id, ct))!.ColumnId);
    }
}
