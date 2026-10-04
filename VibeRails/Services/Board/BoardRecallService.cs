using System.Text;
using System.Text.RegularExpressions;
using VibeRails.Services.BertV2;

namespace VibeRails.Services.Board;

/// <summary>Exact card recall and shared all-local-board BGE/keyword discovery.</summary>
public sealed partial class BoardRecallService(IBoardStore store, IBoardProjectResolver projects,
    Func<IBertV2BgeEmbedder> embedder, ILogger<BoardRecallService> logger, IBertSearchDbService? history = null, BoardSearchService? search = null)
{
    public BoardRecallService(IBoardStore store, IBoardProjectResolver projects, IBertV2BgeEmbedder embedder,
        ILogger<BoardRecallService> logger, IBertSearchDbService? history = null) : this(store, projects, () => embedder, logger, history) { }

    /// <summary>Resolve the model only for semantic discovery, inside its fallback boundary.</summary>
    public static BoardRecallService Create(IServiceProvider services) => new(
        services.GetRequiredService<IBoardStore>(), services.GetRequiredService<IBoardProjectResolver>(),
        () => services.GetRequiredService<IBertV2BgeEmbedder>(), services.GetRequiredService<ILogger<BoardRecallService>>(),
        services.GetRequiredService<IBertSearchDbService>(), services.GetRequiredService<BoardSearchService>());
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
                // Permanent keys resolve globally; ambiguous short/display aliases keep their local meaning.
                var card = BoardKeys.TryParseStored(key, out _)
                    ? await store.FindLocalCardAsync(key, cancellationToken)
                    : await store.FindCardAsync(project, key, cancellationToken);
                if (card is null) { text.AppendLine($"[exact card · project {project}] No card matches {key}."); continue; }
                await AppendCardAsync(text, card.ProjectPath, card, SameProject(project, card.ProjectPath)
                    ? "exact card" : "exact card · WARNING: another repository", linkedSessions, cancellationToken);
            }
        }
        else
        {
            var finder = search ?? new BoardSearchService(store, embedder, Microsoft.Extensions.Logging.Abstractions.NullLogger<BoardSearchService>.Instance);
            var ranked = await finder.SearchAsync(project, query, Math.Min(count, 5), ct: cancellationToken);
            foreach (var hit in ranked)
            {
                var card = await store.FindCardAsync(hit.ProjectPath, hit.Id, cancellationToken);
                if (card is null) continue;
                text.AppendLine($"Match: {hit.Snippet}");
                await AppendCardAsync(text, hit.ProjectPath, card, hit.IsCurrentProject
                    ? "card · BGE/keyword ranking" : "card · BGE/keyword ranking · WARNING: another repository", linkedSessions, cancellationToken);
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
        var currentProject = await projects.ResolveAsync(ct);
        var handoff = SameProject(currentProject, project)
            ? BoardHandoffService.WithFileStatus(detail.PreviousWork, projects.GitWorkingDirectory) : detail.PreviousWork;
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
        var identity = BoardKeys.TryParseStored(card.Key, out _) ? card.Key : card.Id;
        output.AppendLine($"More: get_board_card(card: \"{identity}\"); page older comments with before; read_board_session for linked captured discussion. File contents/diffs are fetched only when needed.");
        output.AppendLine();
    }

    private static bool SameProject(string left, string right) =>
        string.Equals(BoardPaths.NormalizeProjectPath(left), BoardPaths.NormalizeProjectPath(right), BoardPaths.ProjectPathComparison);

    internal static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "… [continued in get_board_card]";
    [GeneratedRegex(@"(?<![\p{L}\p{N}_@./-])[A-Z][A-Z0-9]{0,7}-(?:[A-Z0-9]{5}-)?[1-9][0-9]{0,8}(?![\p{L}\p{N}_/-]|\.[\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CardKey();
    [GeneratedRegex(@"(?<![\w-])card\s+([1-9][0-9]{0,8})(?![\w./-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CardNumber();
}
