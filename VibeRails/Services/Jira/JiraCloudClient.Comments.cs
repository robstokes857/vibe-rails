using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.VCA;

namespace VibeRails.Services.Jira;

/// <summary>The outcome of one non-retried Jira comment POST.</summary>
public sealed record JiraCommentResult(bool Success, bool MayHavePosted, string Message, string? CommentId = null);

/// <summary>Posts comments to the saved Jira origin using its saved credentials.</summary>
public interface IJiraCommentClient
{
    /// <summary>Posts plain text and explicit public replay links as an Atlassian document.</summary>
    Task<JiraCommentResult> AddCommentAsync(string siteUrl, string email, string apiToken, string issueId,
        string body, IReadOnlyList<string> sessionLinks, CancellationToken cancellationToken);
}

public sealed partial class JiraCloudClient
{
    internal const int MaxCommentCharacters = 20_000;
    internal const int MaxSessionLinks = 20;
    private const string UnconfirmedComment = "Jira comment delivery could not be confirmed. Check the issue before retrying; the comment may already exist.";

    /// <inheritdoc />
    public async Task<JiraCommentResult> AddCommentAsync(string siteUrl, string email, string apiToken, string issueId,
        string body, IReadOnlyList<string> sessionLinks, CancellationToken cancellationToken)
    {
        var site = JiraSite.Parse(siteUrl);
        if (!IsNumericId(issueId)) throw new JiraConfigException("The saved Jira issue id is invalid. Pull the board again.");
        var content = ValidateComment(body, sessionLinks);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, site.Api($"issue/{issueId}/comment"));
        Authorize(request, email, apiToken);
        request.Content = new ByteArrayContent(WriteComment(content.Body, content.Links));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                var message = response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "Jira rejected the comment. Check the comment text and issue configuration.",
                    HttpStatusCode.Unauthorized => "Jira refused the saved email and API token. Reconnect Jira in Board settings.",
                    HttpStatusCode.Forbidden => "This Jira account does not have permission to add comments to the issue.",
                    HttpStatusCode.NotFound => "The linked Jira issue was not found or is not visible to this account.",
                    HttpStatusCode.TooManyRequests => "Jira rate limited the comment. Wait before trying again.",
                    _ => UnconfirmedComment
                };
                return new(false, message == UnconfirmedComment, message);
            }

            var bytes = await SessionSharingService.ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(deadline.Token), 256 * 1024, deadline.Token);
            using var document = JsonDocument.Parse(bytes);
            var id = ReadString(document.RootElement, "id");
            return IsNumericId(id)
                ? new(true, false, "Comment posted to Jira.", id)
                : new(false, true, UnconfirmedComment);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Response bodies, public capabilities, credentials and exception prose stay out of logs/replies.
            return new(false, true, UnconfirmedComment);
        }
    }

    internal static (string Body, IReadOnlyList<string> Links) ValidateComment(string? body, IReadOnlyList<string>? links)
    {
        var text = body?.Trim() ?? string.Empty;
        if (text.Length is 0 or > MaxCommentCharacters)
            throw new JiraConfigException($"Comment text must contain 1 to {MaxCommentCharacters} characters.");
        if (links?.Count > MaxSessionLinks)
            throw new JiraConfigException($"Include at most {MaxSessionLinks} session links in one comment.");
        var normalized = new List<string>();
        foreach (var value in links ?? [])
        {
            var link = value?.Trim();
            if (link is null || link.Length > 256 || !SessionShareCommitRule.IsShareUrl(link)
                || new Uri(link).Fragment.Length != 0)
                throw new JiraConfigException("Session links must be public VibeRails replay URLs returned by create_session_share_link.");
            if (!normalized.Contains(link, StringComparer.Ordinal)) normalized.Add(link);
        }
        return (text, normalized);
    }

    private static bool IsNumericId(string? id) => id is { Length: > 0 and <= 40 }
        && id.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0;

    private static byte[] WriteComment(string body, IReadOnlyList<string> links)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("body");
            writer.WriteStartObject();
            writer.WriteString("type", "doc");
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("content");
            foreach (var line in body.ReplaceLineEndings("\n").Split('\n')) WriteParagraph(writer, line);
            if (links.Count > 0)
            {
                WriteParagraph(writer, "VibeRails sessions used for this work:");
                foreach (var link in links) WriteParagraph(writer, link, link);
                WriteParagraph(writer, "Public replay links expire after one month. Active sessions become available after they end and upload.");
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteParagraph(Utf8JsonWriter writer, string text, string? link = null)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "paragraph");
        writer.WriteStartArray("content");
        if (text.Length > 0)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            if (link is not null)
            {
                writer.WriteStartArray("marks");
                writer.WriteStartObject();
                writer.WriteString("type", "link");
                writer.WriteStartObject("attrs");
                writer.WriteString("href", link);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
