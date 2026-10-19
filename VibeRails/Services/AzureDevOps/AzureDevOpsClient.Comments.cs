using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.VCA;

namespace VibeRails.Services.AzureDevOps;

/// <summary>The outcome of one non-retried Azure DevOps comment POST, in the Jira comment result's shape.</summary>
public sealed record AdoCommentResult(bool Success, bool MayHavePosted, string Message, string? CommentId = null);

/// <summary>Posts comments to a work item in the saved organization using its saved token.</summary>
public interface IAdoCommentClient
{
    /// <summary>Posts plain text and explicit public replay links as HTML paragraphs.</summary>
    Task<AdoCommentResult> AddCommentAsync(string organization, string project, string token, int workItemId,
        string body, IReadOnlyList<string> sessionLinks, CancellationToken cancellationToken);
}

public sealed partial class AzureDevOpsClient
{
    /// <summary>Work item comments are still a preview API in 7.1.</summary>
    public const string CommentsApiVersion = "7.1-preview.4";
    internal const int MaxCommentCharacters = 20_000;
    internal const int MaxSessionLinks = 20;
    private const int MaxCommentResponseBytes = 256 * 1024;
    private const string UnconfirmedComment = "Azure DevOps comment delivery could not be confirmed. Check the work item before retrying; the comment may already exist.";
    private const string RefusedComment = "Azure DevOps refused the saved personal access token. Reconnect Azure DevOps in Board settings.";

    /// <summary>
    /// <c>POST {org}/{project}/_apis/wit/workItems/{id}/comments?format=html</c>, never retried. The
    /// text is HTML-encoded; the only markup added is paragraphs, line breaks and a link for each
    /// vetted public replay URL. A refused token (401, or the 203/302 sign-in answer, or a 2xx that
    /// isn't JSON) certainly posted nothing. A 5xx, a network failure, a missed deadline or an
    /// unreadable success may have posted, so it is unconfirmed.
    /// </summary>
    public async Task<AdoCommentResult> AddCommentAsync(string organization, string project, string token, int workItemId,
        string body, IReadOnlyList<string> sessionLinks, CancellationToken cancellationToken)
    {
        if (workItemId <= 0)
            throw new AdoConfigException("The saved Azure DevOps work item id is invalid. Pull the board again.");
        var content = ValidateComment(body, sessionLinks);
        // Organization and project are validated and escaped here, before anything is sent.
        var url = Project(organization, project) + "/_apis/wit/workItems/" + workItemId.ToString(CultureInfo.InvariantCulture) + "/comments";
        using var request = Request(HttpMethod.Post, url, token, "format=html", CommentsApiVersion);
        request.Content = Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("text", WriteComment(content.Body, content.Links));
            writer.WriteEndObject();
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
            {
                var message = response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "Azure DevOps rejected the comment. Check the comment text and the work item.",
                    HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation or HttpStatusCode.Redirect => RefusedComment,
                    HttpStatusCode.Forbidden => "This Azure DevOps token may not comment on the work item. It needs the Work Items (Read & write) scope.",
                    HttpStatusCode.NotFound => "The linked Azure DevOps work item was not found or is not visible to this token.",
                    HttpStatusCode.TooManyRequests => "Azure DevOps rate limited the comment. Wait before trying again.",
                    _ => UnconfirmedComment
                };
                return new(false, message == UnconfirmedComment, message);
            }
            if (!IsJson(response))
                return new(false, false, RefusedComment);

            var bytes = await SessionSharingService.ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(deadline.Token), MaxCommentResponseBytes, deadline.Token);
            using var document = JsonDocument.Parse(bytes);
            return ReadInt(document.RootElement, "id") is int id and > 0
                ? new(true, false, "Comment posted to Azure DevOps.", id.ToString(CultureInfo.InvariantCulture))
                : new(false, true, UnconfirmedComment);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Response bodies, public capabilities, the token and exception prose stay out of logs and replies.
            return new(false, true, UnconfirmedComment);
        }
    }

    /// <summary>The Jira comment rules: 1 to 20,000 characters of text and at most 20 distinct public replay URLs.</summary>
    internal static (string Body, IReadOnlyList<string> Links) ValidateComment(string? body, IReadOnlyList<string>? links)
    {
        var text = body?.Trim() ?? string.Empty;
        if (text.Length is 0 or > MaxCommentCharacters)
            throw new AdoConfigException($"Comment text must contain 1 to {MaxCommentCharacters} characters.");
        if (links?.Count > MaxSessionLinks)
            throw new AdoConfigException($"Include at most {MaxSessionLinks} session links in one comment.");
        var normalized = new List<string>();
        foreach (var value in links ?? [])
        {
            var link = value?.Trim();
            if (link is null || link.Length > 256 || !SessionShareCommitRule.IsShareUrl(link)
                || new Uri(link).Fragment.Length != 0)
                throw new AdoConfigException("Session links must be public VibeRails replay URLs returned by create_session_share_link.");
            if (!normalized.Contains(link, StringComparer.Ordinal)) normalized.Add(link);
        }
        return (text, normalized);
    }

    /// <summary>
    /// The comment as HTML: blank lines separate paragraphs and other line breaks become <c>&lt;br&gt;</c>.
    /// Every character of the text is encoded, so nothing in it can become markup. A link is
    /// written only for a URL <see cref="ValidateComment"/> accepted.
    /// </summary>
    internal static string WriteComment(string body, IReadOnlyList<string> links)
    {
        var html = new StringBuilder();
        var paragraph = new List<string>();
        foreach (var line in body.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                paragraph.Add(WebUtility.HtmlEncode(line));
                continue;
            }
            Flush();
        }
        Flush();
        if (links.Count > 0)
        {
            html.Append("<p>VibeRails sessions used for this work:</p>");
            foreach (var link in links)
            {
                var encoded = WebUtility.HtmlEncode(link);
                html.Append("<p><a href=\"").Append(encoded).Append("\">").Append(encoded).Append("</a></p>");
            }
            html.Append("<p>Public replay links expire after one month. Active sessions become available after they end and upload.</p>");
        }
        return html.ToString();

        void Flush()
        {
            if (paragraph.Count == 0)
                return;
            html.Append("<p>").AppendJoin("<br>", paragraph).Append("</p>");
            paragraph.Clear();
        }
    }
}
