namespace VibeRails.Services.Board;

/// <summary>
/// One Jira Cloud connection for one board. The API token is not a field here: it lives in the
/// local secret store, and <see cref="HasToken"/> only says whether one is saved.
/// </summary>
/// <param name="Jql">What a pull searches when the connection has no Jira board. With a board
/// (VIBE-102) it holds the board filter's JQL, so an older VibeRails that only reads Jql keeps
/// pulling the same issues.</param>
/// <param name="BoardLink">The pasted Jira link as saved (origin, path and the one query
/// parameter that chose the issues). Null for a VB-40 connection saved as site URL plus JQL.</param>
/// <param name="JiraBoardId">The Jira board the link names; a pull then reads that board's issues.</param>
/// <param name="JiraBoardName">The board's name in Jira, from the last Connect.</param>
/// <param name="ColumnMap">JSON array of the board's columns in order, each with the lane the user
/// picked or null for automatic. Read by <c>JiraColumnMap</c>.</param>
/// <param name="NarrowJql">Extra JQL ANDed onto the board or query. Never has ORDER BY.</param>
/// <param name="SkipOldDone">Leave out Done issues not updated in 30 days. Null on a VB-40
/// connection, which pulled its JQL as written.</param>
public sealed record BoardJiraConnectionRecord(
    string Id,
    string ProjectPath,
    string BoardId,
    string SiteUrl,
    string Email,
    bool HasToken,
    string AuthStatus,
    string? StoryPointsFieldId,
    string Jql,
    bool Enabled,
    string? DisabledReason,
    string? OverflowColumnId,
    DateTime? LastTestedUtc,
    DateTime? LastPullUtc,
    string? LastReport,
    string? BoardLink = null,
    string? JiraBoardId = null,
    string? JiraBoardName = null,
    string? ColumnMap = null,
    string? NarrowJql = null,
    bool? SkipOldDone = null);

public static class BoardJiraAuthStatus
{
    public const string None = "none";
    public const string Saved = "saved";
    public const string Expired = "expired";
}

/// <summary>
/// The stable identity of one mirrored Jira issue. <see cref="IssueId"/> is the numeric id
/// (keys change when an issue moves project). <see cref="SiteId"/> is the connection id.
/// </summary>
/// <param name="Mapping">Where the last pull placed the issue: its lane target and story points
/// field (VIBE-102). An unchanged issue is pulled again when this differs, so a changed lane map
/// applies without editing the issue in Jira. Null before board/29 and for a JQL connection with
/// no points field.</param>
public sealed record BoardJiraLinkRecord(
    string CardId,
    string SiteId,
    string IssueId,
    string IssueKey,
    string? AssigneeDisplay,
    DateTime IssueUpdated,
    DateTime LastPulledUtc,
    string? Mapping = null);

/// <summary>
/// A save from the board settings form. With <see cref="BoardLink"/> the link supplies the site and
/// the issues, and SiteUrl/Jql are ignored. Without it, SiteUrl plus Jql is the VB-40 shape; with
/// neither, the saved source is kept. A null optional field keeps its saved value.
/// </summary>
/// <param name="ColumnMap">Jira column name to lane id, or blank for automatic.</param>
public sealed record BoardJiraConnectionSave(
    string SiteUrl,
    string Email,
    string? StoryPointsFieldId,
    string Jql,
    bool Enabled,
    string? BoardLink = null,
    string? NarrowJql = null,
    bool? SkipOldDone = null,
    IReadOnlyDictionary<string, string?>? ColumnMap = null);
