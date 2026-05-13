using System.Diagnostics;
using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

public sealed class BertSearchServiceV2 : IBertSearchServiceV2
{
    private readonly IReadOnlyDictionary<(string Mode, string Scope), IBertSearchStrategy> _strategiesByModeAndScope;
    private readonly IBertSearchDbService _searchDb;
    private readonly IBertDocumentResponseMapper _responseMapper;

    public BertSearchServiceV2(
        IEnumerable<IBertSearchStrategy> strategies,
        IBertSearchDbService searchDb,
        IBertDocumentResponseMapper responseMapper)
    {
        _searchDb = searchDb;
        _responseMapper = responseMapper;
        _strategiesByModeAndScope = BuildStrategyMap(strategies);
    }

    public BertSearchResponse Search(string query, string mode, int topK)
        => Search(query, mode, BertSearchScopes.Inputs, topK);

    public BertSearchResponse Search(string query, string mode, string scope, int topK)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var normalizedMode = NormalizeMode(mode);
        var normalizedScope = NormalizeScope(scope);
        var normalizedTopK = Math.Clamp(topK <= 0 ? BertSearchDefaults.DefaultTopK : topK, 1, BertSearchDefaults.MaxTopK);

        var stopwatch = Stopwatch.StartNew();
        var hits = normalizedScope == BertSearchScopes.Both
            ? RunCombined(query, normalizedMode, normalizedTopK)
            : RunSingleScope(query, normalizedMode, normalizedScope, normalizedTopK);
        stopwatch.Stop();

        return new BertSearchResponse(
            query,
            normalizedMode,
            normalizedScope,
            normalizedTopK,
            CountForScope(normalizedScope),
            stopwatch.ElapsedMilliseconds,
            hits);
    }

    private List<BertSearchHitResponse> RunSingleScope(string query, string mode, string scope, int topK)
    {
        var strategy = ResolveStrategy(mode, scope);
        var documents = strategy.Search(query, topK);

        if (scope == BertSearchScopes.Sessions)
        {
            var meta = _searchDb.GetSessionMetadataByDocumentIds(documents.Select(d => d.DocumentId).ToArray());
            return documents
                .Select(d => _responseMapper.ToSessionSearchHit(d, meta.GetValueOrDefault(d.DocumentId)))
                .ToList();
        }

        var inputMeta = _searchDb.GetMetadataByDocumentIds(documents.Select(d => d.DocumentId).ToArray());
        return documents
            .Select(d => _responseMapper.ToSearchHit(d, inputMeta.GetValueOrDefault(d.DocumentId)))
            .ToList();
    }

    private List<BertSearchHitResponse> RunCombined(string query, string mode, int topK)
    {
        var inputStrategy = ResolveStrategy(mode, BertSearchScopes.Inputs);
        var sessionStrategy = ResolveStrategy(mode, BertSearchScopes.Sessions);

        var inputDocs = inputStrategy.Search(query, topK);
        var sessionDocs = sessionStrategy.Search(query, topK);

        var inputMeta = _searchDb.GetMetadataByDocumentIds(inputDocs.Select(d => d.DocumentId).ToArray());
        var sessionMeta = _searchDb.GetSessionMetadataByDocumentIds(sessionDocs.Select(d => d.DocumentId).ToArray());

        var inputHits = inputDocs
            .Select(d => _responseMapper.ToSearchHit(d, inputMeta.GetValueOrDefault(d.DocumentId)));
        var sessionHits = sessionDocs
            .Select(d => _responseMapper.ToSessionSearchHit(d, sessionMeta.GetValueOrDefault(d.DocumentId)));

        // Semantic scores are comparable (cosine on the same embedder); text mode has no
        // score, so fall back to interleave-by-rank to keep both stores represented.
        if (mode == "semantic")
        {
            return inputHits
                .Concat(sessionHits)
                .OrderByDescending(h => h.Score ?? 0.0)
                .Take(topK)
                .ToList();
        }

        return InterleaveByRank(inputHits.ToList(), sessionHits.ToList(), topK);
    }

    private static List<BertSearchHitResponse> InterleaveByRank(
        IReadOnlyList<BertSearchHitResponse> a,
        IReadOnlyList<BertSearchHitResponse> b,
        int topK)
    {
        var result = new List<BertSearchHitResponse>(Math.Min(topK, a.Count + b.Count));
        var i = 0;
        var j = 0;
        while (result.Count < topK && (i < a.Count || j < b.Count))
        {
            if (i < a.Count) result.Add(a[i++]);
            if (result.Count >= topK) break;
            if (j < b.Count) result.Add(b[j++]);
        }
        return result;
    }

    private int CountForScope(string scope) => scope switch
    {
        BertSearchScopes.Sessions => _searchDb.CountSessionDocuments(),
        BertSearchScopes.Both => _searchDb.CountDocuments() + _searchDb.CountSessionDocuments(),
        _ => _searchDb.CountDocuments(),
    };

    private IBertSearchStrategy ResolveStrategy(string mode, string scope)
    {
        if (_strategiesByModeAndScope.TryGetValue((mode, scope), out var strategy))
            return strategy;

        throw new ArgumentException($"Unsupported BERT search combination mode='{mode}' scope='{scope}'.");
    }

    private static string NormalizeMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "" or null or "semantic" => "semantic",
            "text" or "keyword" => "text",
            _ => throw new ArgumentException("Mode must be 'semantic' or 'text'.", nameof(mode))
        };
    }

    private static string NormalizeScope(string? scope)
    {
        return scope?.Trim().ToLowerInvariant() switch
        {
            "" or null or "inputs" or "input" or "messages" => BertSearchScopes.Inputs,
            "sessions" or "session" => BertSearchScopes.Sessions,
            "both" or "all" or "combined" => BertSearchScopes.Both,
            _ => throw new ArgumentException("Scope must be 'inputs', 'sessions', or 'both'.", nameof(scope))
        };
    }

    private static IReadOnlyDictionary<(string, string), IBertSearchStrategy> BuildStrategyMap(IEnumerable<IBertSearchStrategy> strategies)
    {
        var strategyList = strategies.ToList();
        if (strategyList.Count == 0)
            throw new InvalidOperationException("No BERT search strategies were registered.");

        var duplicates = strategyList
            .GroupBy(s => (s.Mode.ToLowerInvariant(), s.Scope.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Item1}:{g.Key.Item2}")
            .ToArray();
        if (duplicates.Length > 0)
            throw new InvalidOperationException($"Duplicate BERT search strategies registered for: {string.Join(", ", duplicates)}.");

        return strategyList.ToDictionary(
            s => (s.Mode.ToLowerInvariant(), s.Scope.ToLowerInvariant()));
    }
}
