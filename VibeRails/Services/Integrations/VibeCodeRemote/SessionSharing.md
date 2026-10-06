# Session sharing (VIBE-35)

The terminal tab's Share action creates a named public replay link on viberails.ai, captures
the current recording id, and opens a modal with Copy link, expiry and upload status. The name
defaults to the tab title and can be renamed or revoked in the website's Sharing links page.
The modal explains that terminal output, prompts and saved code changes become visible to
anyone holding the link. The public server omits proxy captures. Closing the modal hides its
result while the creation request finishes; it does not deliberately cancel upload scheduling.

`POST /api/v1/sessions/{sessionId:guid}/sharing-links` is active-root-only and requires both
normal process/session and tab credentials through CookieAuthMiddleware. It accepts only
`{ displayName }`, checks the local recording, caps JSON at 4 KiB and names at 160 characters,
and returns no-store `{ success, status, message, url, displayName, expiresUtc, httpStatus }`. Remote errors
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
