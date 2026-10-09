# Jira completion comments

`add_jira_comment` posts an explicit comment to the Jira issue connected to a VibeRails
card. It is available on both MCP transports. It accepts `body` (plain text, 1–20,000
characters), optional `card` (the usual Board key/ID; omitted uses the terminal's original
card), and optional `sessionLinks` (up to 20 public VibeRails replay URLs).

For a completion report:

1. Call `create_session_share_link` with a descriptive `displayName`, or reuse the link
   already created for this session. Save that link with the Board handoff so later
   sessions can reuse it. Only create public shares when the user requests sharing.
2. Call `add_jira_comment` with the completion summary and the links from all sessions
   used for the work. Earlier sessions' links come from their saved handoffs. The tool
   deduplicates identical links and renders them as clickable Jira links.
3. Continue the normal Board handoff/completion workflow. Posting does not change Jira
   status, move the card, or end a session.

Anyone holding a public replay link can read the shared session. Links expire one month
after creation; active sessions become readable after they end and upload. Posting a
comment neither creates a new share nor verifies upload, expiry, or ownership of an
existing link. A text-only Jira update can omit `sessionLinks`.

The tool is an external write and is deliberately outside the automatic local Board tool
grants, like `create_session_share_link`. Existing CLI approval settings still apply.
It takes no token, site URL, issue ID, filesystem path, or arbitrary session target.

`JiraCommentService` reads live-card issue links through `IBoardStore` and joins them to
the original saved connection in the owning project. A local board move retains that
destination. Missing/disconnected or ambiguous connections fail before posting. The
existing cross-process Jira lock serializes posting with connection edits/unlink/pulls.
The saved token stays in `IJiraSecretStore`. There is no schema change or startup backfill.

`JiraCloudClient` makes one `POST /rest/api/3/issue/{numericIssueId}/comment`, using
Atlassian Document Format with literal text and explicit link marks. The shared root/
stdio registration disables redirects and cookies. Requests have a 30-second deadline
and a 256-KiB response cap (Jira echoes the comment). Only a 201 with a valid numeric
comment ID confirms success. Error bodies and exception details are not returned or logged.

A confirmed post returns a saved-origin issue/comment URL and adds an attributed receipt
containing the text and links to Board Comments. If that local write fails, the tool still
reports that Jira succeeded and explicitly says not to repost. Network failures, invalid
success responses, redirects, and server failures report unconfirmed delivery; inspect
Jira before retrying. There is no automatic retry or exactly-once guarantee. Ordinary Jira
permission, missing issue, rejected text, and rate-limit failures have fixed messages.

Tests: `JiraCommentClientTests`, the `BoardToolJiraTests` partial, `McpServerHttpTests`,
`McpStdioHostTests`, and `CookieAuthMiddlewareTests`. They use fake HTTP and disposable
Board stores; they do not post to a real Jira site or create public shares.
