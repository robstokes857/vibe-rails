using VibeRails.DTOs;

namespace VibeRails.Services.Board;

/// <summary>A user-facing board rule violation (empty title, last column, bad sha). Maps to 400.</summary>
public sealed class BoardValidationException(string message) : Exception(message);

/// <summary>A conflicting board operation (deleting the last lane, duplicate link). Maps to 409.</summary>
public sealed class BoardConflictException(string message) : Exception(message);

/// <summary>
/// One board of a project: a named set of lanes (a sprint, a sub-project, a release). Every
/// project has at least one; card keys (<c>VB-n</c>) stay unique per project, never per board, so
/// a key names the same card whichever board it sits on.
/// </summary>
public sealed record BoardRecord(
    string Id,
    string ProjectPath,
    string Name,
    int Position,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record BoardColumnRecord(
    string Id,
    string ProjectPath,
    string Name,
    int? WipLimit,
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
    int DescriptionRevision = 1,
    BaseLlmOptions? BaseLlmOptions = null,
    bool DescriptionChanged = false,
    string Type = BoardCardTypes.Default,
    string BoardId = "")
{
    public string Key => BoardKeys.Format(Number);
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
    string Id, int Number, string Title, string BoardId, string BoardName, string ColumnId, string ColumnName)
{
    public string Key => BoardKeys.Format(Number);
}

/// <summary>
/// The two kinds of BoardComments row. A <em>note</em> is the agent scratchpad: same shape and
/// attribution as a comment, but kept out of the comment stream and the comment count so an agent
/// can checkpoint findings as it goes without spamming the human-facing thread.
/// </summary>
public static class BoardCommentKinds
{
    public const string Comment = "comment";
    public const string Note = "note";

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

    public static BoardAuthor User() => new(UserKind, "You", null, null);
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
    string Kind = BoardCommentKinds.Comment);

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
    public const string McpOrigin = "mcp";
    public const string ManualOrigin = "manual";
}

/// <summary>Where a session id points: the card it is linked to and that card's project.</summary>
public sealed record BoardSessionLink(string SessionId, string CardId, string ProjectPath);

public sealed record BoardAttachmentRecord(
    string Id,
    string CardId,
    string Name,
    string MimeType,
    long Bytes,
    string DataUrl,
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
    BoardAuthor? Author = null,
    string Type = BoardCardTypes.Default,
    string? BoardId = null);

/// <summary>Partial update. Null = leave untouched. <see cref="ClearAssignee"/> / <see cref="ClearPoints"/> express "set to null".</summary>
public sealed record BoardCardPatch(
    string? Title = null,
    string? Description = null,
    string? Assignee = null,
    bool ClearAssignee = false,
    string? Priority = null,
    int? Points = null,
    bool ClearPoints = false,
    IReadOnlyList<string>? Tags = null,
    bool? Blocked = null,
    string? ColumnId = null,
    int? ExpectedDescriptionRevision = null,
    BaseLlmOptions? BaseLlmOptions = null,
    bool ClearBaseLlmOptions = false,
    BoardAuthor? Author = null,
    IReadOnlyList<string>? ActiveSessionIds = null,
    string? Type = null);

public static class BoardKeys
{
    public const string Prefix = "VB-";

    public static string Format(int number) => Prefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="value"/> looks like a card key (<c>VB-12</c>, case-insensitive).</summary>
    public static bool TryParse(string? value, out int number)
    {
        number = 0;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length <= Prefix.Length)
            return false;
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(trimmed.AsSpan(Prefix.Length), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out number) && number > 0;
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
