namespace VibeRails.Services.Board;

/// <summary>
/// One Jira Cloud connection for one board. The API token is not a field here: it lives in the
/// local secret store, and <see cref="HasToken"/> only says whether one is saved.
/// </summary>
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
    string? LastReport);

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
public sealed record BoardJiraLinkRecord(
    string CardId,
    string SiteId,
    string IssueId,
    string IssueKey,
    string? AssigneeDisplay,
    DateTime IssueUpdated,
    DateTime LastPulledUtc);

public sealed record BoardJiraConnectionSave(
    string SiteUrl,
    string Email,
    string? StoryPointsFieldId,
    string Jql,
    bool Enabled);
