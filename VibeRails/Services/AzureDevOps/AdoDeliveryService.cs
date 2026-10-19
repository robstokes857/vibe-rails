using VibeRails.Services.Board;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Services.AzureDevOps;

/// <summary>Creates the existing public replay capability for an explicitly linked Azure DevOps session.</summary>
public interface IAdoSessionSharing
{
    Task<SessionShareResponse> CreateAsync(Guid sessionId, string displayName, CancellationToken cancellationToken);
}

public sealed class AdoSessionSharing(SessionSharingService sharing) : IAdoSessionSharing
{
    public Task<SessionShareResponse> CreateAsync(Guid sessionId, string displayName, CancellationToken cancellationToken) =>
        sharing.CreateAsync(sessionId, displayName, cancellationToken);
}

/// <summary>
/// Drains future local Board activity to linked work items under the same lock as Azure DevOps
/// connection edits and pulls, mirroring <c>JiraDeliveryService</c>. Each event goes pending →
/// sending → sent, failed or uncertain, and an uncertain send is never retried automatically.
/// </summary>
public sealed class AdoDeliveryService(IBoardStore store, IAdoSecretStore secrets, IAdoCommentClient client,
    IAdoSessionSharing sharing, AdoPullLock pullLock)
{
    /// <summary>The most events one drain sends; the rest wait for the next drain.</summary>
    public const int MaxPerDrain = 20;

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        using var held = pullLock.TryAcquire();
        if (held is null) return;
        // A claim left in sending by a process that stopped mid-POST becomes uncertain, never pending.
        await store.RecoverAdoDeliveriesAsync(cancellationToken);
        var connections = await store.GetAdoConnectionsAsync(cancellationToken);
        foreach (var delivery in (await store.GetPendingAdoDeliveriesAsync(cancellationToken)).Take(MaxPerDrain))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = connections.SingleOrDefault(c => c.Id == delivery.ConnectionId
                && string.Equals(BoardPaths.NormalizeProjectPath(c.ProjectPath), delivery.ProjectPath, BoardPaths.ProjectPathComparison));
            var card = await store.GetCardDetailAsync(delivery.ProjectPath, delivery.CardId, cancellationToken);
            var links = await store.GetAdoLinksForCardAsync(delivery.ProjectPath, delivery.CardId, cancellationToken);
            if (connection is null || card is null
                || !links.Any(l => l.ConnectionId == delivery.ConnectionId && l.WorkItemId == delivery.WorkItemId)
                || (delivery.Kind == "session" && !card.Sessions.Any(s => s.SessionId == delivery.SourceId))
                || (delivery.Kind == "comment" && !card.Comments.Concat(card.Notes).Any(c => c.Id == delivery.SourceId)))
            {
                await FinishAsync(delivery, "pending", "cancelled", "The Azure DevOps connection or source activity is no longer linked.", null, cancellationToken);
                continue;
            }
            var token = secrets.ReadToken(connection.Id);
            if (string.IsNullOrWhiteSpace(token))
            {
                await FinishAsync(delivery, "pending", "failed", "Reconnect Azure DevOps in Board settings, then post the update again.", null, cancellationToken);
                continue;
            }
            if (!await store.SetAdoDeliveryAsync(delivery.Id, "pending", "sending", null, null, cancellationToken)) continue;
            try
            {
                var url = delivery.Url;
                if (delivery.Kind == "session" && url is null)
                {
                    if (!Guid.TryParse(delivery.SourceId, out var sessionId))
                        throw new AdoConfigException("The linked session does not have a valid recording ID.");
                    var name = $"{AdoFieldMapping.Key(delivery.WorkItemId)} · {delivery.Body}";
                    var share = await sharing.CreateAsync(sessionId, name[..Math.Min(name.Length, 160)], cancellationToken);
                    if (!share.Success)
                    {
                        // A local "busy" (another link is being created in this process) definitely
                        // sent nothing, so the event goes back to pending for the next drain. Every
                        // other outcome stays failed: a request whose network outcome is unknown is
                        // never retried blindly, and a missing account would block the queue.
                        await FinishAsync(delivery, "sending", share.Status == "busy" ? "pending" : "failed",
                            share.Message, null, cancellationToken);
                        continue;
                    }
                    url = share.Url;
                    // Save the capability before posting. No blind re-creation or re-post after a crash.
                    await store.SetAdoDeliveryAsync(delivery.Id, "sending", "sending", null, url, cancellationToken);
                }
                var result = await client.AddCommentAsync(connection.Organization, connection.Project, token, delivery.WorkItemId,
                    delivery.Body, url is null ? [] : [url], cancellationToken);
                await FinishAsync(delivery, "sending", result.Success ? "sent" : result.MayHavePosted ? "uncertain" : "failed",
                    result.Message, url, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (AdoConfigException ex)
            {
                await FinishAsync(delivery, "sending", "failed", ex.Message, null, cancellationToken);
            }
            catch (Exception)
            {
                await FinishAsync(delivery, "sending", "uncertain",
                    "Delivery could not be confirmed. Check Azure DevOps and Sharing links before posting again.", null, cancellationToken);
            }
        }
    }

    private Task<bool> FinishAsync(BoardAdoDelivery delivery, string expected, string status, string message, string? url, CancellationToken ct) =>
        store.SetAdoDeliveryAsync(delivery.Id, expected, status, message, url, ct);
}

