using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Tracker;

namespace VibeRails.Services.AzureDevOps;

public sealed record AdoPullReport(
    bool DryRun,
    string Outcome,
    int Created,
    int Updated,
    int Skipped,
    int Failed,
    string? Message,
    string? BoardId = null);

/// <summary>
/// What Connect learned about the board or saved query. Warnings say what fell back and why.
/// <paramref name="WorkItemCount"/> stops at <see cref="AzureDevOpsClient.MaxItems"/>, the most one
/// pull reads; <paramref name="CountCapped"/> says it got there.
/// </summary>
public sealed record AdoBoardSummary(
    string? Name,
    string? Team,
    string? BacklogLevel,
    string? QueryId,
    string? QueryType,
    int? WorkItemCount,
    bool CountCapped,
    string? PointsField,
    IReadOnlyList<TrackerColumnLane> Columns,
    IReadOnlyList<string> Warnings);

/// <summary>Test, or Connect: the project the token can read, and the board or query the link names.</summary>
public sealed record AdoTestReport(bool Ok, string? Project, string? Error, AdoBoardSummary? Board);

/// <summary>What the board settings form shows: the saved column map resolved against this board's lanes.</summary>
public sealed record AdoConnectionDetails(
    BoardAdoConnectionRecord? Connection,
    IReadOnlyList<TrackerColumnLane> Columns,
    IReadOnlyList<BoardColumnRecord> Lanes);

/// <summary>
/// A save from the board settings form. A blank <see cref="BoardLink"/> keeps the saved source, so
/// changing only the token or options never needs the link again. A null optional field keeps its
/// saved value; a blank <see cref="PointsField"/> detects the points field from the process.
/// </summary>
/// <param name="ColumnMap">Azure DevOps column name to lane id, or blank for automatic.</param>
public sealed record AdoConnectionSave(
    string? BoardLink,
    bool Enabled,
    string? PointsField = null,
    string? NarrowWiql = null,
    bool? SkipOldDone = null,
    IReadOnlyDictionary<string, string?>? ColumnMap = null);

public interface IAdoPullService
{
    Task<BoardAdoConnectionRecord?> GetAsync(string projectPath, string boardId, CancellationToken cancellationToken);
    Task<AdoConnectionDetails> GetDetailsAsync(string projectPath, string boardId, CancellationToken cancellationToken);
    Task<BoardAdoConnectionRecord> SaveAsync(string projectPath, string boardId, AdoConnectionSave save, string? token, CancellationToken cancellationToken);
    /// <summary>Unlinks Azure DevOps and forgets its token, preserving the board and all imported work. False means the board is outside this project or missing.</summary>
    Task<bool> UnlinkAsync(string projectPath, string boardId, CancellationToken cancellationToken);
    /// <summary>Checks the token and reads the board (columns, team scope, a work item count) or the saved query.</summary>
    Task<AdoTestReport> TestAsync(string projectPath, string boardId, CancellationToken cancellationToken);
    Task<AdoPullReport> PullAsync(string projectPath, string boardId, bool dryRun, CancellationToken cancellationToken);
    /// <summary>
    /// Every enabled connection, for the root scheduler. One failure never stops the next. False
    /// when another process holds the Azure DevOps lock, so nothing was pulled and the interval is not spent.
    /// </summary>
    Task<bool> PullDueAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One-way pull of one Azure DevOps team board, or one saved query, into one board, mirroring
/// <c>JiraPullService</c>. Azure DevOps wins on title, description, type, priority, points and
/// tags. Assignee, flagged, blocked, options, notes, sessions, commits, attachments and the card
/// number are never written. A pull places a new card by its board column or state and never
/// moves a card again: its lane and board are the local workflow, and a later state change is
/// only noted in a comment.
/// </summary>
public sealed class AdoPullService(
    IBoardStore store,
    IBoardService board,
    IAzureDevOpsClient ado,
    IAdoSecretStore secrets,
    AdoPullLock pullLock) : IAdoPullService
{
    public const string OverflowLaneName = AdoFieldMapping.OverflowLaneName;
    private static readonly BoardAuthor AdoAuthor = BoardAuthor.Agent("Azure DevOps", null, null);
    private const string Busy = "Another Azure DevOps operation is running. Try again when it finishes.";

    public async Task<BoardAdoConnectionRecord?> GetAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        var connection = await store.GetAdoConnectionAsync(projectPath, boardId, cancellationToken);
        return connection is null ? null : WithTokenFlag(connection);
    }

