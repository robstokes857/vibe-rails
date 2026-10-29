# Azure DevOps comments and session links

The Azure DevOps delivery contract mirrors [Jira comments](../Jira/Comments.md): same events,
same ledger shape, same states and the same refusal to retry an uncertain send. Only the
transport and the comment format differ.

Delivery is the only ongoing link between a card and its work item. Azure DevOps is the source
of truth for its work items: a pull copies each one into the board once and never changes its
card again (VIBE-236, "Dedicated Azure DevOps boards" in the [Board guide](../Board/AGENTS.md)).
Comments and session links keep flowing out to the work item, addressed by its numeric id, for as
long as the card stays linked. Nothing in Azure DevOps flows back after the import.

New comments on a card linked to an Azure DevOps work item are posted to that work item by
default. REST comment/note requests and both MCP tools `add_board_comment` and
`append_board_note` accept `syncToRemote=false` for an internal note; `syncToJira` stays as an
alias and the resolved value is `syncToRemote ?? syncToJira ?? true`. Storage keeps the
`syncToJira.to` key for both trackers, so an older binary that writes `false` also keeps the
comment off Azure DevOps. It affects tracker delivery only, not hosted Board sync. Launch prompts
(`BoardPromptComposer.AdoGuidance`) and `get_board_card` explain this and ask agents to avoid
progress spam.

New local session attachments, including Start work and Automation recordings, queue a named
public replay link through the existing `SessionSharingService`, then post it to the work item.
Anyone holding the link can read the session; links expire after one month. Active sessions
become readable after ending and uploading. A VibeRails account is required. Nothing is
backfilled on upgrade.

## Persistence and delivery

`board-ado-delivery/1` (see the [Board persistence guide](../../../VibeRails.Data.Sqlite/Board/AGENTS.md))
creates `BoardAdoDeliveries` in board.db, a copy of the Jira ledger. The `BoardComments_QueueAdo`
trigger records a comment's outbound intent in the comment transaction, and
`QueueAdoSessionAsync` records a session's in the session-link transaction. The destination is
pinned to the original connection id and numeric work item id, including after a local board
move. The pull's own notes (author `Azure DevOps`, written only when it imports a work item),
hidden discussion, rows pulled from viberails.ai, copied (`jiraCopy`) and opted-out comments are
excluded. Persistence stays behind `IBoardStore`.

`AdoPullScheduler` starts `AdoDeliveryService.DrainAsync` every 5 seconds on the leased root
scheduler, beside the 15-minute pull and never while a pull runs. Each drain sends at most 20
events under `.ado-pull.lock`, which also serializes connection edits, unlinking, pulls and
`add_ado_comment`. No daemon, listener or stdio background worker is introduced: pending
activity is delivered while a root backend is open.

Before sending, the drain re-reads the live card, the work item link, the original connection in
the card's project and the source comment or session. Deleted comments, removed session links,
unlinked connections and a connection replaced by another organization are cancelled, not
posted. A missing token fails with a reconnect message.

Each event moves pending → sending → sent, failed or uncertain. A session's public link is
created and saved on the event before the POST, so a crash never re-creates or re-posts it. A
local "busy" from the sharing service sent nothing and returns the event to pending; any other
sharing failure is failed. At startup of the next drain, an event left in sending becomes
uncertain and is never sent again automatically. Card detail exposes the statuses as
`remoteDeliveries`. Inspect the work item and Sharing links before reposting an uncertain event.
There is no exactly-once guarantee across a network failure.

## The request

`POST https://dev.azure.com/{org}/{project}/_apis/wit/workItems/{id}/comments?format=html&api-version=7.1-preview.4`
with the saved personal access token over basic auth, redirects and cookies off, one 30-second
deadline and a 256-KiB response cap. The body is `{"text": html}`:

- Every character of the comment is HTML-encoded. Blank lines separate `<p>` paragraphs and other
  line breaks become `<br>`.
- The only `<a>` tags are for vetted public replay URLs (`https://viberails.ai/shared/session?key=`
  plus 64 hex characters, no fragment), under "VibeRails sessions used for this work:" with the
  expiry note, matching Jira's text-plus-links ADF.
- At most 20,000 characters of text and 20 links, the Jira limits. Azure DevOps does not document
  its own maximum; the encoded HTML can be longer than the text, and a rejection is reported as a
  failed delivery while the Board comment stays saved.

A 200/201 JSON answer with a positive numeric `id` is sent. A 400, 403, 404 or 429, and a refused
token (401, or the 203/302 sign-in answer, or a 2xx that isn't JSON) certainly posted nothing and
are failed with a fixed message. Anything else (5xx, network failure, missed deadline, oversized
or unreadable success) may have posted and is uncertain. Tokens, response bodies, exception prose
and sharing URLs are never logged or returned. No work item field, state or card position is
changed.

## Explicit posting

`add_ado_comment` is available on both MCP transports for a deliberate additional post. It
takes body, an optional card and up to 20 already-created public replay URLs, and creates no
shares. It is outside `BoardMcpAuthorization.ToolNames`, so no Board session auto-grants it. Its
local Board receipt opts out of tracker sync, so the explicit post is not echoed back. Do not call
it to duplicate an ordinary synced comment or the automatic session-link post.

`AdoCommentService` resolves the card's work item link and saved connection in the owning
project; a missing, disconnected, tokenless or ambiguous (more than one linked work item)
destination fails before posting with `FAIL:`. An uncertain post returns `UNCONFIRMED:`. A
confirmed post returns the comment id and the work item page, built from the saved organization,
project and id: Azure DevOps documents no per-comment link, so the work item is linked instead.
A failure to save the Board receipt does not invalidate the post; the reply says not to repost.

Tests: `Tests/Services/AzureDevOps/AdoCommentClientTests.cs` (encoding, links, sanitized
failures, bounded response, network failure, deadline), `AdoDeliveryServiceTests.cs` (queueing,
moves, cancellation, failures, interrupted claims, sharing), `Tests/Services/Mcp/BoardToolAdoTests.cs`,
`McpServerHttpTests` and `McpStdioHostTests`.
