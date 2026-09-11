# API authentication coverage

Audit date: 2026-09-11

Full production route/authentication reconciliation completed: 2026-09-11, covering
the current working tree, including uncommitted and untracked source. All 210 mapped
surfaces match this inventory in both directions: 198 `/api/v1` method/path surfaces,
nine protected non-`/api` API surfaces, and three bootstrap/page/probe mappings. No
endpoint needs adding or removing. The existing inventory includes the untracked board
and signing-key routes, the signing-key route group, and constant-based route paths.
The only session-authentication exceptions are exact `GET /health`, global `OPTIONS`,
and exact `GET /auth/bootstrap` with its single-use, expiring code. No additional
endpoint lacking a valid session credential was found, so no `SECURITY_ERROR.md` was
created. Session-only page/static loads and conditional proxy responses remain as
documented in section 2. Both repository-wide listener searches found only the main
Kestrel host, the non-serving port probe, and test-only hosts. Targeted authentication
and route tests passed: **101 passed, 0 failed, 0 skipped**. See Audit observations for
scope and validation details.

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

### Repository-wide listener result — 2026-09-11

- Approved serving implementation: the main Kestrel host in `VibeRails/Program.cs`.
- Rejected and removed before merge: `GrokLoopbackBridge`'s `HttpListener`.
- Non-serving production match: `PortFinder`'s transient loopback `TcpListener` port probe.
- Test-only matches: isolated Kestrel hosts under `Tests/**`.
- No other production .NET accept loop and no JavaScript/TypeScript, Python, or PowerShell
  server/listener implementation was found.

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
`viberails_tab` credential. This treats “SessionToken” in the request as the second,
session-scoped browser credential (`viberails_tab`), which the implementation calls the
*tab token*.

`viberails_terminal_session` is not a credential. LLM-proxy requests may carry it as a
correlation header (the terminal `Sessions.Id` behind the exchange log's `SessionId`
column); the auth gate does not validate it, and the relay strips it — like the two real
credentials — before the upstream provider hop. Spoofing it can only mislabel the
spoofer's own local exchange rows.

Authentication is enforced primarily by
[`CookieAuthMiddleware`](VibeRails/Middleware/CookieAuthMiddleware.cs). The LLM proxy
routes additionally use
[`ILlmProxyAuthGate`](TokenSaver/ILlmProxyAuthGate.cs). There are 210 mapped route
surfaces in this inventory: 198 `/api/v1` method/path mappings, nine non-`/api` protected
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

### Application settings, PIN, export, push, and HTTP relay (18)

- `GET /api/v1/settings`
- `POST /api/v1/settings`
- `GET /api/v1/settings/db-size`
- `POST /api/v1/settings/computer-name`
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

### Compression and token savings (6)

- `GET /api/v1/compression/captures`
- `GET /api/v1/compression/captures/{id:guid}`
- `DELETE /api/v1/compression/captures`
- `GET /api/v1/compression/catalog`
- `POST /api/v1/compression/preview`
- `GET /api/v1/token-savings`

### Git, hooks, and code analyzer (15)

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
- `GET /api/v1/code-analyzer/source`
- `GET /api/v1/code-analyzer/ignores`
- `POST /api/v1/code-analyzer/ignores`
- `POST /api/v1/code-analyzer/ignores/bulk`
- `DELETE /api/v1/code-analyzer/ignores`

### Jobs (13)

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

### VibeRails Demon lifecycle (7; active root backend only)

- `GET /api/v1/jobs/demon`
- `POST /api/v1/jobs/demon/install`
- `POST /api/v1/jobs/demon/start`
- `POST /api/v1/jobs/demon/stop`
- `POST /api/v1/jobs/demon/restart`
- `POST /api/v1/jobs/demon/repair`
- `DELETE /api/v1/jobs/demon`

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

### Python scripts (16)

- `GET /api/v1/python-scripts`
- `POST /api/v1/python-scripts/pin`
- `POST /api/v1/python-scripts/approve`
- `POST /api/v1/python-scripts/revoke`
- `POST /api/v1/python-scripts/run`
- `GET /api/v1/python-scripts/mcp`
- `PUT /api/v1/python-scripts/mcp`
- `DELETE /api/v1/python-scripts/mcp`
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

### Kanban board (23; active root backend only)

All mapped by `BoardRoutes.Map` under `if (isActiveRootBackend)`; every path contains `/api/`,
so both credentials are enforced by the middleware with no route-level registration. The
project is always `ParserConfigs.GetRootPath()` — never a value from the request — so a caller
cannot read or write another project's board through this surface.

- `GET /api/v1/board/columns`, `POST /api/v1/board/columns`, `PUT /api/v1/board/columns/order`,
  `PUT /api/v1/board/columns/{columnId}`, `DELETE /api/v1/board/columns/{columnId}` — lanes.
- `GET /api/v1/board/cards`, `POST /api/v1/board/cards`, `GET /api/v1/board/cards/{card}`,
  `PUT /api/v1/board/cards/{card}`, `DELETE /api/v1/board/cards/{card}`,
  `POST /api/v1/board/cards/{card}/move` — cards (`{card}` is an id or a `VB-n` key).
- `POST /api/v1/board/cards/{card}/launch` — "Start work": creates a terminal tab through the
  in-process tab host and starts the assigned LLM with the card prepended to the environment's
  Initial Message. Same capability class as `POST /api/v1/terminal/tabs/{tabId}/start`, which
  is why the tab credential matters here. The environment is resolved by id and must be
  visible in the current project; there is no fallback by name.
- `POST /api/v1/board/cards/{card}/comments`,
  `POST /api/v1/board/cards/{card}/attachments`,
  `DELETE /api/v1/board/cards/{card}/attachments/{attachmentId}` — comments and inline image attachments (data URLs,
  image types only, size-capped; returned only inside the card body, so no unauthenticated
  image path was added).
- `GET /api/v1/board/cards/{card}/commits`, `POST /api/v1/board/cards/{card}/commits`,
  `DELETE /api/v1/board/cards/{card}/commits/{sha}`,
  `GET /api/v1/board/cards/{card}/commits/{sha}/diff` —
  linked commits. `git` runs only with an argument list and a regex-validated hex sha (never a
  shell string, never a ref expression) inside the project directory.
- `GET /api/v1/board/cards/{card}/sessions`, `POST /api/v1/board/cards/{card}/sessions`,
  `PUT /api/v1/board/cards/{card}/sessions/{sessionId}`,
  `DELETE /api/v1/board/cards/{card}/sessions/{sessionId}` — the card ↔ terminal-session links.

MCP note: the same board operations (minus any delete) are exposed as `*_board_*` tools on
`/mcp` (both credentials) and on the stdio `vb mcp` host. The stdio host reads and writes
`state.db` directly rather than calling this API, so a CLI in any terminal can work a card
without a VibeRails tab; the host is a child process of the CLI over pipes and remains
unauthenticated by design. `Tests/Routes/BoardRoutesTests.cs` pins the two-credential
requirement and the launch composition; `Tests/Services/Mcp/BoardToolTests.cs` pins the tools.

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
