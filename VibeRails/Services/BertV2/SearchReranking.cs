using Microsoft.Extensions.Logging;

namespace VibeRails.Services.BertV2;

/// <summary>
/// Reorders the top hybrid candidates by cross-encoder relevance (VIBE-257). Any failure, including
/// a missing model, keeps the hybrid order, so keyword and vector search work without the re-ranker.
/// </summary>
public static class SearchReranking
{
    /// <summary>How many top hybrid candidates are rescored; the rest keep their order below them.</summary>
    public const int Candidates = 30;

    /// <summary>
    /// Returns <paramref name="ranked"/> with its first <see cref="Candidates"/> items reordered by
    /// <paramref name="reranker"/>. <paramref name="boost"/> scales an item's relevance probability,
    /// which keeps a preference bounded. Equal scores keep their hybrid order.
    /// </summary>
    public static IReadOnlyList<T> Apply<T>(ISearchReranker? reranker, string query, IReadOnlyList<T> ranked,
        Func<T, string> passage, ILogger? logger, Func<T, double>? boost = null)
    {
        if (reranker is not { IsAvailable: true } || ranked.Count < 2 || string.IsNullOrWhiteSpace(query)) return ranked;
        var pool = ranked.Take(Candidates).ToArray();
        float[] scores;
        try
        {
            scores = reranker.Score(query, pool.Select(passage).ToArray());
            if (scores.Length != pool.Length) throw new InvalidOperationException("The re-ranker returned the wrong number of scores.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            logger?.LogDebug(ex, "Search re-ranking model is not installed; keeping hybrid order");
            return ranked;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Search re-ranking failed; keeping hybrid order");
            return ranked;
        }
        return pool.Select((item, index) => (item, index, score: Probability(scores[index]) * (boost?.Invoke(item) ?? 1)))
            .OrderByDescending(entry => entry.score).ThenBy(entry => entry.index)
            .Select(entry => entry.item).Concat(ranked.Skip(pool.Length)).ToArray();
    }

    private static double Probability(float logit) => 1 / (1 + Math.Exp(-logit));
}
