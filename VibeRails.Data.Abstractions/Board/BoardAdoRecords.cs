namespace VibeRails.Services.Board;

/// <summary>
/// One Azure DevOps Services connection for one board (<c>https://dev.azure.com/{org}</c> only).
/// The personal access token is not a field here: it lives in the local token store, and
/// <see cref="HasToken"/> only says whether one is saved. Kept in its own tables, never in Jira's,
/// so an older binary that reads every Jira connection as Jira never sees one.
/// </summary>
/// <param name="Id"><c>ado_</c> plus 12 hex characters. A new organization or project mints a new
/// id, so work item ids (unique only within one organization) start a new link namespace.</param>
/// <param name="Team">The team whose board the link names; null for a saved-query source.</param>
/// <param name="BacklogLevel">The board's backlog level (Stories, Epics, …); null for a query.</param>
/// <param name="QueryId">The saved query's GUID; null for a team board source.</param>
/// <param name="BoardLink">The pasted Azure DevOps link as saved.</param>
/// <param name="AdoBoardName">The board or query name in Azure DevOps, from the last Connect.</param>
/// <param name="ColumnMap">JSON array of the board's columns in order, each with the lane the user
/// picked or null for automatic. Read by <c>TrackerColumnMap</c>.</param>
/// <param name="PointsField">The work item field read as story points, when one was found or set.</param>
/// <param name="NarrowWiql">Extra WIQL ANDed onto the board or query. Never has ORDER BY.</param>
/// <param name="SkipOldDone">Leave out done work items not changed in 30 days.</param>
/// <param name="AuthStatus">One of <see cref="BoardAdoAuthStatus"/>.</param>
/// <param name="DedicatedBoard">True once <see cref="BoardId"/> is the connection's own board.</param>
public sealed record BoardAdoConnectionRecord(
    string Id,
    string ProjectPath,
    string BoardId,
    string Organization,
    string Project,
    string? Team,
    string? BacklogLevel,
    string? QueryId,
    string BoardLink,
    string? AdoBoardName,
    string? ColumnMap,
    string? PointsField,
    string? NarrowWiql,
    bool? SkipOldDone,
    bool HasToken,
    string AuthStatus,
    bool Enabled,
    string? DisabledReason,
    string? OverflowColumnId,
    bool DedicatedBoard,
    DateTime? LastTestedUtc,
    DateTime? LastPullUtc,
    string? LastReport);

public static class BoardAdoAuthStatus
{
    public const string None = "none";
    public const string Saved = "saved";
    public const string Expired = "expired";
}

/// <summary>
/// The stable identity of one mirrored Azure DevOps work item. <see cref="ConnectionId"/> is the
/// connection id; work item ids are unique within one organization.
/// </summary>
/// <param name="Rev">The work item revision the last pull applied. An unchanged revision whose
/// <see cref="Mapping"/> also matches is skipped.</param>
/// <param name="ChangedDate">The work item's <c>System.ChangedDate</c> at that revision.</param>
/// <param name="AssigneeDisplay">The Azure DevOps assignee's display name. Never written to the card.</param>
/// <param name="Mapping">What the last pull applied (points field and state), in the
/// <c>TrackerLinkMapping</c> format.</param>
public sealed record BoardAdoLinkRecord(
    string CardId,
    string ConnectionId,
    int WorkItemId,
    int Rev,
    DateTime ChangedDate,
    string? AssigneeDisplay,
    DateTime LastPulledUtc,
    string? Mapping = null);

/// <summary>A durable outbound Azure DevOps event. Public replay URLs remain local to this ledger.</summary>
public sealed record BoardAdoDelivery(string Id, string CardId, string ProjectPath, string ConnectionId,
    int WorkItemId, string Kind, string SourceId, string Body, string Status, string? Message, string? Url);
