using VibeRails.Services.Board;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Services.Jira;

/// <summary>Creates the existing public replay capability for an explicitly linked Jira session.</summary>
public interface IJiraSessionSharing
{
    Task<SessionShareResponse> CreateAsync(Guid sessionId, string displayName, CancellationToken cancellationToken);
}

public sealed class JiraSessionSharing(SessionSharingService sharing) : IJiraSessionSharing
{
    public Task<SessionShareResponse> CreateAsync(Guid sessionId, string displayName, CancellationToken cancellationToken) =>
        sharing.CreateAsync(sessionId, displayName, cancellationToken);
}

/// <summary>Drains future local Board activity under the same lock as Jira connection edits and pulls.</summary>
public sealed class JiraDeliveryService(IBoardStore store, IJiraSecretStore secrets, IJiraCommentClient client,
    IJiraSessionSharing sharing, JiraPullLock jiraLock)
{
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        using var held = jiraLock.TryAcquire();
        if (held is null) return;
        await store.RecoverJiraDeliveriesAsync(cancellationToken);
        var connections = await store.GetJiraConnectionsAsync(cancellationToken);
        foreach (var delivery in (await store.GetPendingJiraDeliveriesAsync(cancellationToken)).Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = connections.SingleOrDefault(c => c.Id == delivery.ConnectionId
                && string.Equals(BoardPaths.NormalizeProjectPath(c.ProjectPath), delivery.ProjectPath, BoardPaths.ProjectPathComparison));
            var card = await store.GetCardDetailAsync(delivery.ProjectPath, delivery.CardId, cancellationToken);
            var links = await store.GetJiraLinksForCardAsync(delivery.ProjectPath, delivery.CardId, cancellationToken);
            if (connection is null || card is null || !links.Any(l => l.SiteId == delivery.ConnectionId && l.IssueId == delivery.IssueId)
                || (delivery.Kind == "session" && !card.Sessions.Any(s => s.SessionId == delivery.SourceId))
                || (delivery.Kind == "comment" && !card.Comments.Concat(card.Notes).Any(c => c.Id == delivery.SourceId)))
            {
                await FinishAsync(delivery, "pending", "cancelled", "The Jira connection or source activity is no longer linked.", null, cancellationToken);
                continue;
            }
            var token = secrets.ReadToken(connection.Id);
            if (string.IsNullOrWhiteSpace(token))
            {
                await FinishAsync(delivery, "pending", "failed", "Reconnect Jira in Board settings, then post the update again.", null, cancellationToken);
                continue;
            }
            if (!await store.SetJiraDeliveryAsync(delivery.Id, "pending", "sending", null, null, cancellationToken)) continue;
            try
            {
                var url = delivery.Url;
                if (delivery.Kind == "session" && url is null)
                {
                    if (!Guid.TryParse(delivery.SourceId, out var sessionId))
                        throw new JiraConfigException("The linked session does not have a valid recording ID.");
                    var name = $"{links.First(l => l.SiteId == connection.Id).IssueKey} · {delivery.Body}";
                    var share = await sharing.CreateAsync(sessionId, name[..Math.Min(name.Length, 160)], cancellationToken);
                    if (!share.Success)
                    {
                        await FinishAsync(delivery, "sending", "failed", share.Message, null, cancellationToken);
                        continue;
                    }
                    url = share.Url;
                    // Save the capability before posting. No blind re-creation or re-post after a crash.
                    await store.SetJiraDeliveryAsync(delivery.Id, "sending", "sending", null, url, cancellationToken);
                }
                var result = await client.AddCommentAsync(connection.SiteUrl, connection.Email, token, delivery.IssueId,
                    delivery.Body, url is null ? [] : [url], cancellationToken);
                await FinishAsync(delivery, "sending", result.Success ? "sent" : result.MayHavePosted ? "uncertain" : "failed",
                    result.Message, url, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (JiraConfigException ex)
            {
                await FinishAsync(delivery, "sending", "failed", ex.Message, null, cancellationToken);
            }
            catch (Exception)
            {
                await FinishAsync(delivery, "sending", "uncertain",
                    "Delivery could not be confirmed. Check Jira and Sharing links before posting again.", null, cancellationToken);
            }
        }
    }

    private Task<bool> FinishAsync(BoardJiraDelivery delivery, string expected, string status, string message, string? url, CancellationToken ct) =>
        store.SetJiraDeliveryAsync(delivery.Id, expected, status, message, url, ct);
}
