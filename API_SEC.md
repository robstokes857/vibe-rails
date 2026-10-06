# API security contract

Read this before changing API exposure, authentication, production listeners, or outbound
credentials/data. This file holds the current boundaries and significant security decisions.
Open findings and fixes awaiting owner review belong in `SECURITY_ERROR.md`, when present.
Routine audit results belong with the change, not in this document.

- [Authentication and exceptions](#1-authentication)
- [Frozen listener topology](#listener-topology-and-discovery-frozen--review-gate)
- [Capability and data boundaries](#capability-and-data-boundaries)
- [Outbound services and credentials](#outbound-services-and-credentials)
- [Significant incidents](#significant-incidents--do-not-repeat)
- [Review and upkeep](#review-and-upkeep)

## 1. Authentication

**Both credentials** means the process's `viberails_session` secret plus `viberails_tab`.
The HttpOnly session cookie and same-named header are alternative transports of the same
secret, not two factors. The tab token lives in browser `sessionStorage` and travels in a
header. WebSocket handshakes use the session cookie/subprotocol and tab subprotocol;
query-string credentials are not accepted.

[CookieAuthMiddleware](VibeRails/Middleware/CookieAuthMiddleware.cs) runs before static
files and handlers. Production ordering in [Program.cs](VibeRails/Program.cs) is request
logging/redaction → security headers → CORS → WebSockets → authentication → static files →
endpoints. Root-only registration is an additional capability restriction, never authentication.

| Surface | Required protection |
| --- | --- |
| Every business `/api/v1/**` handler, including HTTP and WebSockets | Both credentials before the handler runs |
| `/mcp` and `/mcp/**` | Both credentials on every HTTP request; an MCP session ID grants no access |
| WebSocket upgrades, including terminal, Agent tools and app events | Both credentials; invalid handshakes return 403 |
| `GET /git-guard`; ordinary GET/HEAD pages and existing `wwwroot` assets | Session credential only |
| Enabled LLM proxy operations | Session middleware plus both headers at `ILlmProxyAuthGate` |
| `/llm/control/token-saver/{pause,resume,status}` and `POST /llm/control/agent/end-session` | Session middleware plus both headers at the control handler's proxy gate |

### The complete middleware skip list (FROZEN — review gate)

Exactly these cases bypass `CookieAuthMiddleware`:

1. **Exact `GET /auth/bootstrap`**, case-insensitive: protected separately by an atomically
   consumed, single-use code expiring after two minutes. Missing/invalid codes return 403.
   Redirects must be local paths; external, protocol-relative, backslash and control-character
   forms are rejected. See [AuthRoutes](VibeRails/Routes/AuthRoutes.cs) and
   [AuthService](VibeRails/Auth/AuthService.cs).
2. **Exact `GET /health`**, case-insensitive: bare readiness probe, no application data.
3. **Every `OPTIONS` request**: preflight bypass. Verb-specific business handlers do not run.
   Method-unrestricted mappings can receive non-preflight OPTIONS: enabled proxies still
   enforce their own two-header gate, and WebSocket handlers reject non-upgrade requests.

**This list must not grow.** A new skip path/predicate is a security regression: reject it
unless this contract is amended with an explicit justification in the same PR. Other verbs
and sibling paths such as `/auth/bootstrap-extra` are not exceptions, apart from OPTIONS.
No `/llm/**` path may return to the skip list. The CLIs already carry both credentials.
[CookieAuthMiddlewareTests](Tests/Middleware/CookieAuthMiddlewareTests.cs) pins this rule.

### LLM proxy details

The five main-host catch-all trees are `/llm/anthropic`, `/llm/openai`, `/llm/zai`,
`/llm/xai` and `/llm/cli-chat`. All pass through session middleware; enabled operations
also require both **headers** through [ILlmProxyAuthGate](TokenSaver/ILlmProxyAuthGate.cs).

The middleware's tab check matches paths containing `/api/`, MCP paths and WebSocket
handshakes. Normal Z.AI paths (`/llm/zai/api/paas/v4/...`) therefore require both credentials
in middleware. Other normal proxy paths, and nonstandard Z.AI catch-all paths without `/api/`,
require only the session there. Because handlers check the feature flag before their own gate,
a session-only caller can see disabled 404 versus enabled 401. This is an authenticated
feature-state distinction; it never authorizes relay work. Missing-session requests receive
401 regardless of feature state, except for the documented OPTIONS bypass. `/llm/**` auth
failures are plain-text status responses, never the browser HTML auth page.

`viberails_terminal_session` is correlation metadata, not a credential; spoofing it can
mislabel the caller's local exchange rows. Strip it and both real VibeRails credentials
before the provider hop. Native Grok's `Authorization` / `X-XAI-Token-Auth` are provider
credentials and are forwarded. `/llm/cli-chat` targets `cli-chat-proxy.grok.com` or `api.x.ai`
according to `GrokLlmProxyMode`; `/llm/xai` serves OpenCode's xAI provider. Neither is the
rejected `/llm/grok` sidecar. See [TokenSaver](TokenSaver/README.md).

## Listener topology and discovery (FROZEN — review gate)

The closed production set is:

1. The main Kestrel host in [Program.cs](VibeRails/Program.cs), using `ListenLocalhost`:
   at most one host per VibeRails backend process.
2. [PortFinder](VibeRails/Utils/PortFinder.cs) may briefly start/stop a loopback `TcpListener`
   to probe availability. It has no accept loop and reads no requests; it is not a server.

Test-only Kestrel fixtures under `Tests/**` are outside the production set. There is no
approved secondary HTTP server, sidecar, raw socket accept loop or provider-specific listener.
There is no background host/daemon; Automations run only while a root backend is open.

**A new production listener is a STOP finding**, including loopback-only listeners and random
capability URLs. Reject a new `HttpListener`, accepting `TcpListener`, bound/listening `Socket`,
additional Kestrel host or another runtime's server unless the owner explicitly approves the
topology change and this document records its threat model, authentication, exposure, lifecycle
and necessity **before implementation is accepted**. Forwarding to an authenticated route does
not put the inbound listener behind middleware, CORS, logging/redaction or lifecycle controls.
Loopback limits reachability; it is not authentication.

### Mandatory listener-discovery pass

Every API/authentication re-audit must enumerate routes **and** search the repository for
listeners, including uncommitted and untracked source. `Map*` calls alone cannot find all
network boundaries. Run both searches from the repository root and inspect every result:

```powershell
rg -n -i --hidden -g '!.git/**' -g '!**/bin/**' -g '!**/obj/**' -g '*.cs' -g '*.fs' -g '*.vb' 'HttpListener|TcpListener|UdpClient|new\s+Socket|\.Bind\s*\(|\.Listen\s*\(|Listen(?:Localhost|AnyIP|UnixSocket)|GetContextAsync|Accept(?:TcpClient|Socket|Async)|ConfigureKestrel|UseUrls|WebApplication\.Create' .
rg -n -i --hidden -g '!.git/**' -g '!**/node_modules/**' -g '!**/assets/**' -g '*.js' -g '*.mjs' -g '*.cjs' -g '*.ts' -g '*.py' -g '*.ps1' -g '*.psm1' -g '*.sh' -g '*.go' -g '*.rs' -g '*.java' -g '*.rb' 'HttpListener|TcpListener|(?:http|https|http2|net)\.createServer|HTTPServer|ThreadingHTTPServer|TCPServer|serve_forever|\.listen\s*\(|ListenAndServe|net\.Listen|TcpListener::bind|axum::serve|ServerSocket|HttpServer\.create|WEBrick' .
```

Classify test servers, outbound clients, non-serving probes and request acceptors separately.
Account for any ignored source or excluded generated checkout copies. A production acceptor
outside the closed set invalidates the audit until removed or explicitly approved. A feature
must never expand its own expected listener set just to make its audit pass.

## Capability and data boundaries

Exact method/path mappings live in [Routes](VibeRails/Routes/Routes.cs), its route classes,
[Program.cs](VibeRails/Program.cs) and [TokenSaver](TokenSaver). Resolve grouped paths,
constants, partial classes and the inherited event-WebSocket mapping during review. This
contract intentionally avoids a duplicate route-by-route catalog and drifting route totals.

### Board, search and stored evidence

See [Board instructions](VibeRails/Services/Board/AGENTS.md),
[BoardRoutes](VibeRails/Routes/BoardRoutes.cs) and
[local search routes](VibeRails/Routes/BoardRoutes.Search.cs).

- All Board REST routes are active-root-only and require both credentials. Ordinary routes
  derive the project from the dashboard root, never a request path; board, lane, card and child
  records are checked within that scope. Merge/move checks run in the write transaction.
- **Explicit local discovery is broader (VIBE-6/VIBE-28):** Board search, `/board/local-cards/*`
  and MCP can address other local projects. Full permanent card keys/row IDs resolve stored
  ownership through `IBoardStore`; short/display aliases stay current-project scoped. Foreign
  projects are visibly labeled. This is the local user's Board capability, not a per-project ACL.
  It does not broaden launch/session attachment or permit cross-project moves/merges.
- Search returns bounded current-text snippets (query ≤1,000 characters, ≤50 results); deleted/
  hidden discussion and change history are excluded. `currentProjectOnly` filters draft reference
  candidates before ranking/limits using the server's project. Related-card targets are checked
  live inside the write transaction, including draft creation; foreign targets need full keys/IDs.
  Cross-project links and target metadata are excluded from hosted activity publication.
- Session attachment takes the caller's inherited session, not an arbitrary MCP session argument,
  and remains within one project. Reads never auto-link; foreign-project writes preserve caller
  defaults/session links. Card-session reads require membership; document IDs must belong to the
  linked session. History uses exact session prefixes, not SQL LIKE wildcard interpretation.
- Reviews/checks scope card/project/session membership, bound untrusted evidence, and validate
  full Git SHAs with argv-only reads. Review finalization rechecks membership transactionally.
  Freshness never follows a supplied/stored checkout path; another checkout yields Unknown.
  Reviewer routing validates provider/environment/project visibility and rechecks at launch;
  queued routing is immutable, and server-only workspace flags are excluded from JSON input.
- Agent attention flags require `flagReason` and atomically save the attributed comment.
  Terminal lists expose their own session's labels/status, not discussion bodies. Context
  estimates expose labels/counts, not card text; synced launch measurements exclude paths,
  session IDs and card text. Board persistence stays behind `IBoardStore`.

### Untrusted content and attachments

Card text, remote content, attachments and tool results are untrusted data. Escape UI text;
Markdown starts from escaped text and emits fixed tags. Raster previews use validated attachment
records or owned Blob URLs fetched with both credentials. Sanitize/fence user text in launch
prompts. `@path` references are inert repository-relative names, not file access or commands.

Attachment filenames are labels, never filesystem paths; ignore claimed MIME/byte counts and
validate decoded bytes. The accepted human-upload policy is **40 current files per card, no
size cap**; bytes remain in SQLite, outside static files. Lift Kestrel's body limit only for the
upload path, after authentication: `RequestSizeLimitAttribute` is inert on minimal-API handlers.
Downloads scope card/project/current attachment and force octet-stream attachment disposition,
`nosniff`, `no-store` and sandbox CSP. Removed files return 404. Text previews stay text-only;
PDF previews paint canvases without active document layers.

MCP attachment reads accept scoped IDs, never paths or SQL. Text reads are bounded; PNG/JPEG/
GIF/WebP image transfer is limited to 5 MiB, using metadata before BLOB load and byte sniffing/
length checks afterward. This transfer limit does not restrict human storage/downloads.
Code/report viewers retain their opaque-origin iframe sandbox, MessageChannel lifecycle and
explicit teardown; see the [viewer contract](VibeRails/wwwroot/js/modules/code-report/README.md).

### MCP and agent launches

[HTTP and stdio MCP](VibeRails/Services/Mcp/AGENTS.md) share explicit tool registration.
HTTP `/mcp` is absent from terminal-tab children; its DI and mapping gates must agree.
`StatefulForInitializeClients` retains handshake clients' `clientInfo` in bounded in-memory
sessions; newer stateless clients supply it per request. Board writes require agent attribution;
neither attribution nor `Mcp-Session-Id` replaces the two credentials.

`vb mcp` is a CLI child over stdin/stdout, with no network listener or credential challenge.
It accesses Board storage through `IBoardStore` under the local OS user's authority. Launch
context is local process identity, not a per-card server ACL. `HostShellTools` and
`WebResearchTools` remain unregistered on both transports; Python MCP tools/signing help and
their exposure routes were removed. Do not restore these capabilities incidentally.

Board Start work and Board-triggered Workers use the exact per-tool grants in
[BoardMcpAuthorization](VibeRails/Services/Terminal/Commands/BoardMcpAuthorization.cs).
Other launches default off; card text/environment names cannot grant tools. No server wildcard,
unrelated tool, shared-config permission change or sandbox bypass comes from this grant path.
Native policy/deny rules still apply; Antigravity has prompt-only authorization because a narrow
native grant is unverified. The explicit, default-off base-CLI YOLO option is a separate user
choice; saved environments retain their own arguments. Resolve environments by ID and project
visibility, with no name fallback. Launch options use argv and `ShellArgSanitizer`.

Agent completion reports use the current linked session and do not themselves stop processes
or move cards. `end_agent_session` is the distinct own-session termination capability: no target
arguments, both process-local credentials at `/llm/control/agent/end-session`, current GUID
checked again under the lifecycle gate after a fixed 30-second grace. Repeats keep the first
deadline. Its client rejects non-loopback bases, proxies and redirects; no arbitrary PID is
accepted. Managed launches pin credentials independently of optional proxy settings, with only
environment variable names in Codex argv. Cleanup preserves ordinary terminals and recordings.

**Board workflows and launch automation must not inject keystrokes into a running agent TUI.**
Set launch/session options before spawn. Any new message-the-agent feature is a fresh capability
decision requiring validation against each provider's real TUI; see the removed notification
incident below. Ordinary authenticated terminal-input routes remain their own capability.

### Repository access, scripts and local reads

- Root-only filesystem browsing returns bounded single-level metadata, never contents. Reject
  relative, UNC/device, mapped/unknown Windows-drive and symlink/junction/reparse-component
  paths. Linked rows are metadata-only. Unix network mounts cannot be classified portably;
  downstream operations must revalidate selected paths independently.
- Code graph/changes/diff routes derive the repository root server-side, reject unsafe/link
  paths, and use bounded argv-only Git/file reads. Dependency/build exclusions cannot be bypassed
  by retired options or priority paths. Graph budgets include 1,000 files, 2,800 nodes, 10,000
  edges, 128 KiB/file and 16 MiB source; changes cap at 2,000 entries. Each diff side is bounded
  to 1,000,000 characters/5 MiB, with binary content reported rather than returned. Keep parser
  and resolver work budgets as well as byte limits; authenticated input can still exhaust resources.
- Root-only Automation catalog/import and script discovery use repository containment, reject
  links/reparse points, and never overwrite imported files. Imports re-hash scripts in the target
  and create disabled Automations. Discovery grants no approval: Job save/run hash checks remain
  authoritative. Discovery caps reads at 200 candidates/32 MiB; its ≤256-character query is a
  substring filter applied before budgets, never a path/command. Script execution uses an explicit
  interpreter and argv. Ordinary signed-Python authoring/execution retains its approval checks.
- Card Automation requests resolve card/job in the server's project and recheck enabled state,
  project and overlap in the run transaction. Lane skip matches an exact pending job/event pair;
  stale requests conflict, committed runs keep their lifecycle, and the receipt commits atomically.
- Root-only session replay is read-only/no-store, scopes details to the exact session ID and uses
  SQLite ReadOnly/query_only. Terminal snapshots remain behind both credentials, including retained
  screens after PTY exit (up to 20,000 scrollback lines); input rejects completed sessions.
- Root-only diagnostics use fixed source whitelists and reject linked log directories/files.
  **Accepted exposure (2026-09-06):** Serilog application/legacy daemon tails are served verbatim
  to holders of both credentials. Only `IFeatureLog` is the redacted channel. Never log API keys,
  tokens or transcript text through Serilog, including exception text; a hidden Logs panel is no
  protection. See [InternalToolsRoutes](VibeRails/Routes/InternalToolsRoutes.cs).

### Signing keys

Root-only `/api/v1/settings/keys` operations require both credentials and no-store responses.
List returns public metadata; export returns encrypted PEM only. RSA-4096 private keys use
PKCS#8 AES-256-CBC with PBKDF2-SHA256 (600,000 iterations). Creation requires a nonblank,
non-numeric-only 8–128-character password; export/sign/sync require unlock. Requests cap at
96 KiB; failed unlocks are throttled per process and file operations serialize across processes.

Signing uses RSA-PSS/SHA256 over exact decoded bytes (≤64 KiB) and refuses the registration
challenge prefix. Cloud registration uses a domain-separated proof of possession, sending only
public PEM/name/challenge/proof and a header API key to fixed HTTPS viberails.ai, with no redirects.
Passwords/private keys/ordinary signing payloads stay local. Separately hosted public verification
receives the submitted payload/signature and **discloses the verified signer's account email**,
an owner-approved behavior explained by the KEYS UI. See [SigningKeyRoutes](VibeRails/Routes/SigningKeyRoutes.cs).

## Outbound services and credentials

### Board sync, sharing and remote Start work

[SYNC.md](VibeRails/Services/Board/SYNC.md) defines the full content and hosted authorization
contract. A configured API key enables automatic Board publication and linked activity across
local projects; older per-board consent switches no longer disable it. Read each board's content
using its stored project scope. Portable text travels verbatim and can contain secrets. Activity
can include linked session metadata/outcomes, durable commit code, bounded attachment content
and card links; local project paths, environment IDs, launch settings and Automation definitions
are excluded. Snapshot code comes from the saved capture, not a fresh checkout read.

Use header-only `X-Api-Key`, HTTPS (HTTP only for loopback fixtures), no redirects or endpoint
credentials/query/fragment, a per-call endpoint/key fingerprint, 30-second deadlines and bounded
responses. Configuration changes reject in-flight fingerprint mismatches; owned boards republish
to the new destination. Cross-process file locks serialize sync. Validate activity acknowledgements
against schema/card; bounded error codes/IDs may be consumed, never arbitrary remote error prose.

Sharing checks owner-only collaborator management and accepted membership on every hosted
request, with Auth0/antiforgery for browser writes. Invites do not disclose account existence;
verified-email recipients accept, and declines/blocks stay private. The cap is three pending/
accepted collaborators. Membership changes participate in concurrency checks. Imported boards
retain their remote/destination identity and cannot auto-republish after revocation; moves/merges
must respect the imported-board boundary. Membership does not grant live terminal control or
account-wide archive access; Board activity playback follows its explicit hosted access rules.

Remote Start work is outbound polling by an open root backend, only for its own project's
published, non-imported boards. Requests carry opaque board/card IDs and a required sync sequence,
never executable/path/argv/launch overrides. Sync and recheck destination/project/membership and
application of the target card's edits before ordinary Board launch. Only the owner can request
execution; collaborators cannot. Hosted browser requests require Auth0/antiforgery; delivery and
results bind the owner's API key and desktop instance. Consume once; lost results are Unknown,
never an automatic second launch. Closing the root stops this capability; there is no daemon.

### Account linking

Root-only `/api/v1/settings/remote-link` requires both credentials and no-store responses.
The device code and received API key stay backend-only; responses/events expose a public user
code, fixed verification URL, progress and masked/display information. The configured frontend
origin requires HTTPS (loopback fixtures excepted), with no credentials/path/query/fragment.
The device-link client sends no existing key/cookies and follows no redirects; exchanges cap
at 20 seconds/16 KiB, cleanup at five seconds. Returned verification URLs must equal that
origin's `/link`. The dashboard/VS Code bridge permits only `https://viberails.ai/link` (the
local origin's `/link` in local Front mode, dashboard only) and its optional strict
`#code=ABCD-EFGH` public-code fragment. The hosted page removes that fragment
before Auth0, retains it in same-tab storage for at most ten minutes, and requires explicit
approval with fresh antiforgery.

Serialize/throttle the root's single in-memory attempt; cancellation/replacement invalidates late
responses. Compare the original saved key before replacement so manual changes win. Account
email is response-only, written with the key and its full SHA-256 fingerprint; a mismatch hides
it. Never return the raw credential/fingerprint. See [account-link flow](docs/account-link-auth-flow.md).

### Local Front mode (VB-8NI09-170)

`VIBERAILS_LOCAL_FRONT_ORIGIN` points one desktop process tree at VibeRails-Front's local Docker
stack. It is active only in a Debug build under Development, for an https `localhost`/loopback
origin with no credentials, path, query or fragment. Any other request stops the web host at
startup. It adds no listener or route.

- **Key.** The runtime key is the local stack's key, stored per origin in
  `~/.vibe_rails/local-front-keys.json`. Account linking and the Settings key field read and write
  only that file. The production key is never shown, sent or replaced. The local key never leaves
  for production.
- **Sign-in page.** The dashboard accepts only the backend-reported origin's `/link` in place of
  `https://viberails.ai/link`. The VS Code bridge is unchanged.
- **Production publishing.** Any process that sees the variable, valid or not, publishes
  nothing to production: no Board sync/share/import or remote Start polling, no session upload,
  no backups or data export, no token savings and no signing-key sync. These pause before locks,
  metadata changes or network calls. Its `IHttpClientFactory` clients refuse viberails.ai hosts.
- **HTTPS.** Certificates validate normally; there is no bypass.
- **Shells.** PTY shells and CLIs do not inherit the variable.

See [Services/LocalFront/AGENTS.md](VibeRails/Services/LocalFront/AGENTS.md).

### Jira (VB-40 / VIBE-102)

The four root/project-scoped `/api/v1/board/boards/{boardId}/jira` GET/PUT and `/test`, `/pull`
POST routes use both credentials. API tokens are write-only, stored in plain text in the private
`~/.vibe_rails/jira-tokens.json`, never Board storage/responses/logs. An OS file lock protects
cross-process token writes; blank saves retain the token only for the same origin. Changing
origin requires a token again and deletes the old connection's token. Pulls hold a cross-process
lock, and dry-run counts without card writes.

Accept only HTTPS site/board links without user info. Board/filter IDs must be digits and project
keys match `[A-Z][A-Z0-9_]+` before use in REST paths/JQL; the client rechecks IDs. Discard unrelated
link query parameters. Narrowing JQL is URL-encoded as the board call's `jql` value and cannot
contain ORDER BY. All outbound reads use saved-origin HTTP basic (email/token), with redirects
disabled and 3xx treated as failure: `/rest/api/3/myself`, `/rest/api/3/search/jql`,
`/rest/api/3/filter/{id}`, `/rest/agile/1.0/board/{id}` and its `/configuration`, plus
`/rest/software/1.0/board/{id}/issue` and `/issue/approximate-count`. Do not use the deprecated
agile board-issue endpoint or forward a saved token to another host.

Column mappings compare lane IDs only against the edited board's own lanes. The email suggestion
runs fixed `git config --get user.email` argv in the server-derived root, with a three-second
timeout and bounded output; no request value reaches the command. See
[JiraCloudClient](VibeRails/Services/Jira/JiraCloudClient.cs) and
[JiraBoardLink](VibeRails/Services/Jira/JiraBoardLink.cs).

### Complete backups

[Backup policy](VibeRails/Services/CompleteBackups/README.md) defines datasets, exclusions and
redaction. Active roots send to fixed HTTPS `viberails.ai/api/v1/data-exports/backups/` with
header-only keys, no redirects, bounded/cancellable exchanges and strict account/computer/version
acknowledgements. Hosted ingest uses the API key; downloads are separately Auth0/owner-protected.
Parts are hashed/bounded; local status returns no-store coverage/receipts, never payloads/secrets.

Exclude native credential stores, browser profiles/keychains, private keys, CLI runtime logs/
transcripts and external/link targets from configuration capture. Redact **every** captured JSON/
JSONC and TOML file, including plugin MCP manifests, nested env/header blocks and credentials
inside strings/argv/URLs; classify decoded TOML escapes and percent-encoded credential names.
Unparseable files are reported/omitted, never silently copied or written empty. **YAML is copied
verbatim; database rows, scripts and free text may contain embedded secrets.** These archives
remain private account data, not guaranteed secret-free exports. See the backup incident below.

## Significant incidents — do not repeat

### Grok loopback bridge: rejected and removed (2026-08-15)

`GrokLoopbackBridge` created a separate `HttpListener` on `127.0.0.1` ports **6000–6999**.
Inbound requests bypassed the ASP.NET pipeline and used a random path capability; the bridge
then attached process credentials when forwarding to `/llm/grok`. Loopback binding, a 32-byte
random capability, destination pinning and header stripping did not make this second HTTP
server part of the approved authentication boundary.

**Do not reintroduce, merge or ship it.** The accompanying audit found the listener but labeled
it expected and concluded there was no bypass. That process failure is why the listener set is
frozen: a newly discovered listener starts as a finding, never as its own approval. The approved
`/llm/xai` and `/llm/cli-chat` mappings use the existing Kestrel host.

### Live-TUI notification/plan handshake: removed (2026-09-15)

The Board revision-notify route sent two Escapes, fixed text and Enter into an active CLI.
On idle Claude Code, double Escape opens rewind; the following text/Enter could restore a
checkpoint. It had never been tested against a live CLI. The screen-scraped Codex `/plan` plus
paste/Enter handshake was removed too. Do not revive either as a harmless notification or
launch option; process arguments and real provider validation are the boundary.

### Backup credential leakage: corrected through repeated review (2026-10-02)

Initial filters admitted OpenCode `mcp-auth.json` and Antigravity's Chromium credential profile.
Follow-up review found plugin `.mcp.json`, Codex/Grok `config.toml`, MCP argv secrets, nested
header/env strings and encoded credential spellings still leaking. Filename allowlists alone
were insufficient; all JSON/TOML plus embedded argument forms need redaction. The earlier
TOML residual is closed; YAML/free-text limits remain above. Archive-level
[BackupFilesCredentialTests](Tests/Services/BackupFilesCredentialTests.cs) exercise the real
enumeration/zip writer. No archive had been staged/delivered from the reviewed machine;
owners whose roots already ran the vulnerable feature should check delivered configuration archives.

### Other retained findings

- **Proxy feature oracle, fixed 2026-08-05:** `/llm/openai` and `/llm/zai` skipped middleware
  and exposed unauthenticated disabled-404/enabled-401 responses before their relay gate.
  No upstream action/data was reachable, but the feature bit was public. Removing the skip
  entries closed it; keep the authenticated-only distinction documented in section 1.
- **Code mapper availability, fixed 2026-09-28:** repeated string concatenation and rescanning
  made Python/Rust imports, TypeScript type delimiters, C#/PHP scopes and module resolution
  exhaust time/memory. Linear passes, bounded import text and a per-graph `module-work-limit`
  closed the findings. Preserve allocation/timing regressions; byte caps alone are insufficient.
- **Worker launch restrictions lost on save, 2026-10-05 (VIBE-58):** an Automation rename could
  rewrite its shared Environment and drop `--sandbox read-only` and other unrepresented native
  arguments. Metadata-only saves must leave the Worker untouched; Worker edits must preserve
  unknown arguments. Fix/owner-review status is recorded in `SECURITY_ERROR.md` while pending;
  retain browser and argument round-trip coverage rather than treating a successful save as proof.

## Review and upkeep

For API/authentication changes, inspect the affected routes, production registration and
middleware order; verify missing/session-only/tab-only/both-credential behavior and applicable
project/card/session ownership. Full re-audits enumerate all routes in both directions, resolving
constants/groups/partials/WebSockets, and perform both repository-wide listener searches above.
Check bootstrap expiry/single use/local redirects and proxy/control gates. Run relevant existing
regressions; a source review or targeted suite is not a live sweep of every endpoint.

Useful starting points: `Tests/Middleware/CookieAuthMiddlewareTests.cs`, `Tests/AuthServiceTests.cs`,
`Tests/Routes/AuthRoutesTests.cs`, the five LLM proxy route suites, control-route tests,
`Tests/Services/Mcp/McpServerHttpTests.cs`, and the affected feature's route/service tests.
Record the review scope, listener classification, test command/results and limitations in the
PR, Board comment or private `vibe-books` investigation. Record violations in `SECURITY_ERROR.md`
for owner review; never normalize rejected working-tree changes into this approved contract.

Keep this file small:

- Edit the relevant current rule in place; merge superseded amendments instead of prepending
  another dated section. Link component contracts for implementation details.
- Add history only for a significant incident, accepted exposure, unresolved risk or new security
  decision. State what failed, the disposition and the rule that prevents recurrence.
- Do not append “checked, all good,” repeated test totals, route counts or unchanged listener
  results. Successful checks with no contract change require no edit here. Git history retains
  old audit notes; this document does not need an archive copy.
