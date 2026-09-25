using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VibeRails.Services.Board;
using VibeRails.Utils;

namespace VibeRails.Services.Jira;

public sealed record JiraPullReport(
    bool DryRun,
    string Outcome,
    int Created,
    int Updated,
    int Skipped,
    int Failed,
    string? Message);

public interface IJiraPullService
{
    Task<BoardJiraConnectionRecord?> GetAsync(string projectPath, string boardId, CancellationToken cancellationToken);
    Task<BoardJiraConnectionRecord> SaveAsync(string projectPath, string boardId, BoardJiraConnectionSave save, string? apiToken, CancellationToken cancellationToken);
    Task<JiraCallResult<string>> TestAsync(string projectPath, string boardId, CancellationToken cancellationToken);
    Task<JiraPullReport> PullAsync(string projectPath, string boardId, bool dryRun, CancellationToken cancellationToken);
    /// <summary>Every enabled connection, for the root scheduler. One failure never stops the next.</summary>
    Task PullDueAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One-way pull of one saved JQL filter into one board. Jira wins on the mapped fields.
/// Assignee, flagged, blocked, options, notes, sessions, commits, attachments and the card
/// number are never written. A status change moves the card with lane automations skipped.
/// </summary>
public sealed class JiraPullService(
    IBoardStore store,
    IBoardService board,
    IJiraCloudClient jira,
    IJiraSecretStore secrets,
    JiraPullLock pullLock) : IJiraPullService
{
    public const string OverflowLaneName = JiraFieldMapping.OverflowLaneName;
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(60);
    private static readonly BoardAuthor JiraAuthor = BoardAuthor.Agent("Jira", null, null);

    public async Task<BoardJiraConnectionRecord?> GetAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        var connection = await store.GetJiraConnectionAsync(projectPath, boardId, cancellationToken);
        return connection is null ? null : WithTokenFlag(connection);
    }

    public async Task<BoardJiraConnectionRecord> SaveAsync(
        string projectPath, string boardId, BoardJiraConnectionSave save, string? apiToken, CancellationToken cancellationToken)
    {
        var site = JiraSite.Parse(save.SiteUrl);
        var email = save.Email.Trim();
        if (email.Length == 0 || !email.Contains('@'))
            throw new JiraConfigException("Email is the Atlassian account the API token belongs to.");
        var jql = save.Jql.Trim();
        if (jql.Length == 0)
            throw new JiraConfigException("JQL is required, and Jira requires it to be bounded (include a project).");
        var field = string.IsNullOrWhiteSpace(save.StoryPointsFieldId) ? null : save.StoryPointsFieldId.Trim();
        if (field is not null && !field.StartsWith("customfield_", StringComparison.Ordinal))
            throw new JiraConfigException("Story points field must be a custom field id such as customfield_10016, or left blank.");

        var existing = await store.GetJiraConnectionAsync(projectPath, boardId, cancellationToken);
        var token = apiToken?.Trim();
        // Links are keyed by connection id plus Jira's numeric issue id, and that id is only unique
        // within one site. A different site therefore gets a new connection id: its issues can never
        // match, and overwrite, cards linked to the previous site. Those cards stay on the board and
        // are no longer updated. The saved token is never sent to the new site.
        var siteChanged = existing is not null
            && !string.Equals(existing.SiteUrl, site.Origin, StringComparison.OrdinalIgnoreCase);
        if (siteChanged && string.IsNullOrEmpty(token))
            throw new JiraConfigException("Changing the Jira site needs the API token again. The saved token is never sent to a different site.");
        var id = existing is null || siteChanged ? "jira_" + Guid.NewGuid().ToString("N")[..12] : existing.Id;
        var hasToken = !string.IsNullOrEmpty(token) || secrets.HasToken(id);

        // The row goes first and the token second, so a concurrent token prune (which reads the
        // live connection ids under the token file lock) can never delete the new token.
        var saved = await store.SaveJiraConnectionAsync(new BoardJiraConnectionRecord(
            id, projectPath, boardId, site.Origin, email, hasToken,
            hasToken ? BoardJiraAuthStatus.Saved : BoardJiraAuthStatus.None,
            field, jql, save.Enabled && hasToken, null,
            existing?.OverflowColumnId,
            siteChanged ? null : existing?.LastTestedUtc,
            siteChanged ? null : existing?.LastPullUtc,
            siteChanged
                ? $"Site changed from {existing!.SiteUrl}. Cards pulled from that site stay on the board and are no longer updated."
                : existing?.LastReport), cancellationToken)
            ?? throw new JiraConfigException("That board no longer exists. Open the board again and retry.");
        if (!string.IsNullOrEmpty(token))
            secrets.SaveToken(id, token);
        if (siteChanged)
            secrets.DeleteToken(existing!.Id);
        return WithTokenFlag(saved);
    }

