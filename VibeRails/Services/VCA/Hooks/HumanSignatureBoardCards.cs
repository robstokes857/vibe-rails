using System.Text.RegularExpressions;
using Serilog;
using VibeRails.Data.Sqlite;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Board;
using VibeRails.Utils;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// The Board card a commit belongs to, for the signing popup's summary (VIBE-200): the card the
/// committing VibeRails terminal was launched for (git, the hook and the popup all inherit its
/// <c>VIBERAILS_TOOL_CURRENT_SESSION_ID</c>), then cards the message names by key or display ID that
/// exist on this repository's board. A commit from an ordinary terminal or a Git GUI finds only the
/// second kind.
/// </summary>
/// <remarks>
/// Display only, so it is read-only, bounded to a few seconds and quiet when board.db is absent or
/// busy: a missing card line must never stand between a person and a signature. The text is the
/// card author's and the popup cleans it before printing.
/// </remarks>
internal static partial class HumanSignatureBoardCards
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);
    private const int MaxNamedReferences = 5;

    public static async Task<IReadOnlyList<string>> FindAsync(
        string repository,
        string message,
        CancellationToken cancellationToken)
    {
        var sessionId = Environment.GetEnvironmentVariable(LocalToolApiContext.CurrentSessionIdVariable)?.Trim();
        var named = NamedReferences(message);
        if (string.IsNullOrEmpty(sessionId) && named.Count == 0)
            return [];

        var statePath = PathConstants.GetStateFilePath();
        if (!File.Exists(new SqliteStoragePaths(statePath).BoardDatabasePath))
            return [];

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            return await FindAsync(SqliteStorage.CreateBoardStore(statePath), repository, sessionId, named, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning("Signing popup gave up reading the Board card for {Repository} after {Seconds} s",
                repository, Budget.TotalSeconds);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Signing popup could not read the Board card for {Repository}", repository);
            return [];
        }
    }

    internal static async Task<IReadOnlyList<string>> FindAsync(
        IBoardStore store,
        string repository,
        string? sessionId,
        IReadOnlyList<string> named,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(sessionId)
            && await store.FindSessionLinkAsync(sessionId, cancellationToken) is { } link
            && await store.FindCardAsync(link.ProjectPath, link.CardId, cancellationToken) is { } linked
            && seen.Add(linked.Id))
        {
            lines.Add(Describe(linked, "the committing terminal's card"));
        }

        var project = BoardPaths.NormalizeProjectPath(repository);
        foreach (var reference in named)
        {
            if (await store.FindCardAsync(project, reference, cancellationToken) is { } card && seen.Add(card.Id))
                lines.Add(Describe(card, "named in the message"));
        }

        return lines;
    }

    /// <summary>Distinct key-shaped words, in order: VB-12, VIBE-200, VB-WI983-272.</summary>
    internal static IReadOnlyList<string> NamedReferences(string message) =>
        CardReference().Matches(message)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxNamedReferences)
            .ToList();

    private static string Describe(BoardCardRecord card, string source)
    {
        var id = string.Equals(card.DisplayId, card.Key, StringComparison.OrdinalIgnoreCase)
            ? card.Key
            : $"{card.DisplayId} ({card.Key})";
        return $"{id} · {card.Title} · {source}";
    }

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{0,9}-(?:[A-Z0-9]{5}-)?[0-9]{1,7}\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CardReference();
}
