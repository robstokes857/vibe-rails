using System.Text;
using System.Text.RegularExpressions;
using VibeRails.Services.BertV2;

namespace VibeRails.Services.Board;

/// <summary>Exact card recall and discovery from indexed metadata, without source database enrichment.</summary>
public sealed partial class BoardRecallService(IBoardProjectResolver projects, ISearchIndexStore index,
    BoardSearchService search, IBertSearchDbService? history = null)
{
    public static BoardRecallService Create(IServiceProvider services) => new(
        services.GetRequiredService<IBoardProjectResolver>(), services.GetRequiredService<ISearchIndexStore>(),
        services.GetRequiredService<BoardSearchService>(), services.GetRequiredService<IBertSearchDbService>());
    public sealed record Result(string Text, IReadOnlySet<string> SessionIds, bool Exact);

    public async Task<Result> SearchAsync(string query, int count, CancellationToken cancellationToken = default)
    {
        var project = await projects.ResolveAsync(cancellationToken);
        var prefixes = index.GetBoardPrefixes(project);
        var keys = ExtractKeys(query).Where(key => BoardKeys.TryParseStored(key, out _) || prefixes.Contains(key[..key.IndexOf('-')])).ToList();
        if (keys.Count == 0 && projects.CurrentSessionId is { } session && index.FindSessionCard(session, project) is { } context)
            keys.AddRange(CardNumber().Matches(query).Select(match => BoardKeys.Format(context.KeyPrefix, int.Parse(match.Groups[1].Value))));
        if (keys.Count > 10) throw new BoardValidationException("Search at most 10 explicit card references at a time.");
        var text = new StringBuilder();
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Count > 0)
        {
            foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var hit = (await search.SearchAsync(project, key, 1, ct: cancellationToken, exactOnly: true)).FirstOrDefault();
                if (hit is null) text.AppendLine($"[exact card - project {project}] No card matches {key}.");
                else Append(text, hit, "exact card", sessions);
            }
        }
        else
            foreach (var hit in await search.SearchAsync(project, query, Math.Min(count, 5), ct: cancellationToken))
                Append(text, hit, "card - BGE/keyword ranking", sessions);
        return new(text.ToString(), sessions, keys.Count > 0);
    }

    private void Append(StringBuilder output, BoardSearchHit hit, string source, HashSet<string> sessions)
    {
        var document = index.ReadDocuments("board", ids: [hit.Id]).FirstOrDefault();
        if (document?.Board is not { } card) return;
        output.AppendLine($"[{source} - project {hit.ProjectPath} - {hit.Key} ({hit.DisplayId})]")
            .AppendLine($"{hit.Title} - Board: {hit.BoardName} - Lane: {hit.ColumnName}")
            .AppendLine(hit.IsCurrentProject ? "Current repository" : "WARNING: another repository")
            .AppendLine($"Match: {hit.Snippet}");
        if (source == "exact card")
            output.AppendLine(Clip(card.PreviousWork is { } handoff ? BoardHandoffService.Format(handoff)
                : document.Sources.FirstOrDefault(s => s.Id == "description")?.Text ?? "", 1800));
        foreach (var file in card.FileCandidates.Take(4)) output.AppendLine($"File candidate: {file.Path} - {file.Commit} (uncurated)");
        foreach (var commit in card.Commits.Take(3)) output.AppendLine($"Linked commit: {commit.Sha} - {Clip(commit.Message, 100)}");
        foreach (var session in card.Sessions) sessions.Add(session.SessionId);
        foreach (var session in card.Sessions.OrderByDescending(s => s.CreatedUtc).Take(3))
        {
            output.AppendLine($"Linked session: {session.SessionId} [{session.Origin}] - {Clip(session.DisplayName, 200)}");
            if (history is not null)
                foreach (var message in history.GetSessionRecallPage(session.SessionId, 0, 2))
                    output.AppendLine($"  Linked-session discussion {message.DocumentId}: {Clip(message.RawText, 350)}");
        }
        var identity = BoardKeys.TryParseStored(hit.Key, out _) ? hit.Key : hit.Id;
        output.AppendLine($"More: get_board_card(card: \"{identity}\"); read_board_session for linked captured discussion.").AppendLine();
    }

    internal static IReadOnlyList<string> ExtractKeys(string query) => CardKey().Matches(query)
        .Select(match => match.Value.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    internal static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "... [continued in get_board_card]";
    [GeneratedRegex(@"(?<![\p{L}\p{N}_@./-])[A-Z][A-Z0-9]{0,7}-(?:[A-Z0-9]{5}-)?[1-9][0-9]{0,8}(?![\p{L}\p{N}_/-]|\.[\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CardKey();
    [GeneratedRegex(@"(?<![\w-])card\s+([1-9][0-9]{0,8})(?![\w./-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CardNumber();
}
