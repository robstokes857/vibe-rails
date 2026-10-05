using System.Text.RegularExpressions;
using VibeRails.Services.BertV2;

namespace VibeRails.Services.Board;

/// <summary>Shared read-only local Board retrieval for the UI and MCP.</summary>
public sealed partial class BoardSearchService(ISearchIndexStore store, Func<IBertV2BgeEmbedder> embedder,
    ILogger<BoardSearchService> logger)
{
    private static readonly SemaphoreSlim QueryModel = new(1, 1);
    public BoardSearchService(ISearchIndexStore store, IBertV2BgeEmbedder embedder, ILogger<BoardSearchService> logger)
        : this(store, () => embedder, logger) { }

    public static BoardSearchService Create(IServiceProvider services) => new(
        services.GetRequiredService<ISearchIndexStore>(), () => services.GetRequiredService<IBertV2BgeEmbedder>(),
        services.GetRequiredService<ILogger<BoardSearchService>>());

    /// <summary>Searches indexed local cards, filtering project eligibility before ranking and limits.</summary>
    public async Task<IReadOnlyList<BoardSearchHit>> SearchAsync(string currentProjectPath, string query,
        int count = 50, string? excludeCardId = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? excludedCardIds = null, string? projectScope = null, bool exactOnly = false)
    {
        query = (query ?? "").Trim();
        if (query.Length > 1000) throw new BoardValidationException("Card search must be at most 1000 characters.");
        count = Math.Clamp(count, 1, 50);
        var excluded = excludedCardIds?.ToHashSet(StringComparer.Ordinal) ?? [];
        if (excludeCardId is not null) excluded.Add(excludeCardId);
        var words = Words().Matches(query).Select(match => match.Value).Where(word => word.Length > 1 && !StopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        float[]? vector = null;
        ct.ThrowIfCancellationRequested();
        if (!exactOnly && query.Length > 0 && !BoardKeys.TryParseStored(query, out _) && !query.StartsWith("card_", StringComparison.Ordinal))
        {
            await QueryModel.WaitAsync(ct);
            try { vector = embedder().GenerateEmbedding(query); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Board search model unavailable; using keywords"); }
            finally { QueryModel.Release(); }
        }
        var candidates = new List<Candidate>();
        // Card-level grouping happens in SQL, so a long card cannot consume multiple result slots.
        foreach (var match in store.Search("board", query, vector, int.MaxValue, projectScope, excluded))
        {
            ct.ThrowIfCancellationRequested();
            var document = match.Document.Board!;
            var current = SameProject(document.ProjectPath, currentProjectPath);
            var exact = Exact(document, query, current);
            if (exactOnly && (exact == 0 || !current && !BoardKeys.TryParseStored(query, out _))) continue;
            if (query.Length > 0 && exact == 0 && match.Lexical == 0 && match.Semantic == 0) continue;
            var hit = new BoardSearchHit(document.Id, document.Key, document.DisplayId, document.Title,
                document.BoardId, document.BoardName, document.ColumnId, document.ColumnName, document.ProjectPath,
                current, Snippet(match.Text, words), exact > 0 ? "exact" : match.Lexical > 0 && match.Semantic > 0 ? "keyword+semantic"
                    : match.Lexical > 0 ? "keyword" : match.Semantic > 0 ? "semantic" : "recent", 0,
                document.Type, document.Priority, document.Assignee, document.Blocked, document.Flagged);
            candidates.Add(new(hit, exact, match.Lexical, match.Semantic, document.UpdatedUtc));
        }

        // Weight semantic evidence by its similarity, then give local matches a bounded
        // preference. A weak local match must not hide a much stronger foreign match.
        // Equal relevance shares a rank, independent of arbitrary immutable card IDs.
        var fused = new Dictionary<string, double>(StringComparer.Ordinal);
        var maximumLexical = Math.Max(1, candidates.Select(candidate => candidate.Lexical).DefaultIfEmpty(0).Max());
        foreach (var semanticRanking in new[] { false, true })
        {
            var rank = 0;
            var position = 0;
            double? previous = null;
            var ranking = candidates.Where(candidate => (semanticRanking ? candidate.Semantic : candidate.Lexical) > 0)
                .OrderByDescending(candidate => semanticRanking ? candidate.Semantic : candidate.Lexical);
            foreach (var candidate in ranking)
            {
                position++;
                var relevance = semanticRanking ? candidate.Semantic : candidate.Lexical;
                if (previous != relevance) rank = position;
                previous = relevance;
                var weight = semanticRanking ? candidate.Semantic : candidate.Lexical / maximumLexical;
                fused[candidate.Hit.Id] = fused.GetValueOrDefault(candidate.Hit.Id) + weight / (60 + rank);
            }
        }
        double Score(Candidate candidate) => fused.GetValueOrDefault(candidate.Hit.Id) * (candidate.Hit.IsCurrentProject ? 1.1 : 1);
        return candidates.OrderByDescending(candidate => candidate.Exact)
            .ThenByDescending(candidate => candidate.Exact == 1 && candidate.Hit.IsCurrentProject)
            .ThenByDescending(Score)
            .ThenByDescending(candidate => candidate.Hit.IsCurrentProject)
            .ThenByDescending(candidate => candidate.UpdatedUtc).ThenBy(candidate => candidate.Hit.Id)
            .Take(count).Select(candidate => candidate.Hit with { Score = Score(candidate) + candidate.Exact }).ToArray();
    }

    private static int Exact(BoardSearchDocument document, string query, bool current)
    {
        if (query.Length == 0) return 0;
        if (string.Equals(document.Id, query, StringComparison.OrdinalIgnoreCase)
            || BoardKeys.TryParseStored(document.Key, out _) && string.Equals(document.Key, query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(document.Key, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(document.DisplayId, query, StringComparison.OrdinalIgnoreCase)) return 1;
        return current && (string.Equals(BoardKeys.Format(document.KeyPrefix, document.Number), query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(BoardKeys.Format(BoardKeys.LegacyPrefix, document.Number), query, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    private static double TextScore(string text, string query, string[] words) =>
        (text.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
        + words.Count(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool SameProject(string left, string right) => string.Equals(
        BoardPaths.NormalizeProjectPath(left), BoardPaths.NormalizeProjectPath(right), BoardPaths.ProjectPathComparison);

    private static string Snippet(string text, string[] words)
    {
        var first = words.Select(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase)).Where(index => index >= 0).DefaultIfEmpty(-1).Min();
        // A paraphrase may have no literal anchor. Show both ends of the matched chunk,
        // including its tail, rather than always clipping a deep-source match out of view.
        if (first < 0 && text.Length > 260) return text[..100] + " … " + text[^155..];
        first = Math.Max(0, first);
        var start = Math.Max(0, first - 70);
        var length = Math.Min(260, text.Length - start);
        return (start > 0 ? "…" : "") + text.Substring(start, length) + (start + length < text.Length ? "…" : "");
    }

    private sealed record Candidate(BoardSearchHit Hit, int Exact, double Lexical, double Semantic, DateTime UpdatedUtc);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "card", "cards", "where", "what", "that", "this", "about", "with", "did", "was", "for", "how", "have", "from", "and", "are", "can", "to", "of" };
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
