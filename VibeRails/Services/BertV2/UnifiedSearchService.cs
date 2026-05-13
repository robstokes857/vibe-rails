using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

/// <summary>
/// Runs three searches against one query — per-message semantic, per-session semantic,
/// and lexical (per-message LIKE) — and packages them as one response. The query
/// embedding is computed once and reused for both semantic groups.
/// </summary>
public sealed class UnifiedSearchService : IUnifiedSearchService
{
    private readonly IBertV2BgeEmbedder _embedder;
    private readonly IBertSearchDbService _searchDb;
    private readonly IBertDocumentResponseMapper _responseMapper;
    private readonly ILogger<UnifiedSearchService>? _logger;

    public UnifiedSearchService(
        IBertV2BgeEmbedder embedder,
        IBertSearchDbService searchDb,
        IBertDocumentResponseMapper responseMapper,
        ILogger<UnifiedSearchService>? logger = null)
    {
        _embedder = embedder;
        _searchDb = searchDb;
        _responseMapper = responseMapper;
        _logger = logger;
    }

    public UnifiedSearchResponse Search(string query, int topK)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var normalizedTopK = Math.Clamp(topK <= 0 ? BertSearchDefaults.DefaultTopK : topK, 1, BertSearchDefaults.MaxTopK);

        var totalSw = Stopwatch.StartNew();

        // Embedding is required for the two semantic groups, but lexical search
        // doesn't need it. If the embedder is broken (missing ONNX runtime,
        // missing model file, etc.) we still want lexical to return results
        // instead of failing the whole endpoint.
        float[]? embedding = null;
        try
        {
            embedding = _embedder.GenerateEmbedding(query);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Embedder failed for query — semantic groups will return empty; lexical will still run.");
        }

        // Each group is independent: a failure in the per-message vector search
        // (missing sqlite-vec extension, corrupted vector table, etc.) must not
        // poison the lexical or per-session groups. The route only catches
        // ArgumentException, so anything else thrown here would 500 the unified
        // endpoint — defeat the entire purpose of running three strategies in
        // parallel. SafeRunGroup swaps failures for a labelled empty group.
        var perMessageGroup = embedding is null
            ? EmptyGroup("per-message-semantic", "Per-Message Semantic", "Vector match against individual user messages. (embedder unavailable)", SafeCount(_searchDb.CountDocuments))
            : SafeRunGroup("per-message-semantic", "Per-Message Semantic", "Vector match against individual user messages.",
                _searchDb.CountDocuments, () => RunPerMessageSemantic(embedding, normalizedTopK));
        var perSessionGroup = embedding is null
            ? EmptyGroup("per-session-semantic", "Per-Session Semantic", "Vector match against full-session aggregate chunks. (embedder unavailable)", SafeCount(_searchDb.CountSessionDocuments))
            : SafeRunGroup("per-session-semantic", "Per-Session Semantic", "Vector match against full-session aggregate chunks.",
                _searchDb.CountSessionDocuments, () => RunPerSessionSemantic(embedding, normalizedTopK));
        var lexicalGroup = SafeRunGroup("lexical", "Lexical", "Literal substring match over user messages.",
            _searchDb.CountDocuments, () => RunLexical(query, normalizedTopK));
        var fusedGroup = BuildFusedGroup(perMessageGroup, perSessionGroup, lexicalGroup, normalizedTopK);

        totalSw.Stop();

