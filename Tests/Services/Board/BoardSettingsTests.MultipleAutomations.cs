using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    private async Task ExecuteSql(string sql)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; " + sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private Task RemoveBoard7() => ExecuteSql("""
        DROP TRIGGER BoardCards_Workflow_Insert;
        DROP TRIGGER BoardCards_Workflow_Move;
        DROP TRIGGER BoardPendingAutomations_Workflow;
        DROP TRIGGER BoardPendingAdditionalAutomations_Workflow;
        DROP TRIGGER IF EXISTS BoardLaneAutomations_Workflow_UPDATE;
        DROP TRIGGER IF EXISTS BoardLaneAutomations_Workflow_DELETE;
        DROP TABLE BoardLaneStepReports;
        DROP TABLE BoardLaneWorkflowSteps;
        DROP TABLE BoardLaneWorkflows;
        DELETE FROM SchemaMigrations WHERE Component = 'board-lane-workflow';
        DROP TRIGGER BoardPendingAutomations_RecordCancellation;
        DROP TRIGGER BoardPendingAdditionalAutomations_RecordCancellation;
        DROP TABLE BoardLaneAutomationDispatch;
        DELETE FROM SchemaMigrations WHERE Component = 'board-lane-dispatch';
        DROP TRIGGER BoardCards_AdditionalLaneAutomation_Insert;
        DROP TRIGGER BoardCards_AdditionalLaneAutomation_Move;
        DROP TRIGGER BoardLaneAutomations_ClearAdditional;
        DROP TABLE BoardPendingAdditionalAutomations;
        DROP TABLE BoardLaneAdditionalAutomations;
        DELETE FROM SchemaMigrations WHERE Component = 'board' AND Version = 7;
        """);

    [Fact]
    public async Task MultipleSelections_PersistAndValidateTheEntireListBeforeChangingAnything()
    {
        var (_, a, _, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        var foreign = await Job("Foreign", _root + "-other");
        var saved = await _boards.SaveLaneAutomationAsync(_root, a, [second.Id, first.Id], 0, Ct);
        Assert.Equal(new[] { second.Id, first.Id }, saved!.JobIds);
        Assert.Equal(saved.JobIds, (await new BoardStore(_connectionString, _stateConnectionString).GetLaneAutomationAsync(_root, a, Ct))!.JobIds);
        var card = await Card(a);
        var due = await Due(card.Id);
        foreach (var invalid in new long[][] { [first.Id, foreign.Id], [first.Id, first.Id], [first.Id, 0], [first.Id, 99999] })
            await Assert.ThrowsAsync<BoardValidationException>(() => _boards.SaveLaneAutomationAsync(_root, a, invalid, 1, Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() => _boards.SaveLaneAutomationAsync(_root, a, [first.Id], 0, Ct));
        Assert.Equal(saved.JobIds, (await _boards.GetLaneAutomationAsync(_root, a, Ct))!.JobIds);
        Assert.Equal(due, await Due(card.Id));
        var run = Assert.Single(await Tick(due));
        Assert.Equal(second.Id, (await _jobs.GetRunAsync(run, Ct))!.JobId);
        Assert.Single(await _boards.GetPendingLaneAutomationsAsync(_root, card.Id, Ct));
    }

    [Fact]
    public async Task MultipleAutomations_RapidMovesAndEdits_QueueFinalSelectionOnceAcrossRoots()
    {
        var (_, a, b, c) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        var third = await Job("Third");
        await _boards.SaveLaneAutomationAsync(_root, a, [first.Id, second.Id], 0, Ct);
        await _boards.SaveLaneAutomationAsync(_root, b, [third.Id, first.Id], 0, Ct);
        var card = await Card(a);
        await _boards.MoveCardAsync(_root, card.Id, c, null, Ct);
        Assert.Equal(0, await Due(card.Id));
        await _boards.UpdateCardAsync(_root, card.Id, new(ColumnId: b), Ct);
        var due = await Due(card.Id);
        await _boards.MoveCardAsync(_root, card.Id, b, 0, Ct);
        await _boards.UpdateCardAsync(_root, card.Id, new(Title: "Still here"), Ct);
        Assert.Equal(due, await Due(card.Id));
        Assert.Empty(await Tick(due - 1));
        var roots = await Task.WhenAll(Tick(due, ReopenJobs()), Tick(due, ReopenJobs()));
        var runs = await Task.WhenAll(roots.SelectMany(ids => ids).Select(id => _jobs.GetRunAsync(id, Ct)));
        Assert.Equal(third.Id, Assert.Single(runs)!.JobId);
        Assert.All(runs, run => { Assert.Equal(JobTriggerKind.BoardLane, run!.TriggerKind); Assert.Single(run.Actions!); });
        Assert.Empty(await Tick(due + 100_000));
        await _jobs.CompleteRunAsync(runs[0]!.Id, JobRunStatus.Succeeded, 0, null, Ct);
        var next = Assert.Single(await Tick(due + 100_001));
        Assert.Equal(first.Id, (await _jobs.GetRunAsync(next, Ct))!.JobId);
        Assert.Equal(0, await Due(card.Id));
    }

    [Theory]
    [InlineData("disabled", false)]
    [InlineData("disabled", true)]
    [InlineData("deleted", false)]
    [InlineData("deleted", true)]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("overlap", false)]
    [InlineData("overlap", true)]
    public async Task UnavailableAutomationIsSkipped_AndBusyAutomationWaitsForUserSkip(string reason, bool skipFirst)
    {
        var (_, a, _, _) = await Lanes();
        var skipped = await Job("Skipped");
        var valid = await Job("Valid");
        await _boards.SaveLaneAutomationAsync(_root, a, skipFirst ? [skipped.Id, valid.Id] : [valid.Id, skipped.Id], 0, Ct);
        var card = await Card(a);
        var due = await Due(card.Id);
        if (reason == "disabled")
            await _jobs.UpdateJobAsync(skipped.Id, new(skipped.Name, _root, LLM.NotSet, null, "", null, false, []), Ct);
        if (reason == "deleted") await _jobs.SoftDeleteJobAsync(skipped.Id, Ct);
        if (reason == "missing")
        {
            await using var state = new SqliteConnection(_stateConnectionString);
            await state.OpenAsync(Ct);
            await using var delete = state.CreateCommand();
            delete.CommandText = "DELETE FROM Jobs WHERE Id = $id;";
            delete.Parameters.AddWithValue("$id", skipped.Id);
            await delete.ExecuteNonQueryAsync(Ct);
        }
        if (reason == "overlap") Assert.NotNull(await _jobs.EnqueueManualRunAsync(skipped.Id, Ct));
        var initial = await Tick(due);
        if (skipFirst) Assert.Empty(initial);
        else
        {
            var validRun = Assert.Single(initial);
            Assert.Equal(valid.Id, (await _jobs.GetRunAsync(validRun, Ct))!.JobId);
            await _jobs.CompleteRunAsync(validRun, JobRunStatus.Succeeded, 0, null, Ct);
            Assert.Empty(await Tick(due + 1));
        }
        var entry = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).Single(e => e.JobId == skipped.Id);
        var service = new BoardCardAutomationService(_boards, _jobs, Moq.Mock.Of<VibeRails.Services.Jobs.IJobService>());
        if (reason == "overlap")
            await service.SkipAsync(_root, card.Id, skipped.Id, entry.EventKey, Ct);
        else
        {
            Assert.Equal("Skipped", entry.StepStatus);
            Assert.False(entry.CanSkip);
        }
        if (skipFirst) Assert.Equal(valid.Id, (await _jobs.GetRunAsync(Assert.Single(await Tick(due + 2)), Ct))!.JobId);
        else Assert.Empty(await Tick(due + 2));

    }

    [Theory]
    [InlineData("save")]
    [InlineData("clear")]
    [InlineData("delete-card")]
    [InlineData("delete-board")]
    public async Task SettingChangesAndDeletion_CancelEveryPendingAutomation(string operation)
    {
        var (board, a, _, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        await _boards.SaveLaneAutomationAsync(_root, a, [first.Id, second.Id], 0, Ct);
        var card = await Card(a);
        var due = await Due(card.Id);
        if (operation == "save") await _boards.SaveLaneAutomationAsync(_root, a, [second.Id, first.Id], 1, Ct);
        if (operation == "clear") await _boards.SaveLaneAutomationAsync(_root, a, [], 1, Ct);
        if (operation == "delete-card") await _boards.DeleteCardAsync(_root, card.Id, Ct);
        if (operation == "delete-board")
        {
            await _boards.CreateBoardAsync(_root, "Other", Ct);
            await _boards.DeleteBoardAsync(_root, board, Ct);
        }
        Assert.Equal(0, await Due(card.Id));
        Assert.Empty(await Tick(due));
        if (operation == "save")
        {
            var next = await Card(a);
            Assert.Single(await Tick(await Due(next.Id)));
        }
    }

    [Fact]
    public async Task SchedulerBatchLimit_DoesNotDiscardRemainingAutomationsForTheSameCard()
    {
        var (_, a, _, _) = await Lanes();
        var jobs = new List<long>();
        for (var index = 0; index < 103; index++) jobs.Add((await Job($"Job {index}")).Id);
        await _boards.SaveLaneAutomationAsync(_root, a, jobs, 0, Ct);
        var card = await Card(a);
        var due = await Due(card.Id);
        var runs = new List<string>();
        foreach (var job in jobs)
        {
            var run = Assert.Single(await Tick(due + 1));
            Assert.Equal(job, (await _jobs.GetRunAsync(run, Ct))!.JobId);
            runs.Add(run);
            await _jobs.CompleteRunAsync(run, JobRunStatus.Succeeded, 0, null, Ct);
        }
        Assert.Equal(103, runs.Distinct().Count());
        Assert.Equal(0, await Due(card.Id));
        Assert.Empty(await Tick(due + 2));

    }

    [Fact]
    public async Task Board7Upgrade_PreservesSingleSelectionsAndPendingEntries_AndLegacyWritersRemainCompatible()
    {
        var (_, a, b, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        await _boards.SaveLaneAutomationAsync(_root, a, [first.Id], 0, Ct);
        var card = await Card(a);
        var due = await Due(card.Id);
        await RemoveBoard7();
        var upgraded = new BoardStore(_connectionString, _stateConnectionString);
        var setting = (await upgraded.GetLaneAutomationAsync(_root, a, Ct))!;
        Assert.Equal(new[] { first.Id }, setting.JobIds);
        Assert.Equal(1, setting.Revision);
        Assert.Equal(due, await Due(card.Id));
        await upgraded.SaveLaneAutomationAsync(_root, b, [first.Id, second.Id], 0, Ct);
        // Previous card writers still cause both new and old queues to be filled.
        await ExecuteSql($"UPDATE BoardCards SET ColumnId = '{b}' WHERE Id = '{card.Id}';");
        due = await Due(card.Id);
        // Model the previous scheduler consuming only its queue; extra jobs must survive it.
        await ExecuteSql($"DELETE FROM BoardPendingAutomations WHERE CardId = '{card.Id}';");
        Assert.Empty(await Tick(due)); // A missing old-scheduler result cannot imply a pass.
        var oldStep = (await upgraded.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).Single(e => e.JobId == first.Id && e.IsCurrent);
        await new BoardCardAutomationService(upgraded, _jobs, Moq.Mock.Of<VibeRails.Services.Jobs.IJobService>())
            .SkipAsync(_root, card.Id, first.Id, oldStep.EventKey, Ct);
        var run = (await _jobs.GetRunAsync(Assert.Single(await Tick(due)), Ct))!;
        Assert.Equal(second.Id, run.JobId);
        var next = await Card(b);
        // Previous settings editors replace the selection, advancing the same revision.
        await ExecuteSql($"""
            UPDATE BoardLaneAutomations SET JobId = {first.Id}, Revision = Revision + 1 WHERE ColumnId = '{b}';
            DELETE FROM BoardPendingAutomations WHERE ColumnId = '{b}';
            """);
        Assert.Equal(new[] { first.Id }, (await upgraded.GetLaneAutomationAsync(_root, b, Ct))!.JobIds);
        Assert.Equal(0, await Due(next.Id));
        await Assert.ThrowsAsync<BoardConflictException>(() => upgraded.SaveLaneAutomationAsync(_root, b, [], 1, Ct));
    }
}
