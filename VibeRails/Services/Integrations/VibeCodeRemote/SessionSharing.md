# Session sharing (VIBE-35)

The terminal tab's Share action captures the current recording id and opens a modal that asks
**who can view** before anything is created: anyone with the link (the default), or only people
the user lists by email (up to 10 per session across its links; each person signs in to
viberails.ai with that verified address; nobody is notified or looked up). Create link then
creates the named replay link on viberails.ai and shows Copy link, the audience, expiry and
upload status. The name defaults to the tab title and can be renamed, revoked or re-audienced in
the website's Sharing links page. The modal explains that terminal output, prompts and saved code
changes become visible through the link. The public server omits proxy captures. Closing the
modal hides its result while the creation request finishes; it does not cancel upload scheduling.

`ShareAudience` validates the choice locally with fixed wording. For listed people the client
first asks the server what it supports (`GET /api/v1/session-sharing-links/capabilities`, which
only a server with the sharing update answers) and reports `server_update_required` **before any
link is created** when the answer is missing, not 200, or lacks `email`. A server without the
update ignores the fields and would otherwise have created a public link, which exposes an already
uploaded recording to anyone (PR #80 review, 2026-10-10). The created link must then still confirm
the audience (`access: "email"` with exactly the requested people); an unconfirmed link is reported
as `invalid_response`, naming the link to revoke on the website, and no recording is queued for it.

The `create_session_share_link` MCP tool uses this same root route for the calling terminal's
inherited recording id. It accepts a `displayName`, an optional comma-separated `emails` list
that restricts the link to those people, and returns status, expiry, the audience and a commit line:
`vibe-share:<public URL>`. Multiple sessions can be listed on separate lines. The optional VCA rule
`Require VibeRails session link` checks these lines first, then public URLs elsewhere in the commit
message. It validates URL shape locally, not ownership, upload completion, expiry or revocation.
Creating another link is unnecessary for subsequent commits from the same session. MCP uses the
inherited root credentials over loopback, not the terminal child's control endpoint; it does not
open the database or upload live sessions itself. A newer MCP process can inherit an older root
that is still running, and that root would ignore `emails`, create a public link and upload the
recording for it. With `emails` the tool therefore first reads the root's
`GET /api/v1/session-sharing/capabilities` (both root credentials; an older root has no such route
and is then never asked to create anything), and after creation it prints a URL or commit line only
when the root confirmed `access: "email"` with exactly those people; otherwise it reports a failure
that names the link for revocation.

`GET /api/v1/session-sharing/capabilities` is active-root-only, requires both credentials, and
answers no-store `{ "access": ["public", "email"], "recipientLimit": 10 }` without contacting
the server. `POST /api/v1/sessions/{sessionId:guid}/sharing-links` is active-root-only and requires both
normal process/session and tab credentials through CookieAuthMiddleware. It accepts only
`{ displayName, access?, emails? }`, checks the local recording, caps JSON at 8 KiB and names at
160 characters, and returns no-store `{ success, status, message, url, displayName, expiresUtc,
httpStatus, access, recipients }`. Remote errors
are HTTP-200 domain results so a remote 401 cannot trigger local bootstrap/repeat POSTs.
Failed remote responses retain the numeric `httpStatus` and distinguish missing server updates,
database migrations, account permissions, rate limits and server failures. Network, timeout and
invalid-response failures have separate messages. Only recognized JSON error codes select local
messages; arbitrary remote prose, SQL, headers and exception text are never echoed. Error bodies
have the same 16 KiB bound as successful responses. A missing-schema response from Front uses
`schema_update_required`; a generic HTTP 500 suggests checking migrations without assuming the cause.

`SessionSharingService` sends header-only `X-Api-Key` to the fixed HTTPS export origin's
`/api/v1/session-sharing-links`, without redirects/cookies, within 20 seconds/16 KiB. It validates
the session id, 64 lowercase hex capability, exact relative public path, expiry and required
`uploadRequired` boolean. It constructs URLs only at `https://viberails.ai`, ignores remote error
prose, and never logs keys or returned URLs. Source-generated JSON preserves Native AOT support.
Deploy the companion Front API and SQL migration before releasing this client.

The server's `uploadRequired` is authoritative for that owner and session. A local `ExportedUTC`
alone cannot prove the current account already has it. If upload is required, additive automatic
SQLite migration `session-sharing/1` creates a durable request in `SessionShareUploads` keyed by
session id and SHA-256 of the API key used for creation. It stores no raw key or sharing URL.
Repeated links under that key reuse the request. Account changes during creation are reported;
the request remains pinned to the original key until that key is configured again.

`SessionDataDrainJob` selects completed requests for the current key before the normal queue,
bypassing the ordinary one-minute settle delay. The existing one-minute cadence, low-resource
scheduling, one-upload-at-a-time locks and exponential retry backoff remain. Live recordings
never upload. Root backends must stay open; reopening resumes durable work, without a daemon.
Ordinary selection excludes sessions with pending explicit shares. The exporter independently
checks eligibility against the key it captured before payload I/O, and uses the unchanged
immutable Brotli/chunk/ACK protocol. A changed account cannot complete another key's request.

Previously exported recordings can be explicitly re-exported for a different account.
`AcknowledgeSessionExportAsync` atomically records successful delivery and completes only
requests matching the captured key fingerprint; prior retention evidence is preserved. Spools
remain while other requests need the same immutable bytes. Retention skips pending share
requests; explicit recording deletion still cascades them. Requests for unavailable keys wait
instead of silently switching accounts. Links themselves expire after one month and can be
revoked on the website; the desktop's upload request grants no public read access.

Tests cover local credential combinations/body bounds, outbound response validation, account
changes, live/completed/already-exported selection, durable retry and exact-key ACK, retention,
frozen spool reuse and the real frontend modal at desktop/phone widths. Run `dotnet test
Tests/Tests.csproj`, `node --test Tests/wwwroot/js/*.test.mjs` and, from UITests, `npx playwright
test --config playwright.terminal-automations.config.js --grep "session sharing"`. UI fixtures
serve static assets and intercept APIs/WebSockets; they never open application databases.
