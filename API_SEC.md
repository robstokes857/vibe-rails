# API authentication coverage

## VIBE-46 bounded code maps and working-tree changes (2026-10-03, scoped amendment)

The existing root-only `POST /api/v1/code-analyzer/graph` no longer accepts a dependency
inclusion option. Old clients sending it cannot bypass dependency/build-output exclusions
through either the retired option or priority paths. Both credentials, server-derived root,
path containment, source/response budgets and generated JSON serialization remain in place.
The viewer retains its opaque-origin sandbox and MessageChannel lifecycle; its Code graph view
now draws on canvases inside that sandbox, which changes no boundary.

Added, mapped on the active root backend only and behind both credentials:
`GET /api/v1/code-analyzer/changes` runs `git status`/`git diff --numstat` against HEAD in the
server-derived root (argv only, bounded output, at most 2,000 entries, no file contents) and
`GET /api/v1/code-analyzer/changes/diff?path=` returns one path's HEAD blob (`cat-file HEAD:./path`,
or at the validated `original=` path for a renamed entry) and working-tree text through the existing
working-tree path guard. The safe-path rules used for graph priorities are applied to both query
values before git or the file system is touched; each side is bounded to
1,000,000 characters with a 5 MiB read limit, and binary content is reported, not returned. No
listener, authentication exception, database change or external destination is added.

Scoped route enumeration and both mandatory repository-wide listener searches found only
the approved main Kestrel host, the non-serving PortFinder probe and existing test hosts;
the cross-runtime search had no matches. Route regressions cover credentials, unsafe paths,
missing repositories and source-generated serialization for all three routes; service
regressions cover statuses, staging, counts, binary detection and path refusals. No security
violation was found.

## VIBE-40 attention flags (2026-10-03, scoped amendment)

The existing Board update accepts `flagReason`; agent flag writes validate it and save the
comment and session attribution atomically behind `IBoardStore`. Comments add `isAttention`;
the authenticated terminal list adds nullable `needsAttention` for its own session IDs and
their Automation Workers. No comment body is added to the terminal response. Rendering stays
escape-first. Existing root/project checks, session-plus-tab gates and exact MCP grants remain.

Scoped Board/terminal route enumeration and both repository-wide listener searches found the
approved main Kestrel host, transient PortFinder probe and existing test hosts only; the
cross-runtime search had no matches. No listener or authentication exception was added.

## VIBE-42 lane scripts and waiting cards (2026-10-03, scoped amendment)

Added root-only `POST /api/v1/board/cards/{card}/automations/skip` behind the existing session
and tab credentials. The server resolves the project/card and matches an exact Job/event pair
from its bounded lane status read. Stale requests return conflict; newer entries and committed
runs retain their lifecycle. The skip and its Comments receipt commit atomically. Waiting flags on existing
card/activity responses use scoped pending entries, reconciled against committed run triggers.
Script creation uses the existing Jobs API, repository containment, hash approval and explicit
interpreter/argv execution. No new listener, tool grant or credential exception is introduced.

Both repository-wide listener searches found only the main Kestrel host, the non-serving port
probe and existing test hosts; the cross-runtime search had no matches. Route regressions cover
credentials, missing cards, stale entries and source-generated serialization. This is a scoped
review of the new surface.

## VB-13 card recall (2026-10-02, scoped amendment)

No new HTTP route, listener or authentication exemption. The existing card response includes
bounded structured previous work and inert repository-relative file references. MCP adds
`save_board_handoff` and `read_board_session` on both transports, using exact per-tool Board
grants. Writes resolve the owning project/card, validate field/path budgets, stamp the caller's
provenance, and append a Comments receipt atomically. File references do not execute or load
contents; local status checks stop at symlinks/reparse points.

Session reads require membership on the resolved card. Document IDs must belong to that linked
session; list and text reads are paged. History lookup uses an exact session prefix, not SQL LIKE
wildcards. Search resolves explicit card aliases within the current project; broad captured history
is labeled as a separate local-user source. Discussion questions are bounded and passed through
existing argv/prompt preparation. A discussion never injects context into an active TUI. The launch
route retains both credentials and server-derived project identity.

Scoped route inspection and both repository listener searches found only the approved main
Kestrel host, the transient PortFinder probe and test hosts; the cross-runtime search had no matches.

## VIBE-36 terminal tab card link (2026-10-02, scoped amendment)

No route, listener, authentication exception or outbound destination was added. The existing
`GET /api/v1/terminal/tabs` (session-plus-tab gate, every backend) now adds an optional
`boardCard { id, key, title, displayId }` per tab, read through `IBoardStore.GetSessionCardsAsync`
for the tabs' own session IDs. That is the label data chat history already returns on the same
gate; no description, comment or attachment is exposed. A Board read failure is logged and
returns the list without links. `TerminalTabsRoutesTests` covers the credentialed AOT response.

## VIBE-29 complete backups (2026-10-02, scoped amendment)

Added read-only `GET /api/v1/settings/backups` under the existing session-plus-tab gate,
with no-store coverage/receipt metadata, never credentials or payloads. The resource-aware
backup job runs only in active root backends. Outbound requests use the fixed HTTPS
`viberails.ai/api/v1/data-exports/backups/` destination, header-only API keys, no redirects,
bounded responses, cancellation and strict per-account/computer/version acknowledgements.
No production listener or authentication exception changed.

The hosted contract is in `VibeRails-Front/Services/CompleteBackups/README.md`: API-key
ingest, separately Auth0-protected owner-only reads, owner-scoped EF filters/writes, hashed
4 MiB parts, bounded manifests, four concurrent ingest requests and additive tables.
Credential/external-file exclusions are explicit; preserved user/database content may
contain embedded secrets. Archives remain private account data.

Both mandatory repository-wide listener searches included untracked files: only the approved
main Kestrel host, non-serving PortFinder probe and test hosts were found; no cross-runtime
matches. Scoped route enumeration found the new coverage GET and existing export routes.
Fixtures cover wrong/cross-account ACKs, hosted download ownership and corrupted uploads.
No security violation was found. This is a scoped amendment, not a deployment audit.

**2026-10-02 pre-release correction (VB-3SKWQ-105 review).** The filename filter admitted
native credential stores the policy above claimed to exclude: OpenCode `mcp-auth.json`
(MCP OAuth access/refresh tokens and client secrets) and the whole Antigravity Chromium
profile under `~/.gemini/antigravity-browser-profile` (`Cookies`, `Login Data`, `Local State`).
`BackupFiles.Excluded` now matches the credential family (`auth`/`oauth`/`creds`/`accounts`
name segments, browser directories and Chromium credential files) instead of one `auth.`
prefix, and JSON redaction covers every `*settings.json`/`*config.json`/`opencode.json(c)` so
MCP `env`/`headers` blocks are stripped. `Tests/Services/BackupFilesCredentialTests.cs` runs the
real enumeration and zip writer against a fixture home. No archive had been staged or delivered
from this machine (`~/.vibe_rails/complete-backups` absent), so no revocation was needed here;
owners whose roots already ran the feature should check their delivered configuration archives.
Residual: `config.toml` (Codex, Grok) is copied verbatim because TOML is not redacted.

**2026-10-02 review fixes (VIBE-35).** Redaction now also drops `Proxy-Authorization`, `X-Auth`,
`Cookie`, `access_key`, `session_id`-style names and every entry of object-valued
`env`/`environment`/`headers`/`http_headers` blocks, since header and env credentials use arbitrary
names; Claude's `backups/.claude.json.backup.*` copies are excluded with `.claude.json`. A JSON file
that cannot be parsed is left out rather than written as an empty entry. CLI transcripts, prompt
history and log stores are no longer part of the configuration dataset. The account service is
contacted before staging. Outbound destination, headers and authentication are unchanged.
The lane review then confirmed the boundary was still open: plugin `.mcp.json`, Codex/Grok
`config.toml` and MCP `args` (`--token …`) reached archives with credentials intact. Every
captured JSON file is now redacted (not a name list), every `.toml` file gets line-based TOML
redaction with the same rules, and every string value is scanned: URL passwords, the value of any
credential-named `name=value`/`Name: value` pair at any depth (`--header=Authorization: Bearer …`,
`--env=API_KEY=…`, query tokens), and the item or word after a credential flag or `Bearer`/`Basic`
become `[redacted]` (the second review found the nested forms). A third review found encoded
spellings: TOML quoted keys and basic strings are now unescaped before classification
(`"http\u005fheaders"`, `"--api\u002dkey"`, `\u003d` inside URLs) and query names are also checked
percent-decoded (`api%5Fkey`), each with archive-level regressions.
`BackupFilesCredentialTests` drives the real enumeration and zip writer over those three shapes.
The earlier "TOML copied verbatim" residual is closed; YAML is still copied verbatim (no CLI
reads MCP servers from YAML today), and free-text content remains embedded-secret territory.

## VIBE-22 Switch reviewer (2026-10-01, scoped amendment)

Added root-only GET/PUT `/api/v1/board/cards/{card}/reviews/settings` and POST
`/api/v1/board/cards/{card}/reviews/preview`, within the existing no-store review group and
session-plus-tab credential gate. Project paths are server-derived; attribution validates linked
session membership and excludes known discussion/planning/review origins. Mapping targets validate
provider, environment identity and project visibility. Review overrides use the existing launch API.

Queued routing and retry scope are immutable; launch rechecks the environment and checkout inputs.
The direct-launch workspace flag is server-only (`JsonIgnore`). Reviewer argv uses existing builders
and the CustomArgs sanitizer, without permission escalation or provider substitution. User-declared
metadata is bounded, sanitized and fenced as data in prompts; UI output is escaped. Portable recipes
display routing/permission options in their existing review step and reject local environment IDs.

Scoped Board route enumeration and both mandatory repository listener searches found only the
approved main Kestrel host, non-serving PortFinder probe and test hosts; the cross-runtime search
had no matches. Credential/project/card tests and server-only flag serialization tests cover the
new contract. No security violation was found. This is a scoped amendment, not a full re-audit.

## VIBE-21 waiting lane Automations (2026-10-01, scoped amendment)

The existing root-only card Automations GET adds up to 100 lane entry states and reasons.
Card resolution and run reconciliation remain scoped to the server-derived project, behind
IBoardStore and the existing session-plus-tab middleware. get_board_card and get_board_agent_status
use the same bounded read. No route, tool grant, listener or authentication exception changed.
The UI escapes names/reasons; MCP sanitizes them. Source-generated response serialization remains.

Scoped Board route enumeration and both repository listener searches found the existing main
Kestrel host, non-serving PortFinder probe and test hosts only; no cross-runtime listener matches.
Disposable-fixture tests cover foreign cards/projects, run identity and pre-session states;
browser tests cover escaping and preserving drafts. No security violation was found.

## VIBE-20 explicit code reviews (2026-10-01, scoped amendment)

Added root-only `GET /api/v1/board/cards/{card}/reviews` and
`GET /api/v1/board/cards/{card}/reviews/{reviewId}` with no-store responses and the existing
session-plus-tab credential gate. The existing launch endpoint accepts explicit Code review
intent. Environment create/update validates `work` or `code_review`; omitted edits preserve
purpose. Project identity remains server-derived and every review read/write scopes the live card.

`begin_board_review`, `save_board_review` and `get_board_reviews` join the existing BoardTool
registrations and exact Board launch grants on HTTP/stdio. Writes require an explicitly classified
run and its current session; report finalization rechecks membership inside the store transaction.
Git capture uses the caller's actual checkout and validated full SHAs with argv-only reads.
Freshness never follows a supplied/stored path, and a different checkout reports Unknown. Report
text is bounded and escaped; the MCP report reader chunks output. No new executor, listener,
authentication exception, hosted publication or automatic card movement is introduced.

Scoped route enumeration and both mandatory repository-wide listener searches found only the
approved main Kestrel host, non-serving PortFinder probe and test hosts; the cross-runtime search
had no matches. Credential, project/card/session isolation, immutable reports, source-generated
serialization and launch grants are covered by regressions. No security violation was found.
This is a scoped amendment, not a full inventory re-audit.


## VIBE-26 remote Start work (2026-10-01, scoped amendment)

The active root backend polls the existing configured Board HTTPS destination every ten seconds,
using the header-only API key, no redirects, bounded responses and per-call destination fingerprint.
Only that root's project and published, non-imported Boards are advertised. A launch request carries
opaque Board/card IDs and a required sync sequence; no executable, path, argv or launch override.
The desktop syncs first and rechecks destination, project, Board and card membership before invoking
the ordinary BoardLaunchService. Existing narrow Board grants and local launch checks apply.

Five companion hosted routes are documented in Front `Services/Boards/RemoteLaunch.md`. Browser
launches require Auth0, a positive local Board owner and antiforgery. API polling/results require
the Board owner's API key and bind delivery/results to the exact key and desktop instance;
browser-authenticated API requests are rejected. Board collaborators cannot request execution.
Commands are delivered once; result loss is reported as unknown and never automatically retried.
The process-local hosted registry is bounded, expiring and subject to the existing Board rate limits.

Local route enumeration confirms no local endpoint or authentication exception changes. Both
mandatory repository listener searches found only the main Kestrel host, non-serving PortFinder
probe and existing test hosts; the cross-runtime search had no matches. Credential, owner/member,
CSRF, duplicate-delivery, destination-change and stale-sync regressions pass. No security violation
was found. This is a scoped review; no production listener or database schema was added.

## VIBE-24 card check evidence (2026-09-30, scoped amendment)

Added root-only `GET /api/v1/board/cards/{card}/checks` and
`GET /api/v1/board/cards/{card}/checks/{checkId}` on the existing host. Both retain the session
plus tab credential gate and return no-store responses. Project identity is server-derived;
all evidence reads/writes scope both card and project through `IBoardStore`. Scope capture
uses validated full SHAs and argv-only Git reads; neither endpoint accepts a filesystem path.
The existing per-card Automation POST queues runs; checks add no trigger or lane movement.

`read_board_check` is a read-only addition to BoardTool on HTTP and stdio and its exact launch
grant list. It returns saved, untrusted evidence in bounded text chunks. Reports render escaped
text, and Quality Lab/Atlas retain the existing iframe sandbox and teardown. No new production
listener, authentication exception or outbound disclosure is added; hosted evidence remains
VIBE-25 work. Additive Board evidence is local to the normal board.db.

Both mandatory listener searches found only the approved main Kestrel host, non-serving
PortFinder probe and test-only hosts; no cross-runtime acceptor matched. Scoped route
inspection and credential/project/card isolation regressions cover the new reads. No security
violation was found. This is a scoped amendment, not a full inventory re-audit.


## VIBE-15 account display (2026-09-30, scoped amendment)

The requested follow-up carries only the public `userCode` in a strict
`https://viberails.ai/link#code=ABCD-EFGH` fragment. The backend still accepts only the
fixed fragment-free verification URI. The VS Code bridge permits that optional exact-format
fragment and rejects other hosts, paths, queries, credentials and arbitrary fragments. The
hosted first-party script removes the fragment before Auth0, retains the code in same-tab
session storage for at most ten minutes, and submits the existing authenticated `/link` POST
with a fresh antiforgery token. Final approval remains explicit; secret `deviceCode` and API
keys stay backend-only. This supersedes the earlier code-free UI-link restriction below.

The existing settings responses now include response-only `remoteAccountEmail`. Only device
approval writes this display value, atomically with the credential and its SHA-256 fingerprint
in the existing private settings file. Requests cannot set the email; a full-key fingerprint
mismatch suppresses it, including after older-version or manual key changes. No raw credential
or fingerprint is added to a response. The dashboard renders the email with `textContent`.

