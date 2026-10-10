# Jira comments and session links

New comments on a Jira-linked card are posted to its connected Jira issues by default.
The card editor links to the issue and offers **Post this comment to Jira**; uncheck it for
an internal note. REST comment/note requests and both MCP tools `add_board_comment` and
`append_board_note` accept `syncToJira=false`. The flag is retained in comment metadata and
shown as **Not sent to Jira** only when the card's owning board is currently connected to Jira,
including cards opened from another project. It affects Jira only, not the board's separate hosted sync.
Launch prompts and `get_board_card` explain this behavior and ask agents to avoid progress spam.

New local session attachments, including Start work and Automation recordings, queue creation
of a named public replay link through the existing `SessionSharingService`, then post the link
to Jira. Anyone holding the link can read the session; links expire after one month. Active
sessions become readable after ending and uploading. A VibeRails account is required. No
existing sessions or historical discussion are backfilled on upgrade.

## Persistence and delivery

The additive `board-jira-delivery/1` migration creates `BoardJiraDeliveries` in board.db. A
comment-insert trigger records its outbound intent in the comment transaction. Session intents
are written in the local session-link transaction. Persistence remains behind `IBoardStore`.
The destination is pinned to the original connection and numeric issue ID, including after a
local board move. Moves remap delivery source IDs without requeueing old discussion. Merge
copies, remote Board imports, Jira pull receipts and explicit Jira-post receipts are excluded.
Older local comment writers also use the trigger; an opted-out comment stays excluded.

The existing root scheduler starts a bounded delivery drain on its normal ticks, independently
of the 15-minute pull interval. No daemon, listener or stdio background worker is introduced.
Pending activity survives restarts and is delivered while a root backend is open. The existing
cross-process Jira lock serializes drains with connection edits, unlinking and pulls. Before
sending, the drain checks the live card, issue link, original connection and source activity.
Deleted comments, removed session links and disconnected destinations are not posted.

Each event transitions pending → sending → sent/failed/uncertain. A public capability is saved
locally before the Jira POST. Claims and retained outcomes prevent duplicate sends across roots
and repeated attachments. An abandoned sending claim becomes uncertain when the next process
acquires the lock; it is never blindly retried. Rejected requests remain failed with a readable
reason. Card detail exposes bounded delivery statuses and the editor refreshes pending/failure
messages without replacing drafts. Inspect Jira and Sharing links before manually retrying an
uncertain delivery. There is no exactly-once guarantee across a network failure.

The client posts ADF literal text to the saved-origin numeric issue endpoint with redirects and
cookies disabled, a 30-second deadline and 256-KiB response cap. Comments carry their Board author.
The existing 20,000-character Jira request limit applies; longer Board comments remain saved and
report failed delivery. Public replay links use explicit ADF link marks. Tokens, response bodies,
exception prose and sharing URLs are never logged. No Jira status or card movement is performed.

## Explicit posting

`add_jira_comment` remains available on both MCP transports for a deliberate additional post.
It accepts body, optional card and up to 20 already-created public replay URLs. It does not create
shares and remains outside automatic Board-tool grants. Its local Board receipt opts out of Jira
sync, so the explicit post is not echoed back. Do not call it to duplicate an ordinary synced
comment or the automatic session-link post.

A confirmed explicit post returns a saved-origin comment URL. A failure to save its Board receipt
does not invalidate Jira success; the tool says not to repost. Unconfirmed delivery requires
inspection before retrying. Missing, disconnected or ambiguous explicit destinations fail before
posting. The saved token stays in `IJiraSecretStore`.

Tests cover descriptions returned as strings and ADF, default/opt-out comments, MCP and REST
contracts, source deletion, moves, repeated attachments, public sharing, failures, interrupted
claims, frontend controls, schema snapshots and previous-release compatibility.
