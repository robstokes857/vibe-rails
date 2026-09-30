using VibeRails.DTOs;

namespace VibeRails.Services.Board;

/// <summary>A user-facing board rule violation (empty title, last column, bad sha). Maps to 400.</summary>
public sealed class BoardValidationException(string message) : Exception(message);

/// <summary>A conflicting board operation (deleting the last lane, duplicate link). Maps to 409.</summary>
public sealed class BoardConflictException(string message) : Exception(message);

/// <summary>
/// One board of a project: a named set of lanes (a sprint, a sub-project, a release). Every
/// project has at least one; card keys (<c>VR-n</c>, see <see cref="BoardKeys"/>) stay unique per
/// project, never per board, so a key names the same card whichever board it sits on.
/// </summary>
public sealed record BoardRecord(
    string Id,
    string ProjectPath,
    string Name,
    int Position,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string? DisplayPrefix = null)
{
    public string EffectiveDisplayPrefix => DisplayPrefix ?? BoardDisplayIds.DefaultPrefix(ProjectPath);
}

public sealed record BoardColumnRecord(
    string Id,
    string ProjectPath,
    string Name,
    int Position,
    string Color,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string BoardId = "");

public sealed record BoardColumnDeleteResult(string DeletedColumnId, string MovedToColumnId, int MovedCards);

public sealed record BoardDeleteResult(string DeletedBoardId, int DeletedColumns, int DeletedCards);

/// <summary>Everything a lane card shows. Rails (comments, sessions…) live on <see cref="BoardCardDetailRecord"/>.</summary>
public sealed record BoardCardRecord(
    string Id,
    string ProjectPath,
    int Number,
    string ColumnId,
    int Position,
    string Title,
    string Description,
    string? Assignee,
    string Priority,
    int? Points,
    IReadOnlyList<string> Tags,
    bool Blocked,
    int CommentCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    BaseLlmOptions? BaseLlmOptions = null,
    string Type = BoardCardTypes.Default,
    string BoardId = "",
    bool Flagged = false,
    string KeyPrefix = BoardKeys.LegacyPrefix,
    string? StoredKey = null,
    string? StoredDisplayId = null,
    bool AgentMade = false)
{
    /// <summary>The stored <c>PREFIX-RRRRR-n</c> key (cards created since board/14), else the
    /// computed legacy <c>PREFIX-n</c>.</summary>
    public string Key => StoredKey ?? BoardKeys.Format(KeyPrefix, Number);
    /// <summary>Mutable, human-readable label; identity and sync continue to use Key and Id.</summary>
    public string DisplayId => StoredDisplayId ?? Key;
}

public sealed record BoardCardDetailRecord(
    BoardCardRecord Card,
    IReadOnlyList<BoardCommentRecord> Comments,
    IReadOnlyList<BoardSessionRecord> Sessions,
    IReadOnlyList<BoardAttachmentRecord> Attachments,
    IReadOnlyList<BoardCommitRecord> Commits,
    IReadOnlyList<BoardCommentRecord> Notes)
{
    public IReadOnlyList<BoardLinkedCardRecord> LinkedCards { get; init; } = [];
}

/// <summary>A lightweight, current description of a related card, including its board and lane.</summary>
public sealed record BoardLinkedCardRecord(
    string Id, int Number, string Title, string BoardId, string BoardName, string ColumnId, string ColumnName,
    string KeyPrefix = BoardKeys.LegacyPrefix, string? StoredKey = null, string? StoredDisplayId = null)
{
    public string Key => StoredKey ?? BoardKeys.Format(KeyPrefix, Number);
    public string DisplayId => StoredDisplayId ?? Key;
}

/// <summary>
/// The kinds of BoardComments row. A <em>note</em> is the agent scratchpad: same shape and
/// attribution as a comment, but kept out of the comment stream and the comment count so an agent
/// can checkpoint findings as it goes without spamming the human-facing thread. The change kinds
/// (board/14) are the rest of the Card Log: written by the store in the same transaction as the
/// card write they describe, never posted by a caller, and never shown by older binaries, which
/// read comment and note rows only.
/// </summary>
public static class BoardCommentKinds
{
    public const string Comment = "comment";
    public const string Note = "note";
    public const string Created = "created";
    public const string Change = "change";
    public const string Deleted = "deleted";

    /// <summary>The kinds a caller may post.</summary>
    public static bool IsValid(string? value) => value is Comment or Note;
}

/// <summary>
/// What became of a linked terminal session, read from the Sessions / ChatSummary tables when the
/// host has them. Null fields mean "unknown here", never "did not happen".
/// </summary>
public sealed record BoardSessionOutcomeRecord(string SessionId, DateTime? EndedUtc, int? ExitCode, string? Summary);

public sealed record BoardAuthor(string Kind, string Label, string? Cli, string? SessionId)
{
    public const string UserKind = "user";
    public const string AgentKind = "agent";
    public const string SystemKind = "system";

