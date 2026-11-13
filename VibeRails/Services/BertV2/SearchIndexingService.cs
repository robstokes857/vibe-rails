using System.Diagnostics;
using VibeRails.Services.Board;

namespace VibeRails.Services.BertV2;

/// <summary>Bounded, fair background ingestion and inference for the existing indexing jobs.</summary>
public sealed class SearchIndexingService(ISearchIndexStore store, IBoardStore board,
    Func<SearchTextChunker> chunker, Func<IBertV2BgeEmbedder> embedder, TimeSpan? inferenceBudget = null)
{
    private static readonly string[] Kinds = ["board", "input", "session"];
    /// <summary>A sweep of a very large corpus yields after this long; its durable cursor resumes on the next batch.</summary>
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(10);
    /// <summary>A corpus swept this recently is skipped, in every process, so follow-up batches do not re-sweep it.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _inferenceBudget = inferenceBudget ?? TimeSpan.FromSeconds(3);
    /// <summary>Items claimed per kind per round, in one write transaction (VIBE-321).</summary>
    private const int ClaimBatch = 4;

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
        var sweep = await store.ReconcileAsync(board, 25, ct, SweepBudget, SweepInterval);
        var inference = Stopwatch.StartNew();
        var completed = 0;
        var more = sweep.Incomplete;
        for (var round = 0; round < perKind; round++)
        {
            var claimed = 0;
            foreach (var kind in Kinds)
            {
                ct.ThrowIfCancellationRequested();
                var sources = store.ClaimSources(kind, Batch(inference), SearchIndexVersions.Chunks);
                claimed += sources.Count;
                completed += await ProcessClaimed(sources, true, source =>
                    () => store.CompleteSource(source, chunker().Split(source.Text, source.Title, ct), SearchIndexVersions.Chunks), inference, ct);
                var chunks = store.ClaimChunks(kind, Batch(inference), SearchIndexVersions.Model);
                claimed += chunks.Count;
                completed += await ProcessClaimed(chunks, false, work =>
                    () => store.CompleteChunk(work, embedder().GenerateEmbedding(work.Text), SearchIndexVersions.Model), inference, ct);
            }
            if (claimed == 0) break;
            // Cut short while items were still completing: the rest of the backlog is waiting. Rounds
            // that only failed wait for their retry delay instead of asking for a follow-up.
            if (round == perKind - 1 || inference.Elapsed > _inferenceBudget) { more |= completed > 0; break; }
        }
        Serilog.Log.Information("[SearchIndex] Batch completed in {Milliseconds} ms: swept={Documents} changed={Changed} finished={Completed} more={More}",
            stopwatch.ElapsedMilliseconds, sweep.Documents, sweep.Changed, completed, more);
        return more;
    }

    /// <summary>Sources and chunks still waiting at the current versions: the backlog the job schedules by.</summary>
    public int CountPendingWork() => store.CountPendingWork();

    /// <summary>
    /// Reconciles the source corpora without chunking or inference (VIBE-172). Lexical search reads
    /// sources directly, so a new card stays findable while a large backlog waits for an idle machine.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var sweep = await store.ReconcileAsync(board, 25, ct, SweepBudget, SweepInterval);
        if (sweep.Documents > 0 || sweep.Incomplete)
            Serilog.Log.Information("[SearchIndex] Sweep completed in {Milliseconds} ms while the backlog waits: swept={Documents} changed={Changed} incomplete={Incomplete}",
                stopwatch.ElapsedMilliseconds, sweep.Documents, sweep.Changed, sweep.Incomplete);
    }

    /// <summary>
    /// Past the inference budget every remaining kind still gets one item per round, so one busy kind
    /// cannot starve the others; before it, one claim transaction takes several items.
    /// </summary>
    private int Batch(Stopwatch inference) => inference.Elapsed > _inferenceBudget ? 1 : ClaimBatch;

    /// <summary>
    /// Processes claimed items in order. The first always runs; the rest stop once the inference budget
    /// is spent and are released, so the next round or another root can take them without waiting out
    /// their leases.
    /// </summary>
    private async Task<int> ProcessClaimed(IReadOnlyList<SearchWork> claimed, bool source,
        Func<SearchWork, Action> action, Stopwatch inference, CancellationToken ct)
    {
        var completed = 0;
        var next = 0;
        try
        {
            while (next < claimed.Count && (next == 0 || inference.Elapsed <= _inferenceBudget))
            {
                // Counted before it runs: Process releases its own item if it is cancelled.
                var work = claimed[next++];
                if (await Process(work, source, action(work), ct)) completed++;
            }
        }
        finally
        {
            for (; next < claimed.Count; next++) store.Release(claimed[next], source);
        }
        return completed;
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
