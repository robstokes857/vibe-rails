using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VibeRails.Services.Board;
using VibeRails.Services.Git;
using VibeRails.Utils;

namespace VibeRails.Services.Jira;

public sealed record JiraPullReport(
    bool DryRun,
    string Outcome,
    int Created,
    int Updated,
    int Skipped,
    int Failed,
    string? Message,
    string? BoardId = null);

/// <summary>What Connect learned about the Jira board (VIBE-102). Warnings say what fell back and why.</summary>
public sealed record JiraBoardSummary(
    string Id,
    string? Name,
    string? Type,
    string? ProjectKey,
    int? IssueCount,
    string? StoryPointsFieldId,
    string? StoryPointsFieldName,
    string? Jql,
    IReadOnlyList<JiraColumnLane> Columns,
    IReadOnlyList<string> Warnings);

/// <summary>Test, or Connect for a board link: the account, and the board when the link names one.</summary>
public sealed record JiraTestReport(bool Ok, string? Account, string? Error, JiraBoardSummary? Board);

/// <summary>What the board settings form shows: the saved column map resolved against this board's lanes.</summary>
public sealed record JiraConnectionDetails(
    BoardJiraConnectionRecord? Connection,
    IReadOnlyList<JiraColumnLane> Columns,
    IReadOnlyList<BoardColumnRecord> Lanes,
    string? SuggestedEmail);

public interface IJiraPullService
{
    Task<BoardJiraConnectionRecord?> GetAsync(string projectPath, string boardId, CancellationToken cancellationToken);
    /// <summary>
    /// The connection plus the form's extras. With no saved email, suggests the repository's git
    /// <c>user.email</c> (the form falls back to the VibeRails account email itself).
    /// </summary>
    Task<JiraConnectionDetails> GetDetailsAsync(string projectPath, string boardId, CancellationToken cancellationToken);
    Task<BoardJiraConnectionRecord> SaveAsync(string projectPath, string boardId, BoardJiraConnectionSave save, string? apiToken, CancellationToken cancellationToken);
    /// <summary>Unlinks Jira and forgets its token, preserving the board and all imported work. False means the board is outside this project or missing.</summary>
    Task<bool> UnlinkAsync(string projectPath, string boardId, CancellationToken cancellationToken);
    /// <summary>Checks the token and, for a board link, reads the board, its columns and an issue count.</summary>
    Task<JiraTestReport> TestAsync(string projectPath, string boardId, CancellationToken cancellationToken);
    Task<JiraPullReport> PullAsync(string projectPath, string boardId, bool dryRun, CancellationToken cancellationToken);
    /// <summary>Every enabled connection, for the root scheduler. One failure never stops the next.</summary>
    Task PullDueAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One-way pull of one Jira board, or one saved JQL filter, into one board. Jira wins on the mapped
/// fields. Assignee, flagged, blocked, options, notes, sessions, commits, attachments and the card
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

