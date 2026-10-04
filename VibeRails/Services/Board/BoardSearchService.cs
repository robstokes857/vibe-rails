using System.Text.RegularExpressions;
using VibeRails.Services.BertV2;

namespace VibeRails.Services.Board;

/// <summary>Shared local Board retrieval for the UI and MCP, with incremental BGE indexing.</summary>
public sealed partial class BoardSearchService(IBoardStore store, Func<IBertV2BgeEmbedder> embedder,
    ILogger<BoardSearchService> logger)
{
    private static readonly SemaphoreSlim Indexing = new(1, 1);
    private const int MaximumNewVectors = 32;

    public BoardSearchService(IBoardStore store, IBertV2BgeEmbedder embedder, ILogger<BoardSearchService> logger)
        : this(store, () => embedder, logger) { }

    /// <summary>Model loading is deferred until a nonempty semantic query and may fall back to keywords.</summary>
    public static BoardSearchService Create(IServiceProvider services) => new(
        services.GetRequiredService<IBoardStore>(), () => services.GetRequiredService<IBertV2BgeEmbedder>(),
        services.GetRequiredService<ILogger<BoardSearchService>>());

    /// <summary>Searches all local boards, preferring the current project and preserving exact identity matches.</summary>
    /// <remarks>The optional server-resolved projectScope filter applies before ranking and result limits.</remarks>
    public async Task<IReadOnlyList<BoardSearchHit>> SearchAsync(string currentProjectPath, string query,
        int count = 50, string? excludeCardId = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? excludedCardIds = null, string? projectScope = null)
    {
        query = (query ?? "").Trim();
        if (query.Length > 1000) throw new BoardValidationException("Card search must be at most 1000 characters.");
        count = Math.Clamp(count, 1, 50);
        var excluded = excludedCardIds?.ToHashSet(StringComparer.Ordinal);
        var words = Words().Matches(query).Select(match => match.Value).Where(word => word.Length > 1 && !StopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Exact permanent keys and row IDs do not need a model. Short/display aliases are
        // still ranked in the owning project before ambiguous labels elsewhere.
        var identityQuery = BoardKeys.TryParseStored(query, out _) || query.StartsWith("card_", StringComparison.Ordinal);
        // Serialize cold index work within this host so simultaneous searches neither
        // duplicate model work nor miss all semantic results while another query warms it.
        // Acquire before loading/using the model so superseded queued keystrokes do no
        // inference work. The store is independently safe across processes.
        var ownsIndexing = query.Length > 0 && !identityQuery;
        if (ownsIndexing) await Indexing.WaitAsync(ct);
        var generated = 0;
        var candidates = new List<Candidate>();
        try
        {
            float[]? queryVector = null;
            if (ownsIndexing)
            {
                ct.ThrowIfCancellationRequested();
                try { queryVector = embedder().GenerateEmbedding(query); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogWarning(ex, "Board search model unavailable; using keywords"); }
                if (queryVector is null) { Indexing.Release(); ownsIndexing = false; }
            }
            string? afterCardId = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await store.GetSearchDocumentsAsync(0, ct, includeContent: query.Length > 0, afterCardId: afterCardId);
                foreach (var document in page)
                {
                    if (document.Id == excludeCardId || excluded?.Contains(document.Id) == true) continue;
                    // Project-only consumers such as merge must constrain eligibility before
                    // scoring and the result limit; foreign matches cannot consume their slots.
                    if (projectScope is not null && !SameProject(document.ProjectPath, projectScope)) continue;
                    var current = SameProject(document.ProjectPath, currentProjectPath);
                    var exact = Exact(document, query, current);
                    var lexical = query.Length == 0 ? 0 : TextScore(document.Title, query, words) * 3;
                    var semantic = 0d;
                    var snippet = document.Passages.FirstOrDefault()?.Text ?? document.Title;
                    var snippetScore = -1d;
                    // Keyword input retains the original source: an identifier or phrase
                    // can be longer than an individual model passage or cross its boundary.
                    foreach (var source in document.KeywordSources)
                    {
                        ct.ThrowIfCancellationRequested();
                        var score = query.Length == 0 ? 0 : TextScore(source, query, words);
                        lexical = Math.Max(lexical, score);
                        if (score > snippetScore) { snippet = source; snippetScore = score; }
                    }
                    foreach (var passage in document.Passages)
                    {
                        ct.ThrowIfCancellationRequested();
                        var score = query.Length == 0 ? 0 : TextScore(passage.Text, query, words);
                        lexical = Math.Max(lexical, score);
                        if (score > snippetScore) { snippet = passage.Text; snippetScore = score; }
                        var vector = passage.Embedding;
                        if (vector is null && ownsIndexing && generated < MaximumNewVectors)
                        {
                            try
                            {
                                vector = embedder().GenerateEmbedding(passage.Text);
                                generated++;
                                await store.SaveSearchEmbeddingAsync(document.ProjectPath, document.Id, passage.Id, passage.Version, vector, ct);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            { logger.LogWarning(ex, "Board search indexing unavailable"); generated = MaximumNewVectors; }
                        }
                        if (queryVector is null || vector is null) continue;
                        var similarity = Cosine(queryVector, vector);
                        if (!double.IsFinite(similarity) || similarity <= semantic) continue;
                        semantic = similarity;
                        if (lexical == 0) snippet = passage.Text;
                    }
                    if (semantic < .45) semantic = 0;
                    if (query.Length > 0 && exact == 0 && lexical == 0 && semantic == 0) continue;
                    var hit = new BoardSearchHit(document.Id, document.Key, document.DisplayId, document.Title,
                        document.BoardId, document.BoardName, document.ColumnId, document.ColumnName, document.ProjectPath,
                        current, Snippet(snippet, words), exact > 0 ? "exact" : lexical > 0 && semantic > 0 ? "keyword+semantic"
                            : lexical > 0 ? "keyword" : semantic > 0 ? "semantic" : "recent", 0,
                        document.Type, document.Priority, document.Assignee, document.Blocked, document.Flagged);
                    candidates.Add(new(hit, exact, lexical, semantic, document.UpdatedUtc));
                }
                if (page.Count < 100) break;
                afterCardId = page[^1].Id;
            }
        }
        finally { if (ownsIndexing) Indexing.Release(); }

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
        var first = words.Select(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase)).Where(index => index >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, first - 70);
        var length = Math.Min(260, text.Length - start);
        return (start > 0 ? "…" : "") + text.Substring(start, length) + (start + length < text.Length ? "…" : "");
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length || left.Length == 0) return 0;
        double dot = 0, l = 0, r = 0;
        for (var i = 0; i < left.Length; i++)
        { dot += (double)left[i] * right[i]; l += (double)left[i] * left[i]; r += (double)right[i] * right[i]; }
        return l == 0 || r == 0 ? 0 : dot / Math.Sqrt(l * r);
    }

    private sealed record Candidate(BoardSearchHit Hit, int Exact, double Lexical, double Semantic, DateTime UpdatedUtc);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "card", "cards", "where", "what", "that", "this", "about", "with", "did", "was", "for", "how", "have", "from", "and", "are", "can", "to", "of" };
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
