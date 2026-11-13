using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using Xunit;

namespace Tests.Services.Launches;

/// <summary>VIBE-321: a card's launch runs are read through IX_LaunchRuns_Card, not a scan of every run.</summary>
public sealed class LaunchRunCardIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "viberails-launch-card-index-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(root, "launches.db");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public LaunchRunCardIndexTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task CardRunsUseTheExpressionIndexAndStillFilterByCard()
    {
        var store = new LaunchStore($"Data Source={DatabasePath};Pooling=False");
        var service = new LaunchService(store, Mock.Of<IAutomationScriptService>());
        var preset = (await service.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        var project = LaunchService.ProjectKey(root);
        var mine = await store.EnqueueAsync(new(new(project, root, "board", "card-a-1", CardId: "card_a"), Preset: preset), ct: Ct);
        await store.EnqueueAsync(new(new(project, root, "board", "card-b-1", CardId: "card_b"), Preset: preset), ct: Ct);

        Assert.Equal(mine.Id, Assert.Single(await store.GetBoardRunsAsync(project, "card_a", ct: Ct)).Id);

        using var db = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + LaunchStore.BoardRunsSql;
        foreach (var name in new[] { "$project", "$card", "$run", "$session" }) cmd.Parameters.AddWithValue(name, "x");
        cmd.Parameters.AddWithValue("$offset", 0);
        var plan = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, line => line.Contains("IX_LaunchRuns_Card", StringComparison.Ordinal));
    }
}
