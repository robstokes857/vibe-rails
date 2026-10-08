using System.Diagnostics;
using VibeRails.Services.Board;

namespace VibeRails.Services.BertV2;

/// <summary>Bounded, fair background ingestion and inference for the existing indexing jobs.</summary>
public sealed class SearchIndexingService(ISearchIndexStore store, IBoardStore board,
    Func<SearchTextChunker> chunker, Func<IBertV2BgeEmbedder> embedder)
{
    private static readonly string[] Kinds = ["board", "input", "session"];
    /// <summary>A sweep of a very large corpus yields after this long; its durable cursor resumes on the next batch.</summary>
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InferenceBudget = TimeSpan.FromSeconds(3);

    public static SearchIndexingService Create(IServiceProvider services) => new(
        services.GetRequiredService<ISearchIndexStore>(), services.GetRequiredService<IBoardStore>(),
        () => services.GetRequiredService<SearchTextChunker>(), () => services.GetRequiredService<IBertV2BgeEmbedder>());

    /// <summary>
    /// Sweeps every source corpus once, then runs bounded inference rounds. True means work is still
    /// waiting (an unfinished sweep, or rounds cut short while they were completing items), so the
    /// caller should follow up soon instead of waiting for its idle interval.
    /// </summary>
    public async Task<bool> RunBatchAsync(CancellationToken ct, int perKind = 10)
    {
        var stopwatch = Stopwatch.StartNew();
        var sweep = await store.ReconcileAsync(board, 25, ct, SweepBudget);
        var inference = Stopwatch.StartNew();
        var completed = 0;
        var more = sweep.Incomplete;
        for (var round = 0; round < perKind; round++)
        {
            var claimed = 0;
            foreach (var kind in Kinds)
            {
                ct.ThrowIfCancellationRequested();
                var source = store.ClaimSources(kind, 1, SearchIndexVersions.Chunks).FirstOrDefault();
                if (source is not null)
                {
                    claimed++;
                    if (await Process(source, true, () => store.CompleteSource(source, chunker().Split(source.Text, source.Title, ct), SearchIndexVersions.Chunks), ct))
                        completed++;
                }
                var work = store.ClaimChunks(kind, 1, SearchIndexVersions.Model).FirstOrDefault();
                if (work is not null)
                {
                    claimed++;
                    if (await Process(work, false, () => store.CompleteChunk(work, embedder().GenerateEmbedding(work.Text), SearchIndexVersions.Model), ct))
                        completed++;
                }
            }
            if (claimed == 0) break;
            // Cut short while items were still completing: the rest of the backlog is waiting. Rounds
            // that only failed wait for their retry delay instead of asking for a follow-up.
            if (round == perKind - 1 || inference.Elapsed > InferenceBudget) { more |= completed > 0; break; }
        }
        Serilog.Log.Information("[SearchIndex] Batch completed in {Milliseconds} ms: swept={Documents} changed={Changed} finished={Completed} more={More}",
            stopwatch.ElapsedMilliseconds, sweep.Documents, sweep.Changed, completed, more);
        return more;
    }

    private async Task<bool> Process(SearchWork work, bool source, Action action, CancellationToken ct)
    {
        using var renewals = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = RenewAsync(work, source, renewals.Token);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!store.Renew(work, source)) return false;
            await Task.Run(action, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { store.Release(work, source); throw; }
        catch (Exception ex)
        {
            store.Fail(work, source, ex.Message);
            Serilog.Log.Warning(ex, "[SearchIndex] {Kind} work deferred for retry", work.Kind);
            return false;
        }
        finally
        {
            await renewals.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }

    private async Task RenewAsync(SearchWork work, bool source, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct))
            if (!store.Renew(work, source)) return;
    }
}