    public async Task<AdoConnectionDetails> GetDetailsAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        var connection = await GetAsync(projectPath, boardId, cancellationToken);
        var lanes = await store.GetColumnsAsync(projectPath, cancellationToken, boardId);
        var saved = TrackerColumnMap.Parse(connection?.ColumnMap);
        var columns = saved.Count == 0
            ? []
            : TrackerColumnMap.Resolve(saved.Select(column => column.Name).ToList(), saved, lanes, connection?.OverflowColumnId, OverflowLaneName);
        return new AdoConnectionDetails(connection, columns, lanes);
    }

    public async Task<BoardAdoConnectionRecord> SaveAsync(
        string projectPath, string boardId, AdoConnectionSave save, string? token, CancellationToken cancellationToken)
    {
        using var held = pullLock.TryAcquire();
        if (held is null) throw new AdoConfigException(Busy);
        var existing = await store.GetAdoConnectionAsync(projectPath, boardId, cancellationToken);
        var (link, sameSource) = ResolveSource(save, existing);
        var pointsField = string.IsNullOrWhiteSpace(save.PointsField) ? null : save.PointsField.Trim();
        if (pointsField is not null && !AdoWiql.IsFieldReference(pointsField))
            throw new AdoConfigException("Points field must be a field reference name such as Microsoft.VSTS.Scheduling.StoryPoints, or left blank.");

        // A saved query is pulled as written: the query-by-id call takes no extra WIQL, and a query
        // has no board columns that say which states are done.
        string? narrow = null;
        bool? skipOldDone = null;
        if (link.QueryId is not null)
        {
            if (AdoWiql.ValidateNarrowing(save.NarrowWiql) is not null)
                throw new AdoConfigException("Narrowing WIQL applies to a team board link. Edit the saved query in Azure DevOps to narrow it.");
        }
        else
        {
            narrow = save.NarrowWiql is null ? existing?.NarrowWiql : AdoWiql.ValidateNarrowing(save.NarrowWiql);
            // A board link starts with old done work items left out.
            skipOldDone = save.SkipOldDone ?? existing?.SkipOldDone ?? true;
        }
        // The column map belongs to one board. Another board starts from automatic, and picks sent
        // with its link were made for the previous board's columns, so they are ignored.
        var columnMap = !sameSource || link.QueryId is not null ? null
            : save.ColumnMap is null ? existing!.ColumnMap
            : TrackerColumnMap.Serialize(TrackerColumnMap.WithChoices(TrackerColumnMap.Parse(existing!.ColumnMap), save.ColumnMap));

        // Links are keyed by connection id plus the work item id, which is only unique within one
        // organization. Another organization or project therefore gets a new connection id: its
        // work items can never match, and overwrite, cards linked before. Those cards stay on the
        // board and are no longer updated. The saved token is never sent to another organization;
        // within the same one it carries over to the new id.
        var secret = token?.Trim();
        var organizationChanged = existing is not null && !Same(existing.Organization, link.Organization);
        var projectChanged = existing is not null && !Same(existing.Project, link.Project);
        if (organizationChanged && string.IsNullOrEmpty(secret))
            throw new AdoConfigException("Changing the Azure DevOps organization needs the personal access token again. The saved token is never sent to a different organization.");
        var scopeChanged = organizationChanged || projectChanged;
        if (string.IsNullOrEmpty(secret) && projectChanged)
            secret = secrets.ReadToken(existing!.Id);
        var id = existing is null || scopeChanged ? "ado_" + Guid.NewGuid().ToString("N")[..12] : existing.Id;
        var hasToken = !string.IsNullOrEmpty(secret) || secrets.HasToken(id);

        // The row goes first and the token second, so a concurrent token prune (which reads the
        // live connection ids under the token file lock) can never delete the new token.
        var saved = await store.SaveAdoConnectionAsync(new BoardAdoConnectionRecord(
            id, projectPath, boardId, link.Organization, link.Project, link.Team, link.BacklogLevel, link.QueryId, link.Link,
            sameSource ? existing!.AdoBoardName : null, columnMap, pointsField, narrow, skipOldDone,
            hasToken, hasToken ? BoardAdoAuthStatus.Saved : BoardAdoAuthStatus.None,
            save.Enabled && hasToken, null,
            existing?.OverflowColumnId, existing?.DedicatedBoard ?? false,
            scopeChanged ? null : existing?.LastTestedUtc,
            scopeChanged ? null : existing?.LastPullUtc,
            scopeChanged
                ? $"Organization or project changed from {existing!.Organization}/{existing.Project}. Cards pulled from there stay on the board and are no longer updated."
                : existing?.LastReport), cancellationToken)
            ?? throw new AdoConfigException("That board no longer exists. Open the board again and retry.");
        if (!string.IsNullOrEmpty(secret))
            secrets.SaveToken(id, secret);
        if (scopeChanged)
            secrets.DeleteToken(existing!.Id);
        saved = await store.EnsureDedicatedAdoBoardAsync(projectPath, saved.Id, cancellationToken);
        return WithTokenFlag(saved);
    }

    /// <inheritdoc />
    public async Task<bool> UnlinkAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        using var held = pullLock.TryAcquire();
        if (held is null) throw new AdoConfigException(Busy);
        if (await store.GetBoardAsync(projectPath, boardId, cancellationToken) is null)
            return false;
        var connection = await store.GetAdoConnectionAsync(projectPath, boardId, cancellationToken);
        if (connection is null) return true;

        // Forget the credential first: even a failed database write cannot leave syncing active.
        // A retry can safely finish removing the connection without touching imported work.
        secrets.DeleteToken(connection.Id);
        if (!await store.DeleteAdoConnectionAsync(projectPath, boardId, connection.Id, cancellationToken))
            throw new AdoConfigException("The Azure DevOps connection changed. Reopen Board Settings and try again.");
        return true;
    }

    /// <summary>A pasted link, or (none given) the saved source.</summary>
    private static (AdoBoardLink Link, bool SameSource) ResolveSource(AdoConnectionSave save, BoardAdoConnectionRecord? existing)
    {
        if (!string.IsNullOrWhiteSpace(save.BoardLink))
        {
            var link = AdoBoardLink.Parse(save.BoardLink);
            return (link, existing is not null && SameSource(existing, link));
        }
        if (existing is not null)
            return (LinkOf(existing), true);
        throw new AdoConfigException("Paste the link of your Azure DevOps board or saved query.");
    }

    public async Task<AdoTestReport> TestAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        // A late test result must never recreate a connection after Unlink completes.
        using var held = pullLock.TryAcquire();
        if (held is null) throw new AdoConfigException(Busy);
        var connection = await RequireReady(projectPath, boardId, cancellationToken);
        var token = secrets.ReadToken(connection.Id)
            ?? throw new AdoConfigException("Save a personal access token before testing the connection.");
        var result = await ado.TestAsync(connection.Organization, connection.Project, token, cancellationToken);
        var expired = result.Outcome == AdoCallOutcome.Unauthorized;
        var error = result.Outcome == AdoCallOutcome.Ok ? null : result.Detail ?? "Azure DevOps refused the connection.";
        SourceResolution? source = null;
        if (error is null)
        {
            source = await ResolveSourceAsync(connection, token, cancellationToken);
            error = source.Error;
            expired |= source.Expired;
        }

        // Re-read: a save, a pull or another Connect may have written the row during the calls
        // above. Only this test's fields are applied, and the board's only while it is the same source.
        var current = await store.GetAdoConnectionAsync(projectPath, boardId, cancellationToken);
        if (current is not null && current.Id == connection.Id)
        {
            var same = source is not null && SameSource(current, LinkOf(connection));
            await store.SaveAdoConnectionAsync(current with
            {
                AuthStatus = expired ? BoardAdoAuthStatus.Expired : BoardAdoAuthStatus.Saved,
                LastTestedUtc = DateTime.UtcNow,
                AdoBoardName = same && source!.Name is { } name ? name : current.AdoBoardName,
                ColumnMap = same && source!.ColumnMap is { } map ? map : current.ColumnMap
            }, cancellationToken);
            if (same && current.QueryId is not null && !string.IsNullOrWhiteSpace(source!.Name))
                await store.EnsureDedicatedAdoBoardAsync(projectPath, current.Id, cancellationToken);
        }
        return new AdoTestReport(error is null, result.Value?.Name, error, source?.Summary);
    }

    private sealed record SourceResolution(AdoBoardSummary? Summary, string? Error, bool Expired, string? Name, string? ColumnMap);

    /// <summary>
    /// Reads what a pull will read, plus a work item count. A board whose configuration the token
    /// may not read degrades to a warning (lanes then match state names); anything that stops a
    /// pull is the error.
    /// </summary>
    private async Task<SourceResolution> ResolveSourceAsync(
        BoardAdoConnectionRecord connection, string token, CancellationToken cancellationToken)
    {
        var lanes = await store.GetColumnsAsync(connection.ProjectPath, cancellationToken, connection.BoardId);
        var plan = await PlanAsync(connection, token, lanes, cancellationToken);
        if (plan.Outcome != AdoCallOutcome.Ok)
            return new SourceResolution(null, plan.Detail ?? "Azure DevOps did not return the board.", plan.Outcome == AdoCallOutcome.Unauthorized, null, null);

        var warnings = new List<string>(plan.Warnings);
        string? name;
        string? queryType = null;
        if (connection.QueryId is { } queryId)
        {
            var query = await ado.GetQueryAsync(connection.Organization, connection.Project, queryId, token, cancellationToken);
            if (query.Outcome != AdoCallOutcome.Ok || query.Value is null)
                return new SourceResolution(null, query.Detail ?? $"Azure DevOps did not return saved query {queryId}.",
                    query.Outcome == AdoCallOutcome.Unauthorized, null, null);
            if (query.Value.IsFolder)
                return new SourceResolution(null, "That link is a query folder. Open a saved query and copy its link.", false, null, null);
            name = query.Value.Name;
            queryType = query.Value.QueryType;
        }
        else
            name = plan.Board?.Name is { Length: > 0 } boardName ? boardName : connection.BacklogLevel;

        var ids = await QueryIdsAsync(connection, plan, token, cancellationToken);
        switch (ids.Outcome)
        {
            case AdoCallOutcome.Unauthorized or AdoCallOutcome.BadQuery:
                return new SourceResolution(null, ids.Detail, ids.Outcome == AdoCallOutcome.Unauthorized, null, null);
            case not AdoCallOutcome.Ok:
                warnings.Add("Azure DevOps did not return a work item count. Pull now still works.");
                break;
        }
        var count = ids.Outcome == AdoCallOutcome.Ok ? ids.Value?.Ids.Count : null;

        var summary = new AdoBoardSummary(
            name, connection.Team, connection.BacklogLevel, connection.QueryId, queryType,
            count, count >= AzureDevOpsClient.MaxItems, plan.PointsField, plan.ColumnLanes ?? [], warnings);
        return new SourceResolution(summary, null, false, name,
            plan.Board is null ? null : TrackerColumnMap.Serialize(plan.Choices));
    }

    public async Task<AdoPullReport> PullAsync(string projectPath, string boardId, bool dryRun, CancellationToken cancellationToken)
    {
        var connection = await RequireReady(projectPath, boardId, cancellationToken);
        var token = secrets.ReadToken(connection.Id)
            ?? throw new AdoConfigException("Save a personal access token before pulling.");
        if (dryRun)
            return await PullConnectionAsync(connection, token, dryRun, cancellationToken);

        // One writing pull at a time across every VibeRails process on this machine.
        using var held = pullLock.TryAcquire();
        if (held is null)
            return Report(dryRun, "busy", 0, 0, 0, 0, "Another Azure DevOps pull is running. Try again when it finishes.");
        connection = await store.EnsureDedicatedAdoBoardAsync(projectPath, connection.Id, cancellationToken);
        var report = (await PullConnectionAsync(connection, token, dryRun, cancellationToken)) with { BoardId = connection.BoardId };
        await Remember(connection, report, cancellationToken);
        return report;
    }

    public async Task<bool> PullDueAsync(CancellationToken cancellationToken)
    {
        // A root backend that lost the scheduler lease mid-pull may still be pulling: skip rather
        // than run the same query twice. The scheduler tries again on its next tick.
        using var held = pullLock.TryAcquire();
        if (held is null)
            return false;
        // Tokens whose connection is gone (its board was deleted) are removed here.
        var connections = await store.GetAdoConnectionsAsync(cancellationToken);
        await secrets.PruneAsync(_ => Task.FromResult<IReadOnlyCollection<string>>(
            connections.Select(connection => connection.Id).ToList()), cancellationToken);
        foreach (var connection in connections)
        {
            // An expired token is refused until it is saved again, as RequireReady does for a
            // manual pull; retrying it every interval only hammers Azure DevOps with a dead token.
            if (!connection.Enabled || !connection.HasToken || !HasSource(connection)
                || connection.AuthStatus == BoardAdoAuthStatus.Expired)
                continue;
            var token = secrets.ReadToken(connection.Id);
            if (token is null)
                continue;
            try
            {
                var dedicated = await store.EnsureDedicatedAdoBoardAsync(connection.ProjectPath, connection.Id, cancellationToken);
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
        return true;
    }

    private async Task<AdoPullReport> PullConnectionAsync(
        BoardAdoConnectionRecord connection, string token, bool dryRun, CancellationToken cancellationToken)
    {
        var lanes = await store.GetColumnsAsync(connection.ProjectPath, cancellationToken, connection.BoardId);
        var overflowId = connection.OverflowColumnId;
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var unchanged = 0;
        var deleted = 0;
        var failed = 0;

        var plan = await PlanAsync(connection, token, lanes, cancellationToken);
        if (plan.Outcome != AdoCallOutcome.Ok)
            return Stopped(plan.Outcome, plan.Detail);
        var ids = await QueryIdsAsync(connection, plan, token, cancellationToken);
        if (ids.Outcome != AdoCallOutcome.Ok || ids.Value is null)
            return Stopped(ids.Outcome, ids.Detail);

        // Every batch is read before anything is applied, so a pull that stops part way writes nothing.
        var items = new List<AdoWorkItem>();
        foreach (var batch in ids.Value.Ids.Take(AzureDevOpsClient.MaxItems).Chunk(AzureDevOpsClient.BatchSize).Take(AzureDevOpsClient.MaxBatches))
        {
            var page = await ado.GetWorkItemsAsync(connection.Organization, connection.Project, batch, plan.Fields, token, cancellationToken);
            if (page.Outcome != AdoCallOutcome.Ok || page.Value is null)
                return Stopped(page.Outcome, page.Detail);
            items.AddRange(page.Value);
        }
        foreach (var item in items)
        {
            try
            {
                var pointsField = plan.PointsField
                    ?? (plan.DetectPoints ? AdoFieldMapping.DetectPointsField(item, plan.FieldSet) : null);
                var action = await ApplyWorkItemAsync(connection, plan, pointsField, item, lanes, overflowId, dryRun, cancellationToken);
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
        if (ids.Value.Ids.Count >= AzureDevOpsClient.MaxItems)
            message += $" Only the {AzureDevOpsClient.MaxItems} most recently changed work items were read.";
        return Report(dryRun, "ok", created, updated, skipped, failed, dryRun ? "Dry run. " + message : message);

        AdoPullReport Stopped(AdoCallOutcome outcome, string? detail) => outcome switch
        {
            AdoCallOutcome.Unauthorized => Report(dryRun, "expired", created, updated, skipped, failed, detail),
            // Retry-After is not waited for: the wait would hold the cross-process lock (blocking
            // saves, tests and unlinks) only to give up afterwards.
            AdoCallOutcome.RateLimited => Report(dryRun, "rate-limited", created, updated, skipped, failed,
                "Azure DevOps rate limited the pull. The next interval tries again."),
            AdoCallOutcome.BadQuery => Report(dryRun, "bad-query", created, updated, skipped, failed, detail),
            _ => Report(dryRun, "failed", created, updated, skipped, failed, detail ?? "Azure DevOps did not return the work items.")
        };
    }

    /// <summary>
    /// What one pull reads. A board connection re-reads the board configuration every pull, so a
    /// column change in Azure DevOps comes through; without it (403 or 404) lanes match by state
    /// name. Field discovery is required: requesting a field the organization lacks fails the batch.
    /// </summary>
    /// <param name="Board">The board configuration; null for a saved query, or when it was refused.</param>
    /// <param name="Columns">The board's columns that map at least one state, index-aligned with <paramref name="ColumnLanes"/>.</param>
    /// <param name="Choices">The column map to save: the board's current column names with the saved picks.</param>
    /// <param name="PointsField">The points field, when the override or the board's types name one.</param>
    /// <param name="DetectPoints">No override and no field from the board's types: detect each item's own.</param>
    private sealed record PullPlan(
        AdoCallOutcome Outcome, string? Detail,
        AdoBoardConfiguration? Board, string? Wiql, AdoFieldSet? FieldSet, IReadOnlyList<string> Fields,
        string? PointsField, bool DetectPoints,
        IReadOnlyList<AdoBoardColumn>? Columns, IReadOnlyList<TrackerColumnLane>? ColumnLanes,
        IReadOnlyList<TrackerColumnChoice> Choices, IReadOnlyList<string> Warnings)
    {
        public static PullPlan Stop(AdoCallOutcome outcome, string? detail) =>
            new(outcome, detail, null, null, null, [], null, false, null, null, [], []);
    }

    private async Task<PullPlan> PlanAsync(
        BoardAdoConnectionRecord connection, string token, IReadOnlyList<BoardColumnRecord> lanes, CancellationToken cancellationToken)
    {
        var discovered = await ado.GetFieldsAsync(connection.Organization, token, cancellationToken);
        if (discovered.Outcome != AdoCallOutcome.Ok || discovered.Value is null)
            return Stopped(discovered, "the organization's work item fields");
        var fieldSet = discovered.Value;
        var warnings = new List<string>();
        var pointsOverride = connection.PointsField;
        if (pointsOverride is not null && !fieldSet.Has(pointsOverride))
        {
            warnings.Add($"This organization has no field {pointsOverride}, so points stay off. Change the points field under Advanced.");
            pointsOverride = null;
        }

        if (connection.QueryId is not null)
        {
            var detect = connection.PointsField is null;
            return new PullPlan(AdoCallOutcome.Ok, null, null, null, fieldSet,
                RequestedFields(fieldSet, pointsOverride, detect), pointsOverride, detect, null, null, [], warnings);
        }

        var team = connection.Team!;
        var config = await ado.GetBoardAsync(connection.Organization, connection.Project, team, connection.BacklogLevel!, token, cancellationToken);
        switch (config.Outcome)
        {
            case AdoCallOutcome.Unauthorized or AdoCallOutcome.RateLimited:
                return PullPlan.Stop(config.Outcome, config.Detail);
            // Only a refusal falls back to state names. A transient failure stops the pull instead:
            // matching by name for one pull would place new cards away from their mapped lanes.
            case not (AdoCallOutcome.Ok or AdoCallOutcome.Forbidden or AdoCallOutcome.NotFound):
                return PullPlan.Stop(AdoCallOutcome.Failed,
                    (config.Detail ?? "Azure DevOps did not return the board's configuration.") + " The next interval tries again.");
        }
        var boardConfig = config.Outcome == AdoCallOutcome.Ok ? config.Value : null;
        if (boardConfig is null)
            warnings.Add($"{config.Detail ?? "Azure DevOps did not return the board's columns."} Lanes match state names to lane names.");

        var scope = await ado.GetTeamFieldValuesAsync(connection.Organization, connection.Project, team, token, cancellationToken);
        if (scope.Outcome != AdoCallOutcome.Ok || scope.Value is null)
            return Stopped(scope, $"area paths of team {team}");

        var columns = boardConfig?.Columns.Where(column => column.StateMappings.Count > 0).ToList();
        var types = boardConfig?.Types ?? [];
        var done = columns?.Where(column => column.IsOutgoing)
            .SelectMany(column => column.StateMappings.Select(mapping => (Type: mapping.Key, State: mapping.Value)))
            .Distinct()
            .ToList() ?? [];
        string wiql;
        try
        {
            wiql = AdoWiql.BoardQuery(types, boardConfig is null ? TypeCategory(connection.BacklogLevel) : null, scope.Value,
                done, connection.SkipOldDone == true, connection.NarrowWiql);
        }
        catch (AdoConfigException ex)
        {
            return PullPlan.Stop(AdoCallOutcome.Failed, ex.Message);
        }

        var pointsField = pointsOverride ?? (connection.PointsField is null ? AdoFieldMapping.DetectPointsField(types, fieldSet) : null);
        var detectFromItems = pointsField is null && connection.PointsField is null;
        IReadOnlyList<TrackerColumnChoice> choices = [];
        IReadOnlyList<TrackerColumnLane>? resolved = null;
        if (columns is not null)
        {
            choices = TrackerColumnMap.WithColumns(TrackerColumnMap.Parse(connection.ColumnMap), columns.Select(column => column.Name));
            resolved = TrackerColumnMap.Resolve(columns.Select(column => column.Name).ToList(),
                TrackerColumnMap.Parse(connection.ColumnMap), lanes, connection.OverflowColumnId, OverflowLaneName);
        }
        return new PullPlan(AdoCallOutcome.Ok, null, boardConfig, wiql, fieldSet,
            RequestedFields(fieldSet, pointsField, detectFromItems), pointsField, detectFromItems,
            columns, resolved, choices, warnings);

        static PullPlan Stopped<T>(AdoCallResult<T> result, string what) => PullPlan.Stop(
            result.Outcome is AdoCallOutcome.Unauthorized or AdoCallOutcome.RateLimited ? result.Outcome : AdoCallOutcome.Failed,
            result.Outcome is AdoCallOutcome.Unauthorized or AdoCallOutcome.RateLimited
                ? result.Detail
                : (result.Detail ?? $"Azure DevOps did not return the {what}.") + " The next interval tries again.");
    }

    private Task<AdoCallResult<AdoWorkItemIds>> QueryIdsAsync(
        BoardAdoConnectionRecord connection, PullPlan plan, string token, CancellationToken cancellationToken) =>
        connection.QueryId is { } queryId
            ? ado.RunSavedQueryAsync(connection.Organization, connection.Project, queryId, token, cancellationToken)
            : ado.QueryIdsAsync(connection.Organization, connection.Project, connection.Team!, plan.Wiql!, token, cancellationToken);

    /// <summary>The fields a batch asks for: the ones every organization has, the optional ones this
    /// one has, and the points field (or, to detect it from the items, every estimate field it has).</summary>
    private static IReadOnlyList<string> RequestedFields(AdoFieldSet fields, string? pointsField, bool detect)
    {
        var requested = new List<string>(AdoFields.Always);
        requested.AddRange(AdoFields.Optional.Where(fields.Has));
        if (pointsField is not null)
            requested.Add(pointsField);
        else if (detect)
            requested.AddRange(AdoFields.Estimates.Where(fields.Has));
        return requested.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The backlog category a board's work items belong to when its columns (which list the types)
    /// can't be read, so a refused configuration still never pulls tasks or test cases.
    /// </summary>
    internal static string TypeCategory(string? backlogLevel) => backlogLevel?.Trim().ToLowerInvariant() switch
    {
        "epics" => "Microsoft.EpicCategory",
        "features" => "Microsoft.FeatureCategory",
        _ => "Microsoft.RequirementCategory"
    };

    private enum ApplyAction { Skipped, Unchanged, SkippedDeleted, Created, Updated, CreatedOverflow }

    private async Task<ApplyAction> ApplyWorkItemAsync(
        BoardAdoConnectionRecord connection, PullPlan plan, string? pointsField, AdoWorkItem item,
        IReadOnlyList<BoardColumnRecord> lanes, string? overflowId, bool dryRun, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Title))
            throw new AdoConfigException("Work item has no title.");
        var link = await store.FindAdoLinkAsync(connection.Id, item.Id, cancellationToken);
        BoardCardRecord? existing = link is null ? null : await store.FindCardAsync(connection.ProjectPath, link.CardId, cancellationToken);
        // A soft-deleted card keeps its link: the work item is neither recreated nor updated, and
        // no overflow lane is made on its behalf. Decided before the dry-run count.
        if (link is not null && existing is null)
            return ApplyAction.SkippedDeleted;

        var mapped = AdoFieldMapping.Map(item, pointsField, existing?.Type);
        var mapping = TrackerLinkMapping.Serialize(pointsField, mapped.State);
        var applied = TrackerLinkMapping.Parse(link?.Mapping);
        // The same revision read the same way is skipped; a new points field re-reads it without
        // anyone editing the work item. Where the card sits never matters: a pull does not move cards.
        var unchanged = link is not null && item.Rev == link.Rev;
        if (unchanged && existing is not null && string.Equals(link!.Mapping, mapping, StringComparison.Ordinal))
            return ApplyAction.Unchanged;
        if (mapped.Title.Length == 0)
            throw new AdoConfigException("Work item title is empty after trimming.");

        if (dryRun)
            return existing is null ? ApplyAction.Created : ApplyAction.Updated;

        var key = AdoFieldMapping.Key(item.Id);
        if (existing is null)
        {
            // Only a new card is placed. The overflow lane is made on demand for one whose column
            // or state has no lane; the lane list is refreshed by the caller afterwards.
            var placement = Place(plan, item, mapped.State, lanes, overflowId);
            var columnId = placement.Lane.ColumnId;
            var madeLane = columnId is null;
            columnId ??= await EnsureOverflowLaneAsync(connection, lanes, cancellationToken);
            // Card and link commit together: a crash or a concurrent pull between them would leave
            // an unlinked card, and the next pull would create a duplicate.
            var card = await store.CreateAdoCardAsync(connection.ProjectPath, new NewBoardCard(
                columnId, mapped.Title, mapped.Description, null, mapped.Priority, mapped.Points, mapped.Tags,
                false, null, mapped.Type, connection.BoardId, false),
                new BoardAdoLinkRecord(string.Empty, connection.Id, item.Id, item.Rev, item.ChangedDate, mapped.AssigneeDisplay,
                    DateTime.UtcNow, mapping),
                cancellationToken);
            if (card is null)
                return ApplyAction.Skipped;
            if (placement.Lane.Match != TrackerLaneMatch.Matched)
                await board.AddCommentAsync(connection.ProjectPath, card.Id, AdoAuthor, placement.OverflowComment, cancellationToken, syncToJira: false);
            return madeLane ? ApplyAction.CreatedOverflow : ApplyAction.Created;
        }

        await store.UpdateCardAsync(connection.ProjectPath, existing.Id, new BoardCardPatch(
            Title: mapped.Title,
            Description: mapped.Description,
            Priority: mapped.Priority,
            Points: mapped.Points,
            ClearPoints: mapped.ClearPoints,
            Tags: mapped.Tags.ToList(),
            Type: mapped.Type), cancellationToken, AdoAuthor);
        // The card keeps its lane and board: a state change is noted, never applied. Record the
        // new state first: a failed comment is then lost once, instead of being posted again by
        // every later pull against a link that still holds the old state.
        var stateChanged = !unchanged && applied.Status is not null
            && !string.Equals(applied.Status, mapped.State, StringComparison.Ordinal);
        await store.UpdateAdoLinkAsync(new BoardAdoLinkRecord(
            existing.Id, connection.Id, item.Id, item.Rev, item.ChangedDate, mapped.AssigneeDisplay, DateTime.UtcNow, mapping), cancellationToken);
        if (stateChanged)
            await board.AddCommentAsync(connection.ProjectPath, existing.Id, AdoAuthor,
                StateChangeComment(key, applied.Status!, mapped.State), cancellationToken, syncToJira: false);
        return ApplyAction.Updated;
    }

    private sealed record Placement(TrackerLaneDecision Lane, string OverflowComment);

    /// <summary>
    /// Where a new card goes: the board column it is in, else the lane named like its state. A
    /// null lane means the overflow lane, which does not exist yet.
    /// </summary>
    private static Placement Place(
        PullPlan plan, AdoWorkItem item, string state, IReadOnlyList<BoardColumnRecord> lanes, string? overflowId)
    {
        var key = AdoFieldMapping.Key(item.Id);
        if (plan.Columns is { } columns && plan.ColumnLanes is { } resolved
            && ColumnIndex(columns, item.BoardColumn, item.WorkItemType, state) is var index and >= 0)
            return new Placement(TrackerFieldMapping.DecideLane(resolved[index].LaneId, null, lanes, overflowId),
                ColumnOverflowComment(key, columns[index].Name));
        return new Placement(TrackerFieldMapping.MatchLane(state, null, lanes, overflowId), OverflowComment(key, state));
    }

    /// <summary>
    /// The board column a work item is in, or -1. <c>System.BoardColumn</c> counts only when it
    /// names a column of this board that maps the item's type to its current state: it tells apart
    /// columns that share one state, and a value from another team's board (teams sharing an area
    /// path) that disagrees with this one is ignored. Otherwise, the first column that maps the
    /// item's type to its state.
    /// </summary>
    internal static int ColumnIndex(IReadOnlyList<AdoBoardColumn> columns, string? boardColumn, string type, string state)
    {
        bool Maps(AdoBoardColumn column) =>
            column.StateMappings.TryGetValue(type.Trim(), out var mapped)
            && string.Equals(mapped, state.Trim(), StringComparison.OrdinalIgnoreCase);

        var name = boardColumn?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase) && Maps(columns[i]))
                    return i;
            }
        }
        for (var i = 0; i < columns.Count; i++)
        {
            if (Maps(columns[i]))
                return i;
        }
        return -1;
    }

    private async Task<string> EnsureOverflowLaneAsync(
        BoardAdoConnectionRecord connection, IReadOnlyList<BoardColumnRecord> lanes, CancellationToken cancellationToken)
    {
        var existing = lanes.FirstOrDefault(lane => string.Equals(lane.Name, OverflowLaneName, StringComparison.OrdinalIgnoreCase))
            ?? (connection.OverflowColumnId is null ? null
                : lanes.FirstOrDefault(lane => lane.Id == connection.OverflowColumnId));
        var column = existing ?? await store.CreateColumnAsync(connection.ProjectPath, OverflowLaneName, "#0078d4", cancellationToken, connection.BoardId);
        if (connection.OverflowColumnId != column.Id)
        {
            // Re-read so a save or Connect made while this pull ran is not written back over.
            var current = await store.GetAdoConnectionAsync(connection.ProjectPath, connection.BoardId, cancellationToken);
            if (current is not null && current.Id == connection.Id && current.OverflowColumnId != column.Id)
                await store.SaveAdoConnectionAsync(current with { OverflowColumnId = column.Id }, cancellationToken);
        }
        return column.Id;
    }

    private async Task<string?> RefreshOverflowAsync(BoardAdoConnectionRecord connection, CancellationToken cancellationToken) =>
        (await store.GetAdoConnectionAsync(connection.ProjectPath, connection.BoardId, cancellationToken))?.OverflowColumnId;

    private static string StateChangeComment(string key, string before, string after) =>
        $"{key} is now \"{after}\" in Azure DevOps (was \"{before}\"). The card was not moved: its lane and board are local.";

    private static string OverflowComment(string key, string state) =>
        $"{key} is in the Azure DevOps state \"{state}\", which matches no lane on this board. Parked in {OverflowLaneName}. No lane was created for that state.";

    private static string ColumnOverflowComment(string key, string column) =>
        $"{key} is in the Azure DevOps column \"{column}\", which has no lane on this board. Parked in {OverflowLaneName}. Pick a lane for that column under Board settings, Azure DevOps, Advanced.";

    private async Task Remember(BoardAdoConnectionRecord connection, AdoPullReport report, CancellationToken cancellationToken)
    {
        var current = await store.GetAdoConnectionAsync(connection.ProjectPath, connection.BoardId, cancellationToken) ?? connection;
        var expired = report.Outcome == "expired";
        var badQuery = report.Outcome == "bad-query";
        await store.SaveAdoConnectionAsync(current with
        {
            AuthStatus = expired ? BoardAdoAuthStatus.Expired : current.AuthStatus,
            Enabled = badQuery ? false : current.Enabled,
            DisabledReason = badQuery ? Trim(report.Message) : current.DisabledReason,
            LastPullUtc = DateTime.UtcNow,
            LastReport = Trim($"{report.Outcome}: {report.Message}")
        }, cancellationToken);
    }

    private async Task<BoardAdoConnectionRecord> RequireReady(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        var connection = await store.GetAdoConnectionAsync(projectPath, boardId, cancellationToken)
            ?? throw new AdoConfigException("Save the Azure DevOps connection before using it.");
        if (!HasSource(connection))
            throw new AdoConfigException("Paste an Azure DevOps board or saved query link and connect before pulling.");
        if (connection.AuthStatus == BoardAdoAuthStatus.Expired)
            throw new AdoConfigException("The Azure DevOps connection expired. Save the personal access token again and test it.");
        return connection;
    }

    /// <summary>An organization and project, plus a team board or a saved query.</summary>
    private static bool HasSource(BoardAdoConnectionRecord connection) =>
        AdoBoardLink.IsOrganization(connection.Organization)
        && AdoBoardLink.IsName(connection.Project)
        && (connection.QueryId is not null
            || (AdoBoardLink.IsName(connection.Team) && AdoBoardLink.IsName(connection.BacklogLevel)));

    private static AdoBoardLink LinkOf(BoardAdoConnectionRecord connection) => new(
        connection.Organization, connection.Project, connection.Team, connection.BacklogLevel, connection.QueryId, connection.BoardLink);

    private static bool SameSource(BoardAdoConnectionRecord connection, AdoBoardLink link) =>
        Same(connection.Organization, link.Organization)
        && Same(connection.Project, link.Project)
        && Same(connection.Team, link.Team)
        && Same(connection.BacklogLevel, link.BacklogLevel)
        && Same(connection.QueryId, link.QueryId);

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private BoardAdoConnectionRecord WithTokenFlag(BoardAdoConnectionRecord connection) =>
        connection with { HasToken = secrets.HasToken(connection.Id) };

    private static AdoPullReport Report(bool dryRun, string outcome, int created, int updated, int skipped, int failed, string? message) =>
        new(dryRun, outcome, created, updated, skipped, failed, message);

    private static string? Trim(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= 500 ? value : value[..500];
}