        return new UnifiedSearchResponse(
            query,
            normalizedTopK,
            totalSw.ElapsedMilliseconds,
            [perMessageGroup, perSessionGroup, lexicalGroup, fusedGroup]);
    }

    private static UnifiedSearchHitGroup EmptyGroup(string key, string label, string description, int corpusSize)
        => new(key, label, description, 0, corpusSize, []);

    /// <summary>
    /// Runs <paramref name="run"/> and swaps any exception for an empty group with
    /// a "search failed" description and a best-effort corpus size. The whole
    /// unified endpoint stays available even if one strategy is broken.
    /// </summary>
    private UnifiedSearchHitGroup SafeRunGroup(
        string key,
        string label,
        string description,
        Func<int> corpusCount,
        Func<UnifiedSearchHitGroup> run)
    {
        try
        {
            return run();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Unified search group '{Key}' threw — returning empty group; other groups still served.",
                key);
            return new UnifiedSearchHitGroup(
                key,
                label,
                description + " (search failed — see server log)",
                0,
                SafeCount(corpusCount),
                []);
        }
    }

    private static int SafeCount(Func<int> count)
    {
        try { return count(); } catch { return 0; }
    }

    /// <summary>
    /// Reciprocal Rank Fusion (Cormack et al.). For each candidate that appears in any
    /// source group, score = Σ 1/(k + rank_i). Higher = better. k=60 is the standard
    /// constant. We rewrite the hit's Score to the fused value so the UI can render
    /// the same score chip.
    /// </summary>
    internal static UnifiedSearchHitGroup BuildFusedGroup(
        UnifiedSearchHitGroup perMessage,
        UnifiedSearchHitGroup perSession,
        UnifiedSearchHitGroup lexical,
        int topK)
    {
        const double k = 60.0;
        var sw = Stopwatch.StartNew();

        var rrfScores = new Dictionary<string, double>(StringComparer.Ordinal);
        var firstSeen = new Dictionary<string, BertSearchHitResponse>(StringComparer.Ordinal);
        var sourceCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var group in new[] { perMessage, perSession, lexical })
        {
            for (int rank = 0; rank < group.Hits.Count; rank++)
            {
                var hit = group.Hits[rank];
                var contribution = 1.0 / (k + (rank + 1));
                rrfScores[hit.DocumentId] = rrfScores.GetValueOrDefault(hit.DocumentId) + contribution;
                if (!firstSeen.ContainsKey(hit.DocumentId))
                    firstSeen[hit.DocumentId] = hit;
                sourceCounts[hit.DocumentId] = sourceCounts.GetValueOrDefault(hit.DocumentId) + 1;
            }
        }

        var fusedHits = rrfScores
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => sourceCounts[kv.Key])
            .Take(topK)
            .Select(kv => firstSeen[kv.Key] with { Score = kv.Value })
            .ToList();

        sw.Stop();

        return new UnifiedSearchHitGroup(
            "fused",
            "Fused (RRF)",
            "Reciprocal-rank fusion across the three groups above; documents appearing in multiple groups float to the top.",
            sw.ElapsedMilliseconds,
            firstSeen.Count,
            fusedHits);
    }

    private UnifiedSearchHitGroup RunPerMessageSemantic(float[] embedding, int topK)
    {
        var sw = Stopwatch.StartNew();
        var docs = _searchDb.SearchByEmbedding(embedding, topK);
        var meta = _searchDb.GetMetadataByDocumentIds(docs.Select(d => d.DocumentId).ToArray());
        var hits = docs
            .Select(d => _responseMapper.ToSearchHit(d, meta.GetValueOrDefault(d.DocumentId)))
            .ToList();
        sw.Stop();

        return new UnifiedSearchHitGroup(
            "per-message-semantic",
            "Per-Message Semantic",
            "Vector match against individual user messages.",
            sw.ElapsedMilliseconds,
            _searchDb.CountDocuments(),
            hits);
    }

    private UnifiedSearchHitGroup RunPerSessionSemantic(float[] embedding, int topK)
    {
        var sw = Stopwatch.StartNew();
        var docs = _searchDb.SearchSessionsByEmbedding(embedding, topK);
        var meta = _searchDb.GetSessionMetadataByDocumentIds(docs.Select(d => d.DocumentId).ToArray());
        var hits = docs
            .Select(d => _responseMapper.ToSessionSearchHit(d, meta.GetValueOrDefault(d.DocumentId)))
            .ToList();
        sw.Stop();

        return new UnifiedSearchHitGroup(
            "per-session-semantic",
            "Per-Session Semantic",
            "Vector match against full-session aggregate chunks.",
            sw.ElapsedMilliseconds,
            _searchDb.CountSessionDocuments(),
            hits);
    }

    private UnifiedSearchHitGroup RunLexical(string query, int topK)
    {
        var sw = Stopwatch.StartNew();
        var docs = _searchDb.SearchByText(query, topK);
        var meta = _searchDb.GetMetadataByDocumentIds(docs.Select(d => d.DocumentId).ToArray());
        var hits = docs
            .Select(d => _responseMapper.ToSearchHit(d, meta.GetValueOrDefault(d.DocumentId)))
            .ToList();
        sw.Stop();

        return new UnifiedSearchHitGroup(
            "lexical",
            "Lexical",
            "Literal substring match over user messages.",
            sw.ElapsedMilliseconds,
            _searchDb.CountDocuments(),
            hits);
    }
}