The settings and device-link routes retain the existing session-plus-tab gate; no route,
authentication exception, external-link destination or production listener was added. Scoped
route enumeration and both mandatory repository-wide listener searches found only the existing
main Kestrel host, non-serving PortFinder probe and test-only Kestrel hosts; the cross-runtime
search had no matches. No new security violation was found. This is a scoped addition, not a
full route re-audit.

## Route and authentication reconciliation (2026-09-30)

Reconciled the active inventory against the current working tree in both directions,
including grouped and constant-based paths, all five proxy mappings, MCP, and the inherited
event WebSocket. Added 15 entries previously documented only in scoped amendments:
seven session-replay routes, six Board-sharing routes, card merge, and comment deletion.
No active entry needed removal. Corrected the totals to **246 mapped surfaces**, including
**234 under `/api/v1`** and **59 Board routes**. Earlier dated counts are historical;
removed-route descriptions below remain historical explanations, not active inventory entries.

Checked production registration and middleware ordering, session/tab validation, bootstrap
single-use consumption and two-minute expiry, local redirect validation, and the shared
proxy/control authentication gate. The only session-authentication exceptions remain exact
`GET /health`, `OPTIONS *`, and exact
`GET /auth/bootstrap?code={one-time-code}&redirect={local-path}`. Every other endpoint
requires a valid session credential. Business `/api/v1` handlers, MCP, WebSocket upgrades,
and enabled proxy operations additionally require the tab credential. Cookie and session
header are alternative transports of the same secret; existing session-only page/static
loads and conditional proxy responses remain documented in section 2.

Both mandatory repository-wide listener searches found only the approved main Kestrel host,
the non-serving PortFinder probe, and test-only Kestrel hosts. The cross-runtime search had
no matches. No additional endpoint lacking session authentication or production listener
was found, so no `SECURITY_ERROR.md` was created.

Validation: **136 passed, 0 failed, 0 skipped**, using `dotnet test Tests/Tests.csproj`
with `--artifacts-path C:/source/vibe-rails/Tests/obj/ApiSecAuditArtifacts --verbosity quiet`
and a `FullyQualifiedName` filter covering `CookieAuthMiddlewareTests`, `AuthServiceTests`,
`AuthRoutesTests`, all five LLM proxy route test classes, `TokenSaverPauseRoutesTests`,
`McpServerHttpTests`, `BoardRoutesTests`, `SessionReplayRoutesTests`, and
`RemoteAccountLinkRoutesTests`. The build reported existing xUnit analyzer warnings.
This was source reconciliation plus targeted regressions, not a live request sweep of
every production endpoint.

## VB-52 Board sharing (2026-09-30, scoped amendment)

Six root-only routes use the existing session-plus-tab middleware and no-store responses:
`GET /api/v1/board/shared`, `POST /api/v1/board/shared/{remoteId}/import`,
`GET|POST /api/v1/board/boards/{boardId}/sharing` and
`PUT|DELETE /api/v1/board/boards/{boardId}/sharing/{inviteId}`. Local operations derive the project
server-side and check board scope before remote calls. Transport reuses bounded HTTPS, header-only
API keys, no redirects and destination fingerprints. Imported boards retain their original
remote/destination identity and never auto-publish after revocation.

The companion hosted API checks owner-only collaborator CRUD and accepted-member Board access
on every request. Inviting never queries account existence. Verified-email recipients accept,
decline, leave and block through Auth0 and antiforgery. Declines and blocks are private. Each
board permits three pending/accepted collaborators. Membership extends only Board query filters;
session archives, terminal control and unrelated account data remain actor-owned. Sharing writes
advance a concurrency token, so racing card writes retry authorization. The companion
`Services/Boards/Sharing.md` enumerates hosted routes and narrow privileged queries.

Scoped route enumeration and both mandatory repository-wide listener searches found the main
Kestrel host, the non-serving PortFinder probe and test-only hosts; the cross-runtime search had
no matches. No new listener, authentication exception or unresolved violation was found. Isolated
HTTP tests cover both local credentials, project isolation, hosted owner/member permissions,
conflicting browser/key identities and CSRF. Relational tests cover the cap and revocation during
acceptance/card writes. This is a scoped addition, not a full re-audit.

## VB-6TYZ2-4 session replay (2026-09-30, scoped amendment)

Seven read-only routes are added on the existing root Kestrel host, all requiring the
normal session credential (cookie or header) plus tab header through CookieAuthMiddleware:
`GET /api/v1/session-replay/status`, `/sessions`, `/sessions/{id}`,
`/sessions/{id}/frames`, `/sessions/{id}/exchanges`, `/sessions/{id}/changes/{changeId}`,
and `/sessions/{id}/exchanges/{exchangeId}`. They expose local recording data to the same
authenticated dashboard user as chat history; every detail query includes the exact session ID.
Responses are no-store. SQLite reads use ReadOnly/query_only; Board labels go through IBoardStore.
No listener, authentication exception, outbound destination, schema or logging write was added.

Scoped route enumeration checked all seven mappings and their root-only registration.
Both mandatory repository-wide listener searches were repeated: the existing main Kestrel
host, non-serving PortFinder probe and test-only hosts were the only acceptors; the
cross-runtime search had no matches. The new route regression covers all seven routes with
missing/session-only/tab-only credentials, authorized reads, no-store and absent details.
The existing middleware suite also passes. This is a scoped addition, not a new full inventory audit.

## VIBE-1 card organization (2026-09-30, scoped amendment)

Added active-root `POST /api/v1/board/cards/{card}/merge` and
`DELETE /api/v1/board/cards/{card}/comments/{commentId}`. Both use the existing session-plus-tab
middleware. Project identity is derived server-side; merge scopes both source and destination
inside one transaction; deletion scopes the entry to a live card and accepts only a user author.
No delete/merge MCP tool or launch grant was added. Lane moves now accept another board's lane
within the same project. Navbar login reuses the protected device approval flow already listed.

The additive `deletedComment` sync field carries an entry ID, scoped to the owning board/card;
the hosted projection uses the existing owner-filtered sync service. There are no new hosted
endpoints. No credential or listener boundary changes. Both required repository listener searches
were repeated: approved main Kestrel, non-serving PortFinder and test-only Kestrel fixtures;
no cross-runtime listener matches. Scoped Board enumeration found 54 route mappings. Authentication
and wrong-project/wrong-card regression tests cover both new routes. No violation was found.

## VIBE-9 agent coordination (2026-09-29, scoped amendment)

Added `complete_board_agent` and `get_board_agent_status` to the existing `BoardTool` HTTP and
stdio registrations and exact per-tool launch grants. Completion accepts no caller-selected
session: it uses the current launch context and requires a live card/session link in the resolved
project. Reads scope the card, session and Automation trigger to that project. Reports do not
stop processes, change workflow outcomes, or move cards. Stdio liveness is explicitly unknown
when there is no live probe or recorded exit. Polling bounds agent rows, latest update text and
recent runs. Board-triggered Worker launches receive the same narrow grants; other launches
remain default-off. As with existing Board tools, launch context is local process identity, not
a per-card server ACL.

The optional Automation description is bounded to 2,000 characters, rendered as escaped text
and sanitized/fenced in launch prompts. Existing Jobs routes and `/mcp` remain behind both
session and tab credentials; no route or authentication exception was added. The new closure
event uses source-generated JSON on the existing authenticated event channel.

Scoped route enumeration and both mandatory repository-wide listener searches found the existing
main Kestrel host, non-serving PortFinder probe and test hosts only; the cross-runtime search had
no matches. No new listener or security violation was found. Regression coverage includes HTTP
MCP discovery/call boundaries, exact grants, project/session isolation and preserved recording data.

## VIBE-13 automatic Board publication (2026-09-29, scoped amendment)

At the owner's request, a configured API key now enables publication and linked activity for
every local board. This supersedes VIBE-12's publication/activity consent switches below. Old
switch fields remain accepted by the existing protected route but cannot disable sync. No
schema, route, listener or authentication bypass is added. Navigation sign-in uses the existing
device-link endpoints; Settings retains manual key management.

The root scheduler enumerates board identity metadata across projects through the dedicated
`IBoardStore.GetBoardsForSyncAsync` method. Card/activity reads still use each board's stored
project scope. One board's failure cannot stop later boards. Transport continues to require
HTTPS (loopback test exceptions only), no redirects, per-call destination fingerprints, and
the existing deadlines/bounds. A changed configured destination is republished next tick;
in-flight requests reject a fingerprint mismatch. A missing hosted board gets one bounded
republish/retry, resetting delivery marks for a new remote identity.

Activity also includes session outcome time, exit code and summary (up to 16,000 characters).
All VIBE-12 content limits and exclusions remain. Hosted replay requires the same authenticated
owner's existing uploaded recording and Sessions preview access. Linking Git code still
captures the durable snapshot in the desktop checkout; the website reads that saved snapshot.

Route enumeration confirms no route changes in this amendment. Repeated listener discovery
finds the main Kestrel host, the non-serving port probe and isolated test hosts only; the
cross-runtime search has no matches.

## VIBE-12 published Board activity (2026-09-29, scoped amendment)

