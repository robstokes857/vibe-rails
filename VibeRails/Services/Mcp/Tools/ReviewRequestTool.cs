using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using TokenSaver;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.ReviewRequests;
using VibeRails.Services.VCA;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>
/// <c>request_review</c> (VB-50, slice E): creates a review request for the calling terminal's project
/// through the existing root route, with the calling session attached, and reports what a send would
/// transfer. It never sends, decides or signs: the request stays on this computer until a person opens
/// the card, reads the disclosure and presses Send. Same credential model as
/// <see cref="SessionSharingTool"/>: the inherited root tool API, loopback only, no redirects.
/// </summary>
[McpServerToolType]
public sealed class ReviewRequestTool
{
    public const string HttpClientName = "viberails-review-requests";

    /// <summary>The create answer is one summary; the disclosure lists every staged path, so it gets more room.</summary>
    internal const int MaxCreateResponseBytes = 64 * 1024;
    internal const int MaxDisclosureResponseBytes = 1024 * 1024;

    private readonly IHttpClientFactory clients;
    private readonly Func<string, string?> readEnvironment;

    public ReviewRequestTool(IHttpClientFactory clients) : this(clients, Environment.GetEnvironmentVariable) { }

    internal ReviewRequestTool(IHttpClientFactory clients, Func<string, string?> readEnvironment)
    {
        this.clients = clients;
        this.readEnvironment = readEnvironment;
    }

    [McpServerTool]
    [Description("Ask named people to review the staged changes of this terminal's project before they are committed (VibeRails review request). "
        + "Creates the request on this computer from the staged index (git add first) with this terminal's session attached, and returns the request id, the staged files, the tree id and exactly what would leave the computer. "
        + "If an open, unsent request already covers this project's staged tree, reuses it and returns its id and tree without changing its reviewers, note or sessions; open Review requests in VibeRails to inspect it. "
        + "Nothing is sent by this tool: a person opens the card in VibeRails, reads that disclosure under Request review and presses Send, which transfers the file text, the tree and base ids, the note, and this session's recording (terminal output, prompts, saved code changes, uploaded after the session ends) to viberails.ai for the listed reviewers only, for one month. "
        + "The reviewer approves by signing the staged tree on their own computer; the approval is a vibe-signature line the commit hook applies, so commit the same staged tree afterwards. "
        + "This tool cannot sign, approve or reject. Requires an open VibeRails root backend with review requests and a signed-in account to send; no session-id argument: only the calling terminal is attached.")]
    public async Task<string> RequestReview(
        [Description("Comma-separated reviewer email addresses (1 to 10). Use the approvers of the Signed Approval rule covering the changed paths when there is one.")] string reviewers,
        CancellationToken cancellationToken = default,
        [Description("Optional note for the reviewers, at most 2,000 characters: what changed and what to look at.")] string? note = null,
        [Description("Optional Board card key (for example VB-50 or a display id) so the request shows on that card. Leave empty when the terminal has no card.")] string? cardKey = null)
    {
        var listed = SplitReviewers(reviewers);
        if (listed.Count == 0)
            return "FAIL: Name at least one reviewer by email address.";
        if (listed.Count > ReviewRequestService.MaxReviewers)
            return $"FAIL: A review request names at most {ReviewRequestService.MaxReviewers} reviewers.";
        var invalid = listed.FirstOrDefault(email => !HumanSignatureRule.IsEmail(email));
        if (invalid is not null)
            return $"FAIL: '{invalid}' is not an email address.";
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is { Length: > ReviewRequestService.MaxNoteChars })
            return $"FAIL: The note is at most {ReviewRequestService.MaxNoteChars} characters.";
        var trimmedCard = string.IsNullOrWhiteSpace(cardKey) ? null : cardKey.Trim();
        if (trimmedCard is { Length: > ReviewRequestService.MaxCardKeyChars })
            return $"FAIL: The card key is at most {ReviewRequestService.MaxCardKeyChars} characters.";