    public async Task<JiraConnectionDetails> GetDetailsAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        var connection = await GetAsync(projectPath, boardId, cancellationToken);
        var lanes = await store.GetColumnsAsync(projectPath, cancellationToken, boardId);
        var saved = JiraColumnMap.Parse(connection?.ColumnMap);
        var columns = saved.Count == 0
            ? []
            : JiraColumnMap.Resolve(saved.Select(column => column.Name).ToList(), saved, lanes, connection?.OverflowColumnId);
        var suggested = string.IsNullOrWhiteSpace(connection?.Email) ? await GitEmailAsync(projectPath, cancellationToken) : null;
        return new JiraConnectionDetails(connection, columns, lanes, suggested);
    }

    public async Task<BoardJiraConnectionRecord> SaveAsync(
        string projectPath, string boardId, BoardJiraConnectionSave save, string? apiToken, CancellationToken cancellationToken)
    {
        using var held = pullLock.TryAcquire();
        if (held is null) throw new JiraConfigException("Another Jira operation is running. Try again when it finishes.");
        var existing = await store.GetJiraConnectionAsync(projectPath, boardId, cancellationToken);
        var source = ResolveSource(save, existing);
        var email = save.Email.Trim();
        if (email.Length == 0 || !email.Contains('@'))
            throw new JiraConfigException("Email is the Atlassian account the API token belongs to.");
        var field = string.IsNullOrWhiteSpace(save.StoryPointsFieldId) ? null : save.StoryPointsFieldId.Trim();
        if (field is not null && !JiraCloudClient.IsCustomFieldId(field))
            throw new JiraConfigException("Story points field must be a custom field id such as customfield_10016, or left blank.");
        var narrow = save.NarrowJql is null ? existing?.NarrowJql : JiraJql.ValidateNarrowing(save.NarrowJql);
        // A board link starts with old Done issues left out; a VB-40 JQL connection pulls its JQL as written.
        var skipOldDone = save.SkipOldDone ?? existing?.SkipOldDone ?? (source.Link is not null ? true : null);
        // The column map belongs to one Jira board. Another board starts from automatic, and picks
        // sent with its link were made for the previous board's columns, so they are ignored.
        var columnMap = !source.SameBoard ? null
            : save.ColumnMap is null ? existing!.ColumnMap
            : JiraColumnMap.Serialize(JiraColumnMap.WithChoices(JiraColumnMap.Parse(existing!.ColumnMap), save.ColumnMap));

        var token = apiToken?.Trim();
        // Links are keyed by connection id plus Jira's numeric issue id, and that id is only unique
        // within one site. A different site therefore gets a new connection id: its issues can never
        // match, and overwrite, cards linked to the previous site. Those cards stay on the board and
        // are no longer updated. The saved token is never sent to the new site.
        var siteChanged = existing is not null
            && !string.Equals(existing.SiteUrl, source.Origin, StringComparison.OrdinalIgnoreCase);
        if (siteChanged && string.IsNullOrEmpty(token))
            throw new JiraConfigException("Changing the Jira site needs the API token again. The saved token is never sent to a different site.");
        var id = existing is null || siteChanged ? "jira_" + Guid.NewGuid().ToString("N")[..12] : existing.Id;
        var hasToken = !string.IsNullOrEmpty(token) || secrets.HasToken(id);

        // The row goes first and the token second, so a concurrent token prune (which reads the
        // live connection ids under the token file lock) can never delete the new token.
        var saved = await store.SaveJiraConnectionAsync(new BoardJiraConnectionRecord(
            id, projectPath, boardId, source.Origin, email, hasToken,
            hasToken ? BoardJiraAuthStatus.Saved : BoardJiraAuthStatus.None,
            field, source.Jql, save.Enabled && hasToken, null,
            existing?.OverflowColumnId,
            siteChanged ? null : existing?.LastTestedUtc,
            siteChanged ? null : existing?.LastPullUtc,
            siteChanged
                ? $"Site changed from {existing!.SiteUrl}. Cards pulled from that site stay on the board and are no longer updated."
                : existing?.LastReport,
            source.Link, source.JiraBoardId, source.JiraBoardName, columnMap, narrow, skipOldDone, existing?.DedicatedBoard ?? false), cancellationToken)
            ?? throw new JiraConfigException("That board no longer exists. Open the board again and retry.");
        if (!string.IsNullOrEmpty(token))
            secrets.SaveToken(id, token);
        if (siteChanged)
            secrets.DeleteToken(existing!.Id);
        saved = await store.EnsureDedicatedJiraBoardAsync(projectPath, saved.Id, cancellationToken);
        return WithTokenFlag(saved);
    }

    /// <inheritdoc />
    public async Task<bool> UnlinkAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        using var held = pullLock.TryAcquire();
        if (held is null) throw new JiraConfigException("Another Jira operation is running. Try again when it finishes.");
        if (await store.GetBoardAsync(projectPath, boardId, cancellationToken) is null)
            return false;
        var connection = await store.GetJiraConnectionAsync(projectPath, boardId, cancellationToken);
        if (connection is null) return true;

        // Forget the credential first: even a failed database write cannot leave syncing active.
        // A retry can safely finish removing the connection without touching imported work.
        secrets.DeleteToken(connection.Id);
        if (!await store.DeleteJiraConnectionAsync(projectPath, boardId, connection.Id, cancellationToken))
            throw new JiraConfigException("The Jira connection changed. Reopen Board Settings and try again.");
        return true;
    }

    private sealed record ConnectionSource(
        string Origin, string? Link, string? JiraBoardId, string? JiraBoardName, string Jql, bool SameBoard);

    /// <summary>
    /// Where the issues come from: a pasted link, the VB-40 site plus JQL, or (neither given) the
    /// saved source, so changing only the token, email or options never needs the link again.
    /// </summary>
    private static ConnectionSource ResolveSource(BoardJiraConnectionSave save, BoardJiraConnectionRecord? existing)
    {
        if (!string.IsNullOrWhiteSpace(save.BoardLink))
        {
            var link = JiraBoardLink.Parse(save.BoardLink);
            var sameBoard = existing is not null && link.BoardId is not null
                && BoardIdOf(existing) == link.BoardId
                && string.Equals(existing.SiteUrl, link.Origin, StringComparison.OrdinalIgnoreCase);
            // The same board keeps the filter JQL its last Connect read; anything else starts from
            // the link's own JQL (its project), which Connect replaces with the board filter's.
            var jql = sameBoard && !string.IsNullOrWhiteSpace(existing!.Jql) ? existing.Jql : link.SourceJql ?? string.Empty;
            return new ConnectionSource(link.Origin, link.Link, link.BoardId, sameBoard ? existing!.JiraBoardName : null, jql, sameBoard);
        }
        if (!string.IsNullOrWhiteSpace(save.SiteUrl))
        {
            var site = JiraSite.Parse(save.SiteUrl);
            var jql = save.Jql.Trim();
            if (jql.Length == 0)
                throw new JiraConfigException("JQL is required, and Jira requires it to be bounded (include a project).");
            return new ConnectionSource(site.Origin, null, null, null, jql, false);
        }
        if (existing is not null)
            return new ConnectionSource(existing.SiteUrl, existing.BoardLink, existing.JiraBoardId, existing.JiraBoardName,
                existing.Jql, BoardIdOf(existing) is not null);
        throw new JiraConfigException("Paste the link of your Jira board.");
    }

    public async Task<JiraTestReport> TestAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        // A late test result must never recreate a connection after Unlink completes.
        using var held = pullLock.TryAcquire();
        if (held is null) throw new JiraConfigException("Another Jira operation is running. Try again when it finishes.");
        var connection = await RequireReady(projectPath, boardId, requireSource: false, cancellationToken);
        var token = secrets.ReadToken(connection.Id)
            ?? throw new JiraConfigException("Save an API token before testing the connection.");
        var result = await jira.TestAsync(connection.SiteUrl, connection.Email, token, cancellationToken);
        var expired = result.Outcome == JiraCallOutcome.Unauthorized;
        var error = result.Outcome == JiraCallOutcome.Ok ? null : result.Detail ?? "Jira refused the connection.";
        BoardResolution? board = null;
        if (error is null && BoardIdOf(connection) is { } jiraBoardId)
        {
            board = await ResolveBoardAsync(connection, token, jiraBoardId, cancellationToken);
            error = board.Error;
            expired |= board.Expired;
        }

        // Re-read: a save, a pull or another Connect may have written the row during the calls
        // above. Only this test's fields are applied, and the board's only while it is the same board.
        var current = await store.GetJiraConnectionAsync(projectPath, boardId, cancellationToken);
        if (current is not null && current.Id == connection.Id)
        {
            var sameBoard = board is not null && BoardIdOf(current) == board.JiraBoardId;
            await store.SaveJiraConnectionAsync(current with
            {
                AuthStatus = expired ? BoardJiraAuthStatus.Expired : BoardJiraAuthStatus.Saved,
                LastTestedUtc = DateTime.UtcNow,
                JiraBoardName = sameBoard && board!.Name is { } name ? name : current.JiraBoardName,
                Jql = sameBoard && board!.Jql is { } jql ? jql : current.Jql,
                ColumnMap = sameBoard && board!.ColumnMap is { } map ? map : current.ColumnMap
            }, cancellationToken);
        }
        return new JiraTestReport(error is null, result.Value, error, board?.Summary);
    }

    private sealed record BoardResolution(
        string JiraBoardId, JiraBoardSummary? Summary, string? Error, bool Expired, string? Name, string? Jql, string? ColumnMap);

    /// <summary>
    /// Reads the board, its configuration, its filter's JQL and an issue count. Only the board read
    /// is required; the rest degrade to a warning (a token may not be allowed to read the board
    /// configuration), and the pull then matches lanes by status name.
    /// </summary>
    private async Task<BoardResolution> ResolveBoardAsync(
        BoardJiraConnectionRecord connection, string token, string jiraBoardId, CancellationToken cancellationToken)
    {
        var board = await jira.GetBoardAsync(connection.SiteUrl, connection.Email, token, jiraBoardId, cancellationToken);
        if (board.Outcome != JiraCallOutcome.Ok || board.Value is null)
            return new BoardResolution(jiraBoardId, null, board.Detail ?? $"Jira did not return board {jiraBoardId}.",
                board.Outcome == JiraCallOutcome.Unauthorized, null, null, null);

        var warnings = new List<string>();
        var config = await jira.GetBoardConfigurationAsync(connection.SiteUrl, connection.Email, token, jiraBoardId, cancellationToken);
        var configuration = config.Outcome == JiraCallOutcome.Ok ? config.Value : null;
        if (configuration is null)
            warnings.Add($"{config.Detail ?? "Jira did not return the board's columns."} Lanes match Jira status names to lane names, and story points stay off unless a field is set under Advanced.");

        string? jql = null;
        if (configuration?.FilterId is { } filterId)
        {
            var filter = await jira.GetFilterJqlAsync(connection.SiteUrl, connection.Email, token, filterId, cancellationToken);
            jql = filter.Outcome == JiraCallOutcome.Ok ? filter.Value : $"filter = {filterId}";
        }
        jql ??= JiraBoardLink.ProjectJql(board.Value.ProjectKey?.Trim().ToUpperInvariant());

        var count = await jira.CountBoardIssuesAsync(connection.SiteUrl, connection.Email, token, jiraBoardId,
            BoardNarrowing(connection, configuration), cancellationToken);
        if (count.Outcome != JiraCallOutcome.Ok)
            warnings.Add("Jira did not return an issue count for this board. Pull now still works.");

        var lanes = await store.GetColumnsAsync(connection.ProjectPath, cancellationToken, connection.BoardId);
        var choices = JiraColumnMap.WithColumns(JiraColumnMap.Parse(connection.ColumnMap), ColumnNames(configuration));
        var columns = JiraColumnMap.Resolve(choices.Select(choice => choice.Name).ToList(), choices, lanes, connection.OverflowColumnId);
        var overridden = connection.StoryPointsFieldId is not null;
        var summary = new JiraBoardSummary(
            jiraBoardId, board.Value.Name, board.Value.Type, board.Value.ProjectKey,
            count.Outcome == JiraCallOutcome.Ok ? count.Value?.Count : null,
            connection.StoryPointsFieldId ?? configuration?.EstimationFieldId,
            overridden ? null : configuration?.EstimationFieldName,
            jql ?? connection.Jql, columns, warnings);
        return new BoardResolution(jiraBoardId, summary, null, false, board.Value.Name, jql,
            configuration is null ? null : JiraColumnMap.Serialize(choices));
    }

    public async Task<JiraPullReport> PullAsync(string projectPath, string boardId, bool dryRun, CancellationToken cancellationToken)
    {
        var connection = await RequireReady(projectPath, boardId, requireSource: true, cancellationToken);
        var token = secrets.ReadToken(connection.Id)
            ?? throw new JiraConfigException("Save an API token before pulling.");
        if (dryRun)
            return await PullConnectionAsync(connection, token, dryRun, cancellationToken);

        // One writing pull at a time across every VibeRails process on this machine.
        using var held = pullLock.TryAcquire();
        if (held is null)
            return Report(dryRun, "busy", 0, 0, 0, 0, "Another Jira pull is running. Try again when it finishes.");
        connection = await store.EnsureDedicatedJiraBoardAsync(projectPath, connection.Id, cancellationToken);
        var report = (await PullConnectionAsync(connection, token, dryRun, cancellationToken)) with { BoardId = connection.BoardId };
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
            if (!connection.Enabled || !connection.HasToken || !HasSource(connection))
                continue;
            var token = secrets.ReadToken(connection.Id);
            if (token is null)
                continue;
            try
            {
                var dedicated = await store.EnsureDedicatedJiraBoardAsync(connection.ProjectPath, connection.Id, cancellationToken);
                var report = await PullConnectionAsync(dedicated, token, dryRun: false, cancellationToken);
                await Remember(dedicated, report, cancellationToken);
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
        var unchanged = 0;
        var deleted = 0;
        var failed = 0;
        string? pageToken = null;
        var pages = 0;

        var plan = await PlanAsync(connection, token, lanes, cancellationToken);
        if (plan.Outcome != JiraCallOutcome.Ok)
            return await StoppedAsync(plan.Outcome, plan.Detail, plan.RetryAfter);

        while (true)
        {
            var page = plan.JiraBoardId is { } jiraBoardId
                ? await jira.SearchBoardAsync(connection.SiteUrl, connection.Email, token, jiraBoardId, plan.Jql,
                    pageToken, plan.PointsField, cancellationToken)
                : await jira.SearchAsync(connection.SiteUrl, connection.Email, token, plan.Jql ?? connection.Jql,
                    pageToken, plan.PointsField, cancellationToken);
            if (page.Outcome != JiraCallOutcome.Ok || page.Value is null)
                return await StoppedAsync(page.Outcome, page.Detail, page.RetryAfter);

            foreach (var issue in page.Value.Issues)
            {
                try
                {
                    var action = await ApplyIssueAsync(connection, plan, issue, lanes, overflowId, dryRun, cancellationToken);
                    if (action is ApplyAction.Created or ApplyAction.CreatedOverflow) created++;
                    else if (action == ApplyAction.Updated) updated++;
                    else
                    {
                        skipped++;
                        if (action == ApplyAction.Unchanged) unchanged++;
                        else if (action == ApplyAction.SkippedDeleted) deleted++;
                    }
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

        var message = $"{created} created, {updated} updated, {skipped} skipped, {failed} failed.";
        if (skipped > 0)
        {
            var reasons = new List<string>();
            if (unchanged > 0) reasons.Add($"{unchanged} unchanged");
            if (deleted > 0) reasons.Add($"{deleted} previously deleted in VibeRails");
            var alreadyLinked = skipped - unchanged - deleted;
            if (alreadyLinked > 0) reasons.Add($"{alreadyLinked} already linked");
            message += $" Skipped: {string.Join(", ", reasons)}.";
        }
        return Report(dryRun, "ok", created, updated, skipped, failed, dryRun ? "Dry run. " + message : message);

        async Task<JiraPullReport> StoppedAsync(JiraCallOutcome outcome, string? detail, TimeSpan? retryAfter)
        {
            switch (outcome)
            {
                case JiraCallOutcome.Unauthorized:
                    return Report(dryRun, "expired", created, updated, skipped, failed, detail);
                case JiraCallOutcome.RateLimited:
                    if (retryAfter is TimeSpan wait && wait > TimeSpan.Zero && wait <= MaxRetryWait)
                        await Task.Delay(wait, cancellationToken);
                    return Report(dryRun, "rate-limited", created, updated, skipped, failed,
                        "Jira rate limited the pull. The next interval tries again.");
                case JiraCallOutcome.BadJql:
                    return Report(dryRun, "bad-jql", created, updated, skipped, failed, detail);
                default:
                    return Report(dryRun, "failed", created, updated, skipped, failed, detail ?? "Jira did not return the issues.");
            }
        }
    }

    /// <summary>
    /// What one pull reads. A board connection re-reads the board configuration every pull, so a
    /// column or estimation change in Jira comes through; without it (403, say) lanes match by status
    /// name and only a story points field set under Advanced is read.
    /// </summary>
    /// <param name="Jql">The board's narrowing, or the whole query for a JQL connection.</param>
    /// <param name="StatusLanes">Status id to its Jira column and that column's lane (null: overflow).</param>
    private sealed record PullPlan(
        JiraCallOutcome Outcome, string? Detail, TimeSpan? RetryAfter,
        string? JiraBoardId, string? Jql, string? PointsField,
        IReadOnlyDictionary<string, (string Column, string? LaneId)>? StatusLanes);

    private async Task<PullPlan> PlanAsync(
        BoardJiraConnectionRecord connection, string token, IReadOnlyList<BoardColumnRecord> lanes, CancellationToken cancellationToken)
    {
        if (BoardIdOf(connection) is not { } jiraBoardId)
        {
            var narrowing = JiraJql.And(connection.SkipOldDone == true ? JiraJql.SkipOldDoneClause : null, connection.NarrowJql);
            return new PullPlan(JiraCallOutcome.Ok, null, null, null, JiraJql.Narrow(connection.Jql, narrowing),
                connection.StoryPointsFieldId, null);
        }

        var config = await jira.GetBoardConfigurationAsync(connection.SiteUrl, connection.Email, token, jiraBoardId, cancellationToken);
        switch (config.Outcome)
        {
            case JiraCallOutcome.Unauthorized or JiraCallOutcome.RateLimited:
                return new PullPlan(config.Outcome, config.Detail, config.RetryAfter, jiraBoardId, null, null, null);
            // Only a refusal falls back to status names. A transient failure stops the pull instead:
            // matching by name for one pull would move updated cards out of their mapped lanes.
            case not (JiraCallOutcome.Ok or JiraCallOutcome.Forbidden or JiraCallOutcome.NotFound):
                return new PullPlan(JiraCallOutcome.Failed,
                    (config.Detail ?? "Jira did not return the board's configuration.") + " The next interval tries again.",
                    null, jiraBoardId, null, null, null);
        }
        var configuration = config.Outcome == JiraCallOutcome.Ok ? config.Value : null;

        Dictionary<string, (string Column, string? LaneId)>? statusLanes = null;
        if (configuration is not null)
        {
            var columns = configuration.Columns.Where(column => column.StatusIds.Count > 0).ToList();
            var resolved = JiraColumnMap.Resolve(columns.Select(column => column.Name).ToList(),
                JiraColumnMap.Parse(connection.ColumnMap), lanes, connection.OverflowColumnId);
            statusLanes = new Dictionary<string, (string Column, string? LaneId)>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
            {
                foreach (var status in columns[i].StatusIds)
                    statusLanes.TryAdd(status, (columns[i].Name, resolved[i].LaneId));
            }
        }
        return new PullPlan(JiraCallOutcome.Ok, null, null, jiraBoardId, BoardNarrowing(connection, configuration),
            connection.StoryPointsFieldId ?? configuration?.EstimationFieldId, statusLanes);
    }

    /// <summary>
    /// The jql passed to the board's issue and count calls: the kanban sub-query (ANDing it again is
    /// harmless if Jira already applies it), old Done issues left out, then the user's narrowing.
    /// </summary>
    private static string? BoardNarrowing(BoardJiraConnectionRecord connection, JiraBoardConfiguration? configuration) =>
        JiraJql.And(configuration?.SubQuery,
            connection.SkipOldDone == true ? JiraJql.SkipOldDoneClause : null,
            connection.NarrowJql);

    private static IEnumerable<string> ColumnNames(JiraBoardConfiguration? configuration) =>
        configuration?.Columns.Where(column => column.StatusIds.Count > 0).Select(column => column.Name) ?? [];

    private enum ApplyAction { Skipped, Unchanged, SkippedDeleted, Created, Updated, CreatedOverflow }

    private async Task<ApplyAction> ApplyIssueAsync(
        BoardJiraConnectionRecord connection, PullPlan plan, JiraIssue issue, IReadOnlyList<BoardColumnRecord> lanes,
        string? overflowId, bool dryRun, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issue.Summary))
            throw new JiraConfigException("Issue has no summary.");
        var link = await store.FindJiraLinkAsync(connection.Id, issue.Id, cancellationToken);
        BoardCardRecord? existing = link is null ? null : await store.FindCardAsync(connection.ProjectPath, link.CardId, cancellationToken);
        // A soft-deleted card keeps its link (VB-51): the issue is neither recreated nor updated,
        // and no overflow lane is made on its behalf. Decided before the dry-run count.
        if (link is not null && existing is null)
            return ApplyAction.SkippedDeleted;

        var mapped = JiraFieldMapping.Map(issue, plan.PointsField, existing?.Type);
        // A board connection places the issue by its Jira column; a status the board's columns
        // don't list (or a JQL connection) falls back to matching the status name to a lane name.
        JiraLaneDecision lane;
        string laneTarget;
        string overflowComment;
        if (plan.StatusLanes is not null && issue.StatusId is not null
            && plan.StatusLanes.TryGetValue(issue.StatusId, out var target))
        {
            lane = JiraFieldMapping.DecideLane(target.LaneId, existing?.ColumnId, lanes, overflowId);
            laneTarget = target.LaneId ?? OverflowTarget;
            overflowComment = ColumnOverflowComment(issue.Key, target.Column);
        }
        else
        {
            lane = JiraFieldMapping.MatchLane(mapped.StatusName, existing?.ColumnId, lanes, overflowId);
            var byName = JiraFieldMapping.MatchLane(mapped.StatusName, null, lanes, null);
            laneTarget = byName.Match == JiraLaneMatch.Matched ? byName.ColumnId! : OverflowTarget;
            overflowComment = OverflowComment(issue.Key, mapped.StatusName);
        }

        // An unchanged issue is skipped only while it would land in the same place: a lane picked
        // under Advanced, a new story points field or a JQL connection switched to a board link
        // re-applies it without anyone editing the issue in Jira. A card dragged to another lane
        // here stays put while neither the issue nor the mapping changes.
        var mapping = MappingKey(plan, laneTarget);
        var unchanged = link is not null && issue.Updated.ToUniversalTime() == link.IssueUpdated.ToUniversalTime();
        if (unchanged && existing is not null && existing.BoardId == connection.BoardId
            && string.Equals(link!.Mapping, mapping, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(existing.Description) || mapped.Description.Length == 0)
                return ApplyAction.Unchanged;
            // Repair descriptions missed by older string-only endpoint handling, without moving
            // the card or overwriting locally edited fields of an otherwise unchanged issue.
            if (!dryRun)
                await store.UpdateCardAsync(connection.ProjectPath, existing.Id,
                    new BoardCardPatch(Description: mapped.Description), cancellationToken, JiraAuthor);
            return ApplyAction.Updated;
        }
        if (mapped.Title.Length == 0)
            throw new JiraConfigException("Issue title is empty after trimming.");

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
                new BoardJiraLinkRecord(string.Empty, connection.Id, issue.Id, issue.Key, mapped.AssigneeDisplay, issue.Updated, DateTime.UtcNow, mapping),
                cancellationToken);
            if (card is null)
                return ApplyAction.Skipped;
            if (parked)
                await CommentOnceAsync(connection.ProjectPath, card.Id, overflowComment, cancellationToken);
            return parked ? ApplyAction.CreatedOverflow : ApplyAction.Created;
        }

        await store.UpdateCardAsync(connection.ProjectPath, existing.Id, new BoardCardPatch(
            Title: mapped.Title,
            Description: mapped.Description,
            Priority: mapped.Priority,
            Points: mapped.Points,
            ClearPoints: mapped.ClearPoints,
            Tags: mapped.Tags.ToList(),
            Type: mapped.Type), cancellationToken, JiraAuthor);
        if (lane.Match == JiraLaneMatch.Matched && lane.ColumnId is not null && lane.Comment)
        {
            await board.MoveCardAsync(connection.ProjectPath, existing.Id,
                new BoardCardMoveRequest(lane.ColumnId, null, SkipLaneAutomations: true, JiraAuthor), cancellationToken);
            await board.AddCommentAsync(connection.ProjectPath, existing.Id, JiraAuthor, unchanged
                ? $"Moved to {lane.ColumnName} because this board's Jira lane mapping changed. Lane automations were skipped."
                : $"Moved to {lane.ColumnName} because {issue.Key} changed status in Jira. Lane automations were skipped.", cancellationToken, syncToJira: false);
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
                await CommentOnceAsync(connection.ProjectPath, existing.Id, overflowComment, cancellationToken);
            }
        }
        await store.UpdateJiraLinkAsync(new BoardJiraLinkRecord(
            existing.Id, connection.Id, issue.Id, issue.Key, mapped.AssigneeDisplay, issue.Updated, DateTime.UtcNow, mapping), cancellationToken);
        return ApplyAction.Updated;
    }

    private const string OverflowTarget = "overflow";

    /// <summary>
    /// Where a pull places an issue, stored on its link (<see cref="BoardJiraLinkRecord.Mapping"/>):
    /// a board connection's lane target and story points field. A JQL connection only names its
    /// points field (its lanes follow status names, which nobody configures), and none without
    /// one, so a VB-40 link written before the column existed still matches.
    /// </summary>
    private static string? MappingKey(PullPlan plan, string laneTarget) =>
        plan.JiraBoardId is not null ? $"lane:{laneTarget};points:{plan.PointsField}"
        : plan.PointsField is { } field ? $"points:{field}"
        : null;

    private async Task<string> EnsureOverflowLaneAsync(
        BoardJiraConnectionRecord connection, IReadOnlyList<BoardColumnRecord> lanes, CancellationToken cancellationToken)
    {
        var existing = lanes.FirstOrDefault(lane => string.Equals(lane.Name, OverflowLaneName, StringComparison.OrdinalIgnoreCase))
            ?? (connection.OverflowColumnId is null ? null
                : lanes.FirstOrDefault(lane => lane.Id == connection.OverflowColumnId));
        var column = existing ?? await store.CreateColumnAsync(connection.ProjectPath, OverflowLaneName, "#7c3aed", cancellationToken, connection.BoardId);
        if (connection.OverflowColumnId != column.Id)
        {
            // Re-read so a save or Connect made while this pull ran is not written back over.
            var current = await store.GetJiraConnectionAsync(connection.ProjectPath, connection.BoardId, cancellationToken);
            if (current is not null && current.Id == connection.Id && current.OverflowColumnId != column.Id)
                await store.SaveJiraConnectionAsync(current with { OverflowColumnId = column.Id }, cancellationToken);
        }
        return column.Id;
    }

    private async Task<string?> RefreshOverflowAsync(BoardJiraConnectionRecord connection, CancellationToken cancellationToken) =>
        (await store.GetJiraConnectionAsync(connection.ProjectPath, connection.BoardId, cancellationToken))?.OverflowColumnId;

    private async Task CommentOnceAsync(string projectPath, string cardId, string body, CancellationToken cancellationToken)
    {
        var detail = await store.GetCardDetailAsync(projectPath, cardId, cancellationToken);
        if (detail?.Comments.Any(comment => comment.Body == body) == true)
            return;
        await board.AddCommentAsync(projectPath, cardId, JiraAuthor, body, cancellationToken, syncToJira: false);
    }

    private static string OverflowComment(string issueKey, string status) =>
        $"{issueKey} is in Jira status \"{status}\", which matches no lane on this board. Parked in {OverflowLaneName}. No lane was created for that status.";

    private static string ColumnOverflowComment(string issueKey, string column) =>
        $"{issueKey} is in the Jira column \"{column}\", which has no lane on this board. Parked in {OverflowLaneName}. Pick a lane for that column under Board settings, Jira Cloud, Advanced.";

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
        string projectPath, string boardId, bool requireSource, CancellationToken cancellationToken)
    {
        var connection = await store.GetJiraConnectionAsync(projectPath, boardId, cancellationToken)
            ?? throw new JiraConfigException("Save the Jira connection before using it.");
        if (string.IsNullOrWhiteSpace(connection.SiteUrl) || string.IsNullOrWhiteSpace(connection.Email))
            throw new JiraConfigException("A Jira board link and email are required.");
        if (requireSource && !HasSource(connection))
            throw new JiraConfigException("Paste a Jira board link and connect before pulling.");
        if (connection.AuthStatus == BoardJiraAuthStatus.Expired)
            throw new JiraConfigException("The Jira connection expired. Save the API token again and test it.");
        return connection;
    }

    /// <summary>
    /// The Jira board a pull reads, or null to search <see cref="BoardJiraConnectionRecord.Jql"/>.
    /// The link must still be on the saved site: an older version that changed the site wrote
    /// SiteUrl and Jql but not the board columns, and a board id means nothing on another site.
    /// </summary>
    internal static string? BoardIdOf(BoardJiraConnectionRecord connection) =>
        JiraBoardLink.IsBoardId(connection.JiraBoardId)
        && connection.BoardLink is { } link
        && link.StartsWith(connection.SiteUrl + "/", StringComparison.OrdinalIgnoreCase)
            ? connection.JiraBoardId
            : null;

    private static bool HasSource(BoardJiraConnectionRecord connection) =>
        BoardIdOf(connection) is not null || !string.IsNullOrWhiteSpace(connection.Jql);

    // The repository's git user.email, read only to fill in an empty email field. Best effort: a
    // missing git, a folder that isn't a repository or a slow config all mean no suggestion.
    private static async Task<string?> GitEmailAsync(string projectPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(projectPath))
            return null;
        try
        {
            var result = await GitCli.RunAsync(projectPath, ["config", "--get", "user.email"], cancellationToken,
                TimeSpan.FromSeconds(3), maxOutputChars: 320);
            var email = result.Succeeded ? result.StdOut.Trim() : string.Empty;
            return email.Length is > 2 and <= 254 && email.Contains('@') && !email.Any(char.IsWhiteSpace) ? email : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
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
    private Task _delivering = Task.CompletedTask;
    private DateTime _nextDeliveryUtc = DateTime.MinValue;

    public Task? Tick(DateTime nowUtc, CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            if (_delivering.IsCompleted && nowUtc >= _nextDeliveryUtc)
            {
                _nextDeliveryUtc = nowUtc.AddSeconds(5);
                _delivering = Task.Run(() => DeliverAsync(stoppingToken), CancellationToken.None);
            }
            if (nowUtc < _nextUtc || !_running.IsCompleted)
                return null;
            _nextUtc = nowUtc.Add(Interval);
            return _running = Task.Run(() => PullAsync(stoppingToken), CancellationToken.None);
        }
    }

    public Task WhenIdleAsync()
    {
        lock (_gate)
            return Task.WhenAll(_running, _delivering);
    }

    private async Task DeliverAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<JiraDeliveryService>() is { } delivery)
                await delivery.DrainAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { Log.Warning("[Jira] Activity delivery could not complete; inspect card delivery status."); }
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
