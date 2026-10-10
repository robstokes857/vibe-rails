# Public card sharing

Implemented locally 2026-10-10 (VB-9KWRU-16), paired with the Front CardSharing service.
The feature requires the companion hosted deployment and its additive CardSharingLinks migration.
Implementation tests never publish actual user cards or run production migrations.

The saved card editor's **Share card** section creates public read-only links and lists,
renames, revokes, copies and opens them. New cards must be saved first. Creation and manual
refresh refuse unsaved fields/comments/files; link management never saves or replaces drafts.
The panel states that card text, discussion, documents, saved commit diffs, linked-session
recordings and future saved updates become public to anyone with the link. A link expires
one calendar month after creation. Local deletion does not revoke a publication; use the
link controls or https://viberails.ai/CardSharingLinks to revoke it.

## Capture and transport

`CardShareCapture` reads through IBoardStore, ISessionStore and IChatSummaryStore. It includes
full discussion/history, saved handoff/review/check evidence, attached bytes, stored commit
before/after content and session summaries. Repository references remain inert text, and related
cards expose labels only. It never reads arbitrary paths or fresh checkout contents. It rejects
concurrent card changes/deletion and complete publications above the 64 MiB transfer bound.
No data is silently truncated to fit ordinary Board-sync limits; Jira and local-only boards
use this independent publication flow.

History includes full saved portable field changes, including each saved description, rather
than just change summaries. The existing Board-sync projection filters local launch settings
and normalizes assignees before publication. Current assignees use the same portable CLI form.

`CardShareClient` sends only to https://viberails.ai/api/v1/card-sharing-links with X-Api-Key,
no redirects/cookies, bounded responses (1 MiB) and 90-second publication/refresh deadlines.
Returned card identities, numeric ordering, paths, capability encoding, expiry and requested
recording IDs must match the operation. Errors use fixed local wording, never remote prose.
A creation POST is never retried automatically. The UI rereads links after an uncertain result.

`CardSharePublisher` captures one account key for an operation and uses its fingerprint for
archive queuing even if the account changes during the request. SessionShareUploads stays behind
ISessionArchiveReader. EnsureSessionShareUploadAsync preserves pending retry backoff across
refreshes/restarts. It reopens completed work only when the server says that archive is missing.
No independent session-share capability is created by sharing a card.

Each publication privately records its immutable local row ID as well as its card key. This
separates legacy short keys shared by different projects. Neither the row ID nor session IDs
enter the public card projection. Remote IDs authorize only stored lookups, never filesystem paths.

## Local routes and automatic refresh

All routes are under `/api/v1/board/cards/{card}/sharing-links`, registered with BoardRoutes
on the active root only. CookieAuthMiddleware requires both local credentials, and the dashboard
project is derived server-side. GET lists (100 per page); POST creates; PATCH/DELETE `/{id}`
rename/revoke; POST `/refresh` replaces existing active publications made with the current key.
Names accept at most 160 characters; request bodies are capped at 4 KiB, including chunked bodies.
Responses use no-store and a source-generated JSON context. Upstream failures stay domain results
so a remote 401 cannot trigger local authentication bootstrap and repeat a creation POST.

The existing leased root BoardSyncScheduler refreshes cards after ordinary Board sync, reusing
the cross-process BoardSyncLock. Each sweep reads up to 100 creator-key-bound publications and
has a two-minute deadline; its in-memory cursor continues next cycle. Saved content is captured
again, but validated unchanged snapshots are not retransferred. The first sweep after restart
reconciles upload requests. Missing local cards never delete or overwrite a remote publication.
If a linked recording is absent locally, restore it and use Refresh shared card; unchanged
large documents are not repeatedly resent because that recording is missing. A failed or
oversized update preserves the last complete public version; manual Refresh reports the error.
No new database diagnostics, visitor counters, background host or listener is introduced.

## Tests

- `dotnet test Tests/Tests.csproj --no-restore --filter "FullyQualifiedName~CardShare|FullyQualifiedName~SessionShare|FullyQualifiedName~BoardSyncScheduler|FullyQualifiedName~BoardRoutesTests"`
- `node --test Tests/wwwroot/js/board-card-sharing.test.mjs`
- `node UITests/node_modules/@playwright/test/cli.js test --config UITests/playwright.card-sharing.config.js`
- `dotnet test Tests/headless/BoardSync.Integration/BoardSync.Integration.csproj --filter FullyQualifiedName~CardSharingRoundTrip`

The cross-repository test uses the sibling Front checkout (or `-p:FrontRepoRoot=...`), temporary
SQLite stores, in-memory hosted storage and an intercepted transport. It verifies capture through
public document/replay reads, revision/hash compatibility, automatic refresh, rename and revoke.
It transfers the real local recording bytes to synthetic private blob storage; the existing
archive upload protocol/authentication is tested separately. Browser fixtures use the actual
editor and public viewer with synthetic requests, including narrow screens and late responses.