    public static BoardAuthor User() => new(UserKind, "You", null, null);
    public static BoardAuthor System(string label = "VibeRails") => new(SystemKind, label, null, null);
    public static BoardAuthor Agent(string label, string? cli, string? sessionId) => new(AgentKind, label, cli, sessionId);

    internal static bool IsGenericAgentLabel(string? label) =>
        string.IsNullOrWhiteSpace(label)
        || string.Equals(label, "Agent", StringComparison.OrdinalIgnoreCase)
        || string.Equals(label, "Agent session", StringComparison.OrdinalIgnoreCase);
}

public sealed record BoardCommentRecord(
    string Id,
    string CardId,
    BoardAuthor Author,
    string Body,
    DateTime CreatedUtc,
    string Kind = BoardCommentKinds.Comment,
    string? Changes = null);

public sealed record BoardSessionRecord(
    string SessionId,
    string CardId,
    string? TabId,
    string Selection,
    string Cli,
    string DisplayName,
    string Origin,
    DateTime CreatedUtc)
{
    public const string LaunchOrigin = "launch";
    public const string AutomationOrigin = "automation";
    public const string McpOrigin = "mcp";
    public const string ManualOrigin = "manual";
}

/// <summary>A session's default card link and that card's project; other attachments may also exist.</summary>
public sealed record BoardSessionLink(string SessionId, string CardId, string ProjectPath);

/// <summary>A lightweight card label attached to a recorded session. <see cref="DisplayId"/> is the label shown; <see cref="Key"/> stays the identity.</summary>
public sealed record BoardSessionCard(string SessionId, string CardId, string Key, string Title, string? DisplayId = null);

public sealed record BoardAttachmentRecord(
    string Id,
    string CardId,
    string Name,
    string MimeType,
    long Bytes,
    string DataUrl,
    DateTime CreatedUtc);

/// <summary>Attachment fields safe to inspect without materializing either the BLOB or legacy data URL.</summary>
public sealed record BoardAttachmentMetadata(
    string Id,
    string CardId,
    string Name,
    string MimeType,
    long Bytes,
    DateTime CreatedUtc);

public sealed record BoardCommitRecord(
    string CardId,
    string Sha,
    string Author,
    string Message,
    DateTime CommittedUtc,
    DateTime LinkedUtc)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
}

/// <summary>
/// <see cref="BoardId"/> picks the board whose left-most lane takes the card when
/// <see cref="ColumnId"/> is omitted; null means the project's default (first) board.
/// </summary>
public sealed record NewBoardCard(
    string? ColumnId,
    string Title,
    string Description,
    string? Assignee,
    string Priority,
    int? Points,
    IReadOnlyList<string> Tags,
    bool Blocked,
    BaseLlmOptions? BaseLlmOptions = null,
    string Type = BoardCardTypes.Default,
    string? BoardId = null,
    bool Flagged = false,
    string? DisplayId = null,
    IReadOnlyList<string>? LinkedCardIds = null,
    bool AgentMade = false);

/// <summary>Partial update. Null = leave untouched. <see cref="ClearAssignee"/> / <see cref="ClearPoints"/> express "set to null".</summary>
public sealed record BoardCardPatch(
    string? Title = null,
    string? Description = null,
    string? DescriptionAppend = null,
    string? Assignee = null,
    bool ClearAssignee = false,
    string? Priority = null,
    int? Points = null,
    bool ClearPoints = false,
    IReadOnlyList<string>? Tags = null,
    bool? Blocked = null,
    string? ColumnId = null,
    BaseLlmOptions? BaseLlmOptions = null,
    bool ClearBaseLlmOptions = false,
    string? Type = null,
    bool? Flagged = null,
    string? DisplayId = null);

/// <summary>
/// A card key is <c>PREFIX-n</c>: the project's prefix and the card's number within the project.
/// Every project used to display <see cref="LegacyPrefix"/>, which made VB-1 mean a different card
/// in every repository. A project now takes its prefix from its folder name when its first card is
/// numbered (the store owns that assignment) and keeps it for good; projects that had cards before
/// prefixes existed keep VB, so no existing key ever changes.
/// </summary>
public static class BoardKeys
{
    /// <summary>The prefix every card displayed before projects had their own, and what a card
    /// still displays while its project has no prefix row (its first card was numbered by an
    /// older binary). Never derived for a new project, so it keeps that one meaning.</summary>
    public const string LegacyPrefix = "VB";

    public const int MinPrefixLength = 2;
    public const int MaxPrefixLength = 4;

    /// <summary>Length of the random middle part of a stored key (<c>VB-A7K2P-53</c>).</summary>
    public const int RandomPartLength = 5;

