using Xunit;

namespace Tests.Services.BertV2;

public sealed partial class SharedSearchIndexTests
{
    /// <summary>The backlog size the backfill job schedules by (VIBE-172) agrees with the progress counts.</summary>
    [Fact]
    public async Task CountPendingWorkMatchesProgressAndReachesZeroOnceEverythingIsEmbedded()
    {
        for (var i = 0; i < 3; i++) await Card("Pending " + i, "Some card text to index");
        await index.ReconcileAsync(board, 25, Ct);

        var swept = index.GetProgress();
        Assert.True(swept.PendingSources >= 3);
        Assert.Equal(swept.PendingSources + swept.Chunks - swept.Embedded, index.CountPendingWork());

        for (var i = 0; i < 20 && index.CountPendingWork() > 0; i++) await Worker().RunBatchAsync(Ct, 25);

        var done = index.GetProgress();
        Assert.Equal(0, index.CountPendingWork());
        Assert.Equal(0, done.PendingSources);
        Assert.Equal(done.Chunks, done.Embedded);
    }
}
