using System.ComponentModel;
using ModelContextProtocol.Server;
using VibeRails.Services.Jira;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    /// <summary>Explicitly publishes a completion/update comment to the card's connected Jira issue.</summary>
    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Post a comment to the Jira issue linked to a VibeRails card, using the saved Jira connection. Use when the user asks to report work to Jira. Include a completion summary and optionally one or more sessionLinks from create_session_share_link or earlier Board handoffs. Links are public: anyone holding one can read its session; active sessions replay after ending/uploading and links expire after one month. This tool does not create shares, finish/move cards, or change Jira status. Save/reuse each session's link rather than creating duplicates. This is an external write, separate from local Board-tool authorization. No automatic retries: if delivery is unconfirmed, inspect Jira before calling again. A successful call records a receipt in Board Comments.")]
    public async Task<string> AddJiraComment(
        [Description("Plain-text completion summary or comment, 1 to 20000 characters. Newlines are preserved.")] string body,
        [Description("Public VibeRails replay URLs for the sessions used to finish the work (at most 20). Reuse links from previous sessions' handoffs. Optional; omitted posts text only.")] string[]? sessionLinks = null,
        [Description(CardArgumentHelp)] string? card = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null)
    {
        try
        {
            var content = JiraCloudClient.ValidateComment(body, sessionLinks);
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            if (await ResolveAuthorAsync(server, cancellationToken) is not { } author) return UnnamedClientHint;
            if (jiraComments is null) return "FAIL: Jira commenting is unavailable in this host.";
            var (result, issueKey, url) = await jiraComments.PostAsync(target.Project, target.CardId!, content.Body, content.Links, cancellationToken);
            if (!result.Success) return (result.MayHavePosted ? "UNCONFIRMED: " : "FAIL: ") + result.Message;

            var confirmation = $"Posted Jira comment {result.CommentId} to {issueKey}: {url}";
            try
            {
                var receipt = confirmation + "\n\n" + content.Body;
                if (content.Links.Count > 0) receipt += "\n\nVibeRails sessions:\n" + string.Join('\n', content.Links);
                if (await service.AddCommentAsync(target.Project, target.CardId!, author, receipt, cancellationToken) is null)
                    return confirmation + "\nThe Board receipt could not be saved. Do not repost the Jira comment.";
            }
            catch (Exception)
            {
                // Jira has committed already. A local receipt failure must not invite another POST.
                return confirmation + "\nThe Board receipt could not be saved. Do not repost the Jira comment.";
            }
            return confirmation + $"\nReceipt saved on {target.CardKey}.";
        }
        catch (JiraConfigException ex) { return "FAIL: " + ex.Message; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return "UNCONFIRMED: Could not complete Jira commenting. Check the issue before retrying; no successful delivery was confirmed.";
        }
    }
}
