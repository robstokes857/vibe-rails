# API authentication coverage

Audit date: 2026-08-11

Jobs inventory re-checked: 2026-08-03 (three run-history routes added; see § 3).

LLM proxy posture re-audited: 2026-08-05 (`/llm/openai` and `/llm/zai` removed from the
`CookieAuthMiddleware` skip list; the § 1 conditional-response finding is resolved — see
§§ 1–3).

Route inventory re-checked: 2026-08-07 (five authenticated HTTP-relay proof mappings
added; see § 3).

Route inventory and authentication coverage re-checked: 2026-08-11 (no endpoint
additions or removals; the 151 mapped surfaces and frozen three-case middleware bypass
remain unchanged).

Scope: the current working tree, including uncommitted changes.

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

Authentication is enforced primarily by
[`CookieAuthMiddleware`](VibeRails/Middleware/CookieAuthMiddleware.cs). The LLM proxy
routes additionally use
[`ILlmProxyAuthGate`](TokenSaver/ILlmProxyAuthGate.cs). There are 151 mapped route
surfaces in this inventory: 141 `/api/v1` method/path mappings, seven non-`/api` protected
API surfaces, and three bootstrap/page/probe routes. Static-file middleware and the
global `OPTIONS` behavior are noted separately because they are not finite mapped-route
lists.

## 1. No protection

- `GET /health` — deliberately unauthenticated readiness probe. It returns `200 OK` and
  no application data.
- `OPTIONS *` — `CookieAuthMiddleware` skips every CORS preflight request, regardless of
  path. These requests do not execute the verb-specific business handlers, but the auth
  layer itself does not protect them.

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

### Non-`/api/v1` API surfaces (7)

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

### Agent files and rules (12)

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

### CLI, environment, and LLM-picker management (15)

- `GET /api/v1/environments`
- `POST /api/v1/environments`
- `GET /api/v1/environments/{name}`
- `PUT /api/v1/environments/{name}`
- `DELETE /api/v1/environments/{name}`
- `GET /api/v1/environments/{name}/launch`
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

### Git, hooks, and code analyzer (16)

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
- `POST /api/v1/debug-bundle/{sessionId}/send`

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

- If “auth Cookie and SessionToken” is meant literally as the cookie plus the
  `viberails_session` header, **no route requires both**: the middleware deliberately accepts
  either transport for the same session secret. The actual second credential is
  `viberails_tab`.
- The normal `/api/v1/**` invariant is strong and simple: every mapped HTTP and WebSocket
  API is behind both session and tab validation before its handler runs.
- As of 2026-08-05, no `/llm` path bypasses `CookieAuthMiddleware`. All three proxy trees
  clear the middleware with the same header-borne credentials the in-handler gate checks,
  making the gate defense in depth rather than the only line. The feature-disabled `404`
  is now observable only by callers that already hold valid credentials; unauthenticated
  callers receive `401` from the middleware in every feature state.
- The bootstrap and health bypasses now match only the exact, case-insensitive GET routes.
  Other verbs and sibling paths such as `/auth/bootstrap-extra` remain behind the session
  credential check (apart from the separately documented global `OPTIONS` behavior).
