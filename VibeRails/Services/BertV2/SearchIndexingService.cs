using System.Diagnostics;
using VibeRails.Services.Board;

namespace VibeRails.Services.BertV2;

/// <summary>Bounded, fair background ingestion and inference for the existing indexing jobs.</summary>
public sealed class SearchIndexingService(ISearchIndexStore store, IBoardStore board,
    Func<SearchTextChunker> chunker, Func<IBertV2BgeEmbedder> embedder)
{
    public static SearchIndexingService Create(IServiceProvider services) => new(
        services.GetRequiredService<ISearchIndexStore>(), services.GetRequiredService<IBoardStore>(),
        () => services.GetRequiredService<SearchTextChunker>(), () => services.GetRequiredService<IBertV2BgeEmbedder>());

    public async Task RunBatchAsync(CancellationToken ct, int perKind = 10)
    {
        var stopwatch = Stopwatch.StartNew();
        await store.ReconcileAsync(board, 25, ct);
        for (var round = 0; round < perKind; round++)
        {
            foreach (var kind in new[] { "board", "input", "session" })
            {
                ct.ThrowIfCancellationRequested();
                var source = store.ClaimSources(kind, 1, SearchIndexVersions.Chunks).FirstOrDefault();
                if (source is not null)
                    await Process(source, true, () => store.CompleteSource(source, chunker().Split(source.Text, source.Title, ct), SearchIndexVersions.Chunks), ct);
                var work = store.ClaimChunks(kind, 1, SearchIndexVersions.Model).FirstOrDefault();
                if (work is not null)
                    await Process(work, false, () => store.CompleteChunk(work, embedder().GenerateEmbedding(work.Text), SearchIndexVersions.Model), ct);
            }
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(3)) break;
        }
        Serilog.Log.Information("[SearchIndex] Batch completed in {Milliseconds} ms", stopwatch.ElapsedMilliseconds);
    }

    private async Task Process(SearchWork work, bool source, Action action, CancellationToken ct)
    {
        using var renewals = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = RenewAsync(work, source, renewals.Token);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (store.Renew(work, source)) await Task.Run(action, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { store.Release(work, source); throw; }
        catch (Exception ex)
        {
            store.Fail(work, source, ex.Message);
            Serilog.Log.Warning(ex, "[SearchIndex] {Kind} work deferred for retry", work.Kind);
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
