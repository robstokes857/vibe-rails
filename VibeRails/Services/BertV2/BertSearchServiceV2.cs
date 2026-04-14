using System.Diagnostics;
using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

public sealed class BertSearchServiceV2 : IBertSearchServiceV2
{
    private readonly IReadOnlyDictionary<string, IBertSearchStrategy> _strategiesByMode;
    private readonly IBertSearchDbService _searchDb;
    private readonly IBertDocumentResponseMapper _responseMapper;

    public BertSearchServiceV2(
        IEnumerable<IBertSearchStrategy> strategies,
        IBertSearchDbService searchDb,
        IBertDocumentResponseMapper responseMapper)
    {
        _searchDb = searchDb;
        _responseMapper = responseMapper;
        _strategiesByMode = BuildStrategyMap(strategies);
    }

    public BertSearchResponse Search(string query, string mode, int topK)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var normalizedMode = NormalizeMode(mode);
        var normalizedTopK = Math.Clamp(topK <= 0 ? BertSearchDefaults.DefaultTopK : topK, 1, BertSearchDefaults.MaxTopK);
        var strategy = ResolveStrategy(normalizedMode);

        var stopwatch = Stopwatch.StartNew();
        var documents = strategy.Search(query, normalizedTopK);
        stopwatch.Stop();

        var metadataByDocumentId = _searchDb.GetMetadataByDocumentIds(documents.Select(static doc => doc.DocumentId).ToArray());
        var hits = documents
            .Select(doc => _responseMapper.ToSearchHit(doc, metadataByDocumentId.GetValueOrDefault(doc.DocumentId)))
            .ToList();

        return new BertSearchResponse(
            query,
            normalizedMode,
            normalizedTopK,
            _searchDb.CountDocuments(),
            stopwatch.ElapsedMilliseconds,
            hits);
    }

    private IBertSearchStrategy ResolveStrategy(string mode)
    {
        if (_strategiesByMode.TryGetValue(mode, out var strategy))
            return strategy;

        throw new ArgumentException($"Unsupported BERT search mode '{mode}'.", nameof(mode));
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

    private static IReadOnlyDictionary<string, IBertSearchStrategy> BuildStrategyMap(IEnumerable<IBertSearchStrategy> strategies)
    {
        var strategyList = strategies.ToList();
        if (strategyList.Count == 0)
            throw new InvalidOperationException("No BERT search strategies were registered.");

        var duplicates = strategyList
            .GroupBy(strategy => strategy.Mode, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
            throw new InvalidOperationException($"Duplicate BERT search strategies registered for mode(s): {string.Join(", ", duplicates)}.");

        return strategyList.ToDictionary(strategy => strategy.Mode, StringComparer.OrdinalIgnoreCase);
    }
}