/// <summary>Starts the pull about every 15 minutes from the root scheduler that holds the lease.</summary>
public interface IAdoPullScheduler
{
    /// <summary>
    /// Starts a pull when one is due and none is running, and returns without waiting for it. A
    /// pull can take minutes, far longer than the scheduler lease, so it must not hold up the
    /// Automation cycle. Returns the started pull, or null when nothing was started.
    /// </summary>
    Task? Tick(DateTime nowUtc, CancellationToken stoppingToken);

    /// <summary>Completes when no pull is running. A running pull observes the stopping token.</summary>
    Task WhenIdleAsync();
}

/// <summary>
/// Singleton so the intervals survive between scheduler cycles, with Jira's cadence. The pull
/// service is scoped (it needs the scoped <see cref="IBoardService"/>), so each pull resolves it
/// from a fresh scope. The pull and the activity drain share the cross-process Azure DevOps lock,
/// so a due pull is started ahead of the drain and the drain waits out a running pull; a pull
/// that still found the lock held (another process) is due again on the next tick instead of
/// after a whole interval.
/// </summary>
public sealed class AdoPullScheduler(IServiceScopeFactory scopeFactory) : IAdoPullScheduler
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DeliveryInterval = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private DateTime _nextUtc = DateTime.MinValue;
    private Task _running = Task.CompletedTask;
    private Task _delivering = Task.CompletedTask;
    private DateTime _nextDeliveryUtc = DateTime.MinValue;

    public Task? Tick(DateTime nowUtc, CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            if (_running.IsCompleted && nowUtc >= _nextUtc)
            {
                _nextUtc = nowUtc.Add(Interval);
                return _running = Task.Run(() => PullAsync(nowUtc, stoppingToken), CancellationToken.None);
            }
            if (_running.IsCompleted && _delivering.IsCompleted && nowUtc >= _nextDeliveryUtc)
            {
                _nextDeliveryUtc = nowUtc.Add(DeliveryInterval);
                _delivering = Task.Run(() => DeliverAsync(stoppingToken), CancellationToken.None);
            }
            return null;
        }
    }

    public Task WhenIdleAsync()
    {
        lock (_gate)
            return Task.WhenAll(_running, _delivering);
    }

    // Never throws. Delivery detail stays on the card: no token, body or sharing URL is logged.
    private async Task DeliverAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<AdoDeliveryService>() is { } delivery)
                await delivery.DrainAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { Log.Warning("[AzureDevOps] Activity delivery could not complete; inspect card delivery status."); }
    }

    // Never throws: the task is observed only by WhenIdleAsync at shutdown.
    private async Task PullAsync(DateTime nowUtc, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            if (!await scope.ServiceProvider.GetRequiredService<IAdoPullService>().PullDueAsync(stoppingToken))
            {
                // Nothing was pulled, so the interval was not spent: due again on the next tick.
                lock (_gate)
                    _nextUtc = nowUtc;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AzureDevOps] Scheduled pull failed");
        }
    }
}