/// <summary>Resolves a card's retained work item identity before making one explicit outbound comment.</summary>
public sealed class AdoCommentService(IBoardStore store, IAdoSecretStore secrets, IAdoCommentClient client, AdoPullLock pullLock)
{
    /// <summary>
    /// Posts using the original work item and connection, even when the card has moved to another
    /// local board. <c>Url</c> is the work item page: Azure DevOps documents no per-comment link.
    /// </summary>
    public async Task<(AdoCommentResult Result, string WorkItemKey, string? Url)> PostAsync(
        string project, string cardId, string body, IReadOnlyList<string>? sessionLinks, CancellationToken cancellationToken)
    {
        var content = AzureDevOpsClient.ValidateComment(body, sessionLinks);
        using var held = pullLock.TryAcquire()
            ?? throw new AdoConfigException("Azure DevOps is busy connecting or pulling. No comment was posted; try again when it finishes.");
        var links = await store.GetAdoLinksForCardAsync(project, cardId, cancellationToken);
        var connections = await store.GetAdoConnectionsAsync(cancellationToken);
        var targets = (from link in links
                       join connection in connections on link.ConnectionId equals connection.Id
                       where string.Equals(BoardPaths.NormalizeProjectPath(project), BoardPaths.NormalizeProjectPath(connection.ProjectPath),
                           BoardPaths.ProjectPathComparison)
                       select (Link: link, Connection: connection)).ToList();
        if (targets.Count == 0)
            throw new AdoConfigException("This card has no connected Azure DevOps work item. Connect or pull its Azure DevOps board in Board settings first.");
        if (targets.Count != 1)
            throw new AdoConfigException("This card has multiple connected Azure DevOps work items. No comment was posted because the destination is ambiguous.");
        var (workItem, saved) = targets[0];
        var token = secrets.ReadToken(saved.Id);
        if (string.IsNullOrWhiteSpace(token))
            throw new AdoConfigException("No personal access token is saved for this Azure DevOps connection. Reconnect Azure DevOps in Board settings.");
        var result = await client.AddCommentAsync(saved.Organization, saved.Project, token, workItem.WorkItemId,
            content.Body, content.Links, cancellationToken);
        // Built from the saved organization, project and numeric id. Never trust a response URL.
        var url = result.Success ? AdoBoardLink.WorkItemUrl(saved.Organization, saved.Project, workItem.WorkItemId) : null;
        return (result, AdoFieldMapping.Key(workItem.WorkItemId), url);
    }
}