    public async Task<JiraCallResult<string>> TestAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        var connection = await RequireReady(projectPath, boardId, requireJql: false, cancellationToken);
        var token = secrets.ReadToken(connection.Id)
            ?? throw new JiraConfigException("Save an API token before testing the connection.");
        var result = await jira.TestAsync(connection.SiteUrl, connection.Email, token, cancellationToken);
        var status = result.Outcome == JiraCallOutcome.Unauthorized ? BoardJiraAuthStatus.Expired : BoardJiraAuthStatus.Saved;
        await store.SaveJiraConnectionAsync(connection with
        {
            AuthStatus = status,
            LastTestedUtc = DateTime.UtcNow
        }, cancellationToken);
        return result;
    }

    public async Task<JiraPullReport> PullAsync(string projectPath, string boardId, bool dryRun, CancellationToken cancellationToken)
    {
        var connection = await RequireReady(projectPath, boardId, requireJql: true, cancellationToken);
        var token = secrets.ReadToken(connection.Id)
            ?? throw new JiraConfigException("Save an API token before pulling.");
        if (dryRun)
            return await PullConnectionAsync(connection, token, dryRun, cancellationToken);

        // One writing pull at a time across every VibeRails process on this machine.
        using var held = pullLock.TryAcquire();
        if (held is null)
            return Report(dryRun, "busy", 0, 0, 0, 0, "Another Jira pull is running. Try again when it finishes.");
        var report = await PullConnectionAsync(connection, token, dryRun, cancellationToken);
        await Remember(connection, report, cancellationToken);
        return report;
    }

    public async Task PullDueAsync(CancellationToken cancellationToken)
    {
        // Tokens whose connection is gone (its board was deleted) are removed here.
        await secrets.PruneAsync(async ct =>
            (await store.GetJiraConnectionsAsync(ct)).Select(connection => connection.Id).ToList(), cancellationToken);

        // A root backend that lost the scheduler lease mid-pull may still be pulling; skip rather
        // than run the same filter twice. The next interval tries again.
        using var held = pullLock.TryAcquire();
        if (held is null)
            return;
        foreach (var connection in await store.GetJiraConnectionsAsync(cancellationToken))
        {
            if (!connection.Enabled || !connection.HasToken || string.IsNullOrWhiteSpace(connection.Jql))
                continue;
            var token = secrets.ReadToken(connection.Id);
            if (token is null)
                continue;
            try
            {
                var report = await PullConnectionAsync(connection, token, dryRun: false, cancellationToken);
                await Remember(connection, report, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // One board's pull never sinks the scheduler cycle. The next interval tries again.
            }
        }
    }

    private async Task<JiraPullReport> PullConnectionAsync(
        BoardJiraConnectionRecord connection, string token, bool dryRun, CancellationToken cancellationToken)
    {
        var lanes = await store.GetColumnsAsync(connection.ProjectPath, cancellationToken, connection.BoardId);
        var overflowId = connection.OverflowColumnId;
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var failed = 0;
        string? pageToken = null;
        var pages = 0;

        while (true)
        {
            var page = await jira.SearchAsync(connection.SiteUrl, connection.Email, token, connection.Jql,
                pageToken, connection.StoryPointsFieldId, cancellationToken);
            switch (page.Outcome)
            {
                case JiraCallOutcome.Unauthorized:
                    return Report(dryRun, "expired", created, updated, skipped, failed, page.Detail);
                case JiraCallOutcome.RateLimited:
                    if (page.RetryAfter is TimeSpan wait && wait > TimeSpan.Zero && wait <= MaxRetryWait)
                        await Task.Delay(wait, cancellationToken);
                    return Report(dryRun, "rate-limited", created, updated, skipped, failed,
                        "Jira rate limited the pull. The next interval tries again.");
                case JiraCallOutcome.BadJql:
                    return Report(dryRun, "bad-jql", created, updated, skipped, failed, page.Detail);
                case JiraCallOutcome.Failed:
                    return Report(dryRun, "failed", created, updated, skipped, failed, page.Detail);
            }

            foreach (var issue in page.Value!.Issues)
            {
                try
                {
                    var action = await ApplyIssueAsync(connection, issue, lanes, overflowId, dryRun, cancellationToken);
                    if (action is ApplyAction.Created or ApplyAction.CreatedOverflow) created++;
                    else if (action == ApplyAction.Updated) updated++;
                    else skipped++;
                    if (action == ApplyAction.CreatedOverflow)
                    {
                        overflowId = await RefreshOverflowAsync(connection, cancellationToken) ?? overflowId;
                        lanes = await store.GetColumnsAsync(connection.ProjectPath, cancellationToken, connection.BoardId);
                    }
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    failed++;
                }
            }

            pageToken = page.Value.NextPageToken;
            pages++;
            if (pageToken is null || pages >= 40)
                break;
        }

        var message = $"{created} created, {updated} updated, {skipped} unchanged, {failed} skipped.";
        return Report(dryRun, "ok", created, updated, skipped, failed, dryRun ? "Dry run. " + message : message);
    }

    private enum ApplyAction { Skipped, Created, Updated, CreatedOverflow }

    private async Task<ApplyAction> ApplyIssueAsync(
        BoardJiraConnectionRecord connection, JiraIssue issue, IReadOnlyList<BoardColumnRecord> lanes,
        string? overflowId, bool dryRun, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issue.Summary))
            throw new JiraConfigException("Issue has no summary.");
        var link = await store.FindJiraLinkAsync(connection.Id, issue.Id, cancellationToken);
        BoardCardRecord? existing = link is null ? null : await store.FindCardAsync(connection.ProjectPath, link.CardId, cancellationToken);
        if (link is not null && existing is not null
            && issue.Updated.ToUniversalTime() == link.IssueUpdated.ToUniversalTime())
            return ApplyAction.Skipped;

        var mapped = JiraFieldMapping.Map(issue, connection.StoryPointsFieldId, existing?.Type);
        if (mapped.Title.Length == 0)
            throw new JiraConfigException("Issue title is empty after trimming.");
        var lane = JiraFieldMapping.MatchLane(mapped.StatusName, existing?.ColumnId, lanes, overflowId);

        if (dryRun)
            return existing is null ? ApplyAction.Created : link is null ? ApplyAction.Created : ApplyAction.Updated;

        if (existing is null)
        {
            var columnId = lane.ColumnId;
            var parked = false;
            if (columnId is null)
            {
                columnId = await EnsureOverflowLaneAsync(connection, lanes, cancellationToken);
                parked = true;
            }
            // Card and link commit together: a crash or a concurrent pull between them would leave
            // an unlinked card, and the next pull would create a duplicate.
            var card = await store.CreateJiraCardAsync(connection.ProjectPath, new NewBoardCard(
                columnId, mapped.Title, mapped.Description, null, mapped.Priority, mapped.Points, mapped.Tags,
                false, null, mapped.Type, connection.BoardId, false),
                new BoardJiraLinkRecord(string.Empty, connection.Id, issue.Id, issue.Key, mapped.AssigneeDisplay, issue.Updated, DateTime.UtcNow),
                cancellationToken);
            if (card is null)
                return ApplyAction.Skipped;
            if (parked)
                await CommentOnceAsync(connection.ProjectPath, card.Id, OverflowComment(issue.Key, mapped.StatusName), cancellationToken);
            return parked ? ApplyAction.CreatedOverflow : ApplyAction.Created;
        }

        await store.UpdateCardAsync(connection.ProjectPath, existing.Id, new BoardCardPatch(
            Title: mapped.Title,
            Description: mapped.Description,
            Priority: mapped.Priority,
            Points: mapped.Points,
            ClearPoints: mapped.ClearPoints,
            Tags: mapped.Tags.ToList(),
            Type: mapped.Type), cancellationToken);
        if (lane.Match == JiraLaneMatch.Matched && lane.ColumnId is not null && lane.Comment)
        {
            await board.MoveCardAsync(connection.ProjectPath, existing.Id,
                new BoardCardMoveRequest(lane.ColumnId, null, SkipLaneAutomations: true, JiraAuthor), cancellationToken);
            await board.AddCommentAsync(connection.ProjectPath, existing.Id, JiraAuthor,
                $"Moved to {lane.ColumnName} because {issue.Key} changed status in Jira. Lane automations were skipped.", cancellationToken);
        }
        // Overflow: the status matches no lane and the overflow lane already exists, so MatchLane
        // names it. Unresolved: it does not exist yet (or the name matched several lanes).
        else if (lane.Match is JiraLaneMatch.Overflow or JiraLaneMatch.Unresolved)
        {
            var overflow = await EnsureOverflowLaneAsync(connection, lanes, cancellationToken);
            if (!string.Equals(existing.ColumnId, overflow, StringComparison.Ordinal))
            {
                await board.MoveCardAsync(connection.ProjectPath, existing.Id,
                    new BoardCardMoveRequest(overflow, null, SkipLaneAutomations: true, JiraAuthor), cancellationToken);
                await CommentOnceAsync(connection.ProjectPath, existing.Id, OverflowComment(issue.Key, mapped.StatusName), cancellationToken);
            }
        }
        await store.UpdateJiraLinkAsync(new BoardJiraLinkRecord(
            existing.Id, connection.Id, issue.Id, issue.Key, mapped.AssigneeDisplay, issue.Updated, DateTime.UtcNow), cancellationToken);
        return ApplyAction.Updated;
    }

    private async Task<string> EnsureOverflowLaneAsync(
        BoardJiraConnectionRecord connection, IReadOnlyList<BoardColumnRecord> lanes, CancellationToken cancellationToken)
    {
        var existing = lanes.FirstOrDefault(lane => string.Equals(lane.Name, OverflowLaneName, StringComparison.OrdinalIgnoreCase))
            ?? (connection.OverflowColumnId is null ? null
                : lanes.FirstOrDefault(lane => lane.Id == connection.OverflowColumnId));
        var column = existing ?? await store.CreateColumnAsync(connection.ProjectPath, OverflowLaneName, "#7c3aed", cancellationToken, connection.BoardId);
        if (connection.OverflowColumnId != column.Id)
            await store.SaveJiraConnectionAsync(connection with { OverflowColumnId = column.Id }, cancellationToken);
        return column.Id;
    }

    private async Task<string?> RefreshOverflowAsync(BoardJiraConnectionRecord connection, CancellationToken cancellationToken) =>
        (await store.GetJiraConnectionAsync(connection.ProjectPath, connection.BoardId, cancellationToken))?.OverflowColumnId;

    private async Task CommentOnceAsync(string projectPath, string cardId, string body, CancellationToken cancellationToken)
    {
        var detail = await store.GetCardDetailAsync(projectPath, cardId, cancellationToken);
        if (detail?.Comments.Any(comment => comment.Body == body) == true)
            return;
        await board.AddCommentAsync(projectPath, cardId, JiraAuthor, body, cancellationToken);
    }

    private static string OverflowComment(string issueKey, string status) =>
        $"{issueKey} is in Jira status \"{status}\", which matches no lane on this board. Parked in {OverflowLaneName}. No lane was created for that status.";

    private async Task Remember(BoardJiraConnectionRecord connection, JiraPullReport report, CancellationToken cancellationToken)
    {
        var current = await store.GetJiraConnectionAsync(connection.ProjectPath, connection.BoardId, cancellationToken) ?? connection;
        var expired = report.Outcome == "expired";
        var badJql = report.Outcome == "bad-jql";
        await store.SaveJiraConnectionAsync(current with
        {
            AuthStatus = expired ? BoardJiraAuthStatus.Expired : current.AuthStatus,
            Enabled = badJql ? false : current.Enabled,
            DisabledReason = badJql ? Trim(report.Message) : current.DisabledReason,
            LastPullUtc = DateTime.UtcNow,
            LastReport = Trim($"{report.Outcome}: {report.Message}")
        }, cancellationToken);
    }

    private async Task<BoardJiraConnectionRecord> RequireReady(
        string projectPath, string boardId, bool requireJql, CancellationToken cancellationToken)
    {
        var connection = await store.GetJiraConnectionAsync(projectPath, boardId, cancellationToken)
            ?? throw new JiraConfigException("Save the Jira connection before using it.");
        if (string.IsNullOrWhiteSpace(connection.SiteUrl) || string.IsNullOrWhiteSpace(connection.Email))
            throw new JiraConfigException("Site URL and email are required.");
        if (requireJql && string.IsNullOrWhiteSpace(connection.Jql))
            throw new JiraConfigException("JQL is required.");
        if (connection.AuthStatus == BoardJiraAuthStatus.Expired)
            throw new JiraConfigException("The Jira connection expired. Save the API token again and test it.");
        return connection;
    }

    private BoardJiraConnectionRecord WithTokenFlag(BoardJiraConnectionRecord connection) =>
        connection with { HasToken = secrets.HasToken(connection.Id) };

    private static JiraPullReport Report(bool dryRun, string outcome, int created, int updated, int skipped, int failed, string? message) =>
        new(dryRun, outcome, created, updated, skipped, failed, message);

    private static string? Trim(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= 500 ? value : value[..500];
}

