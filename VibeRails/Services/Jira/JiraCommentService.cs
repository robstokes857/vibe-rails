using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services.Board;

namespace VibeRails.Services.Jira;

/// <summary>Resolves a card's retained Jira identity before making one explicit outbound comment.</summary>
public sealed class JiraCommentService(IBoardStore store, IJiraSecretStore secrets, IJiraCommentClient client, JiraPullLock jiraLock)
{
    /// <summary>Posts using the original issue/connection, even when the card has moved to another local board.</summary>
    public async Task<(JiraCommentResult Result, string IssueKey, string? Url)> PostAsync(
        string project, string cardId, string body, IReadOnlyList<string>? sessionLinks, CancellationToken cancellationToken)
    {
        var content = JiraCloudClient.ValidateComment(body, sessionLinks);
        using var held = jiraLock.TryAcquire()
            ?? throw new JiraConfigException("Jira is busy connecting or pulling. No comment was posted; try again when it finishes.");
        var links = await store.GetJiraLinksForCardAsync(project, cardId, cancellationToken);
        var connections = await store.GetJiraConnectionsAsync(cancellationToken);
        var targets = (from link in links
                       join connection in connections on link.SiteId equals connection.Id
                       where string.Equals(BoardPaths.NormalizeProjectPath(project), BoardPaths.NormalizeProjectPath(connection.ProjectPath),
                           BoardPaths.ProjectPathComparison)
                       select (Link: link, Connection: connection)).ToList();
        if (targets.Count == 0)
            throw new JiraConfigException("This card has no connected Jira issue. Connect or pull its Jira board in Board settings first.");
        if (targets.Count != 1)
            throw new JiraConfigException("This card has multiple connected Jira issues. No comment was posted because the destination is ambiguous.");
        var (issue, saved) = targets[0];
        var token = secrets.ReadToken(saved.Id);
        if (string.IsNullOrWhiteSpace(token))
            throw new JiraConfigException("No API token is saved for this Jira connection. Reconnect Jira in Board settings.");
        var result = await client.AddCommentAsync(saved.SiteUrl, saved.Email, token, issue.IssueId,
            content.Body, content.Links, cancellationToken);
        // Use the stable numeric identity for the issue page too. Never trust a response URL.
        var url = result.Success
            ? JiraSite.Parse(saved.SiteUrl).Origin + "/secure/ViewIssue.jspa?id=" + Uri.EscapeDataString(issue.IssueId)
                + "&focusedCommentId=" + Uri.EscapeDataString(result.CommentId!)
            : null;
        return (result, issue.IssueKey, url);
    }
}

/// <summary>Shared outbound Jira composition for the root and the local stdio MCP host.</summary>
public static class JiraServiceRegistration
{
    /// <summary>Registers Jira clients and commenting without starting a scheduler or listener.</summary>
    public static IServiceCollection AddJiraClients(this IServiceCollection services)
    {
        services.AddHttpClient(JiraCloudClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<IJiraSecretStore, JiraSecretStore>();
        services.AddSingleton(sp => new JiraCloudClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(JiraCloudClient.HttpClientName)));
        services.AddSingleton<IJiraCloudClient>(sp => sp.GetRequiredService<JiraCloudClient>());
        services.AddSingleton<IJiraCommentClient>(sp => sp.GetRequiredService<JiraCloudClient>());
        services.AddSingleton(_ => JiraPullLock.BesideStateDatabase());
        services.AddScoped<JiraCommentService>();
        return services;
    }
}