Boards explicitly enabled for linked activity send snapshots to the same pinned website/account
through `PUT /api/v1/boards/{remoteBoard}/cards/{card}/activity`, after normal Card Log push/pull.
The outbound contract adds linked session identifiers/labels, saved commit before/after code,
attachment metadata/bounded content and linked card identities. See
[SYNC.md](VibeRails/Services/Board/SYNC.md#what-leaves-the-machine) for the bounds and exclusions.
This extends the older VB-51 scope only after the updated publish consent. Existing publications
keep the old scope until the user chooses **Sync linked activity**. `includeActivity:true` is
required on the existing authenticated, project-scoped publish action; old/omitted requests do
not enable it. Additive automatic board/23 stores the consent version (default 0) and durable
rotation cursor; older binaries preserve both. No project path, tab id, environment id, launch setting or Automation definition is
added. Session replay uses the existing completed-session export and same-owner hosted manifest.

The existing `X-Api-Key`, endpoint/key fingerprint, HTTPS policy (loopback-only HTTP fixtures),
redirect prohibition, 30-second deadline and bounded response reader apply unchanged. Every
activity acknowledgement must name schema 1 and the requested card. An older or incompatible
server cannot silently acknowledge activity; failures retry without undoing Card Log progress.
The snapshot caps transfer content, not local human uploads or storage. The store reads scoped
metadata before attachment bytes and reads durable code snapshots, never the current checkout.
No local route, listener or authentication bypass is added. The existing route retains session
and tab credentials, root-only mapping and server-derived project scope.

Both mandatory listener searches were repeated: only the existing main Kestrel host, non-serving
port probe and isolated test hosts matched; the cross-runtime search had no matches.

## VB-VF336-66 account linking (2026-09-29, scoped amendment)

Added `POST`, `GET`, and `DELETE /api/v1/settings/remote-link`, mapped only by an active
root backend. All three require the existing session and tab credentials and return
`Cache-Control: no-store`. POST starts approval, GET polls and saves an approved key, and
DELETE cancels. Responses and the `remote-account-linked` event contain only a short user
code, fixed verification page, progress, masked key hint and account display details.
Neither the secret device code nor API key is returned to the local browser.

The backend calls `POST /api/v1/device-links`, `POST /api/v1/device-links/token` and
`DELETE /api/v1/device-links` on the configured `VibeRails:FrontendUrl` origin. HTTPS is required
(HTTP only for loopback fixtures); endpoint credentials, paths, query strings and fragments
are rejected. The named client sends no existing API key or cookies and follows no redirects.
Every exchange has a 20-second deadline (cancellation cleanup: five seconds), JSON responses
are capped at 16 KiB, and the returned verification URL must equal that origin's `/link`.
The dashboard and VS Code bridge further restrict navigation to `https://viberails.ai/link`.

One in-memory attempt belongs to each root. Polling is serialized and throttled. Cancellation
and replacement invalidate late responses before persistence. A received key stays in backend
memory if saving fails, allowing a retry without a second redemption; cancel/replacement/shutdown
release it. The settings writer compares the original saved key before replacing it, so a
manual credential edit wins. Stored keys use the existing private `settings.json` file and
runtime activation/reset sequence. No database change is involved on the local side.

Both mandatory listener searches found only the approved main Kestrel host, non-serving
`PortFinder` probe and test-only Kestrel fixtures, including the new route tests. The
cross-runtime search had no matches. Both searches were repeated during review follow-up.
The canonical inventory below includes these three additions: 230 total surfaces, 218 under
`/api/v1`, and 21 in the application-settings group. The frozen listener set, skip list and
CORS policy are unchanged. This is a scoped addition, not a new full route reconciliation.

## Route and authentication reconciliation (2026-09-28)

Compared the current working tree's registered routes against the active inventory in both
directions, resolving grouped paths, constants, proxy mappings and the inherited event WebSocket.
Added the missing `GET /api/v1/board/cards/{card}/context` entry. No active entry needed removal.
The inventory now contains **227 mapped surfaces**, including **215 under `/api/v1`** and
**50 Board routes**. Earlier dated counts are historical.

VB-63 data boundary for that route and the launch it measures: `GET …/context` is read-only and
answers with labels and counts (characters, estimated tokens per part, the latest recorded
launch sample), never card text; a card outside the dashboard's project is 404. The existing
`POST …/launch` now also writes a `BoardContextSamples` row and a Card Log `change` entry with a
`context` field in `board.db`, and that field joins the outbound Board sync allowlist. It carries
token/character counts, the prompt/card-read split, intent, CLI and the per-part breakdown
(labels, counts, short notes); no session id, no card text, no paths. The hosted contract already
stores unknown change fields verbatim without applying them, so no server change or credential
was involved. The measurement runs after the terminal is linked, without the request token, and
cannot fail or cancel a launch. Route regressions cover both credentials, project scoping and the
no-card-text response.

Checked production registration, middleware ordering, session/tab validation, bootstrap code
expiry and single-use consumption, and the shared proxy/control gate. The only session-authentication
exceptions remain exact `GET /health`, `OPTIONS *`, and exact
`GET /auth/bootstrap?code={one-time-code}&redirect={local-path}`. Bootstrap requires a valid
single-use code expiring after two minutes. Every other endpoint requires a valid session
credential. All `/api/v1` business handlers, including the added context route, MCP, WebSocket
upgrades and enabled proxy operations additionally require the tab credential. Cookie and session
header carry the same secret as alternative transports; session-only pages/static files and
conditional proxy responses remain documented in section 2. No additional endpoint lacking
session authentication was found, so no `SECURITY_ERROR.md` was created.

Both mandatory repository-wide listener searches found only the approved main Kestrel host,
the non-serving port probe and test-only Kestrel hosts; the cross-runtime search had no matches.

Validation: **135 passed, 0 failed, 0 skipped**, using `dotnet test Tests/Tests.csproj
--artifacts-path C:/source/vibe-rails/Tests/obj/ApiSecAuditArtifacts --verbosity quiet` with a
`FullyQualifiedName` filter covering `CookieAuthMiddlewareTests`, `AuthServiceTests`,
`AuthRoutesTests`, all five LLM proxy route test classes, `TokenSaverPauseRoutesTests`,
`McpServerHttpTests`, `InternalToolsRoutesTests`, `SigningKeyRoutesTests`, `BoardRoutesTests`,
`JobRoutesTests` and `CodeGraphRoutesTests`. The build reported existing xUnit analyzer warnings.
This was source reconciliation plus targeted regression tests, not a live request sweep of
every production endpoint.

## VB-51 outbound Board sync review (2026-09-27)

Publishing is a default-off, per-board setting. The desktop sends board/lane metadata and
portable card fields, comments, notes and recorded field changes to the configured website.
The payload omits attachments, linked commits/cards, terminal sessions, launch options,
Automation definitions/settings, project paths and environment IDs. Environment assignments
become base CLI assignments remotely; user-authored text, including `@path`, travels verbatim.
See [the sync contract](VibeRails/Services/Board/SYNC.md) for the complete data boundary.

The existing API key is sent only in `X-Api-Key`. HTTPS is required except for loopback testing;
redirects and endpoint credentials/query/fragment are rejected. Each publication stores a
SHA-256 fingerprint of its endpoint and key, so a configuration change stops uploads until
publication is explicitly enabled again. The full exchange has a 30-second timeout and bounded
response size. Errors never copy arbitrary remote bodies into local logs or the status UI.
An OS file lock serializes scheduled and manual sync across root processes.

The four local routes use the existing root host and session-plus-tab authentication; History
is available only through the explicit human view, with no MCP addition. The Front repository
uses its API-key ownership filter for publish/push/pull and authenticated browser ownership
checks plus antiforgery for web writes. No desktop listener or authentication bypass was added.
Both mandatory listener searches were repeated: main Kestrel, the existing non-serving port
probe and test-only Kestrel hosts; no cross-runtime acceptors. The outbound environment-ID and
destination-binding findings in the inherited, undeployed implementation are resolved.

VB-51 review follow-up: hosted History now pages metadata in SQL before loading payloads;
History/pull responses use a conservative 8 MiB budget and discussions page 20 entries. (An
earlier draft of this note also claimed a global four-request browser concurrency limit; no such
limiter exists in either repository and the claim is withdrawn.) Desktop error handling reads
only bounded, validated error codes/IDs (`invalid_entry` with its entry id, `invalid_request`,
`board_not_found`, `write_conflict`, each accepted only on its documented HTTP status), retains
the rejected local data and shows a durable status notice in the desktop's own wording. It never
displays remote error prose, and it never switches publishing off by itself. These changes introduce
no local route, listener or authentication exception. The hosted resource-exhaustion findings
are resolved; their regression tests cover SQL paging and response bounds.

Full route/authentication reconciliation (2026-09-27): **226 mapped surfaces**, including
**214 under `/api/v1`** and **49 Board routes**, match the current working tree, including
uncommitted and untracked source. Added four missing Board mappings: `GET
/api/v1/board/boards/{boardId}/history`, `GET` and `PUT /api/v1/board/boards/{boardId}/sync`,
and `POST /api/v1/board/boards/{boardId}/sync/now`. No active inventory entry needed removal.
Earlier dated audit and amendment counts are historical.

The only session-authentication exceptions remain exact `GET /health`, `OPTIONS *`, and
exact `GET /auth/bootstrap?code={one-time-code}&redirect={local-path}`. Bootstrap validates
and atomically consumes a single-use code that expires after two minutes. Every other
endpoint requires a valid session credential. All `/api/v1` business handlers, including
the four additions, MCP, WebSocket upgrades, and enabled proxy operations additionally
require the tab credential. Cookie and session header are alternative transports of the
same secret; session-only page/static loads and conditional proxy responses remain
documented in section 2. No additional endpoint lacking session authentication was found,
so no `SECURITY_ERROR.md` was created.

Resolved grouped and constant-based paths, proxy mappings, the inherited event WebSocket,
and production registration; checked middleware ordering, credential validation, bootstrap
validation, and the shared proxy/control gate. Both mandatory repository-wide listener
searches found only the approved main Kestrel host, non-serving port probe, and test-only
Kestrel hosts; the cross-runtime search had no matches.

Validation: **129 passed, 0 failed, 0 skipped**, using `dotnet test Tests/Tests.csproj
--artifacts-path C:/source/vibe-rails/Tests/obj/ApiSecAuditArtifacts --verbosity quiet` with a
`FullyQualifiedName` filter covering `CookieAuthMiddlewareTests`, `AuthServiceTests`,
`AuthRoutesTests`, all five LLM proxy route test classes, `TokenSaverPauseRoutesTests`,
`McpServerHttpTests`, `InternalToolsRoutesTests`, `SigningKeyRoutesTests`, `BoardRoutesTests`,
`JobRoutesTests`, and `CodeGraphRoutesTests`. The build reported nullable-reference and
xUnit analyzer warnings in existing tests. This was source reconciliation plus targeted
regression tests, not a live request sweep of every production endpoint.

Full route/authentication reconciliation (2026-09-26): **222 mapped surfaces**, including
**210 under `/api/v1`** and **45 Board routes**, match the current working tree in both
directions, including uncommitted and untracked source. No endpoint needed adding or removal;
updated the stale current totals below. Earlier dated audit and amendment counts are historical.
Resolved grouped and constant-based paths, proxy mappings, the inherited event WebSocket,
and production route registration; checked middleware ordering and credential validation.

The only session-authentication exceptions are exact `GET /health`, `OPTIONS *`, and
exact `GET /auth/bootstrap?code={one-time-code}&redirect={local-path}`. Bootstrap validates
and atomically consumes a single-use code that expires after two minutes. Every other
endpoint requires a valid session credential. All `/api/v1` business handlers, MCP,
WebSocket upgrades, and enabled proxy operations additionally require the tab credential.
Cookie and session header carry the same secret as alternative transports; session-only
page/static loads and conditional proxy responses remain documented in section 2.
No additional unauthenticated endpoint was found, so no `SECURITY_ERROR.md` was created.

Both mandatory repository-wide listener searches found only the approved main Kestrel host,
non-serving port probe, and test-only Kestrel hosts; the cross-runtime search had no matches.
Validation: **129 passed, 0 failed, 0 skipped**, using `dotnet test Tests/Tests.csproj
--artifacts-path C:/source/vibe-rails/Tests/obj/ApiSecAuditArtifacts --verbosity quiet` with a
`FullyQualifiedName` filter covering `CookieAuthMiddlewareTests`, `AuthServiceTests`,
`AuthRoutesTests`, all five LLM proxy route test classes, `TokenSaverPauseRoutesTests`,
`McpServerHttpTests`, `InternalToolsRoutesTests`, `SigningKeyRoutesTests`, `BoardRoutesTests`,
`JobRoutesTests`, and `CodeGraphRoutesTests`. This was source reconciliation plus targeted
regression tests, not a live request sweep of every production endpoint.

VB-44 review amendment (2026-09-26): added read-only `POST /api/v1/board/cards/activity`.
The active-root registration and session-plus-tab middleware apply unchanged. Requests supply a
board ID and at most 100 card IDs (each ID at most 100 characters); the project is server-derived.
Parameterized SQL scopes the board, lanes, cards and live links within that project, returning
only card IDs, active session/tab IDs and Automation indicators. It selects no descriptions,
comments, attachments or ended session history. No schema or listener change. Source-generated
JSON and route regressions cover both credentials, request bounds and project/board scoping.
Linked recordings are also excluded before the existing card Automation query's 20-run limit.

Scoped enumeration now finds 45 Board routes. Both mandatory repository-wide listener searches
found only the existing main Kestrel host, non-serving port probe and test Kestrel hosts; the
cross-runtime search had no matches. No security violation was found. This is a scoped amendment.

VB-44 card Automation amendment (2026-09-26): added `GET` and `POST
/api/v1/board/cards/{card}/automations`. Both are active-root only behind the existing
session and tab credentials. GET returns project Automation choices and up to 20 recent
card-originated runs without a linked recording. POST accepts only `jobId`; the card resolves
through `IBoardStore` in the server-derived project. The job must belong to that project and
be enabled, undeleted and runnable. Project, enabled state and overlap are checked again
inside the existing run/action snapshot transaction. No browser-supplied trigger key, project
path or terminal input is accepted. Runs reuse the normal scheduler and Board terminal-tab
launcher; their recordings link through `IBoardStore`. No schema or credential change.

Scoped route enumeration found 44 Board routes (two additions to the previous inventory).
Both mandatory repository-wide listener searches found only the existing main Kestrel host,
the non-serving port probe and test-only Kestrel hosts; no cross-runtime matches. Authenticated
route tests cover source-generated JSON, missing credentials, invalid jobs, scope, queuing and
overlap. This is a scoped amendment, not a new full authentication audit. No security violation
was found.

Full route/authentication reconciliation (2026-09-24): **215 mapped surfaces**, including
**203 under `/api/v1`** and **38 Board routes**, match the current working tree in both
directions, including uncommitted and untracked source. No endpoint needed adding or removal.
Resolved grouped and constant-based paths, the inherited event-WebSocket mapping, and
production registration; checked middleware ordering, session/tab validation, bootstrap
expiry and single-use consumption, and the shared proxy/control authentication gate.

The only session-authentication exceptions remain exact `GET /health`, `OPTIONS *`, and
exact `GET /auth/bootstrap?code={one-time-code}&redirect={local-path}`. Bootstrap requires
a valid single-use code that expires after two minutes. Every other endpoint requires a
valid session credential. All `/api/v1` business handlers, MCP, WebSocket upgrades, and
enabled proxy operations additionally require the tab credential. Cookie and session header
are alternative transports of the same secret; the session-only page/static loads and
conditional proxy responses remain documented in section 2. No additional unauthenticated
endpoint was found, so no `SECURITY_ERROR.md` was created.

Both mandatory repository-wide listener searches found only the approved main Kestrel host,
non-serving port probe, and test-only Kestrel hosts; the cross-runtime search had no matches.
Validation: **126 passed, 0 failed, 0 skipped**, using `dotnet test Tests/Tests.csproj
--artifacts-path C:/source/vibe-rails/Tests/obj/ApiSecAuditArtifacts --verbosity quiet` with a
`FullyQualifiedName` filter covering `CookieAuthMiddlewareTests`, `AuthServiceTests`,
`AuthRoutesTests`, all five LLM proxy route test classes, `TokenSaverPauseRoutesTests`,
`McpServerHttpTests`, `InternalToolsRoutesTests`, `SigningKeyRoutesTests`, `BoardRoutesTests`,
`JobRoutesTests`, and `CodeGraphRoutesTests`. This was source reconciliation plus targeted
regression tests, not a live request sweep of every production endpoint.

VB-41 code report amendment (2026-09-23): added read-only
`POST /api/v1/code-analyzer/graph`, mapped only by an active root backend and requiring
both existing credentials. The request can prioritize up to 1,000 safe repository-relative
paths, never supply a root or arbitrary file to read. Git's file catalog and the existing
working-tree path guard constrain source reads; links/junctions, unsafe paths and non-source
files are excluded. Limits are 1,000 files, 2,800 nodes, 10,000 edges, 128 KiB per file,
16 MiB source budget and a 4 Mi-character catalog with a 20-second Git timeout. The response
contains directory/declaration structure and lexical reference evidence; it changes no files
or database data. The viewer uses the authenticated host API/assets, existing themes and
nonce, and an opaque-origin sandboxed iframe with explicit navigation teardown. CSP,
credentials, middleware and production listeners are unchanged.

The active inventory is now **219 mapped surfaces**, **207 under `/api/v1`** and
**42 Board routes**. Route enumeration/registration and both mandatory listener searches
were repeated: production matches remain the main Kestrel host and non-serving port probe;
other matches are test-only Kestrel hosts, with no cross-runtime matches. No security
violation was found. Graph route tests cover both credentials, unsafe input, real repository
response and AOT serialization. This is a scoped amendment, not a fresh full authentication audit.

VB-40 Jira pull amendment (2026-09-25): added four Board routes,
`GET` and `PUT /api/v1/board/boards/{boardId}/jira`, `POST …/jira/test` and
`POST …/jira/pull`. Same active-root mapping and the same session-plus-tab credentials as
every other Board route; the project still comes from the dashboard root, never the request.
The PUT body may carry an API token. The token is written to `~/.vibe_rails/jira-tokens.json`
(plain text, same user-locked directory as `settings.json`) and is absent from `board.db`,
from every response, and from logs. A blank token on a later save keeps the stored one.
Outbound calls go to the saved `https` site origin only (`/rest/api/3/myself` and
`/rest/api/3/search/jql`); redirects are off, so the token is not forwarded to another host.
Changing the site origin requires the token again: a saved token is never sent to a different
origin, and the old connection's token is deleted. The token file is read and written under an
OS file lock (`jira-tokens.json.lock`), so two root backends cannot lose each other's tokens.
Invalid input, a missing token or an expired connection is a 400 with a readable message.
No new listener. No security violation was found, so no `SECURITY_ERROR.md` entry.

Full route/authentication reconciliation (2026-09-23): **214 mapped surfaces**, including
**202 under `/api/v1`** and **38 Board routes**, match the current working tree in both
directions. No endpoint needed adding or removal; corrected the stale totals in the
terminology section. These are the totals for that audit; dated amendments below retain their
historical counts.

The only session-authentication exceptions remain exact `GET /health`, `OPTIONS *`, and
exact `GET /auth/bootstrap?code={one-time-code}&redirect={local-path}`. Bootstrap validates
and atomically consumes a single-use code that expires after two minutes. Every other
endpoint requires a valid session credential. All `/api/v1` business handlers, MCP,
WebSocket upgrades, and enabled proxy operations additionally require the tab credential;
session-only page/static loads and conditional proxy responses remain documented in section 2.
Cookie and session header are alternative transports of the same secret, not two independent
credentials. No additional unauthenticated endpoint was found, so no `SECURITY_ERROR.md`
was created.

Resolved grouped and constant-based routes and the inherited event-WebSocket mapping;
checked production registration, middleware ordering, session/tab validation, bootstrap
validation, and shared proxy/control authentication. Both mandatory repository-wide listener
searches found only the approved main Kestrel host, non-serving port probe, and test-only
hosts; the cross-runtime search had no matches. Validation: **121 passed, 0 failed, 0 skipped**
using `dotnet test Tests/Tests.csproj --artifacts-path
C:/source/vibe-rails/Tests/obj/ApiSecAuditArtifacts --verbosity quiet` with a
`FullyQualifiedName` filter covering `CookieAuthMiddlewareTests`, `AuthServiceTests`,
`AuthRoutesTests`, all five LLM proxy route test classes, `TokenSaverPauseRoutesTests`,
`McpServerHttpTests`, `InternalToolsRoutesTests`, `SigningKeyRoutesTests`, `BoardRoutesTests`,
and `JobRoutesTests`. This was source reconciliation plus targeted regression tests, not
a live request sweep of every production endpoint.

VB-37 MCP image-bound amendment (2026-09-23): the existing `read_board_attachment` tool now
returns PNG/JPEG/GIF/WebP content only up to 5 MiB. A server-derived, project/card-scoped metadata
read uses the signature-derived MIME stored at upload to reject larger images before selecting their
BLOB or legacy data URL. The bytes are re-sniffed after loading, and a defensive byte-length check
runs before MCP serialization for inconsistent legacy/corrupt metadata. The error reports the actual/allowed sizes and directs the
caller to the authenticated Board viewer. This is only an MCP transfer budget: human uploads,
stored attachment bytes and Board-viewer downloads remain unlimited. Markdown/TXT behavior is
unchanged. No route, tool name, grant, listener, credential rule, schema or migration changed.

VB-37 Board launch/paging amendment (2026-09-23): the existing authenticated card create/update
payload can persist a default-false `baseLlmOptions.yolo` choice only for a base-CLI assignee.
Start work translates it server-side to that provider's documented global bypass/auto-approve
process flag; it does not rewrite provider configuration, and saved environments retain their own
arguments. This is an explicit user-selected capability separate from the narrow, always-on Board
tool allowlist. The existing paged card-list route also accepts an opaque `continuationToken`; if
the filtered lane order changed since the preceding page, it returns `restartRequired` and a page
from offset zero so the browser can refresh instead of silently omitting a promoted card. No route,
listener, credential rule, middleware order, schema or migration changed.

VB-29 Board pagination amendment (2026-09-23): the existing authenticated, active-root
`GET /api/v1/board/cards` accepts optional `pageSize`, `columnId`, `offset`, `q`, `assignee`,
`type`, `priority` and `tag` query fields. Omitting `pageSize` retains the unpaged response.
The server still derives the project, resolves the selected board within it, and rejects a
lane outside that board. SQL parameters carry all filter values; page size is bounded to
1–100 and offsets are nonnegative. Counts and filter choices are scoped to the same board,
including unloaded cards. Page membership and metadata share a deferred read snapshot;
only selected cards' full summaries are materialized. No route, credential exception,
listener or MCP capability was added. Route enumeration (including grouped/constant paths,
proxy mappings and the inherited event WebSocket) and both mandatory listener searches
were repeated: only the approved main Kestrel host, non-serving port probe and test hosts
matched; there were no cross-runtime listener matches. This is a scoped change review,
not a new full authentication audit. Real SQLite and authenticated/AOT Board route tests
cover page bounds, unloaded-card filtering, cross-project/board rejection, both credentials
and compatibility with unpaged clients.

VB-29 attachment-read amendment (2026-09-23): the existing `read_board_attachment`
tool now returns MCP image content for byte-sniffed PNG/JPEG/GIF/WebP attachments,
preserving the original bytes. Markdown/TXT retain their bounded text reads. Resolution
still uses the current card/project and attachment id through `IBoardStore`; removed or
foreign-card attachments are not readable. No path argument, SQL tool, temporary file
export, new tool name/grant, route or listener is introduced. Image and text results remain
untrusted task data, and unsupported binary types remain in the Board viewer. Tool help
no longer claims retained historical attachments exist. Launch prompts explicitly direct
agents to Board tools as the only access path for card data and attachments; storage details
are absent from generated guidance and busy-error responses. HTTP retains
both credentials; stdio retains the same local child-process boundary.

VB-35 file-reference amendment (2026-09-23): one authenticated route added, `GET /api/v1/board/files?q=`,
mapped with the other Board routes only by an active root backend and behind both credentials
through the existing `/api/v1` middleware; no new listener, credential rule or exception. It returns
repository-relative file **names** (never contents) from `git ls-files --cached --others
--exclude-standard` under the dashboard's root path, or a bounded directory walk when git is
unavailable, capped at 50 results with `q` at most 256 characters; nothing is stored. Card text may
now carry `@path` references, which the renderer emits from escaped text with no href and the
launch prompt lists inside the fenced card block after sanitising. The active inventory becomes
**214 mapped surfaces**, **202 under `/api/v1`** and **38 Board routes**; `Tests/Routes/BoardRoutesTests.cs`
pins the credential requirement and the AOT JSON binding.

VB-31 cross-repository Automation import amendment (2026-09-22): two authenticated routes added under
`/api/v1/jobs`, `GET /api/v1/jobs/catalog` and `POST /api/v1/jobs/import`, both mapped only by an
active root backend (`JobRoutes.Map(app, launchDirectory, isActiveRootBackend)`) like the Python-script
import route, because they read other repositories' Automations and Workers out of state.db and write
script files into the current working tree. Both credentials apply through the existing `/api/v1`
middleware; no new listener, credential rule or exception. Script copies are repository-relative only,
contained on both sides, refuse links/reparse points, never overwrite an existing target file, and are
re-hashed in the target repository before the Automation is created (disabled). The active inventory
becomes **213 mapped surfaces**, **201 under `/api/v1`**; `Tests/Routes/JobRoutesTests.cs` pins the
root-only mapping and the AOT JSON binding.

Full route/authentication reconciliation (2026-09-22): **211 mapped surfaces**, including
**199 under `/api/v1`** and **37 Board routes**, in the current working tree. The active
inventory matches the code in both directions; no endpoint needed adding or removal. Corrected
the stale Kanban subsection heading from 38 routes to 37 after the description-history route's
2026-09-20 removal. The only session-authentication exceptions remain exact `GET /health`,
global `OPTIONS`, and exact `GET /auth/bootstrap?code={one-time-code}&redirect={local-path}`.
Every other endpoint requires the session credential; `/api/v1`, MCP, WebSocket upgrades, and
enabled proxy operations retain their documented additional tab-token checks. The mandatory
listener searches found only the approved main Kestrel host, non-serving port probe, and
test-only hosts. No insecure endpoint or additional production listener was found, so no
`SECURITY_ERROR.md` was created. The targeted authentication, proxy, MCP, diagnostics,
signing-key, and Board route suite passed **114 tests** with no failures or skips.

VB-25 session attachment amendment (2026-09-21): `attach_board_session` adds one explicitly
granted Board tool (14 total) on the existing HTTP and stdio transports. The only caller argument
is a card key/id; session identity comes from launch context and project identity from the
existing resolver. Store writes reject links to another project within the same transaction.
The existing session REST routes now allow the same session on multiple cards in one project;
rename/unlink still scopes each pair. No route, listener, credential or middleware is added.
The existing commit-link tool now shares a captured commit with the caller's attached cards,
scoped to the resolved project and committed atomically. The session is supplied by launch
context, never a tool argument; no new unlink/edit capability or grant is added.
Route enumeration and both mandatory listener searches were repeated: production matches are
the existing main Kestrel host and non-serving port probe; other matches are test hosts, with no
cross-runtime matches. This is a scoped change review, not a new full authentication audit.

Board simplification amendment (2026-09-20): removed the description-history GET route and
MCP history tool. The active inventory is **211 mapped surfaces**, **199 under `/api/v1`**,
including **37 Board routes**. Card create/update/list/detail now include a boolean `flagged`
for user attention; it uses the existing scoped/authenticated routes and MCP update tool.
There are 13 Board tool grants. Removed files are no longer downloadable; deletion cascades
their bytes. No credential rule, listener or middleware changed. Both listener-discovery
searches found only the approved main host, port probe and test hosts (no cross-runtime matches).
The earlier counts and history descriptions below are dated historical audits.


Full route/authentication reconciliation (2026-09-19): **212 mapped surfaces**, including
**200 under `/api/v1`** and **38 Board routes**, in the current working tree, including
uncommitted and untracked source. Added four previously undocumented Board mappings:
GET/PUT board context settings and GET/PUT lane automation settings. No active inventory
entry refers to a removed endpoint. These totals supersede all dated historical totals below.

The only session-authentication exceptions remain exact `GET /health`, `OPTIONS *`, and
exact `GET /auth/bootstrap?code={one-time-code}&redirect={local-path}`. Bootstrap validates
and atomically consumes a code that expires after two minutes. Every other endpoint requires
a valid session credential; `/api/v1`, MCP, WebSocket upgrades, and enabled proxy operations
additionally require the tab credential. Cookie and session header are alternative transports
of the same secret. Session-only page/static loads and conditional proxy responses remain
documented in section 2. No additional unauthenticated endpoint was found, so no
`SECURITY_ERROR.md` was created.

Resolved grouped and constant-based routes and the inherited event-WebSocket mapping;
checked production registration, middleware ordering, session/tab validation, bootstrap
validation, and shared proxy/control authentication. Both mandatory repository-wide listener
searches found only the main Kestrel host, non-serving port probe, and test-only hosts; the
cross-runtime search had no matches. This is source reconciliation plus targeted regression
validation, not a live request sweep of every production endpoint.

Validation: **109 passed, 0 failed, 0 skipped**, covering `CookieAuthMiddlewareTests`,
`AuthServiceTests`, `AuthRoutesTests`, all five LLM proxy route test classes,
`TokenSaverPauseRoutesTests`, `McpServerHttpTests`, `InternalToolsRoutesTests`,
`SigningKeyRoutesTests`, and `BoardRoutesTests`. Ran `dotnet test Tests/Tests.csproj`
with a `FullyQualifiedName` filter for those classes and
`--artifacts-path C:/source/vibe-rails/Tests/obj/ApiSecAuditArtifacts --verbosity quiet`.
The initial build with only a separate output path hit a compiler-held intermediate DLL;
the successful run isolated both intermediate and output artifacts.

Card-linking amendment (2026-09-18): three authenticated active-root Board routes add candidate
search, link, and unlink. Both cards resolve inside the server-derived project, including links
across that project's boards; self-links are rejected and duplicates are idempotent. Searches
are parameterized, capped at 50 results, and return only card identity/title and board/lane
metadata. `get_board_card` also reads existing links through its existing project scope; no MCP
tool or launch grant was added. The inventory is now **208 mapped surfaces**, **196 under
`/api/v1`**, including **34 board routes**, superseding the earlier totals below. Route
enumeration and both mandatory listener searches found only the main Kestrel host, non-serving
port probe, and test-only hosts; the cross-runtime search had no matches. No listener,
middleware, credential rule, or security exception changed.
Validation: 344 Board/authentication/database tests passed, including the generated schema
snapshot and legacy-compatibility checks; the Board frontend and browser regression suites passed.

Python MCP removal amendment (2026-09-18): custom Python tools and
`python_script_signing_help` are no longer registered in either MCP transport. Removed
`GET|PUT|DELETE /api/v1/python-scripts/mcp`; ordinary signed-script authoring and execution
remain. This is an owner-requested feature removal. Board launches use exact per-tool
grants for the 14 Board tools, with no server-wide approval rule.

Route enumeration now finds **205 mapped surfaces**, **193 under `/api/v1`**, including
**31 board routes** and **13 Python-script routes**. These totals supersede the earlier
amendment totals below. Both mandatory listener searches were repeated: the main Kestrel
host, non-serving port probe, and test-only Kestrel hosts were the only matches; the
cross-runtime search had no matches. No listener, middleware, or credential rule changed.
This amendment is a capability-removal check, not a new full authentication audit.
Validation: 415 targeted backend tests passed (one skipped), covering Board, Python scripts,
MCP, launch grants, and authentication middleware; 163 affected frontend tests passed.

Audit date: 2026-09-15 (route inventory amended 2026-09-17: +2 board note routes, both under
`/api/v1` and therefore behind both credentials by construction — **207 mapped surfaces**,
**195 under `/api/v1`**; the rest of the reconciliation below is unchanged).

Full route/authentication reconciliation (2026-09-15): **205 mapped surfaces**, including
**193 under `/api/v1`**, nine protected non-`/api` API surfaces, and three bootstrap/page/probe
mappings. The current inventory matches the working tree in both directions, including
uncommitted and untracked source; no endpoint needs adding or removing. Corrected the stale
totals in the terminology section below. The only session-authentication exceptions remain
exact `GET /health`, global `OPTIONS`, and exact `GET /auth/bootstrap` with its single-use,
expiring code. Every other endpoint requires a valid session credential; `/api/v1`, MCP,
WebSocket upgrades, and enabled proxy operations additionally require the tab credential.
Session-only page/static loads and conditional proxy responses remain documented in section 2.
Both mandatory listener searches found only the main Kestrel host, the non-serving port probe,
and test-only hosts; the cross-runtime search had no matches. No insecure endpoint or additional
production listener was found, so no `SECURITY_ERROR.md` was created. Targeted authentication
and route tests passed: **103 passed, 0 failed, 0 skipped**. See Audit observations for scope.

Board usability amendment (2026-09-14, revised 2026-09-15): two active-root board mappings were
added: description history and attachment content downloads. A third, explicit agent notification,
was added and then **removed** on 2026-09-15 — see the Kanban board section for why. The inventory
is **205 mapped surfaces**, **193 under `/api/v1`**, including **25 board routes**. Both surviving
additions require both credentials and server-derived project scope.
Multi-board amendment (2026-09-18, VB-11): four board-management routes (`/api/v1/board/boards`
list/create, rename, delete) bring the inventory to **209 mapped surfaces**, **197 under
`/api/v1`**, **29 board routes**. Same middleware, same server-derived project scope; a board id
in a query string or body is resolved inside that project only.
The combined board/terminal/CLI-authorization/authentication regression suite passed **437 tests**. Route
enumeration and both mandatory listener searches were repeated: only the existing main
Kestrel listener, non-serving port probe, and test-only hosts matched; the cross-runtime
search had no matches. No listener or middleware bypass was added. This amendment covers
the new board capabilities; the preceding full authentication reconciliation follows.

Board MCP authorization amendment (2026-09-14): Board **Start work** explicitly sets the
default-false `AuthorizeBoardTools` launch field, including when a saved environment is
selected. The authenticated terminal-start API forwards this explicit choice; card text,
titles and environment names cannot enable it. `BoardMcpAuthorization` enumerates exactly
fourteen Board tools (as of 2026-09-18), with no server wildcard, unrelated MCP tools, dynamic Python tools, global
approval-policy change or sandbox bypass from that authorization path. The later VB-37 YOLO
choice is a separate, explicit base-CLI launch option. Codex, Claude, Copilot and Grok receive per-tool
argv grants; OpenCode and its variants receive per-tool `OPENCODE_PERMISSION` entries.
The latter retains unrelated inherited rules and conservatively skips matching inherited
environment deny rules. Native CLI configuration precedence and managed policies still
apply. Antigravity receives only the explicit authorization prompt because a narrow native
grant is unverified. Grants live only in the launched process, never in shared settings or
MCP registration commands. No route, listener, credential exception or middleware change
was introduced by this amendment. Route enumeration and both listener searches were repeated
with the same results recorded above. Installed Codex 0.154.0 accepted the per-tool config
in an isolated offline `mcp get` check; live provider approval behavior was not exercised.

Prior full check after removing the seven VibeRails Demon lifecycle routes: all 203 mapped
surfaces matched that working tree (191 under `/api/v1`), with no missing entries.
Both mandatory listener searches and the targeted authentication suite were repeated:
**101 passed,
0 failed, 0 skipped**. The three permitted authentication exceptions remain unchanged;
no insecure endpoint was found and no `SECURITY_ERROR.md` was needed.

Full production route/authentication reconciliation completed before this board amendment
on 2026-09-14, including uncommitted and untracked source. All 203 mapped
surfaces match this inventory in both directions: 191 `/api/v1` method/path surfaces,
nine protected non-`/api` API surfaces, and three bootstrap/page/probe mappings. No
endpoint needs adding or removing. The existing inventory includes the board and
signing-key routes, the signing-key route group, and constant-based route paths.
The only session-authentication exceptions are exact `GET /health`, global `OPTIONS`,
and exact `GET /auth/bootstrap` with its single-use, expiring code. No additional
endpoint lacking a valid session credential was found, so no `SECURITY_ERROR.md` was
created. Session-only page/static loads and conditional proxy responses remain as
documented in section 2. Both repository-wide listener searches found only the main
Kestrel host, the non-serving port probe, and test-only hosts. Targeted authentication
and route tests passed: **101 passed, 0 failed, 0 skipped**. See Audit observations for
scope and validation details.

Historical production route/authentication reconciliations on 2026-09-12 and 2026-09-11
covered the 210 mapped surfaces present before the Demon lifecycle routes were removed.
The frozen listener set and three-case middleware bypass were unchanged. Those observations
remain below with their historical counts.

Signing-key amendment: 2026-09-09. Five authenticated active-root settings routes were
added, bringing the current inventory to 175 `/api/v1` surfaces and 187 total mapped
surfaces. The frozen listener set and middleware bypass list are unchanged. The full
2026-09-08 audit below remains historical; this amendment checks the new routes and
repeats route enumeration and both repository-wide listener searches. Only the main
Kestrel host, the non-serving port probe, and test-only Kestrel hosts matched; no other
production listener was found. New route round-trip/authentication tests and the
existing `CookieAuthMiddlewareTests` passed (31 tests).

Kanban board inventory reconciled: 2026-09-09 (the 23 authenticated, active-root-only
`/api/v1/board/*` routes added to section 3, bringing the `/api/v1` count from 175 to 198; the
board's MCP tools ride the existing `/mcp` surface and the stdio `vb mcp` host, which adds no
listener — see that section for why the stdio host needs no credential).

Historical production route/authentication reconciliation: 2026-09-08, covering all 170
current `/api/v1` method/path surfaces, nine protected non-`/api` API surfaces, and
the three bootstrap/page/probe mappings. The only middleware bypasses remain exact
`GET /health`, exact `GET /auth/bootstrap`, and global `OPTIONS` requests.

This audit found no missing or removed endpoints and no additional endpoint lacking a
valid session credential. No `SECURITY_ERROR.md` was needed. The existing one-credential
page/static-file behavior and conditional proxy responses remain documented in section 2.
Cookie and session-header authentication are alternative transports of the same secret;
the additional check on business APIs is the tab token, not a second copy of the session token.

Internal diagnostics inventory reconciled: 2026-09-06 (the two authenticated, active-root-only
`GET /api/v1/internal/*` read routes introduced with the Internal tools modal were added to
section 3, bringing the `/api/v1` count to 170; the raw-Serilog exposure decision is recorded
there).

Python-script MCP inventory reconciled: 2026-08-21 (three authenticated MCP-configuration
routes and the authenticated, active-root-only interactive-run route added to the inventory).

Python-script and automation-navigation inventory reconciled: 2026-08-18 (12 authenticated
Python-script routes and three authenticated automation-navigation preference routes added to
the inventory; the Python-script import route is active-root-only). Six of the Python-script
routes are new working-tree mappings; the other six Python-script and three automation routes
were existing mappings missing from the prior inventory.

Filesystem-picker inventory added: 2026-08-16 (one authenticated, active-root-only metadata
endpoint; no new listener and no authentication bypass).

Jobs inventory re-checked: 2026-08-03 (three run-history routes added; see § 3).

LLM proxy posture re-audited: 2026-08-05 (`/llm/openai` and `/llm/zai` removed from the
`CookieAuthMiddleware` skip list; the § 1 conditional-response finding is resolved — see
§§ 1–3).

Route inventory re-checked: 2026-08-07 (five authenticated HTTP-relay proof mappings
added; see § 3).

Route inventory and authentication coverage re-checked: 2026-08-13 (one authenticated
environment-step test endpoint added; the frozen three-case middleware bypass remains
unchanged).

Route inventory updated: 2026-08-15 (`ANY /llm/xai/{**rest}` added as a fourth Kestrel-mapped
LLM proxy tree for OpenCode's xAI/Grok provider). This is not the rejected `/llm/grok`
sidecar. The frozen three-case middleware bypass remains otherwise
unchanged.

Route inventory updated: 2026-08-23 (`ANY /llm/cli-chat/{**rest}` added as a fifth
Kestrel-mapped LLM proxy tree for the native Grok Build CLI). Upstream is
`cli-chat-proxy.grok.com` (subscription / `grok login`) or `api.x.ai` (API key),
selected by `GrokLlmProxyMode`. This is not `/llm/grok` and not a second listener.
The frozen three-case middleware bypass remains unchanged.

Listener topology re-checked: 2026-08-26. The only approved production request listener
is the main Kestrel host. An uncommitted Grok integration's second `HttpListener` was
rejected and is logged below; no other production request listener was found.

Scope: the current working tree, including uncommitted changes. Approved inventory counts
describe the surfaces allowed to remain. Rejected working-tree changes are logged separately
and must be removed before merge rather than normalized into the approved inventory.

## Listener topology and discovery (FROZEN — review gate)

The authentication inventory is valid only if it finds **every way the process can accept a
network request**, not merely endpoints mapped on the main ASP.NET application. A second
loopback listener is a second security boundary: it does not pass through
`CookieAuthMiddleware`, the normal pipeline ordering, request logging/redaction, CORS, or the
main server's lifecycle controls merely because it eventually forwards to an authenticated
route. Loopback binding limits reachability; it is not authentication and does not make a
listener part of the existing gate.

### Approved production listener set

The closed set is:

1. The Kestrel host created in `VibeRails/Program.cs`, configured with `ListenLocalhost`.
   Each VibeRails backend process may create that host once.
2. `VibeRails/Utils/PortFinder.cs` may briefly start and stop a loopback `TcpListener` to
   test whether a port is available. It has no accept loop, reads no requests, and is not a
   serving surface.

There is no approved secondary HTTP server, sidecar listener, raw socket accept loop, or
provider-specific listener in production code. Test-only Kestrel hosts under `Tests/**` are
not shipped and are outside this production closed set.

**This set must not grow as an implementation detail.** A new production `HttpListener`,
`TcpListener` accept loop, bound/listening `Socket`, additional Kestrel/WebApplication host, or
server in another runtime is a STOP finding, including when it binds only to loopback or uses a
random capability URL. It must be rejected unless the owner explicitly approves a topology
change and this document is amended with the threat model, authentication, exposure, lifecycle,
and necessity **before** the listener implementation is accepted.

### Mandatory listener-discovery pass

Every re-audit of this file must perform both route enumeration and a repository-wide listener
search. Do not infer “all APIs” from `Map*` calls, and do not ignore untracked files. At minimum,
run these searches from the repository root and inspect every result:

```powershell
rg -n -i --hidden -g '!.git/**' -g '!**/bin/**' -g '!**/obj/**' -g '*.cs' -g '*.fs' -g '*.vb' 'HttpListener|TcpListener|UdpClient|new\s+Socket|\.Bind\s*\(|\.Listen\s*\(|Listen(?:Localhost|AnyIP|UnixSocket)|GetContextAsync|Accept(?:TcpClient|Socket|Async)|ConfigureKestrel|UseUrls|WebApplication\.Create'
rg -n -i --hidden -g '!.git/**' -g '!**/node_modules/**' -g '!**/assets/**' -g '*.js' -g '*.mjs' -g '*.cjs' -g '*.ts' -g '*.py' -g '*.ps1' -g '*.psm1' -g '*.sh' -g '*.go' -g '*.rs' -g '*.java' -g '*.rb' 'HttpListener|TcpListener|(?:http|https|http2|net)\.createServer|HTTPServer|ThreadingHTTPServer|TCPServer|serve_forever|\.listen\s*\(|ListenAndServe|net\.Listen|TcpListener::bind|axum::serve|ServerSocket|HttpServer\.create|WEBrick'
```

Classify test servers, outbound clients, port probes, and real request acceptors separately.
Any production acceptor outside the closed set above invalidates the route/authentication audit
until it is removed or explicitly approved. Record the listener result whenever the route count
or audit date is updated.

### Rejected listener incident — Grok loopback bridge (2026-08-15)

An uncommitted Grok integration added `GrokLoopbackBridge`, which constructed a separate
`HttpListener` on `127.0.0.1` ports 6000–6999. Its inbound leg was outside the main ASP.NET
pipeline and used a random path capability instead of the normal middleware credentials. The
bridge later attached the process credentials when forwarding inference to `/llm/grok`, and it
included meaningful mitigations (loopback-only binding, a 32-byte random capability, pinned
destinations, and header stripping), but those mitigations did not change the architectural
fact that VibeRails was operating a second HTTP server outside its established auth boundary.

Disposition: **rejected and removed from the working tree; do not reintroduce, merge, or ship
the listener.**
The rejected `/llm/grok` route and listener are deliberately not added to the approved surface
counts in this document.

The accompanying auth-gate audit did search for listener APIs, listed the Grok listener as an
expected result, and then concluded there was no bypass. That exposed the process failure:
discovery alone is insufficient if the same feature change is allowed to expand its own expected
set. The production listener set is now frozen above so a new match starts as a finding, not as
an expectation.

### Repository-wide listener result — 2026-09-30

- Approved serving implementation: the main Kestrel host in `VibeRails/Program.cs`.
- Rejected and removed before merge: `GrokLoopbackBridge`'s `HttpListener`.
- Non-serving production match: `PortFinder`'s transient loopback `TcpListener` port probe.
- Test-only matches: isolated Kestrel hosts under `Tests/**`.
- Removed 2026-09-13: `VibeRails.Daemon`'s current-user `NamedPipeServerStream` control pipe was
  deleted along with the VibeRails Demon feature. It was never an HTTP listener, so its removal
  does not change the frozen listener set.
- No other production .NET accept loop and no JavaScript/TypeScript, Python, or PowerShell
  server/listener implementation was found. The cross-runtime search had no matches.

## Terminology used in this report

The code does not have two independent credentials named “auth Cookie” and
“SessionToken.” The normal authentication pair is:

1. **Session credential** — `viberails_session`. Browser requests normally send it as
   the HttpOnly auth cookie. Clients that cannot use the cookie may send the same value
   in a `viberails_session` header (or as a WebSocket subprotocol). The cookie and header
   are alternatives carrying the same secret; they are not two factors.
2. **Per-tab credential** — `viberails_tab`. The bootstrap page stores it in
   `sessionStorage`; normal HTTP APIs send it in a header and WebSockets send it as a
   subprotocol.

In the lists below, **both** means a valid `viberails_session` credential **and** a valid
`viberails_tab` credential. The session token is `viberails_session`, whether sent in
a cookie or header; `viberails_tab` is a separate *tab token*. No endpoint requires
both the cookie and a duplicate session header.

`viberails_terminal_session` is not a credential. LLM-proxy requests may carry it as a
correlation header (the terminal `Sessions.Id` behind the exchange log's `SessionId`
column); the auth gate does not validate it, and the relay strips it — like the two real
credentials — before the upstream provider hop. Spoofing it can only mislabel the
spoofer's own local exchange rows.

Authentication is enforced primarily by
[`CookieAuthMiddleware`](VibeRails/Middleware/CookieAuthMiddleware.cs). The LLM proxy
routes additionally use
[`ILlmProxyAuthGate`](TokenSaver/ILlmProxyAuthGate.cs). There are 246 mapped route
surfaces in this inventory: 234 `/api/v1` method/path mappings, nine non-`/api` protected
API surfaces, and three bootstrap/page/probe routes. Static-file middleware and the
global `OPTIONS` behavior are noted separately because they are not finite mapped-route
lists.

## 1. No protection

- `GET /health` — deliberately unauthenticated readiness probe. It returns `200 OK` and
  no application data.
- `OPTIONS *` — `CookieAuthMiddleware` skips every CORS preflight request, regardless of
  path. These requests do not execute the verb-specific business handlers, but the auth
  layer itself does not protect them. Method-unrestricted `Map` routes (the proxy and
  WebSocket surfaces) can receive non-preflight `OPTIONS`; enabled proxies still require
  both headers in their relay gate, and WebSocket handlers reject non-upgrade requests.

### Neither normal credential, but protected another way

- `GET /auth/bootstrap?code={one-time-code}&redirect={local-path}` — bypasses session and
  tab authentication because it creates those credentials. It is protected by a
  single-use, expiring bootstrap code and returns `403` for an absent or invalid code.
  This is therefore **not** an unprotected endpoint, even though it uses neither normal
  credential.

### The complete middleware skip list (FROZEN — review gate)

`CookieAuthMiddleware.InvokeAsync` bypasses authentication for exactly three cases, all
already listed above:

1. `GET /auth/bootstrap` (exact path match, case-insensitive) — protected by the one-time
   bootstrap code.
2. `GET /health` (exact path match, case-insensitive) — bare readiness probe.
3. Every `OPTIONS` request — CORS preflights.

**This list must not grow.** Any PR that adds a path or predicate to the skip condition
in `CookieAuthMiddleware.InvokeAsync` is a security regression and must be rejected
unless this document is amended with an explicit justification in the same PR. In
particular, the `/llm/**` proxy trees were removed from this list on 2026-08-05 and must
not return: the CLIs authenticate with the same session/tab secrets as every other
caller, sent as headers, so nothing about the proxy requires a bypass.
`Tests/Middleware/CookieAuthMiddlewareTests.cs` pins this invariant executable-y; the
list here is the reviewable statement of intent.

### Resolved findings

- **Conditional unauthenticated responses on proxy routes** (reported 2026-08-01,
  resolved 2026-08-05). `ANY /llm/openai/{**rest}` and `ANY /llm/zai/{**rest}` formerly
  bypassed `CookieAuthMiddleware` and relied only on the proxy's in-handler two-header
  gate; because each handler checked its feature flag before that gate, an
  unauthenticated caller could distinguish `404` (feature disabled) from `401` (feature
  enabled). No proxy action or upstream data was ever reachable on that branch — the
  exposure was the one-bit feature oracle. Resolved by removing both trees from the skip
  list: an unauthenticated caller now receives `401` from the middleware regardless of
  feature state, and the feature-flag distinction is visible only to callers already
  holding valid credentials. Both routes now appear in §§ 2–3 alongside
  `/llm/anthropic`.

## 2. Exactly one credential, not both

### Mapped route

- `GET /git-guard` — requires the `viberails_session` credential, but page loads are
  intentionally exempt from the `viberails_tab` check.

There are **no mapped business `/api/v1` endpoints** in this category.

### Static browser surface (not individual API mappings)

- `GET`/`HEAD /`, `/index.html`, and existing files under `VibeRails/wwwroot/**` — require
  the `viberails_session` credential only. Default/static-file middleware runs after
  `CookieAuthMiddleware`, while the tab-token rule intentionally excludes ordinary page
  and asset loads.

### Conditional one-credential responses on the LLM proxies

- `ANY /llm/anthropic/{**rest}`
- `ANY /llm/openai/{**rest}` (on the middleware since 2026-08-05)
- `ANY /llm/zai/{**rest}` — only for nonstandard catch-all paths that do not contain
  `/api/`; normal OpenCode requests use `/llm/zai/api/paas/v4/...` and therefore receive
  both middleware checks.
- `ANY /llm/xai/{**rest}` — same shape as Claude/Codex: real OpenCode requests use
  `/llm/xai/v1/chat/completions`, which contains no `/api/` segment, so the middleware
  enforces the session credential only. The in-handler proxy gate then requires both.
- `ANY /llm/cli-chat/{**rest}` — same shape: native Grok uses
  `/llm/cli-chat/v1/chat/completions` (no `/api/`), so the middleware enforces the
  session credential only. The in-handler proxy gate then requires both.

No `/llm` path is on the middleware skip list, so a valid `viberails_session` credential
(sent as a header by the CLIs) is always required before the handler runs. The
middleware's tab rule keys off `/api/` appearing in the path, which no real Claude or
Codex request path contains — so for those two trees the middleware enforces the session
credential only. Normal Z.AI requests do contain `/api/`, but its catch-all mapping also
accepts nonstandard paths that do not. Each handler checks its feature flag before the
proxy gate checks both headers: on a path where the middleware has required only the
session credential, a caller holding only that credential sees `404` when the feature is
disabled and `401` when it is enabled. When enabled, the relay requires both credentials
as listed in section 3. This conditional distinction does not create a public endpoint:
every `/llm/**` request must first present a valid session credential.

Auth failures anywhere under `/llm/**` return plain-text status codes, never the HTML
auth page — every caller there is a CLI HTTP client or an MCP tool.

## 3. Both credentials

Unless a proxy-specific note says otherwise, every route in this section is protected by
`CookieAuthMiddleware` before its handler runs. Ordinary HTTP calls use the
`viberails_session` cookie (or same-named header) plus the `viberails_tab` header.
WebSocket handshakes use the session cookie/subprotocol plus the tab-token subprotocol.

### Non-`/api/v1` API surfaces (9)

- `MCP /mcp` — the Streamable HTTP MCP endpoint. The middleware also protects `/mcp/**`.
- `ANY /llm/openai/{**rest}` — `CookieAuthMiddleware` checks the session credential
  (skip-list removal, 2026-08-05); when enabled, the proxy's own gate then requires both
  `viberails_session` and `viberails_tab` as headers.
- `ANY /llm/anthropic/{**rest}` — same shape: the middleware checks the session
  credential, and when enabled the proxy gate requires both headers.
- `ANY /llm/zai/{**rest}` — the middleware checks the session credential, and because
  every normal OpenCode request path contains `/api/` (`/llm/zai/api/paas/v4/...`), the
  middleware's tab rule applies too — both credentials are enforced before the handler
  runs. When enabled, the proxy gate re-checks both; the nonstandard catch-all-path nuance
  is documented in section 2.
- `ANY /llm/xai/{**rest}` — same shape as `/llm/anthropic` and `/llm/openai`: the
  middleware checks the session credential (the real path is `/llm/xai/v1/...`, no
  `/api/`); when enabled, the proxy gate requires both headers. This is a main-host
  Kestrel mapping, not the rejected `GrokLoopbackBridge` / `/llm/grok` sidecar.
- `ANY /llm/cli-chat/{**rest}` — same shape for native Grok (`/llm/cli-chat/v1/...`).
  Grok's `Authorization` / `X-XAI-Token-Auth` are forwarded; VibeRails session/tab
  headers are stripped. Not `/llm/grok` and not a second listener.
- `POST /llm/control/token-saver/pause` — both headers are required by the control
  handler's proxy auth gate.
- `POST /llm/control/token-saver/resume` — both headers are required by the control
  handler's proxy auth gate.
- `GET /llm/control/token-saver/status` — both headers are required by the control
  handler's proxy auth gate.

### Agent tools (7)

- `GET /api/v1/agent-tools/terminal`
- `POST /api/v1/agent-tools/terminal/open`
- `POST /api/v1/agent-tools/terminal/input`
- `POST /api/v1/agent-tools/terminal/{tabId}/input`
- `POST /api/v1/agent-tools/terminal/snapshot`
- `GET /api/v1/agent-tools/terminal/{tabId}/snapshot`
- `WS /api/v1/agent-tools/ws`

### Rule files and rules (12)

- `GET /api/v1/agents`
- `POST /api/v1/agents`
- `PUT /api/v1/agents/name`
- `GET /api/v1/agents/rules`
- `POST /api/v1/agents/rules`
- `DELETE /api/v1/agents/rules`
- `PUT /api/v1/agents/rules/enforcement`
- `GET /api/v1/agents/content`
- `GET /api/v1/agents/files`
- `POST /api/v1/agents/validate`
- `GET /api/v1/rules`
- `GET /api/v1/rules/details`

### Application settings, PIN, export, push, and HTTP relay (21)

- `GET /api/v1/settings`
- `POST /api/v1/settings`
- `GET /api/v1/settings/db-size`
- `POST /api/v1/settings/computer-name`
- `POST /api/v1/settings/remote-link` — start account approval; active root backend only.
- `GET /api/v1/settings/remote-link` — poll approval and save the key; active root backend only.
- `DELETE /api/v1/settings/remote-link` — cancel approval; active root backend only.
- `GET /api/v1/settings/pin/status`
- `POST /api/v1/settings/pin`
- `DELETE /api/v1/settings/pin`
- `POST /api/v1/settings/export-data` — mapped only by an active root-backend process.
- `GET /api/v1/settings/export-data/progress` — mapped only by an active root-backend
  process.
- `POST /api/v1/push/send`
- `GET /api/v1/update/check`
- `GET /api/v1/update/version`
- `GET /api/v1/version`
- `GET /api/v1/http-relay/test/posts`
- `GET /api/v1/http-relay/test/posts/{id:int}`
- `POST /api/v1/http-relay/test/posts`
- `PUT /api/v1/http-relay/test/posts/{id:int}`
- `DELETE /api/v1/http-relay/test/posts/{id:int}`

### Signing keys (5; active root backend only)

- `GET /api/v1/settings/keys` — public metadata only, never the encrypted private-key file;
  also lists, by file name only, any stray or damaged file skipped in the key folder.
- `POST /api/v1/settings/keys` — RSA-4096 generation; a nonblank 8–128 character password
  that is not digits only is mandatory (password entropy is the only protection for a copied
  key file or backup, so a numeric PIN is refused; the dashboard sends NFC). The private key
  is persisted only as encrypted PKCS#8 using AES-256-CBC and PBKDF2-SHA256 (600,000
  iterations), in the user's private signing-key directory.
- `POST /api/v1/settings/keys/{id:guid}/sync` — password required to answer an
  API-key-authenticated, domain-separated proof-of-possession challenge before registering
  the public key at the fixed HTTPS viberails.ai destination. Redirects are disabled.
- `POST /api/v1/settings/keys/{id:guid}/export` — password required; encrypted PEM only.
- `POST /api/v1/settings/keys/{id:guid}/sign` — password required; RSA-PSS/SHA256 signs
  the exact decoded bytes of a bounded base64 payload (64 KiB maximum) and returns the
  public key and fingerprint alongside the signature; payloads carrying the registration
  challenge prefix are refused.

All five routes require the existing session and tab credentials. Responses are no-store,
request bodies are capped at 96 KiB, failed unlocks are throttled in each backend process,
and file operations serialize across dashboard processes. No password, private key,
or ordinary signing payload is sent to the cloud by these routes: registration sends public
PEM, name, challenge ID, and proof signature, with the saved API key in `X-Api-Key` over
fixed-destination HTTPS. Explicit verification calls made by clients
to the separately hosted public cloud endpoint necessarily send their payload and signature.
Successful public verification discloses the signer's verified account email, explicitly
requested by the owner; the desktop KEYS panel describes that disclosure before creation.
No local endpoint is anonymous and no production listener was added.

### Automation navigation preferences (3)

- `GET /api/v1/automation-nav/preferences`
- `PUT /api/v1/automation-nav/preferences`
- `DELETE /api/v1/automation-nav/preferences`

### BERT and unified search (7)

- `GET /api/v1/bert/status`
- `GET /api/v1/bert/captures`
- `GET /api/v1/bert/session-captures`
- `GET /api/v1/bert/captures/by-session/{sessionId}`
- `GET /api/v1/bert/captures/{documentId}`
- `POST /api/v1/bert/search`
- `POST /api/v1/search`

### Chat history and sessions (13)

- `GET /api/v1/chatHistory`
- `GET /api/v1/chatHistory/{sessionId}`
- `PATCH /api/v1/chatHistory/{sessionId}`
- `DELETE /api/v1/chatHistory/{sessionId}`
- `GET /api/v1/chatHistory/{sessionId}/transcript`
- `GET /api/v1/chatHistory/{sessionId}/raw-session`
- `GET /api/v1/chatHistory/{sessionId}/replay`
- `GET /api/v1/chatHistory/{sessionId}/terminal-replay`
- `GET /api/v1/chatHistory/{sessionId}/summary`
- `GET /api/v1/sessions/{sessionId}/logs`
- `GET /api/v1/sessions/recent`
- `GET /api/v1/sessions/{sessionId}/inputs`
- `GET /api/v1/sessions/{sessionId}/output`

### Session replay (7; active root backend only)

All seven read-only routes require session and tab credentials and return no-store responses.
Detail reads are scoped to the requested session ID.

- `GET /api/v1/session-replay/status`
- `GET /api/v1/session-replay/sessions`
- `GET /api/v1/session-replay/sessions/{id}`
- `GET /api/v1/session-replay/sessions/{id}/frames`
- `GET /api/v1/session-replay/sessions/{id}/exchanges`
- `GET /api/v1/session-replay/sessions/{id}/changes/{changeId:long}`
- `GET /api/v1/session-replay/sessions/{id}/exchanges/{exchangeId}`

### CLI, environment, and LLM-picker management (16)

- `GET /api/v1/environments`
- `POST /api/v1/environments`
- `GET /api/v1/environments/{name}`
- `PUT /api/v1/environments/{name}`
- `DELETE /api/v1/environments/{name}`
- `GET /api/v1/environments/{name}/launch`
- `POST /api/v1/environments/steps/test`
- `POST /api/v1/cli/launch/{cli}`
- `POST /api/v1/cli/launch/vscode`
- `GET /api/v1/codex/settings/{envName}`
- `PUT /api/v1/codex/settings/{envName}`
- `GET /api/v1/claude/settings/{envName}`
- `PUT /api/v1/claude/settings/{envName}`
- `GET /api/v1/llm-picker/preferences`
- `PUT /api/v1/llm-picker/preferences`
- `DELETE /api/v1/llm-picker/preferences`

### Compression and token savings (3)

- `GET /api/v1/compression/catalog`
- `POST /api/v1/compression/preview`
- `GET /api/v1/token-savings`

### Git, hooks, and code analyzer (18)

- `POST /api/v1/git/init`
- `POST /api/v1/git/open-directory`
- `GET /api/v1/hooks/status`
- `POST /api/v1/hooks/install`
- `DELETE /api/v1/hooks`
- `POST /api/v1/hooks/preview`
- `POST /api/v1/hooks/validate`
- `POST /api/v1/git/preflight/stream`
- `POST /api/v1/git/preflight/console`
- `POST /api/v1/code-analyzer`
- `POST /api/v1/code-analyzer/graph` — read-only, active root backend only; bounded repository structure and lexical references.
- `GET /api/v1/code-analyzer/changes` — read-only, active root backend only; the working tree's changes against HEAD with statuses and line counts, no contents.
- `GET /api/v1/code-analyzer/changes/diff` — read-only, active root backend only; one safe repository-relative path's bounded HEAD and working-tree text.
- `GET /api/v1/code-analyzer/source`
- `GET /api/v1/code-analyzer/ignores`
- `POST /api/v1/code-analyzer/ignores`
- `POST /api/v1/code-analyzer/ignores/bulk`
- `DELETE /api/v1/code-analyzer/ignores`

### Jobs (15)

- `GET /api/v1/jobs/catalog` — mapped only by an active root-backend process.
- `POST /api/v1/jobs/import` — mapped only by an active root-backend process.
- `GET /api/v1/jobs`
- `POST /api/v1/jobs`
- `GET /api/v1/jobs/{id:long}`
- `PUT /api/v1/jobs/{id:long}`
- `DELETE /api/v1/jobs/{id:long}`
- `POST /api/v1/jobs/{id:long}/run`
- `GET /api/v1/jobs/runs`
- `GET /api/v1/jobs/runs/summary`
- `POST /api/v1/jobs/runs/delete`
- `GET /api/v1/jobs/runs/{runId}`
- `DELETE /api/v1/jobs/runs/{runId}`
- `POST /api/v1/jobs/runs/{runId}/cancel`
- `POST /api/v1/jobs/runs/{runId}/retry`

### VibeRails Demon lifecycle — REMOVED 2026-09-13

The seven `/api/v1/jobs/demon` surfaces were deleted with the VibeRails Demon feature and no longer
exist in the tree. The current counts and inventory above exclude them. Nothing else in this
inventory changed — the removal deleted routes, it did not alter any authentication rule.

### Lifecycle and app events (4)

- `POST /api/v1/lifecycle/ping`
- `POST /api/v1/lifecycle/disconnect`
- `POST /api/v1/shutdown`
- `WS /api/v1/events/ws`

### MCP Explorer REST API (4)

- `GET /api/v1/mcp/status`
- `GET /api/v1/mcp/tools`
- `POST /api/v1/mcp/inspect`
- `POST /api/v1/mcp/tools/{name}`

### Project metadata (3)

- `GET /api/v1/context`
- `GET /api/v1/projects/name`
- `PUT /api/v1/projects/name`

### Python scripts (13)

- `GET /api/v1/python-scripts`
- `POST /api/v1/python-scripts/pin`
- `POST /api/v1/python-scripts/approve`
- `POST /api/v1/python-scripts/revoke`
- `POST /api/v1/python-scripts/run`
- `POST /api/v1/python-scripts/run/interactive` — mapped only by an active root-backend process.
- `GET /api/v1/python-scripts/runs`
- `GET /api/v1/python-scripts/content`
- `POST /api/v1/python-scripts/content`
- `POST /api/v1/python-scripts/create`
- `POST /api/v1/python-scripts/rename`
- `DELETE /api/v1/python-scripts`
- `POST /api/v1/python-scripts/import` — mapped only by an active root-backend process.

### Internal diagnostics tools (2; active root backend only)

- `GET /api/v1/internal/logs` — read-only. `source=features` (the default) pages the
  redacted feature journal; `source=application` and `source=daemon` return bounded tails of
  the existing Serilog `vb-*.log` / `vbd-*.log` files under the install directory's `logs`
  folder. `source` is a fixed whitelist: no caller-supplied value ever reaches the filesystem,
  and both the directory and each file are refused when they are symlinks, junctions, or other
  reparse points.
- `GET /api/v1/internal/uploads` — read-only; the feature journal grouped to the latest event
  per upload operation.

Accepted exposure (decided 2026-09-06): the application and daemon sources serve Serilog
lines verbatim. Whatever the process logged, including full exception messages, becomes
readable by any holder of both credentials. This is accepted because those files already
belong to the same OS user under private permissions, the surface is GET-only and mapped only
by the active root backend, and the feature journal remains the redacted channel: the
`IFeatureLog` contract forbids secrets and payload bodies there, but nothing redacts Serilog
output. Code that logs through `ILogger`/Serilog must therefore keep API keys, tokens, and
transcript text out of messages and exception text; do not rely on the Logs viewer being
hidden. `Tests/Routes/InternalToolsRoutesTests.cs` pins the two-credential requirement, the
whitelist rejection of path-like sources, and the absence of mutating verbs.

### Kanban board (63; active root backend only)

- `GET /api/v1/board/cards/{card}/reviews` — paged review attempts, run status and latest saved report.
- `GET /api/v1/board/cards/{card}/reviews/{reviewId}` — canonical report; `verify=true` compares the current checkout.

- `GET /api/v1/board/cards/{card}/checks` — paged saved check summaries and current check runs.
- `GET /api/v1/board/cards/{card}/checks/{checkId}` — full saved evidence and qualified freshness.

All mapped by `BoardRoutes.Map` under `if (isActiveRootBackend)`; every path contains `/api/`,
so both credentials are enforced by the middleware with no route-level registration. The
project is always `ParserConfigs.GetRootPath()` — never a value from the request — so a caller
cannot read or write another project's board through this surface.

- `GET /api/v1/board/shared` — discover shared remote boards.
- `POST /api/v1/board/shared/{remoteId}/import` — import into the server-derived project.
- `GET /api/v1/board/boards/{boardId}/sharing`,
  `POST /api/v1/board/boards/{boardId}/sharing`,
  `PUT /api/v1/board/boards/{boardId}/sharing/{inviteId}`,
  `DELETE /api/v1/board/boards/{boardId}/sharing/{inviteId}` — scoped collaborator management.
  All six sharing routes require session and tab credentials and return no-store responses;
  local board operations check project/board scope before making remote calls.
- `POST /api/v1/board/cards/{card}/merge` — merge cards within the server-derived project.
- `DELETE /api/v1/board/cards/{card}/comments/{commentId}` — remove a user-authored comment
  belonging to the live card. Merge and deletion require session and tab credentials.
- `GET /api/v1/board/boards`, `POST /api/v1/board/boards`, `PUT /api/v1/board/boards/{boardId}`,
  `DELETE /api/v1/board/boards/{boardId}` — boards (2026-09-18). A project holds one or more
  boards (sprints, sub-projects); every lane belongs to one and a card belongs to a board through
  its lane. Card keys stay per project. Deleting a board deletes its lanes and cards; the last
  board is refused (409). `GET …/columns` and `GET …/cards` take `?boardId=`, and the create-lane,
  reorder and create-card bodies take `boardId`; omitted, the project's first board is meant, so
  every pre-board client reads exactly what it did before. A board id is looked up within the
  current project only — an id from another project is a 400, never a cross-project read.
- `GET /api/v1/board/boards/{boardId}/context`,
  `PUT /api/v1/board/boards/{boardId}/context` — read/save Board context settings.
  Both require session and tab credentials and use the server-derived project.
- `GET /api/v1/board/boards/{boardId}/jira`, `PUT /api/v1/board/boards/{boardId}/jira`,
  `POST /api/v1/board/boards/{boardId}/jira/test`,
  `POST /api/v1/board/boards/{boardId}/jira/pull` — one Jira Cloud connection per board
  (VB-40, 2026-09-25). The PUT accepts the API token and never echoes it; GET and the pull
  report say only whether a token is saved. Pull writes cards on this board. `?dryRun=true`
  counts creates and updates without writing. Outbound only; no new listener.
- `GET /api/v1/board/columns/{columnId}/automation`,
  `PUT /api/v1/board/columns/{columnId}/automation` — read/save lane automation settings.
  `GET /api/v1/board/columns/{columnId}/automation/running` (VIBE-35) returns only that lane's
  running agents for the panel's poll. All require session and tab credentials and use the
  server-derived project; a lane outside it is a 404.
- `GET /api/v1/board/boards/{boardId}/history` — Board history scoped to the server-derived
  project and board, optionally filtered by `card`. Returns up to 100 entries, a next offset,
  and a has-more indicator; `offset` must be between 0 and 1,000,000. Missing or foreign
  boards/cards return 404. Requires both credentials.
- `GET /api/v1/board/boards/{boardId}/sync`, `PUT /api/v1/board/boards/{boardId}/sync`,
  `POST /api/v1/board/boards/{boardId}/sync/now` — read sync status, set publication with
  `{ enabled }`, or run a manual sync. All require both credentials and resolve the board
  within the server-derived project. Sync makes outbound requests to viberails.ai using
  the saved API key; these local responses do not include that key. No additional listener.
- `GET /api/v1/board/cards/{card}/automations`,
  `POST /api/v1/board/cards/{card}/automations` — list project Automation choices and recent
  card-originated runs, or queue an enabled Automation with the originating card retained.
  The POST body is `{ jobId }`; card and job both resolve within the server-derived project.
- `POST /api/v1/board/cards/{card}/automations/skip` — skip one pending lane entry by
  `{ jobId, eventKey }`, with both credentials and server-derived project/card resolution.
  Stale entries conflict; a committed run keeps its lifecycle. A Comments receipt records the request.
- `GET /api/v1/board/cards/{card}/context` — read-only estimate of the launch prompt and
  initial Board tool reads, in characters and estimated tokens, plus the latest recorded
  launch sample. Requires both credentials and resolves the card within the server-derived
  project; asking for the estimate stores nothing.
- `GET /api/v1/board/columns`, `POST /api/v1/board/columns`, `PUT /api/v1/board/columns/order`,
  `PUT /api/v1/board/columns/{columnId}`, `DELETE /api/v1/board/columns/{columnId}` — lanes.
- `POST /api/v1/board/cards/activity` — read-only live status for up to 100 explicit card IDs
  on the supplied board, scoped to the server's project; no card text or historical rails.
- `GET /api/v1/board/cards`, `POST /api/v1/board/cards`, `GET /api/v1/board/cards/{card}`,
  `PUT /api/v1/board/cards/{card}`, `DELETE /api/v1/board/cards/{card}`,
  `POST /api/v1/board/cards/{card}/move` — cards (`{card}` is an id or a `VB-n` key).
  The list optionally takes `pageSize=1..100`, `columnId`, `offset`, `continuationToken` and
  `q`/`assignee`/`type`/`priority`/`tag`. Initial paged reads include all open cards and
  one page per completed lane; a scoped `columnId` reads one lane page. Counts and
  filter choices cover the selected board, and filters run before the page limit. A stale
  continuation restarts at offset zero and marks `restartRequired`.
- `GET /api/v1/board/cards/link-candidates?q=` (draft link search),
  `GET /api/v1/board/cards/{card}/links/candidates?q=`,
  `POST /api/v1/board/cards/{card}/links`,
  `DELETE /api/v1/board/cards/{card}/links/{linkedCard}` — related cards. The POST body supplies
  `card` as an id or key. Both endpoints of a link must exist in the open project; foreign ids
  return 404. Reads include links in the ordinary full card response. Link rows cascade when
  either card is deleted, and linking does not launch a terminal or change a description.
- `GET /api/v1/board/files?q=` — repository file names for the composer's `@path` typeahead
  (VB-35). Root backend only, like every Board route; `q` is capped at 256 characters and the
  answer at 50 repo-relative paths under the dashboard's root path. Names only, never contents,
  nothing written.
- `POST /api/v1/board/cards/{card}/launch` — "Start work": creates a terminal tab through the
  in-process tab host and starts the assigned LLM with the card prepended to the environment's
  Initial Message. Same capability class as `POST /api/v1/terminal/tabs/{tabId}/start`, which
  is why the tab credential matters here. The environment is resolved by id and must be
  visible in the current project; there is no fallback by name. Base-CLI cards may persist an
  explicit, default-off YOLO choice that adds the provider's global bypass/auto-approve launch
  flag. Saved environments keep their own launch arguments.
**Terminal input reachable from the board.** The description-notification route
(`POST /api/v1/board/cards/{card}/revisions/{revision:int}/notify`) was **removed 2026-09-15**:
it forwarded two semantic Escapes, fixed text and Enter to an agent that was already working, and
on an idle Claude Code prompt a double Escape opens the rewind menu rather than clearing a draft —
so the text and its Enter were delivered into that menu, where they could restore a checkpoint. It
had never been exercised against a live CLI.

**No board route sends terminal input**, and as of 2026-09-15 no route anywhere types into a
running TUI. The Codex plan-mode handshake that did — reached from this same launch route, and
briefly documented here as a narrower exception — was removed alongside the notification route:
it sent `/plan` + Enter and then the card text as a bracketed paste + Enter, deciding when to type
by screen-scraping the TUI for readiness. Every launch option is now a command-line argument fixed
before the process starts; session options are set at spawn time or not at all. Any new "message
the agent" surface is a fresh capability decision and must be validated against each provider's
real TUI before it ships.
- `POST /api/v1/board/cards/{card}/comments`,
  `GET /api/v1/board/cards/{card}/notes`, `POST /api/v1/board/cards/{card}/notes`,
  `POST /api/v1/board/cards/{card}/attachments`,
  `DELETE /api/v1/board/cards/{card}/attachments/{attachmentId}` — comments, agent notes and file
  attachments. Notes (added 2026-09-17) are the same row shape as comments with
  `BoardComments.Kind = 'note'`; same 50,000-character cap, same `textContent`-only rendering,
  same both-credentials requirement, and never merged into the comment stream.
  Upload bytes are base64-decoded and counted by the server; supplied MIME/byte counts are
  untrusted. There is deliberately **no upload size limit** — only 40 current files per card.
  Kestrel's body limit is lifted for this one path in middleware, which is the only place it can
  be lifted: a `RequestSizeLimitAttribute` on a minimal-API endpoint is inert (only the MVC filter
  pipeline reads it), so the previously documented 29 MB cap never applied and Kestrel's 30 MB
  default silently governed instead. This is a local single-user surface behind both credentials;
  the bound on what it can store is the user's own disk. File names are display labels, never
  filesystem paths. Bytes remain immutable in SQLite, outside static files.
- `GET /api/v1/board/cards/{card}/attachments/{attachmentId}/content` — authenticated bytes
  scoped to that card/project and current attachments only; removed files return 404.
  Responses force octet-stream attachment disposition, nosniff, no-store and sandbox CSP.
  Preview fetches carry both credentials; URLs contain no secrets. Markdown and TXT both reach
  the DOM only through textContent — no Markdown renderer or HTML sanitizer is shipped — and
  PDFs paint canvases without active document layers.
- `GET /api/v1/board/cards/{card}/commits`, `POST /api/v1/board/cards/{card}/commits`,
  `DELETE /api/v1/board/cards/{card}/commits/{sha}`,
  `GET /api/v1/board/cards/{card}/commits/{sha}/diff` —
  linked commits. `git` runs only with an argument list and a regex-validated hex sha (never a
  shell string, never a ref expression) inside the project directory.
- `GET /api/v1/board/cards/{card}/sessions`, `POST /api/v1/board/cards/{card}/sessions`,
  `PUT /api/v1/board/cards/{card}/sessions/{sessionId}`,
  `DELETE /api/v1/board/cards/{card}/sessions/{sessionId}` — the card ↔ terminal-session links.

MCP note (VIBE-28): board discovery shows all local boards, current-project boards first and
other projects labeled with their stored paths. Explicit board IDs/unambiguous names and full
random permanent card keys/row IDs resolve the owning project through `IBoardStore`; short keys
and display IDs retain current-project scope, including imported legacy short keys. Operations
then use existing scoped service/store methods. REST still uses the dashboard project. This is
the local user's Board capability; it introduces no per-project authorization claim.
Reads never link the calling session. Writes in the current project auto-link an entirely
unlinked session; writes to another project preserve the caller's defaults and session links.
`attach_board_session` explicitly adds another card within the
same project. The same board operations (minus any delete or terminal input) are exposed as `*_board_*` tools on
`/mcp` (both credentials) and on the stdio `vb mcp` host. The stdio host reads and writes
`board.db` through `IBoardStore` rather than calling this API, so a CLI in any terminal can work a card
without a VibeRails tab. `read_board_attachment` returns bounded UTF-8 Markdown/TXT text or
byte-sniffed PNG/JPEG/GIF/WebP MCP image content up to 5 MiB using current card/project-scoped IDs,
never paths or direct SQL arguments. Removed attachments are unavailable. The host is a child process of the CLI over pipes and remains
unauthenticated by design. `Tests/Routes/BoardRoutesTests.cs` pins the two-credential
requirement and the launch composition; `Tests/Services/Mcp/BoardToolTests.cs` and its discovery
partial pin the tools. Scoped inspection of Board route mappings and both listener searches
found only the approved main Kestrel host, the non-serving PortFinder probe and test hosts;
the cross-runtime search had no matches. No transport, route, authentication exemption or
provider grant changes for VIBE-28.

### Local filesystem browser (1)

- `GET /api/v1/filesystem/entries` — mapped only by an active root-backend process and
  returns one directory level of metadata; it never returns file contents. Listings use
  bounded cursor pages with literal server-side name search. Requests reject relative,
  UNC/device, mapped/unknown Windows-drive, and any symlink/junction/reparse-component path;
  linked rows are metadata-only and the picker will not open or return them. Ordinary Unix
  network mounts cannot be classified portably, and downstream filesystem operations must
  independently revalidate a selected path before use.

### Sandboxes (9)

- `GET /api/v1/sandboxes`
- `POST /api/v1/sandboxes`
- `DELETE /api/v1/sandboxes/{id:int}`
- `POST /api/v1/sandboxes/{id:int}/launch/shell`
- `POST /api/v1/sandboxes/{id:int}/launch/vscode`
- `POST /api/v1/sandboxes/{id:int}/launch/{cli}`
- `GET /api/v1/sandboxes/{id:int}/diff`
- `POST /api/v1/sandboxes/{id:int}/push`
- `POST /api/v1/sandboxes/{id:int}/merge`

### Terminal and terminal tabs (14)

VB-60 retains an owned terminal's final screen and up to 20,000 scrollback lines in its host
after the PTY exits. The existing snapshot routes (including the per-tab Agent tools route)
return that read-only snapshot while inactive; a new session clears it. Live snapshots retain
their screen-only behavior. No input route accepts completed sessions, and no route, grant,
authentication exception or listener was added. Retained output ends when its host is dismissed,
reclaimed at capacity or shut down; normal recordings remain available in History.

Scoped route enumeration and both required repository-wide listener searches found only the
existing main Kestrel host, non-serving PortFinder probe and test-only hosts; the cross-runtime
search had no matches. Session and tab credentials still gate snapshot requests. No violation
was found. This is a scoped behavior amendment, not a full API re-audit.

- `GET /api/v1/terminal/status`
- `POST /api/v1/terminal/start`
- `POST /api/v1/terminal/stop`
- `POST /api/v1/terminal/input`
- `GET /api/v1/terminal/snapshot`
- `GET /api/v1/terminal/bootstrap-command`
- `WS /api/v1/terminal/ws`
- `GET /api/v1/terminal/tabs`
- `POST /api/v1/terminal/tabs`
- `DELETE /api/v1/terminal/tabs/{tabId}`
- `GET /api/v1/terminal/tabs/{tabId}/status`
- `POST /api/v1/terminal/tabs/{tabId}/start`
- `POST /api/v1/terminal/tabs/{tabId}/stop`
- `WS /api/v1/terminal/tabs/{tabId}/ws`

## Audit observations

- Inventory amendment on 2026-09-17: the three compression-capture routes
  (`GET /api/v1/compression/captures`, `GET /api/v1/compression/captures/{id:guid}`,
  `DELETE /api/v1/compression/captures`) were removed with the retired `CompressionCaptures`
  writer (`CompressionCaptureRoutes` → `CompressionRoutes`; the `{captureId}` form of
  `POST /api/v1/compression/preview` went with them, the text form stays). Section 3 count
  for that group 6 → 3; `/api/v1` mappings 193 → 190, total surfaces 205 → 202. No listener,
  middleware, or bypass change. Not a full re-validation.
- Full validation on 2026-09-15: reconciled **205 mapped route surfaces**, including
  **193 `/api/v1` mappings**, against the working tree, including uncommitted and untracked
  source. No missing or removed method/path pairs. Resolved the signing-key route group,
  HTTP-relay/proxy/control constants, and inherited event-WebSocket mapping; checked the
  production registration aggregator and searched for other routing and middleware branches.
  Inspected middleware ordering (authentication precedes static files and endpoint handlers),
  the exact three-case bypass predicate, session/tab validation, bootstrap expiry and single-use
  consumption, redirect normalization, and the shared proxy/control authentication gate.
  The board attachment-size middleware runs after authentication and does not bypass it.
  Both mandatory repository-wide listener searches found only the approved main Kestrel host,
  non-serving loopback port probe, and test-only Kestrel hosts. The cross-runtime search had
  no matches. No additional endpoint lacking a valid session credential was found.
  Ran `dotnet test Tests/Tests.csproj -p:OutputPath=bin/ApiSecAudit/ --verbosity quiet` with a
  `FullyQualifiedName` filter covering `CookieAuthMiddlewareTests`, `AuthServiceTests`,
  `AuthRoutesTests`, all five LLM proxy route test classes, `TokenSaverPauseRoutesTests`,
  `McpServerHttpTests`, `InternalToolsRoutesTests`, `SigningKeyRoutesTests`, and
  `BoardRoutesTests`: **103 passed, 0 failed, 0 skipped**. The build reported one existing
  xUnit2029 assertion-style warning in `BoardStoreTests.cs`. This was source reconciliation
  plus targeted tests, not a live request sweep of every production endpoint.

- Full validation on 2026-09-14: reconciled all **203 mapped route surfaces** in the current
  working tree, including uncommitted and untracked source: **191 `/api/v1` mappings**, nine
  protected non-`/api` API surfaces, and three bootstrap/page/probe mappings. The seven removed
  VibeRails Demon lifecycle routes account for the difference from the 2026-09-12 inventory.
  Method/path pairs matched in both directions, the listener set was unchanged, and the three
  permitted authentication exceptions remained exact `GET /health`, global `OPTIONS`, and exact
  `GET /auth/bootstrap`.
- Full validation on 2026-09-12 (kanban VB-5): compared the current categorized inventory
  against all **210 mapped route surfaces** in the then-current working tree, including untracked
  source: **198 `/api/v1` mappings**, nine protected non-`/api` API surfaces, and three
  bootstrap/page/probe mappings. Method/path pairs matched in both directions after
  resolving the signing-key `MapGroup`, HTTP-relay/proxy/control constants, and inherited
  event-WebSocket mapping. No missing or removed endpoints.
  Inspected the production registration aggregator (`Routes.cs` + `Program.cs` MCP/git-guard
  maps), middleware ordering (request log → security headers → CORS → WebSockets →
  `CookieAuthMiddleware` → static files → endpoints), the exact three-case bypass
  predicate, session/tab validation, bootstrap expiry and single-use consumption, redirect
  normalization, and the shared proxy/control `ILlmProxyAuthGate`.
  `HostShellTools` and `WebResearchTools` remain in-tree and unregistered on both HTTP and
  stdio MCP hosts (security review 2026-07-02). Dynamic signed-Python MCP tools still
  require user PIN approval plus explicit dashboard exposure.
  Both mandatory repository-wide listener searches found only the approved main Kestrel
  host, non-serving port probe, and test-only hosts; the daemon named pipe is current-user
  IPC, not an HTTP acceptor. The cross-runtime search had no production matches.
  No additional endpoint lacking a valid session credential was found, so no
  `SECURITY_ERROR.md` was created. All `/api/v1` business handlers require both
  credentials; page/static loads and conditional proxy responses retain the session-only
  behavior documented in section 2.
  Ran `dotnet test Tests/Tests.csproj -p:OutputPath=bin/ApiSecAudit/` with a
  `FullyQualifiedName` filter covering `CookieAuthMiddlewareTests`, `AuthServiceTests`,
  `AuthRoutesTests`, all five LLM proxy route test classes, `TokenSaverPauseRoutesTests`,
  `McpServerHttpTests`, `InternalToolsRoutesTests`, `SigningKeyRoutesTests`, and
  `BoardRoutesTests`: **101 passed, 0 failed, 0 skipped**. This was source reconciliation
  plus targeted tests, not a live request sweep of every production endpoint.

- Full validation on 2026-09-11: compared the current categorized inventory against all
  **210 mapped route surfaces** in the working tree, including untracked source:
  **198 `/api/v1` mappings**, nine protected non-`/api` API surfaces, and three
  bootstrap/page/probe mappings. Neither direction had unmatched method/path pairs.
  Resolved the signing-key route group, constant-based HTTP-relay/proxy/control paths,
  and inherited event-WebSocket mapping; checked the production registration aggregator
  and searched for other routing and middleware branches.
  Verified middleware ordering, exact GET health/bootstrap and global OPTIONS exceptions,
  session/tab validation, bootstrap code expiry and single-use consumption, and shared
  proxy/control authentication. Every other endpoint requires a valid session credential;
  all `/api/v1` business handlers additionally require the tab credential. The existing
  session-only page/static and conditional proxy behavior remains documented in section 2.
  Both mandatory repository-wide listener searches found only the approved main Kestrel
  host, non-serving port probe, and test-only hosts; the cross-runtime search had no matches.
  No additional endpoint lacking session authentication was found, so no
  `SECURITY_ERROR.md` was created.
  Ran `dotnet test Tests/Tests.csproj --no-restore --verbosity quiet` with a
  `FullyQualifiedName` filter covering `CookieAuthMiddlewareTests`, `AuthServiceTests`,
  `AuthRoutesTests`, all five LLM proxy route test classes, `TokenSaverPauseRoutesTests`,
  `McpServerHttpTests`, `InternalToolsRoutesTests`, `SigningKeyRoutesTests`, and
  `BoardRoutesTests`: **101 passed, 0 failed, 0 skipped**. This was source reconciliation
  plus targeted tests, not a live request sweep of every production endpoint.

- Full validation on 2026-09-10: reconciled all **210 mapped route surfaces**, including
  uncommitted and untracked source: 198 `/api/v1` mappings, nine protected non-`/api`
  API surfaces, and three bootstrap/page/probe mappings. Compared method/path pairs in
  both directions, resolving the signing-key route group, proxy/control/HTTP-relay
  constants, and inherited event-WebSocket mapping. No missing or removed endpoints;
  corrected stale current totals and expanded abbreviated board inventory paths.
  Inspected route registration, production middleware ordering, session/tab validation,
  the exact three-case bypass predicate, bootstrap expiry and single-use consumption,
  and the shared proxy/control gate. All `/api/v1` business handlers require both
  credentials. Page/static loads and conditional proxy responses retain the session-only
  behavior documented in section 2; cookie and session header are alternative transports
  of the same credential, not independent factors.
  Both mandatory repository-wide listener searches found only the approved main Kestrel
  host, non-serving loopback port probe, and test-only Kestrel hosts. No additional
  production listener or endpoint lacking a valid session credential was found, so no
  `SECURITY_ERROR.md` was created.
  Existing targeted tests passed: **101 passed, 0 failed, 0 skipped**, covering
  `CookieAuthMiddlewareTests`, `AuthServiceTests`, `AuthRoutesTests`, all five LLM proxy
  route test classes, `TokenSaverPauseRoutesTests`, `McpServerHttpTests`,
  `InternalToolsRoutesTests`, `SigningKeyRoutesTests`, and `BoardRoutesTests`.
  Ran `dotnet test Tests/Tests.csproj` with a filter for those classes and
  `-p:OutputPath=bin/ApiSecAudit/`. This was source reconciliation plus targeted tests,
  not a live request sweep of every production endpoint. Earlier dated observations
  below retain their historical counts and test results.
- Full validation on 2026-09-09: reconciled all **187 mapped route surfaces** against
  the current working tree, including untracked source. Compared method/path pairs in
  both directions, resolving the signing-key route group, constant-based proxy/control/
  HTTP-relay paths, and inherited event-WebSocket mapping. No missing or removed entries.
  Inspected the registration aggregator, production middleware ordering, exact three-case
  bypass predicate, session/tab validation, bootstrap expiry and single-use consumption,
  and shared proxy/control authentication gate. All 175 `/api/v1` business mappings
  require both session and tab credentials before their handlers run. Both mandatory
  repository-wide listener searches found only the approved main Kestrel host, transient
  non-serving port probe, and test-only Kestrel hosts; no other production network
  request listener was found.
  Existing targeted tests passed: **97 passed, 0 failed, 0 skipped**, covering
  `CookieAuthMiddlewareTests`, `AuthServiceTests`, `AuthRoutesTests`, all five LLM proxy
  route test classes, `TokenSaverPauseRoutesTests`, `McpServerHttpTests`,
  `InternalToolsRoutesTests`, and `SigningKeyRoutesTests`. Ran `dotnet test
  Tests/Tests.csproj` with a filter for those classes and
  `-p:OutputPath=bin/ApiSecAudit/` to build current source separately from running apps.
  No additional endpoint lacking a valid session credential was found, so no
  `SECURITY_ERROR.md` was created. This was source reconciliation plus targeted tests,
  not a live request sweep of every production endpoint.
- Full validation on 2026-09-08: reconciled all **182 mapped route surfaces** against the
  current working tree, including untracked source files. The 170 `/api/v1` method/path
  entries matched in both directions: no missing or removed endpoints. Also verified the
  nine protected non-`/api` API surfaces and three bootstrap/page/probe mappings, resolving
  constant-based proxy/control/HTTP-relay paths and the inherited event-WebSocket mapping.
  Inspected route registration, middleware ordering, session/tab validation, bootstrap
  code expiry and single-use consumption, and the shared proxy/control authentication gate.
  Both repository-wide listener searches found only the main Kestrel host, the non-serving
  port probe, and test-only hosts; no additional production request listener was found.
  The only session-authentication exceptions remain `GET /health`, `OPTIONS *`, and
  `GET /auth/bootstrap?code={one-time-code}&redirect={local-path}`. All `/api/v1` business
  handlers require both session and tab credentials. Session-only page/static loads and
  conditional proxy responses remain as documented in section 2.
  Existing targeted tests passed: **96 passed, 0 failed, 0 skipped**, covering
  `CookieAuthMiddlewareTests`, `AuthServiceTests`, `AuthRoutesTests`, all five LLM proxy
  route test classes, `TokenSaverPauseRoutesTests`, `McpServerHttpTests`, and
  `InternalToolsRoutesTests`. The default build encountered DLL locks from running
  Visual Studio/VibeRails processes; the successful run used
  `-p:OutputPath=bin/ApiSecAudit/` to build current source separately.
  No additional endpoint lacking a session credential was found, so no `SECURITY_ERROR.md`
  was created. This was source reconciliation plus targeted tests, not a live sweep of
  every endpoint.
- Full validation on 2026-09-07: reconciled all **182 mapped route surfaces** against the
  current working tree, including uncommitted changes: 170 `/api/v1` mappings, nine protected
  non-`/api` API surfaces, and three bootstrap/page/probe mappings. Resolved constant-based
  HTTP-relay/control/proxy paths and the inherited event-WebSocket mapping; no endpoints
  were missing from the inventory or removed from the codebase.
  Inspected the registration aggregator, production middleware ordering, exact three-case
  bypass predicate, bootstrap code validation/consumption, session/tab validation, and
  proxy/control gates. Both repository-wide listener searches found only the approved main
  Kestrel host, the non-serving port probe, and test-only hosts.
  Existing targeted tests passed: **95 passed, 0 failed, 0 skipped**, covering
  `CookieAuthMiddlewareTests`, `AuthServiceTests`, `AuthRoutesTests`, all five LLM proxy
  route test classes, `TokenSaverPauseRoutesTests`, and `McpServerHttpTests`.
  No additional endpoint lacking a session credential was found, so no `SECURITY_ERROR.md`
  was created. The only session-authentication exceptions remain `GET /health`, `OPTIONS *`,
  and `GET /auth/bootstrap?code={one-time-code}&redirect={local-path}`. All `/api/v1` business
  handlers additionally require the tab credential; the existing session-only page/static
  behavior and conditional proxy responses are documented in section 2.
  This was source reconciliation plus targeted tests, not a live sweep of every endpoint.
- Full validation on 2026-09-06: compared all 170 documented `/api/v1` method/path entries
  against source mappings, resolving the HTTP-relay constants and event-WebSocket mapping;
  neither set had unmatched entries. Also inspected all nine non-`/api` API surfaces,
  bootstrap/page/probe mappings, the registration aggregator, middleware ordering, the exact
  three-case bypass predicate, credential validation, and proxy/control gates. Repeated both
  repository-wide listener searches above, including untracked files: only the approved main
  Kestrel host, the non-serving port probe, and test-only hosts matched. Existing targeted
  tests passed: **96 passed, 0 failed, 0 skipped**, covering `CookieAuthMiddlewareTests`,
  `AuthServiceTests`, `AuthRoutesTests`, all five LLM proxy route test classes,
  `TokenSaverPauseRoutesTests`, `McpServerHttpTests`, and `InternalToolsRoutesTests`.
  No additional authentication exception was found, so no `SECURITY_ERROR.md` was created.
  This was source reconciliation plus targeted tests, not a live sweep of every endpoint.
- Amendment on 2026-09-06: the Internal tools feature added `GET /api/v1/internal/logs` and
  `GET /api/v1/internal/uploads`, which the 2026-09-05 inventory did not list. Both are now in
  section 3 with their accepted raw-Serilog exposure, and the `/api/v1` count is 170. Verified
  for these two routes only: path under `/api/`, so `CookieAuthMiddleware` requires both
  credentials; mapped only by the active root backend; GET only; `source` whitelisted before
  any filesystem access. Pinned by `Tests/Routes/InternalToolsRoutesTests.cs`. This was not a
  repeat of the full route sweep.
- Validation on 2026-09-05: compared all 168 documented `/api/v1` method/path entries
  against source mappings, including constant-based HTTP-relay paths and WebSocket
  mappings; neither set had unmatched entries. Inspected the route-registration aggregator,
  the nine non-`/api` API surfaces, and the bootstrap/page/probe mappings. Repeated both
  repository-wide listener searches above, including untracked files.
- Inspected production pipeline ordering, the exact middleware bypass predicate, session
  and tab validation, bootstrap code consumption/expiry, and the shared proxy/control gate.
  Existing targeted tests passed: **95 passed, 0 failed, 0 skipped**, covering
  `CookieAuthMiddlewareTests`, `AuthServiceTests`, `AuthRoutesTests`, all five LLM proxy
  route test classes, `TokenSaverPauseRoutesTests`, and `McpServerHttpTests`. This is a
  source audit plus targeted tests, not a live request sweep of every production endpoint.
- If “auth Cookie and SessionToken” is meant literally as the cookie plus the
  `viberails_session` header, **no route requires both**: the middleware deliberately accepts
  either transport for the same session secret. The actual second credential is
  `viberails_tab`.
- The normal `/api/v1/**` invariant is strong and simple: every mapped HTTP and WebSocket
  API is behind both session and tab validation before its handler runs.
- As of 2026-08-05, no `/llm` path bypasses `CookieAuthMiddleware`. All five proxy trees
  clear the middleware with the same header-borne credentials the in-handler gate checks,
  making the gate defense in depth rather than the only line. The feature-disabled `404`
  is now observable only by callers that already hold valid credentials; unauthenticated
  callers receive `401` from the middleware in every feature state.
- The bootstrap and health bypasses now match only the exact, case-insensitive GET routes.
  Other verbs and sibling paths such as `/auth/bootstrap-extra` remain behind the session
  credential check (apart from the separately documented global `OPTIONS` behavior).

## VB-69 scoped amendment (2026-09-28)

Added `GET /api/v1/board/cards/link-candidates?q=` under the existing active-root Board routes,
behind both session and tab credentials. It searches at most 50 current cards in the
server-derived project; query length is bounded to 300. Creation accepts at most 50 linked
card IDs and resolves both ends inside the creation transaction, rolling back on a foreign,
missing or deleted target. The existing board/card writes also accept validated display prefixes
and display IDs. Outbound sync includes these labels; immutable IDs and the consent boundary
are unchanged. Source-generated DTO serialization is retained.

Route tests cover credentials, scope, search and atomic draft links. Both mandatory listener
searches found the existing main Kestrel host, the non-serving PortFinder probe and test-only
hosts; the cross-runtime search had no matches. No new listener or auth exception was added.
This is a scoped review; unrelated existing findings remain in SECURITY_ERROR.md.

## Code mapper availability review (2026-09-28)

The review of the changes since v10.11.1 closed the two unresolved `SECURITY_ERROR.md`
entries and the file was removed. Python and Rust import paths are now built in linear time
(the analyzer's own dotted-name and `use`-path readers had the same repeated-concatenation
shape and were converted as well), import path text is bounded to 256 Ki characters per file,
and TypeScript type-argument delimiters are paired in one pass per file. The same review found
and bounded the same class of defect in the new `RepositoryModuleResolver`: Python root
discovery and Cargo owner lookup walk path prefixes once against span lookups, cross-root
Python probes only consult roots holding the module's first segment, and a per-graph work
budget (`module-work-limit`) stops repeated crate-map walks. The C#/PHP scope reader no longer
rescans the file for every unterminated `using`. Allocation and timing regressions cover each
case. No route, authentication, listener or repository-containment behaviour changed.


## VIBE-34 composer and lane activity (2026-10-01, scoped amendment)

The existing protected lane Automation GET adds running-agent metadata. The service validates
project, originating lane and current card/board membership; foreign or deleted cards are omitted.
Existing authenticated Board/history/attachment routes serve reference search and raster previews.
No new route, listener, credential exception or schema change is introduced. Markdown starts from
escaped text and emits fixed tags; image sources resolve through raster attachment records or
owned Blob URLs fetched with both credentials. Browser tests cover escaping and URL disposal.

Each root scheduler closes only Automation hosts with terminal run status, inactive PTY and an
ended recording, using the existing versioned reservation to reject concurrent starts. Ordinary
interactive terminals and stored recordings are preserved. No new launch permissions are added.
Scoped Board route enumeration and both mandatory listener searches found only the main Kestrel
host, non-serving port probe and existing test hosts; the cross-runtime search had no matches.