        var session = readEnvironment(LocalToolApiContext.CurrentSessionIdVariable);
        var sessionToken = readEnvironment(LocalToolApiContext.SessionTokenVariable);
        var tabToken = readEnvironment(LocalToolApiContext.TabTokenVariable);
        if (!Guid.TryParseExact(session, "D", out var sessionId) || sessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(tabToken)
            || !LlmProxyBaseUrl.TryNormalizeLoopback(readEnvironment(LocalToolApiContext.ApiBaseUrlVariable) ?? "", out var baseUrl))
            return "FAIL: No current VibeRails terminal session with root API credentials is available. Launch the CLI from VibeRails, or use Request review on the card.";

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var http = clients.CreateClient(HttpClientName);

            using var create = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "/api/v1/review-requests"));
            create.Headers.Add(LlmProxyCodexConfig.SessionHeaderName, sessionToken);
            create.Headers.Add(LlmProxyCodexConfig.TabHeaderName, tabToken);
            create.Content = JsonContent.Create(
                new CreateReviewRequest(ReviewScope.Staged, listed, [sessionId.ToString("D")], trimmedCard, trimmedNote),
                ReviewRequestJsonContext.Default.CreateReviewRequest);
            using var created = await http.SendAsync(create, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (created.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                return "FAIL: The running VibeRails backend does not support review requests, so nothing was created. Update VibeRails and restart it, then try again.";
            var createdBytes = await SessionSharingService.ReadBoundedAsync(
                await created.Content.ReadAsStreamAsync(deadline.Token), MaxCreateResponseBytes, deadline.Token);
            if (!created.IsSuccessStatusCode)
                return Refusal(created, createdBytes, "create the review request");
            if (created.StatusCode == HttpStatusCode.OK)
            {
                var reused = JsonSerializer.Deserialize(createdBytes, ReviewRequestJsonContext.Default.ReviewRequestReuse);
                if (reused is null || !Guid.TryParseExact(reused.Id, "D", out _) || !CommitSignatureFormat.IsObjectId(reused.TreeId))
                    return "FAIL: The local VibeRails backend returned no review request.";
                return $"Review request {reused.Id} reused (open, unsent). Nothing has been sent by this tool.\nTree: {reused.TreeId}\n"
                    + "The existing reviewers, note and attached sessions are unchanged. Open Review requests in VibeRails to inspect this request and its disclosure before sending. This tool cannot sign, approve or reject.";
            }
            var summary = JsonSerializer.Deserialize(createdBytes, ReviewRequestJsonContext.Default.ReviewRequestSummary);
            if (summary is null || !Guid.TryParseExact(summary.Id, "D", out var requestId))
                return "FAIL: The local VibeRails backend returned no review request.";

            using var read = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), $"/api/v1/review-requests/{requestId:D}/disclosure"));
            read.Headers.Add(LlmProxyCodexConfig.SessionHeaderName, sessionToken);
            read.Headers.Add(LlmProxyCodexConfig.TabHeaderName, tabToken);
            using var disclosed = await http.SendAsync(read, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var disclosureBytes = await SessionSharingService.ReadBoundedAsync(
                await disclosed.Content.ReadAsStreamAsync(deadline.Token), MaxDisclosureResponseBytes, deadline.Token);
            var disclosure = disclosed.IsSuccessStatusCode
                ? JsonSerializer.Deserialize(disclosureBytes, ReviewRequestJsonContext.Default.ReviewRequestDisclosure)
                : null;
            return Describe(summary, disclosure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Never relay exception text: it can carry the loopback credentials or response bodies.
            return "FAIL: Could not confirm the review request with the local VibeRails backend. Open the card's Request review section before retrying; a request may already exist.";
        }
    }

    internal static IReadOnlyList<string> SplitReviewers(string? reviewers)
    {
        var result = new List<string>();
        foreach (var entry in (reviewers ?? string.Empty).Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!result.Contains(entry, StringComparer.OrdinalIgnoreCase)) result.Add(entry);
        }
        return result;
    }

    /// <summary>The root's own refusal, in its fixed wording and code; a non-JSON answer reports only the status.</summary>
    private static string Refusal(HttpResponseMessage response, byte[] body, string action)
    {
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var error = JsonSerializer.Deserialize(body, ReviewRequestJsonContext.Default.ReviewRequestError);
                if (error is { Error: { Length: > 0 } message, Code: { Length: > 0 } code })
                    return $"FAIL ({code}): {message}";
            }
            catch (JsonException) { /* Fall through to the status-only wording. */ }
        }
        return $"FAIL: The local VibeRails backend could not {action} (HTTP {(int)response.StatusCode}). Use Request review on the card to check the connection.";
    }

    /// <summary>The request as created and, when the disclosure could be read, exactly what a send would transfer.</summary>
    internal static string Describe(ReviewRequestSummary summary, ReviewRequestDisclosure? disclosure)
    {
        var text = new StringBuilder();
        text.Append("Review request ").Append(summary.Id).Append(" created (").Append(summary.Status).Append(')');
        if (disclosure is not null) text.Append(" for ").Append(disclosure.Repository);
        text.AppendLine(". Nothing has been sent.");
        text.Append("Tree: ").AppendLine(summary.TreeId);
        text.Append("Reviewers: ").AppendLine(string.Join(", ", summary.Reviewers));
        if (summary.CardKey is not null) text.Append("Card: ").AppendLine(summary.CardKey);
        if (disclosure is null)
        {
            text.Append("Files: ").Append(summary.FileCount).Append("; sessions attached: ").Append(summary.SessionCount).AppendLine(".");
            text.AppendLine("The disclosure could not be read; open the card's Request review section to see what a send would transfer.");
            return text.ToString().TrimEnd();
        }

        text.Append("Files (").Append(disclosure.Files.Count).Append(", ").Append(disclosure.FilesWithText).Append(" with text, ")
            .Append(disclosure.FilesWithoutText).AppendLine(" without):");
        foreach (var file in disclosure.Files)
        {
            text.Append("  ").Append(file.Change).Append(' ').Append(file.Path);
            if (file.PreviousPath is not null) text.Append(" (from ").Append(file.PreviousPath).Append(')');
            if (file.ChangedLines is { } lines) text.Append(" [").Append(lines).Append(lines == 1 ? " line]" : " lines]");
            if (!file.TextIncluded) text.Append(" [no text: ").Append(file.Omitted ?? "omitted").Append(']');
            text.AppendLine();
        }
        text.Append("Sessions attached (").Append(disclosure.Sessions.Count).AppendLine("):");
        foreach (var session in disclosure.Sessions)
        {
            text.Append("  ").Append(session.Name).Append(" (").Append(session.Cli).Append(session.Live
                ? "; still running, so it uploads after it ends)" : "; ended)").AppendLine();
        }
        if (disclosure.Note is not null) text.Append("Note: ").AppendLine(disclosure.Note);
        text.AppendLine();
        text.Append("What leaves this computer when a person sends it: to ").Append(disclosure.Destination)
            .Append(", readable only by the listed reviewers signed in with those verified emails, until ")
            .Append(disclosure.ExpiresUtc.ToString("u")).Append(": the files above with their text (")
            .Append(disclosure.TextBytes).Append(" bytes), the tree and base ids, the reviewers, the card key and the note. ")
            .AppendLine(disclosure.SessionDisclosure);
        text.Append("Sending is a person's step: open ")
            .Append(summary.CardKey is null ? "the card" : "card " + summary.CardKey)
            .AppendLine(" in VibeRails, read this disclosure under Request review and press Send. Sending needs viberails.ai support for review requests; an older server answers server_update_required and nothing is transferred.");
        text.Append("The reviewer approves by signing tree ").Append(summary.TreeId[..Math.Min(12, summary.TreeId.Length)])
            .AppendLine(" on their own computer; the commit hook applies that approval to a commit of the same staged tree, so do not restage before committing. This tool cannot sign, approve or reject.");
        return text.ToString().TrimEnd();
    }
}
