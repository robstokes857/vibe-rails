using System.Text;
using System.Text.RegularExpressions;
using VibeRails.Services.BertV2;

namespace VibeRails.Services.Board;

/// <summary>Project-scoped exact card recall followed by BGE/keyword card discovery.</summary>
public sealed partial class BoardRecallService(IBoardStore store, IBoardProjectResolver projects,
    Func<IBertV2BgeEmbedder> embedder, ILogger<BoardRecallService> logger, IBertSearchDbService? history = null)
{
    public BoardRecallService(IBoardStore store, IBoardProjectResolver projects, IBertV2BgeEmbedder embedder,
        ILogger<BoardRecallService> logger, IBertSearchDbService? history = null) : this(store, projects, () => embedder, logger, history) { }

    /// <summary>Resolve the model only for semantic discovery, inside its fallback boundary.</summary>
    public static BoardRecallService Create(IServiceProvider services) => new(
        services.GetRequiredService<IBoardStore>(), services.GetRequiredService<IBoardProjectResolver>(),
        () => services.GetRequiredService<IBertV2BgeEmbedder>(), services.GetRequiredService<ILogger<BoardRecallService>>(),
        services.GetRequiredService<IBertSearchDbService>());
    public sealed record Result(string Text, IReadOnlySet<string> SessionIds, bool Exact);

    public async Task<Result> SearchAsync(string query, int count, CancellationToken cancellationToken = default)
    {
        var project = await projects.ResolveAsync(cancellationToken);
        var keys = ExtractKeys(query).ToList();
        if (keys.Count > 0)
        {
            var prefixes = await store.GetRecallKeyPrefixesAsync(project, cancellationToken);
            // An incidental model/encoding/version token (GPT-5, UTF-8, dotnet-10) must
            // not disable discovery. Full permanent keys remain explicit, including misses.
            keys.RemoveAll(key => !BoardKeys.TryParseStored(key, out _) && !prefixes.Contains(key[..key.IndexOf('-')]));
        }
        if (keys.Count == 0 && projects.CurrentSessionId is { } session)
        {
            var link = await store.FindSessionLinkAsync(session, cancellationToken);
            if (link is not null)
            {
                var context = await store.FindCardAsync(project, link.CardId, cancellationToken);
                if (context is not null)
                    keys.AddRange(CardNumber().Matches(query).Select(match => BoardKeys.Format(context.KeyPrefix, int.Parse(match.Groups[1].Value))));
            }
        }
        if (keys.Count > 10) throw new BoardValidationException("Search at most 10 explicit card references at a time.");
        var text = new StringBuilder();
        var linkedSessions = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Count > 0)
        {
            foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Even full permanent keys remain project-scoped for search. Explicit cross-project
                // navigation continues to use the Board tools' existing discovery contract.
                var card = await store.FindCardAsync(project, key, cancellationToken);
                if (card is null) { text.AppendLine($"[exact card · project {project}] No card matches {key}."); continue; }
                await AppendCardAsync(text, project, card, "exact card", linkedSessions, cancellationToken);
            }
        }
        else
        {
            var ranked = await DiscoverAsync(project, query, count, cancellationToken);
            foreach (var id in ranked)
            {
                var card = await store.FindCardAsync(project, id, cancellationToken);
                if (card is not null) await AppendCardAsync(text, project, card, "card · BGE/keyword ranking", linkedSessions, cancellationToken);
            }
        }
        return new(text.ToString(), linkedSessions, keys.Count > 0);
    }

    internal static IReadOnlyList<string> ExtractKeys(string query) => CardKey().Matches(query)
        .Select(match => match.Value.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private async Task AppendCardAsync(StringBuilder output, string project, BoardCardRecord card, string source,
        HashSet<string> sessions, CancellationToken ct)
    {
        var start = output.Length;
        var detail = await store.GetCardDetailAsync(project, card.Id, ct);
        if (detail is null) return;
        var lane = await store.GetColumnAsync(project, card.ColumnId, ct);
        output.AppendLine($"[{source} · project {project} · {card.Key} ({card.DisplayId})]")
            .AppendLine($"{card.Title} · {lane?.Name ?? card.ColumnId}");
        var handoff = BoardHandoffService.WithFileStatus(detail.PreviousWork, projects.GitWorkingDirectory);
        output.AppendLine(Clip(handoff is null ? card.Description : BoardHandoffService.Format(handoff), 1800));
        if (handoff is null)
        {
            var candidates = await store.GetHandoffCandidatesAsync(project, card.Id, ct);
            foreach (var file in candidates.Take(4)) output.AppendLine($"File candidate: {file.Path} · {file.Commit} (uncurated)");
            var latest = detail.Comments.OrderByDescending(c => c.CreatedUtc).FirstOrDefault();
            if (latest is not null) output.AppendLine($"Latest comment {latest.Id}: {Clip(latest.Body, 400)}");
        }
        foreach (var commit in detail.Commits.Take(3)) output.AppendLine($"Linked commit: {commit.Sha} · {Clip(commit.Message, 100)}");
        foreach (var session in detail.Sessions.OrderByDescending(s => s.CreatedUtc).Take(3))
        {
            sessions.Add(session.SessionId);
            var outcome = await store.FindSessionOutcomeAsync(session.SessionId, ct);
            output.AppendLine($"Linked session: {session.SessionId} [{session.Origin}] · {Clip(outcome?.Summary ?? session.DisplayName, 500)}");
            if (history is not null)
            {
                try
                {
                    foreach (var message in history.GetSessionRecallPage(session.SessionId, 0, 2))
                        output.AppendLine($"  Linked-session discussion {message.DocumentId}: {Clip(message.RawText, 350)}");
                }
                catch (Exception ex) { logger.LogWarning(ex, "Linked captured discussion unavailable"); }
            }
        }
        // Match any linked history hit, even when the display budget lists only three sessions.
        foreach (var session in detail.Sessions) sessions.Add(session.SessionId);
        if (output.Length - start > 3600)
        {
            output.Length = start + 3600;
            output.AppendLine("\n[card summary truncated; continue with the tools below]");
        }
        output.AppendLine($"More: get_board_card(card: \"{card.Key}\"); page older comments with before; read_board_session for linked captured discussion. File contents/diffs are fetched only when needed.");
        output.AppendLine();
    }

    private async Task<IReadOnlyList<string>> DiscoverAsync(string project, string query, int count, CancellationToken ct)
    {
        var words = Words().Matches(query.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => w.Length > 2 && !StopWords.Contains(w)).Distinct().ToArray();
        float[]? queryVector = null;
        try { queryVector = embedder().GenerateEmbedding(query); }
        catch (Exception ex) { logger.LogWarning(ex, "Card recall embedder unavailable; using keywords"); }
        var lexical = new List<(string Id, double Score)>();
        var semantic = new List<(string Id, double Score)>();
        var generated = 0;
        for (var offset = 0; ; offset += 100)
        {
            ct.ThrowIfCancellationRequested();
            var page = await store.GetRecallDocumentsAsync(project, offset, ct);
            foreach (var document in page)
            {
                var score = words.Sum(word => document.Title.Contains(word, StringComparison.OrdinalIgnoreCase) ? 3
                    : document.Text.Contains(word, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                if (score > 0) lexical.Add((document.CardId, score));
                var vector = document.Embedding;
                // Incremental derived index: cap model work per call, keep keyword coverage over
                // the whole project, and never require an upgrade/backfill command.
                if (vector is null && queryVector is not null && generated < 32)
                {
                    try
                    {
                        vector = embedder().GenerateEmbedding(document.Text[..Math.Min(document.Text.Length, 6000)]);
                        await store.SaveRecallEmbeddingAsync(project, document.CardId, document.Version, vector, ct);
                        generated++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { logger.LogWarning(ex, "Card recall indexing unavailable"); generated = 32; }
                }
                if (queryVector is not null && vector is not null)
                {
                    var similarity = Cosine(queryVector, vector);
                    if (similarity >= .45) semantic.Add((document.CardId, similarity));
                }
            }
            if (page.Count < 100) break;
        }
        // Same reciprocal rank fusion rule as captured-history retrieval; source stays explicit.
        var scores = new Dictionary<string, double>();
        foreach (var group in new[] { lexical, semantic })
            foreach (var (hit, rank) in group.OrderByDescending(h => h.Score).ThenBy(h => h.Id).Take(count * 3).Select((h, i) => (h, i)))
                scores[hit.Id] = scores.GetValueOrDefault(hit.Id) + 1d / (60 + rank + 1);
        return scores.OrderByDescending(h => h.Value).ThenBy(h => h.Key).Take(Math.Min(count, 5)).Select(h => h.Key).ToList();
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length) return 0;
        double dot = 0, l = 0, r = 0;
        for (var i = 0; i < left.Length; i++) { dot += left[i] * right[i]; l += left[i] * left[i]; r += right[i] * right[i]; }
        return l == 0 || r == 0 ? 0 : dot / Math.Sqrt(l * r);
    }
    internal static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "… [continued in get_board_card]";
    private static readonly HashSet<string> StopWords = ["the", "card", "where", "what", "that", "this", "about", "with", "did", "was", "for", "how", "have", "from"];
    [GeneratedRegex(@"(?<![\p{L}\p{N}_@./-])[A-Z][A-Z0-9]{0,7}-(?:[A-Z0-9]{5}-)?[1-9][0-9]{0,8}(?![\p{L}\p{N}_/-]|\.[\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CardKey();
    [GeneratedRegex(@"(?<![\w-])card\s+([1-9][0-9]{0,8})(?![\w./-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CardNumber();
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
