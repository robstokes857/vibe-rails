namespace VibeRails.Services.Board;

/// <summary>A user-facing board rule violation (empty title, last column, bad sha). Maps to 400.</summary>
public sealed class BoardValidationException(string message) : Exception(message);

/// <summary>A conflicting board operation (deleting the last lane, duplicate link). Maps to 409.</summary>
public sealed class BoardConflictException(string message) : Exception(message);

public sealed record BoardColumnRecord(
    string Id,
    string ProjectPath,
    string Name,
    int? WipLimit,
    int Position,
    string Color,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record BoardColumnDeleteResult(string DeletedColumnId, string MovedToColumnId, int MovedCards);

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
    DateTime UpdatedUtc)
{
    public string Key => BoardKeys.Format(Number);
}

public sealed record BoardCardDetailRecord(
    BoardCardRecord Card,
    IReadOnlyList<BoardCommentRecord> Comments,
    IReadOnlyList<BoardSessionRecord> Sessions,
    IReadOnlyList<BoardAttachmentRecord> Attachments,
    IReadOnlyList<BoardCommitRecord> Commits);

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
    DateTime CreatedUtc);

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

public sealed record NewBoardCard(
    string? ColumnId,
    string Title,
    string Description,
    string? Assignee,
    string Priority,
    int? Points,
    IReadOnlyList<string> Tags,
    bool Blocked);

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
    string? ColumnId = null);

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