/// <summary>Starts the pull about every 15 minutes from the root scheduler that holds the lease.</summary>
public interface IJiraPullScheduler
{
    /// <summary>
    /// Starts a pull when one is due and none is running, and returns without waiting for it.
    /// A pull can take minutes (up to 40 pages of 30-second requests), far longer than the
    /// scheduler lease, so it must not hold up the Automation cycle. Returns the started pull,
    /// or null when nothing was started.
    /// </summary>
    Task? Tick(DateTime nowUtc, CancellationToken stoppingToken);

    /// <summary>Completes when no pull is running. A running pull observes the stopping token.</summary>
    Task WhenIdleAsync();
}

/// <summary>
/// Singleton so the interval survives between scheduler cycles. The pull service is scoped
/// (it needs the scoped <see cref="IBoardService"/>), so each pull resolves it from a fresh scope.
/// </summary>
public sealed class JiraPullScheduler(IServiceScopeFactory scopeFactory) : IJiraPullScheduler
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private readonly Lock _gate = new();
    private DateTime _nextUtc = DateTime.MinValue;
    private Task _running = Task.CompletedTask;

    public Task? Tick(DateTime nowUtc, CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            if (nowUtc < _nextUtc || !_running.IsCompleted)
                return null;
            _nextUtc = nowUtc.Add(Interval);
            return _running = Task.Run(() => PullAsync(stoppingToken), CancellationToken.None);
        }
    }

    public Task WhenIdleAsync()
    {
        lock (_gate)
            return _running;
    }

    // Never throws: the task is observed only by WhenIdleAsync at shutdown.
    private async Task PullAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IJiraPullService>().PullDueAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Jira] Scheduled pull failed");
        }
    }
}

/// <summary>
/// The OS file lock that lets one writing pull run at a time across every VibeRails process on
/// this machine (browser app and VS Code can both be root backends). Released by the OS if the
/// process dies mid-pull.
/// </summary>
public sealed class JiraPullLock(string path)
{
    public const string FileName = ".jira-pull.lock";

    public static JiraPullLock BesideStateDatabase() =>
        new(CrossProcessFileLock.BesideStateDatabase(ParserConfigs.GetStatePath(), FileName));

    internal CrossProcessFileLock? TryAcquire() => CrossProcessFileLock.TryAcquire(path);
}

internal static class JiraReportFormat
{
    public static string Stamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