    private const string RandomLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string RandomPartAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string Format(string prefix, int number) =>
        prefix + "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A new card's immutable key: <c>PREFIX-RRRRR-n</c>, where the middle part is five
    /// cryptographically random upper-case letters or digits. The number stays the project's
    /// sequence, so <c>PREFIX-n</c> still finds the card; the random part keeps keys minted on
    /// different machines (a synced board, VB-51) from ever naming two cards.
    /// </summary>
    public static string NewStoredKey(string prefix, int number) =>
        prefix + "-" + new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(RandomPartAlphabet, RandomPartLength))
        + "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// True when <paramref name="value"/> is a stored key, <c>PREFIX-RRRRR-n</c>, in any case.
    /// <paramref name="key"/> comes back upper-cased, the only form keys are stored in.
    /// </summary>
    public static bool TryParseStored(string? value, out string key)
    {
        key = "";
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;
        var first = trimmed.IndexOf('-');
        var last = trimmed.LastIndexOf('-');
        if (first < 1 || last - first - 1 != RandomPartLength)
            return false;
        foreach (var c in trimmed.AsSpan(first + 1, RandomPartLength))
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        }
        var legacy = string.Concat(trimmed.AsSpan(0, first + 1), trimmed.AsSpan(last + 1));
        if (!TryParse(legacy, out _, out _))
            return false;
        key = trimmed.ToUpperInvariant();
        return true;
    }

    /// <summary>
    /// True when <paramref name="value"/> looks like a card key: letters and digits, a dash, then a
    /// positive number (<c>VB-12</c>, <c>vr-3</c>, case-insensitive). The prefix comes back
    /// upper-cased; whether it names the caller's project is the store's decision, not the parser's.
    /// </summary>
    public static bool TryParse(string? value, out string prefix, out int number)
    {
        prefix = "";
        number = 0;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;
        var dash = trimmed.IndexOf('-');
        if (dash < 1 || dash > 8 || dash == trimmed.Length - 1)
            return false;
        var head = trimmed.AsSpan(0, dash);
        if (!char.IsAsciiLetter(head[0]))
            return false;
        foreach (var c in head)
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        }
        if (!int.TryParse(trimmed.AsSpan(dash + 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out number) || number <= 0)
        {
            number = 0;
            return false;
        }
        prefix = head.ToString().ToUpperInvariant();
        return true;
    }

    public static bool TryParse(string? value, out int number) => TryParse(value, out _, out number);

    /// <summary>
    /// The prefixes a project would like, best first, from its folder name: the initials of the
    /// name's words (split on <c>-</c>, <c>_</c>, <c>.</c>, spaces and camelCase), then the name's
    /// first letters. Each is two to four upper-case ASCII letters; <see cref="LegacyPrefix"/> is
    /// never offered. Empty when the name has fewer than two usable letters, so the caller falls
    /// back to <see cref="RandomPrefix"/>. Accented letters count by their base letter.
    /// </summary>
    public static IReadOnlyList<string> DerivePrefixCandidates(string projectPath)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectPath.Trim()));
        var words = SplitWords(name);
        var candidates = new List<string>(2);
        AddCandidate(candidates, string.Concat(words.Select(word => word[0])));
        AddCandidate(candidates, string.Concat(words));
        return candidates;
    }

    /// <summary>Four random upper-case letters, for a name no candidate could serve.</summary>
    public static string RandomPrefix() =>
        new(Random.Shared.GetItems<char>(RandomLetters, MaxPrefixLength));

    private static void AddCandidate(List<string> candidates, string letters)
    {
        if (letters.Length < MinPrefixLength)
            return;
        var candidate = letters.Length > MaxPrefixLength ? letters[..MaxPrefixLength] : letters;
        if (candidate != LegacyPrefix && !candidates.Contains(candidate))
            candidates.Add(candidate);
    }

    /// <summary>The name's words as their upper-case ASCII letters; digits separate nothing but are not letters.</summary>
    private static List<string> SplitWords(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        var previous = '\0';
        foreach (var raw in name.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(raw) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            if (!char.IsAsciiLetterOrDigit(raw))
            {
                Flush();
                previous = '\0';
                continue;
            }
            if (char.IsAsciiLetterUpper(raw) && (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous)))
                Flush();
            if (char.IsAsciiLetter(raw))
                current.Append(char.ToUpperInvariant(raw));
            previous = raw;
        }
        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
                words.Add(current.ToString());
            current.Clear();
        }
    }
}

public static class BoardPriorities
{
    public static readonly IReadOnlyList<string> All = ["critical", "high", "medium", "low"];
    public const string Default = "medium";

    public static bool IsValid(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>Stable wire/storage values for the kind of work a board card represents.</summary>
public static class BoardCardTypes
{
    public const string Task = "task";
    public const string Bug = "bug";
    public const string Feature = "feature";
    public const string ResearchSpike = "research-spike";
    public const string Chore = "chore";
    public const string Default = Task;

    public static readonly IReadOnlyList<string> All = [Task, Bug, Feature, ResearchSpike, Chore];

    public static bool IsValid(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);

    public static string Label(string? value) => value switch
    {
        Bug => "Bug",
        Feature => "Feature",
        ResearchSpike => "Research spike",
        Chore => "Chore / tech debt",
        _ => "Task"
    };
}

public static class BoardCardLimits
{
    public const int MaxDescriptionLength = 100_000;
}
