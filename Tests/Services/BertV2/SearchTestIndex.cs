using Microsoft.ML.Tokenizers;
using VibeRails.Data.Sqlite;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;

namespace Tests.Services.BertV2;

internal static class SearchTestIndex
{
    internal static SqliteSearchIndexStore Build(IBoardStore board, string root, IBertV2BgeEmbedder? model = null)
    {
        var index = new SqliteSearchIndexStore(Path.Combine(root, "search.db"), Path.Combine(root, "absent-state.db"));
        Reconcile(index, board);
        if (model is not null) Embed(index, model);
        return index;
    }

    internal static void Reconcile(SqliteSearchIndexStore index, IBoardStore board)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var cards = 0;
        string? after = null;
        while (true)
        {
            var page = board.GetSearchDocumentsAsync(0, ct, false, after).GetAwaiter().GetResult();
            cards += page.Count;
            if (page.Count < 100) break;
            after = page[^1].Id;
        }
        for (var i = 0; i <= cards / 25; i++) index.ReconcileAsync(board, 25, ct).GetAwaiter().GetResult();
    }

    internal static void Embed(SqliteSearchIndexStore index, IBertV2BgeEmbedder model)
    {
        var chunker = new SearchTextChunker(Path.Combine(BertV2TestAssets.GetBundledModelDirectory(), "vocab.txt"));
        foreach (var kind in new[] { "board", "input", "session" })
        {
            while (index.ClaimSources(kind, 1, SearchIndexVersions.Chunks).FirstOrDefault() is { } source)
                index.CompleteSource(source, chunker.Split(source.Text, source.Title), SearchIndexVersions.Chunks);
            while (index.ClaimChunks(kind, 1, SearchIndexVersions.Model).FirstOrDefault() is { } chunk)
            {
                try { index.CompleteChunk(chunk, Expand(model.GenerateEmbedding(chunk.Text)), SearchIndexVersions.Model); }
                catch (Exception ex) { index.Fail(chunk, false, ex.Message); break; }
            }
        }
    }

    internal static float[] Expand(float[] vector)
    {
        if (vector.Length == 384) return vector;
        var expanded = new float[384];
        vector.CopyTo(expanded, 0);
        if (expanded.All(value => value == 0)) expanded[383] = 1;
        return expanded;
    }
}
