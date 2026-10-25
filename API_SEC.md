# API security contract

Read this before changing API exposure, authentication, production listeners, or outbound
credentials/data. This file holds the current boundaries and significant security decisions.
Routine audit results belong with the change, not in this document.

- [Authentication and exceptions](#1-authentication)
- [Frozen listener topology](#listener-topology-and-discovery-frozen--review-gate)
- [Endpoint inventory](#endpoint-inventory)
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

The VS Code bridge keeps credential variables in its private closure and adds session/tab
credentials only to the exact backend HTTP or WebSocket origin. Browser pages keep the session
cookie HttpOnly. Dashboard and bootstrap inline scripts use a fresh response nonce; script CSP
does not allow arbitrary inline scripts, and browser connections are restricted to `'self'`.

CLI descendants receive separate random credential pairs, never the dashboard pair.
Root tool contacts rotate on each accepted tab launch, bind to the resulting session, and require its
tab to remain active and reachable. They permit only GET picker/sharing capabilities, POST
current-project card launch, POST sharing links for an owned session, and (VB-50 slice E) POST
`/api/v1/review-requests` attaching only the contact's own session plus GET
`/api/v1/review-requests/{id}/disclosure` for a request that attaches it, both refused 403 otherwise;
never the request list, staged preview, detail, send, decisions, cancel or delete. They cannot reach
Settings, terminal input/start, shutdown, WebSockets or HTTP MCP. Proxy/control pairs rotate
per PTY session and are revoked on exit; they permit only the five proxy trees and the exact
token-saver/self-completion controls. Both values must belong to the same contact.
Replacement contacts stay unbound until the child confirms startup; a rejected start discards
only its candidate and preserves the live session's contact. Uncertain starts never promote a
candidate. These scopes add no middleware bypass or listener. The stdio Board store capability
remains the local OS user's authority described below.

The bootstrap cookie is HttpOnly, SameSite=Lax and Secure on HTTPS and the normal HTTP
`localhost` origin. Numeric loopback HTTP (`127.0.0.1` / `[::1]`) retains a non-Secure cookie:
Chromium rejects Secure cookies on those origins. This compatibility exception adds no
listener, auth bypass or remote HTTP support.

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
   Method-unrestricted mappings can receive non-preflight OPTIONS: proxies enforce their
   two-header gate before checking feature state, and WebSocket handlers reject non-upgrade requests.

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
require only the session there. All five handlers check their two-header gate before feature
state, including OPTIONS. Missing or incomplete credentials receive 401 regardless of whether
the proxy is enabled. Only an authenticated caller can see a disabled 404. `/llm/**` auth
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

## Endpoint inventory

Current explicit mappings are listed below, including grouped routes, partial Board routes,
constant-based paths, the inherited event WebSocket, and the SDK HTTP MCP transport.
Update this inventory whenever a mapping is added, removed, or renamed. Registration and
feature gates can make a listed endpoint unavailable in a particular process; they do not
replace authentication. `ANY` means the mapping does not restrict the HTTP method.

All `/api/v1/**` operations require the session credential **and** tab credential.
WebSocket upgrades require both through the middleware handshake check. All five `/llm/**`
proxy trees require a session in middleware and both headers before enabled relay work;
LLM controls require both headers. `/mcp` requires both credentials. `/git-guard` requires
a session. These requirements exclude the global `OPTIONS *` exception documented above.
`GET /health` and `GET /auth/bootstrap?code={one-time-code}&redirect={local-path}` are the
only explicit mappings exempt from session authentication. Bootstrap still validates its code.

Ordinary GET/HEAD pages and existing static assets are also session-protected; static-file
middleware serves their paths rather than registering individual API endpoints.

| Method | Path | Mapping source |
| --- | --- | --- |
| OPTIONS | `*` | [CookieAuthMiddleware.cs](VibeRails/Middleware/CookieAuthMiddleware.cs); global bypass, not a business mapping |
| GET | `/api/v1/agent-tools/terminal` | [AgentToolRoutes.cs](VibeRails/Routes/AgentToolRoutes.cs) |
| POST | `/api/v1/agent-tools/terminal/input` | [AgentToolRoutes.cs](VibeRails/Routes/AgentToolRoutes.cs) |
| POST | `/api/v1/agent-tools/terminal/open` | [AgentToolRoutes.cs](VibeRails/Routes/AgentToolRoutes.cs) |
| POST | `/api/v1/agent-tools/terminal/snapshot` | [AgentToolRoutes.cs](VibeRails/Routes/AgentToolRoutes.cs) |
| POST | `/api/v1/agent-tools/terminal/{tabId}/input` | [AgentToolRoutes.cs](VibeRails/Routes/AgentToolRoutes.cs) |
| GET | `/api/v1/agent-tools/terminal/{tabId}/snapshot` | [AgentToolRoutes.cs](VibeRails/Routes/AgentToolRoutes.cs) |
| ANY (WebSocket) | `/api/v1/agent-tools/ws` | [AgentToolRoutes.cs](VibeRails/Routes/AgentToolRoutes.cs) |
| GET | `/api/v1/agents` | [AgentRoutes.cs](VibeRails/Routes/AgentRoutes.cs) |
| POST | `/api/v1/agents` | [AgentRoutes.cs](VibeRails/Routes/AgentRoutes.cs) |
| GET | `/api/v1/agents/content` | [AgentRoutes.cs](VibeRails/Routes/AgentRoutes.cs) |
| GET | `/api/v1/agents/files` | [AgentRoutes.cs](VibeRails/Routes/AgentRoutes.cs) |
| PUT | `/api/v1/agents/name` | [AgentRoutes.cs](VibeRails/Routes/AgentRoutes.cs) |
| DELETE | `/api/v1/agents/rules` | [AgentRoutes.cs](VibeRails/Routes/AgentRoutes.cs) |
| GET | `/api/v1/agents/rules` | [AgentRoutes.cs](VibeRails/Routes/AgentRoutes.cs) |
| POST | `/api/v1/agents/rules` | [AgentRoutes.cs](VibeRails/Routes/AgentRoutes.cs) |
| PUT | `/api/v1/agents/rules/enforcement` | [AgentRoutes.cs](VibeRails/Routes/AgentRoutes.cs) |
| POST | `/api/v1/agents/validate` | [AgentRoutes.cs](VibeRails/Routes/AgentRoutes.cs) |
| DELETE | `/api/v1/automation-nav/preferences` | [AutomationNavRoutes.cs](VibeRails/Routes/AutomationNavRoutes.cs) |
| GET | `/api/v1/automation-nav/preferences` | [AutomationNavRoutes.cs](VibeRails/Routes/AutomationNavRoutes.cs) |
| PUT | `/api/v1/automation-nav/preferences` | [AutomationNavRoutes.cs](VibeRails/Routes/AutomationNavRoutes.cs) |
| GET | `/api/v1/bert/captures` | [BertRoutes.cs](VibeRails/Routes/BertRoutes.cs) |
| GET | `/api/v1/bert/captures/by-session/{sessionId}` | [BertRoutes.cs](VibeRails/Routes/BertRoutes.cs) |
| GET | `/api/v1/bert/captures/{documentId}` | [BertRoutes.cs](VibeRails/Routes/BertRoutes.cs) |
| POST | `/api/v1/bert/search` | [BertRoutes.cs](VibeRails/Routes/BertRoutes.cs) |
| GET | `/api/v1/bert/session-captures` | [BertRoutes.cs](VibeRails/Routes/BertRoutes.cs) |
| GET | `/api/v1/bert/status` | [BertRoutes.cs](VibeRails/Routes/BertRoutes.cs) |
| GET | `/api/v1/board/boards` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/boards` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/boards/order` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/boards/{boardId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/boards/{boardId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/boards/{boardId}/ado` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/boards/{boardId}/ado` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/boards/{boardId}/ado` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/boards/{boardId}/ado/pull` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/boards/{boardId}/ado/test` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/boards/{boardId}/context` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/boards/{boardId}/context` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/boards/{boardId}/history` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/boards/{boardId}/jira` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/boards/{boardId}/jira` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/boards/{boardId}/jira` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/boards/{boardId}/jira/pull` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/boards/{boardId}/jira/test` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/boards/{boardId}/sharing` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/boards/{boardId}/sharing` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/boards/{boardId}/sharing/{inviteId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/boards/{boardId}/sharing/{inviteId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/boards/{boardId}/sync` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/boards/{boardId}/sync` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/boards/{boardId}/sync/now` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/activity` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/link-candidates` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/search` | [BoardRoutes.Search.cs](VibeRails/Routes/BoardRoutes.Search.cs) |
| DELETE | `/api/v1/board/cards/{card}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/cards/{card}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/attachments` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/cards/{card}/attachments/{attachmentId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/attachments/{attachmentId}/content` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/automations` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/automations` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/automations/rerun` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/automations/skip` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/checks` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/checks/{checkId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/comments` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/cards/{card}/comments/{commentId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/code-links` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/cards/{card}/code-links/{linkId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/commits` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/commits` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/cards/{card}/commits/{sha}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/commits/{sha}/diff` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/context` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/launch` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/links` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/links/candidates` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/cards/{card}/links/{linkedCard}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/merge` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/move` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/notes` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/notes` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/reviews` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/reviews/preview` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/reviews/settings` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/cards/{card}/reviews/settings` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/reviews/{reviewId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/sessions` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/cards/{card}/sessions` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/cards/{card}/sessions/{sessionId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/cards/{card}/sessions/{sessionId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/cards/{card}/sharing-links` | [BoardRoutes.CardSharing.cs](VibeRails/Routes/BoardRoutes.CardSharing.cs) |
| POST | `/api/v1/board/cards/{card}/sharing-links` | [BoardRoutes.CardSharing.cs](VibeRails/Routes/BoardRoutes.CardSharing.cs) |
| POST | `/api/v1/board/cards/{card}/sharing-links/refresh` | [BoardRoutes.CardSharing.cs](VibeRails/Routes/BoardRoutes.CardSharing.cs) |
| DELETE | `/api/v1/board/cards/{card}/sharing-links/{id:int}` | [BoardRoutes.CardSharing.cs](VibeRails/Routes/BoardRoutes.CardSharing.cs) |
| PATCH | `/api/v1/board/cards/{card}/sharing-links/{id:int}` | [BoardRoutes.CardSharing.cs](VibeRails/Routes/BoardRoutes.CardSharing.cs) |
| PUT | `/api/v1/board/cards/{card}/sharing-links/{id:int}/access` | [BoardRoutes.CardSharing.cs](VibeRails/Routes/BoardRoutes.CardSharing.cs) |
| GET | `/api/v1/board/columns` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/columns` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/columns/order` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| DELETE | `/api/v1/board/columns/{columnId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/columns/{columnId}` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/columns/{columnId}/automation` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| PUT | `/api/v1/board/columns/{columnId}/automation` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/columns/{columnId}/automation/running` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/files` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/board/local-cards/{card}` | [BoardRoutes.Search.cs](VibeRails/Routes/BoardRoutes.Search.cs) |
| PUT | `/api/v1/board/local-cards/{card}` | [BoardRoutes.Search.cs](VibeRails/Routes/BoardRoutes.Search.cs) |
| POST | `/api/v1/board/local-cards/{card}/comments` | [BoardRoutes.Search.cs](VibeRails/Routes/BoardRoutes.Search.cs) |
| POST | `/api/v1/board/local-cards/{card}/links` | [BoardRoutes.Search.cs](VibeRails/Routes/BoardRoutes.Search.cs) |
| GET | `/api/v1/board/local-cards/{card}/links/candidates` | [BoardRoutes.Search.cs](VibeRails/Routes/BoardRoutes.Search.cs) |
| DELETE | `/api/v1/board/local-cards/{card}/links/{linkedCard}` | [BoardRoutes.Search.cs](VibeRails/Routes/BoardRoutes.Search.cs) |
| POST | `/api/v1/board/local-cards/{card}/move` | [BoardRoutes.Search.cs](VibeRails/Routes/BoardRoutes.Search.cs) |
| GET | `/api/v1/board/remote` | [BoardRoutes.Remote.cs](VibeRails/Routes/BoardRoutes.Remote.cs) |
| POST | `/api/v1/board/remote` | [BoardRoutes.Remote.cs](VibeRails/Routes/BoardRoutes.Remote.cs) |
| PUT | `/api/v1/board/remote/{id}` | [BoardRoutes.Remote.cs](VibeRails/Routes/BoardRoutes.Remote.cs) |
| POST | `/api/v1/board/remote/{id}/delete` | [BoardRoutes.Remote.cs](VibeRails/Routes/BoardRoutes.Remote.cs) |
| GET | `/api/v1/board/shared` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| POST | `/api/v1/board/shared/{remoteId}/import` | [BoardRoutes.cs](VibeRails/Routes/BoardRoutes.cs) |
| GET | `/api/v1/chatHistory` | [ChatHistoryRoutes.cs](VibeRails/Routes/ChatHistoryRoutes.cs) |
| DELETE | `/api/v1/chatHistory/{sessionId}` | [ChatHistoryRoutes.cs](VibeRails/Routes/ChatHistoryRoutes.cs) |
| GET | `/api/v1/chatHistory/{sessionId}` | [ChatHistoryRoutes.cs](VibeRails/Routes/ChatHistoryRoutes.cs) |
| PATCH | `/api/v1/chatHistory/{sessionId}` | [ChatHistoryRoutes.cs](VibeRails/Routes/ChatHistoryRoutes.cs) |
| GET | `/api/v1/chatHistory/{sessionId}/raw-session` | [ChatHistoryRoutes.cs](VibeRails/Routes/ChatHistoryRoutes.cs) |
| GET | `/api/v1/chatHistory/{sessionId}/replay` | [ChatHistoryRoutes.cs](VibeRails/Routes/ChatHistoryRoutes.cs) |
| GET | `/api/v1/chatHistory/{sessionId}/summary` | [ChatHistoryRoutes.cs](VibeRails/Routes/ChatHistoryRoutes.cs) |
| GET | `/api/v1/chatHistory/{sessionId}/terminal-replay` | [ChatHistoryRoutes.cs](VibeRails/Routes/ChatHistoryRoutes.cs) |
| GET | `/api/v1/chatHistory/{sessionId}/transcript` | [ChatHistoryRoutes.cs](VibeRails/Routes/ChatHistoryRoutes.cs) |
| GET | `/api/v1/claude/settings/{envName}` | [LlmSettingsRoutes.cs](VibeRails/Routes/LlmSettingsRoutes.cs) |
| PUT | `/api/v1/claude/settings/{envName}` | [LlmSettingsRoutes.cs](VibeRails/Routes/LlmSettingsRoutes.cs) |
| POST | `/api/v1/cli/launch/vscode` | [CliLaunchRoutes.cs](VibeRails/Routes/CliLaunchRoutes.cs) |
| POST | `/api/v1/cli/launch/{cli}` | [CliLaunchRoutes.cs](VibeRails/Routes/CliLaunchRoutes.cs) |
| POST | `/api/v1/code-analyzer` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| GET | `/api/v1/code-analyzer/changes` | [CodeGraphRoutes.cs](VibeRails/Routes/CodeGraphRoutes.cs) |
| GET | `/api/v1/code-analyzer/changes/diff` | [CodeGraphRoutes.cs](VibeRails/Routes/CodeGraphRoutes.cs) |
| POST | `/api/v1/code-analyzer/graph` | [CodeGraphRoutes.cs](VibeRails/Routes/CodeGraphRoutes.cs) |
| DELETE | `/api/v1/code-analyzer/ignores` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| GET | `/api/v1/code-analyzer/ignores` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| POST | `/api/v1/code-analyzer/ignores` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| POST | `/api/v1/code-analyzer/ignores/bulk` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| GET | `/api/v1/code-analyzer/source` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| GET | `/api/v1/codex/settings/{envName}` | [LlmSettingsRoutes.cs](VibeRails/Routes/LlmSettingsRoutes.cs) |
| PUT | `/api/v1/codex/settings/{envName}` | [LlmSettingsRoutes.cs](VibeRails/Routes/LlmSettingsRoutes.cs) |
| GET | `/api/v1/compression/catalog` | [CompressionRoutes.cs](VibeRails/Routes/CompressionRoutes.cs) |
| POST | `/api/v1/compression/preview` | [CompressionRoutes.cs](VibeRails/Routes/CompressionRoutes.cs) |
| GET | `/api/v1/context` | [ProjectRoutes.cs](VibeRails/Routes/ProjectRoutes.cs) |
| GET | `/api/v1/environments` | [EnvironmentRoutes.cs](VibeRails/Routes/EnvironmentRoutes.cs) |
| POST | `/api/v1/environments` | [EnvironmentRoutes.cs](VibeRails/Routes/EnvironmentRoutes.cs) |
| POST | `/api/v1/environments/steps/test` | [EnvironmentStepRoutes.cs](VibeRails/Routes/EnvironmentStepRoutes.cs) |
| DELETE | `/api/v1/environments/{name}` | [EnvironmentRoutes.cs](VibeRails/Routes/EnvironmentRoutes.cs) |
| GET | `/api/v1/environments/{name}` | [EnvironmentRoutes.cs](VibeRails/Routes/EnvironmentRoutes.cs) |
| PUT | `/api/v1/environments/{name}` | [EnvironmentRoutes.cs](VibeRails/Routes/EnvironmentRoutes.cs) |
| GET | `/api/v1/environments/{name}/launch` | [CliLaunchRoutes.cs](VibeRails/Routes/CliLaunchRoutes.cs) |
| ANY (WebSocket) | `/api/v1/events/ws` | [AppNotificationRoutes.cs](VibeRails/Routes/AppNotificationRoutes.cs); [WebSocketHandler.cs](VibeRails/Routes/WebSocketHandler.cs) |
| GET | `/api/v1/filesystem/entries` | [FileSystemRoutes.cs](VibeRails/Routes/FileSystemRoutes.cs) |
| POST | `/api/v1/git/init` | [ProjectRoutes.cs](VibeRails/Routes/ProjectRoutes.cs) |
| POST | `/api/v1/git/open-directory` | [ProjectRoutes.cs](VibeRails/Routes/ProjectRoutes.cs) |
| POST | `/api/v1/git/preflight/console` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| POST | `/api/v1/git/preflight/stream` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| DELETE | `/api/v1/hooks` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| POST | `/api/v1/hooks/install` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| POST | `/api/v1/hooks/preview` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| GET | `/api/v1/hooks/status` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| POST | `/api/v1/hooks/validate` | [HookRoutes.cs](VibeRails/Routes/HookRoutes.cs) |
| GET | `/api/v1/http-relay/test/posts` | [HttpRelayRoutes.cs](VibeRails/Routes/HttpRelayRoutes.cs) |
| POST | `/api/v1/http-relay/test/posts` | [HttpRelayRoutes.cs](VibeRails/Routes/HttpRelayRoutes.cs) |
| DELETE | `/api/v1/http-relay/test/posts/{id:int}` | [HttpRelayRoutes.cs](VibeRails/Routes/HttpRelayRoutes.cs) |
| GET | `/api/v1/http-relay/test/posts/{id:int}` | [HttpRelayRoutes.cs](VibeRails/Routes/HttpRelayRoutes.cs) |
| PUT | `/api/v1/http-relay/test/posts/{id:int}` | [HttpRelayRoutes.cs](VibeRails/Routes/HttpRelayRoutes.cs) |
| GET | `/api/v1/internal/logs` | [InternalToolsRoutes.cs](VibeRails/Routes/InternalToolsRoutes.cs) |
| GET | `/api/v1/internal/uploads` | [InternalToolsRoutes.cs](VibeRails/Routes/InternalToolsRoutes.cs) |
| GET | `/api/v1/jobs` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| POST | `/api/v1/jobs` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| GET | `/api/v1/jobs/catalog` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| POST | `/api/v1/jobs/import` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| GET | `/api/v1/jobs/runs` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| POST | `/api/v1/jobs/runs/delete` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| GET | `/api/v1/jobs/runs/summary` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| DELETE | `/api/v1/jobs/runs/{runId}` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| GET | `/api/v1/jobs/runs/{runId}` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| POST | `/api/v1/jobs/runs/{runId}/cancel` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| POST | `/api/v1/jobs/runs/{runId}/retry` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| GET | `/api/v1/jobs/scripts` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| DELETE | `/api/v1/jobs/{id:long}` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| GET | `/api/v1/jobs/{id:long}` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| PUT | `/api/v1/jobs/{id:long}` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| POST | `/api/v1/jobs/{id:long}/run` | [JobRoutes.cs](VibeRails/Routes/JobRoutes.cs) |
| POST | `/api/v1/lifecycle/disconnect` | [LifecycleRoutes.cs](VibeRails/Routes/LifecycleRoutes.cs) |
| POST | `/api/v1/lifecycle/ping` | [LifecycleRoutes.cs](VibeRails/Routes/LifecycleRoutes.cs) |
| DELETE | `/api/v1/llm-picker/preferences` | [LlmPickerRoutes.cs](VibeRails/Routes/LlmPickerRoutes.cs) |
| GET | `/api/v1/llm-picker/preferences` | [LlmPickerRoutes.cs](VibeRails/Routes/LlmPickerRoutes.cs) |
| PUT | `/api/v1/llm-picker/preferences` | [LlmPickerRoutes.cs](VibeRails/Routes/LlmPickerRoutes.cs) |
| POST | `/api/v1/mcp/inspect` | [McpRoutes.cs](VibeRails/Routes/McpRoutes.cs) |
| GET | `/api/v1/mcp/status` | [McpRoutes.cs](VibeRails/Routes/McpRoutes.cs) |
| GET | `/api/v1/mcp/tools` | [McpRoutes.cs](VibeRails/Routes/McpRoutes.cs) |
| POST | `/api/v1/mcp/tools/{name}` | [McpRoutes.cs](VibeRails/Routes/McpRoutes.cs) |
| GET | `/api/v1/pr-reviews` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| POST | `/api/v1/pr-reviews` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| POST | `/api/v1/pr-reviews/github/sync` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| GET | `/api/v1/pr-reviews/queue` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| GET | `/api/v1/pr-reviews/settings` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| PUT | `/api/v1/pr-reviews/settings` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| DELETE | `/api/v1/pr-reviews/reviews/{id:long}` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| PUT | `/api/v1/pr-reviews/reviews/{id:long}` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| DELETE | `/api/v1/pr-reviews/{number:int}` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| GET | `/api/v1/pr-reviews/{number:int}` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| PUT | `/api/v1/pr-reviews/{number:int}` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| POST | `/api/v1/pr-reviews/{number:int}/reviews/complete` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| POST | `/api/v1/pr-reviews/{number:int}/reviews/draft` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| POST | `/api/v1/pr-reviews/{number:int}/reviews/start` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| POST | `/api/v1/pr-reviews/{number:int}/reviews/{id:long}/publish` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| POST | `/api/v1/pr-reviews/{number:int}/legacy-conflict/resolve` | [PrReviewRoutes.cs](VibeRails/Routes/PrReviewRoutes.cs) |
| GET | `/api/v1/projects/name` | [ProjectRoutes.cs](VibeRails/Routes/ProjectRoutes.cs) |
| PUT | `/api/v1/projects/name` | [ProjectRoutes.cs](VibeRails/Routes/ProjectRoutes.cs) |
| POST | `/api/v1/push/send` | [PushRoutes.cs](VibeRails/Routes/PushRoutes.cs) |
| DELETE | `/api/v1/python-scripts` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| GET | `/api/v1/python-scripts` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/approve` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| GET | `/api/v1/python-scripts/content` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/content` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/create` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/import` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/pin` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/rename` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/revoke` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/run` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/run-pin` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/run/interactive` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| GET | `/api/v1/python-scripts/run/terminal/ws` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| GET | `/api/v1/python-scripts/runs` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| POST | `/api/v1/python-scripts/settings` | [PythonScriptRoutes.cs](VibeRails/Routes/PythonScriptRoutes.cs) |
| GET | `/api/v1/review-requests` | [ReviewRequestRoutes.cs](VibeRails/Routes/ReviewRequestRoutes.cs) |
| POST | `/api/v1/review-requests` | [ReviewRequestRoutes.cs](VibeRails/Routes/ReviewRequestRoutes.cs) |
| GET | `/api/v1/review-requests/commit-approvals` | [ReviewRequestRoutes.cs](VibeRails/Routes/ReviewRequestRoutes.cs) |
| GET | `/api/v1/review-requests/staged` | [ReviewRequestRoutes.cs](VibeRails/Routes/ReviewRequestRoutes.cs) |
| DELETE | `/api/v1/review-requests/{id:guid}` | [ReviewRequestRoutes.cs](VibeRails/Routes/ReviewRequestRoutes.cs) |
| GET | `/api/v1/review-requests/{id:guid}` | [ReviewRequestRoutes.cs](VibeRails/Routes/ReviewRequestRoutes.cs) |
| POST | `/api/v1/review-requests/{id:guid}/cancel` | [ReviewRequestRoutes.cs](VibeRails/Routes/ReviewRequestRoutes.cs) |
| POST | `/api/v1/review-requests/{id:guid}/decisions` | [ReviewRequestRoutes.cs](VibeRails/Routes/ReviewRequestRoutes.cs) |
| GET | `/api/v1/review-requests/{id:guid}/disclosure` | [ReviewRequestRoutes.cs](VibeRails/Routes/ReviewRequestRoutes.cs) |
| POST | `/api/v1/review-requests/{id:guid}/send` | [ReviewRequestRoutes.cs](VibeRails/Routes/ReviewRequestRoutes.cs) |
| GET | `/api/v1/rules` | [RulesRoutes.cs](VibeRails/Routes/RulesRoutes.cs) |
| GET | `/api/v1/rules/details` | [RulesRoutes.cs](VibeRails/Routes/RulesRoutes.cs) |
| GET | `/api/v1/sandboxes` | [SandboxRoutes.cs](VibeRails/Routes/SandboxRoutes.cs) |
| POST | `/api/v1/sandboxes` | [SandboxRoutes.cs](VibeRails/Routes/SandboxRoutes.cs) |
| DELETE | `/api/v1/sandboxes/{id:int}` | [SandboxRoutes.cs](VibeRails/Routes/SandboxRoutes.cs) |
| GET | `/api/v1/sandboxes/{id:int}/diff` | [SandboxRoutes.cs](VibeRails/Routes/SandboxRoutes.cs) |
| POST | `/api/v1/sandboxes/{id:int}/launch/shell` | [SandboxRoutes.cs](VibeRails/Routes/SandboxRoutes.cs) |
| POST | `/api/v1/sandboxes/{id:int}/launch/vscode` | [SandboxRoutes.cs](VibeRails/Routes/SandboxRoutes.cs) |
| POST | `/api/v1/sandboxes/{id:int}/launch/{cli}` | [SandboxRoutes.cs](VibeRails/Routes/SandboxRoutes.cs) |
| POST | `/api/v1/sandboxes/{id:int}/merge` | [SandboxRoutes.cs](VibeRails/Routes/SandboxRoutes.cs) |
| POST | `/api/v1/sandboxes/{id:int}/push` | [SandboxRoutes.cs](VibeRails/Routes/SandboxRoutes.cs) |
| POST | `/api/v1/search` | [SearchRoutes.cs](VibeRails/Routes/SearchRoutes.cs) |
| GET | `/api/v1/session-replay/sessions` | [SessionReplayRoutes.cs](VibeRails/Routes/SessionReplayRoutes.cs) |
| GET | `/api/v1/session-replay/sessions/{id}` | [SessionReplayRoutes.cs](VibeRails/Routes/SessionReplayRoutes.cs) |
| GET | `/api/v1/session-replay/sessions/{id}/changes/{changeId:long}` | [SessionReplayRoutes.cs](VibeRails/Routes/SessionReplayRoutes.cs) |
| GET | `/api/v1/session-replay/sessions/{id}/exchanges` | [SessionReplayRoutes.cs](VibeRails/Routes/SessionReplayRoutes.cs) |
| GET | `/api/v1/session-replay/sessions/{id}/exchanges/{exchangeId}` | [SessionReplayRoutes.cs](VibeRails/Routes/SessionReplayRoutes.cs) |
| GET | `/api/v1/session-replay/sessions/{id}/frames` | [SessionReplayRoutes.cs](VibeRails/Routes/SessionReplayRoutes.cs) |
| GET | `/api/v1/session-replay/status` | [SessionReplayRoutes.cs](VibeRails/Routes/SessionReplayRoutes.cs) |
| GET | `/api/v1/session-sharing/capabilities` | [SessionSharingRoutes.cs](VibeRails/Routes/SessionSharingRoutes.cs) |
| GET | `/api/v1/sessions/recent` | [SessionRoutes.cs](VibeRails/Routes/SessionRoutes.cs) |
| POST | `/api/v1/sessions/{sessionId:guid}/sharing-links` | [SessionSharingRoutes.cs](VibeRails/Routes/SessionSharingRoutes.cs) |
| GET | `/api/v1/sessions/{sessionId}/inputs` | [SessionRoutes.cs](VibeRails/Routes/SessionRoutes.cs) |
| GET | `/api/v1/sessions/{sessionId}/logs` | [SessionRoutes.cs](VibeRails/Routes/SessionRoutes.cs) |
| GET | `/api/v1/sessions/{sessionId}/output` | [SessionRoutes.cs](VibeRails/Routes/SessionRoutes.cs) |
| GET | `/api/v1/settings` | [AppSettingsRoutes.cs](VibeRails/Routes/AppSettingsRoutes.cs) |
| POST | `/api/v1/settings` | [AppSettingsRoutes.cs](VibeRails/Routes/AppSettingsRoutes.cs) |
| GET | `/api/v1/settings/security-challenge` | [AppSettingsRoutes.cs](VibeRails/Routes/AppSettingsRoutes.cs) |
| GET | `/api/v1/settings/backups` | [DataExportRoutes.cs](VibeRails/Routes/DataExportRoutes.cs) |
| POST | `/api/v1/settings/computer-name` | [AppSettingsRoutes.cs](VibeRails/Routes/AppSettingsRoutes.cs) |
| GET | `/api/v1/settings/keys` | [SigningKeyRoutes.cs](VibeRails/Routes/SigningKeyRoutes.cs) |
| POST | `/api/v1/settings/keys` | [SigningKeyRoutes.cs](VibeRails/Routes/SigningKeyRoutes.cs) |
| POST | `/api/v1/settings/keys/{id:guid}/export` | [SigningKeyRoutes.cs](VibeRails/Routes/SigningKeyRoutes.cs) |
| POST | `/api/v1/settings/keys/{id:guid}/sign` | [SigningKeyRoutes.cs](VibeRails/Routes/SigningKeyRoutes.cs) |
| POST | `/api/v1/settings/keys/{id:guid}/sync` | [SigningKeyRoutes.cs](VibeRails/Routes/SigningKeyRoutes.cs) |
| DELETE | `/api/v1/settings/pin` | [PinRoutes.cs](VibeRails/Routes/PinRoutes.cs) |
| POST | `/api/v1/settings/pin` | [PinRoutes.cs](VibeRails/Routes/PinRoutes.cs) |
| GET | `/api/v1/settings/pin/status` | [PinRoutes.cs](VibeRails/Routes/PinRoutes.cs) |
| GET | `/api/v1/setup/install/ws` | [SetupRoutes.cs](VibeRails/Routes/SetupRoutes.cs) |
| GET | `/api/v1/setup/status` | [SetupRoutes.cs](VibeRails/Routes/SetupRoutes.cs) |
| POST | `/api/v1/setup/status/refresh` | [SetupRoutes.cs](VibeRails/Routes/SetupRoutes.cs) |
| DELETE | `/api/v1/settings/remote-link` | [RemoteAccountLinkRoutes.cs](VibeRails/Routes/RemoteAccountLinkRoutes.cs) |
| GET | `/api/v1/settings/remote-link` | [RemoteAccountLinkRoutes.cs](VibeRails/Routes/RemoteAccountLinkRoutes.cs) |
| POST | `/api/v1/settings/remote-link` | [RemoteAccountLinkRoutes.cs](VibeRails/Routes/RemoteAccountLinkRoutes.cs) |
| POST | `/api/v1/shutdown` | [LifecycleRoutes.cs](VibeRails/Routes/LifecycleRoutes.cs) |
| POST | `/api/v1/terminal/input` | [TerminalRoutes.cs](VibeRails/Routes/TerminalRoutes.cs) |
| GET | `/api/v1/terminal/snapshot` | [TerminalRoutes.cs](VibeRails/Routes/TerminalRoutes.cs) |
| POST | `/api/v1/terminal/start` | [TerminalRoutes.cs](VibeRails/Routes/TerminalRoutes.cs) |
| GET | `/api/v1/terminal/status` | [TerminalRoutes.cs](VibeRails/Routes/TerminalRoutes.cs) |
| POST | `/api/v1/terminal/stop` | [TerminalRoutes.cs](VibeRails/Routes/TerminalRoutes.cs) |
| GET | `/api/v1/terminal/tabs` | [TerminalTabsRoutes.cs](VibeRails/Routes/TerminalTabsRoutes.cs) |
| POST | `/api/v1/terminal/tabs` | [TerminalTabsRoutes.cs](VibeRails/Routes/TerminalTabsRoutes.cs) |
| DELETE | `/api/v1/terminal/tabs/{tabId}` | [TerminalTabsRoutes.cs](VibeRails/Routes/TerminalTabsRoutes.cs) |
| POST | `/api/v1/terminal/tabs/{tabId}/start` | [TerminalTabsRoutes.cs](VibeRails/Routes/TerminalTabsRoutes.cs) |
| GET | `/api/v1/terminal/tabs/{tabId}/status` | [TerminalTabsRoutes.cs](VibeRails/Routes/TerminalTabsRoutes.cs) |
| POST | `/api/v1/terminal/tabs/{tabId}/stop` | [TerminalTabsRoutes.cs](VibeRails/Routes/TerminalTabsRoutes.cs) |
| ANY (WebSocket) | `/api/v1/terminal/tabs/{tabId}/ws` | [TerminalTabsRoutes.cs](VibeRails/Routes/TerminalTabsRoutes.cs) |
| ANY (WebSocket) | `/api/v1/terminal/ws` | [TerminalRoutes.cs](VibeRails/Routes/TerminalRoutes.cs) |
| GET | `/api/v1/token-savings` | [TokenSavingsRoutes.cs](VibeRails/Routes/TokenSavingsRoutes.cs) |
| GET | `/api/v1/update/check` | [UpdateRoutes.cs](VibeRails/Routes/UpdateRoutes.cs) |
| GET | `/api/v1/update/version` | [UpdateRoutes.cs](VibeRails/Routes/UpdateRoutes.cs) |
| GET | `/api/v1/version` | [UpdateRoutes.cs](VibeRails/Routes/UpdateRoutes.cs) |
| GET | `/auth/bootstrap` | [AuthRoutes.cs](VibeRails/Routes/AuthRoutes.cs) |
| GET | `/git-guard` | [Program.cs](VibeRails/Program.cs) |
| GET | `/health` | [ProjectRoutes.cs](VibeRails/Routes/ProjectRoutes.cs) |
| ANY | `/llm/anthropic/{**rest}` | [LlmAnthropicProxyRoutes.cs](TokenSaver/LlmAnthropicProxyRoutes.cs) |
| ANY | `/llm/cli-chat/{**rest}` | [LlmCliChatProxyRoutes.cs](TokenSaver/LlmCliChatProxyRoutes.cs) |
| POST | `/llm/control/agent/end-session` | [AgentSessionControlRoutes.cs](VibeRails/Routes/AgentSessionControlRoutes.cs) |
| POST | `/llm/control/token-saver/pause` | [TokenSaverPauseRoutes.cs](VibeRails/Routes/TokenSaverPauseRoutes.cs) |
| POST | `/llm/control/token-saver/resume` | [TokenSaverPauseRoutes.cs](VibeRails/Routes/TokenSaverPauseRoutes.cs) |
| GET | `/llm/control/token-saver/status` | [TokenSaverPauseRoutes.cs](VibeRails/Routes/TokenSaverPauseRoutes.cs) |
| ANY | `/llm/openai/{**rest}` | [LlmProxyRoutes.cs](TokenSaver/LlmProxyRoutes.cs) |
| ANY | `/llm/xai/{**rest}` | [LlmXaiProxyRoutes.cs](TokenSaver/LlmXaiProxyRoutes.cs) |
| ANY | `/llm/zai/{**rest}` | [LlmZaiProxyRoutes.cs](TokenSaver/LlmZaiProxyRoutes.cs) |
| GET / POST / DELETE | `/mcp` | [Program.cs](VibeRails/Program.cs) |

## Capability and data boundaries

Exact method/path mappings live in [Routes](VibeRails/Routes/Routes.cs), its route classes,
[Program.cs](VibeRails/Program.cs) and [TokenSaver](TokenSaver). Resolve grouped paths,
constants, partial classes and the inherited event-WebSocket mapping during review. The
[endpoint inventory](#endpoint-inventory) above lists the current mappings and their sources.

### Board, search and stored evidence

See [Board instructions](VibeRails/Services/Board/AGENTS.md),
[BoardRoutes](VibeRails/Routes/BoardRoutes.cs) and
[local search routes](VibeRails/Routes/BoardRoutes.Search.cs).

- All Board REST routes are active-root-only and require both credentials. Ordinary routes
  derive the project from the dashboard root, never a request path; board, lane, card and child
  records are checked within that scope. Merge/move checks run in the write transaction.
- GitHub card links accept only bounded HTTPS `github.com/owner/repo/pull/number` or
  `/commit/sha` references. They are never fetched server-side. Labels are derived from the
  canonical URL; the UI escapes text and uses `noopener noreferrer`. Session exports include
  saved URLs and their attached card keys/titles, never unrelated card content. Public replay
  explicitly projects these fields; normal share audience checks still apply.
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
  Public card shares use that same project-filtered related-card projection.
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

The six PR review tools (`get_pr_review_queue`, `get_pr_review`, `start_pr_review`,
`save_pr_review_draft`, `complete_pr_review`, `retry_pr_review_publish`) are on both transports and outside the Board
auto-grants. They resolve the project through `IBoardProjectResolver`, take no repository or
path argument, and persist results/settings/deliveries in `pr_reviews.db`. Completion/retry can
publish commit-pinned reviews through authenticated `gh` when the reviewer switch is on; see
the outbound contract below. No caller-supplied repository or credential reaches publishing.
Legacy adoption reads `state.db` through a separate read-only connection. List responses include
project-scoped conflicting histories, escaped as untrusted text in Reviews. Resolution requires
both credentials, the current repository, and matching source/target content-version tokens;
stale choices return 409. Both snapshots remain archived locally. No paths or SQL are returned.

`request_review` (VB-50 slice E, both transports, outside the Board auto-grants) takes reviewer
emails, an optional note and card key, never a session id, a path or a credential. It calls only
the root's `POST /api/v1/review-requests` and `GET /{id}/disclosure` with the inherited
`VIBERAILS_TOOL_*` credentials over loopback (no redirects, cookies or proxies; 60 s; 64 KiB and
1 MiB response bounds), attaches the calling session only, and never sends, decides or signs:
nothing leaves the computer until a person confirms the disclosure on the card. Those two
operations are the only review-request routes a tool contact may reach (`AgentCredentialService.Allows`),
and the route itself refuses (403 `forbidden`) a contact that names any other session or reads a
request that does not attach its own session (Codex slice E review, F1). The root's refusals
are relayed with their code; unparsed answers report only the status; exception text is never
relayed. See [Request review from a card](#request-review-from-a-card-and-request_review-vb-50-slice-e).

Board Start work and Board-triggered Workers use the exact per-tool grants in
[BoardMcpAuthorization](VibeRails/Services/Terminal/Commands/BoardMcpAuthorization.cs).
Other launches default off; card text/environment names cannot grant tools. No server wildcard,
unrelated tool, shared-config permission change or sandbox bypass comes from this grant path.
Native policy/deny rules still apply; Antigravity has prompt-only authorization because a narrow
native grant is unverified. The explicit, default-off base-CLI YOLO option is a separate user
choice; saved environments retain their own arguments. Resolve environments by ID and project
visibility, with no name fallback. Launch options use argv and `ShellArgSanitizer`.
`AuthorizeBoardTools` is excluded from JSON in both directions; only the authenticated
root-to-tab-child request carries the server's decision in a private header. CLI contacts
cannot call that child start route. Positional Claude/Codex/Grok prompts follow `--`; CLIs
with named prompt options reject initial messages whose first non-whitespace character is `-`.

Board MCP assignment parity (2026-10-10): create/update accept the UI's typed base launch
options. `list_board_agent_options` and `start_board_agent` use inherited root tool API
credentials to call the existing picker and card-launch routes. Both are explicit Board
grants on both transports. The relay accepts only a loopback base, sends both credentials,
disables redirects/proxies/cookies, and bounds time and response size. Start takes only a
card identifier and uses its saved assignment; the root enforces its current project and
ordinary launch validation. No caller-supplied executable, argv, project path or credential
is accepted. Uncertain launch results require a status check, never an automatic retry.

Desktop MCP presence uses server-generated connection identities and bounded local leases
in `board.db`, through `IBoardStore`. It adds no endpoint or listener. Same-project writes or
explicit attachment can mark activity; reads cannot. No fake terminal recording, launch grant,
public replay or cross-project attachment is created. Stateless HTTP app-name aggregates are
not client ownership and cannot be cleared as another caller's completion. Presence projects
only a boolean into Board responses and is excluded from hosted sync.

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

Settings writes and all remote-PIN routes are root-only. Account-key replacement/removal,
Remote Access and relay toggle changes, and PIN set/clear require the current PIN. Without
a configured PIN, the dashboard shows a random confirmation code that expires after two
minutes and is consumed once. Five failed proofs impose a five-minute process-wide cooldown;
issuing another challenge does not reset it. Scoped CLI credentials cannot obtain a challenge
or perform these writes. This is dashboard confirmation, not protection against arbitrary
same-origin JavaScript executing with the dashboard's full authority. Responses do not echo
proofs; key changes emit a fixed event and warning with no credential. Account linking retains
its separate explicit hosted approval flow.

Approved Python script discovery starts in the installed scripts directory and excludes
repository/active-venv and PYBRIDGE_PYTHON overrides. Visible Windows shell launches resolve
an absolute pwsh.exe from absolute PATH entries and change directory inside the quoted script;
VS Code uses its resolved application executable. Repository folders never select these programs.

The root-only script terminal WebSocket (`/api/v1/python-scripts/run/terminal/ws`) uses the
existing authenticated Kestrel listener and both credentials, with no middleware exception.
Its bounded first message carries a registered script ID, dimensions and optional run PIN;
verified bytes and discrete interpreter argv are resolved server-side before launch. Binary input
is never routed to session recording, observers or remote sharing. Stop/disconnect kills the
process tree; no input/output or PIN is persisted by this private runner.

**Setup installer (prototype, awaiting owner review).** The root-only `/api/v1/setup/install/ws`
shares that private PTY lifetime (`PrivatePtySession`). Its bounded first message carries only a
tool ID, a method ID and dimensions. The command is a compile-time constant from
`SetupToolCatalog`, looked up by ordinal ID for the current OS; unknown pairs are refused before
any process starts. This is a deliberate exception to argv-only launching: vendor installers are
pipelines (`irm … | iex`, `curl … | bash`), so the constant runs as one `-Command`/`-c` argument
of a fixed shell (pwsh, else `%SystemRoot%` Windows PowerShell 5.1, else the Unix login shell).
No request field reaches that text. The capability is "download and run a fixed vendor installer
as the local user", the same as the user pasting the shown command. Status/refresh probe only
file existence and file version metadata, and start no processes. Results live in GlobalCache.

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
  interpreter and argv. Approved Python runs use isolated mode (`-I`); scripts must be
  self-contained. Signing-PIN checks share a process-wide five-failure/five-minute cooldown.
  First signing-PIN setup requires dashboard presence. Create/restore stay inside UserScripts
  or the server-derived current repository; saves and renames outside those roots require the
  signing PIN. Import alone grants neither approval nor permission to write the file.
- Card Automation requests resolve card/job in the server's project and recheck enabled state,
  project and overlap in the run transaction. Lane skip matches the current card/job/event pair;
  stale requests conflict. Waiting skips and their receipts commit atomically; running skips
  request cancellation and successors wait for terminal run state. Skips are user-only status
  decisions. `report_automation_step` uses the inherited linked session and current Automation
  run; fixing may come from a linked coding session. Code-review pass requires that session’s
  complete saved review with matching captured inputs. Progress cannot reverse a passed/skipped
  receipt. The tool is in the exact Board launch grant list on both transports.
- Root-only session replay is read-only/no-store, scopes details to the exact session ID and uses
  SQLite ReadOnly/query_only. Terminal snapshots remain behind both credentials, including retained
  screens after PTY exit (up to 20,000 scrollback lines); input rejects completed sessions.
- Root-only diagnostics use fixed source whitelists and reject linked log directories/files.
  **Accepted exposure (2026-09-06):** Serilog application/legacy daemon tails are served verbatim
  to holders of both credentials. Only `IFeatureLog` is the redacted channel. Never log API keys,
  tokens or transcript text through Serilog, including exception text; a hidden Logs panel is no
  protection. See [InternalToolsRoutes](VibeRails/Routes/InternalToolsRoutes.cs).
  **Owner-approved MCP diagnostic exception (2026-10-06, VIBE-108):** retain complete tool
  names, error replies and exception details in `McpClientService` logs, without redaction or
  truncation. Escape CR/LF, terminal controls and Unicode display controls so the values cannot
  forge separate records. These diagnostics can contain sensitive text supplied by a tool;
  the owner explicitly accepted that retention. Tool-call arguments are not newly logged.

- Rules-page reads and writes accept only absolute `vc.rules.md` paths inside the current
  repository. Reject Git metadata, symlinks/junctions and ambiguous Windows path components
  before touching the file or custom-name metadata. Creation uses CreateNew to avoid replacing
  existing data. This is a path boundary, not isolation from another local process racing a
  filesystem change.

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

Commit approvals (VCA `Human verified` / `Signed Approval`, 2026-10-20) are domain-separated: the
Sign route also refuses payloads starting `VibeRails human-verified commit v1\n`, and no MCP tool
signs. Only the Git Guard signing popup (`vb --vca-hook sign-commit-msg`, a CREATE_NEW_CONSOLE
window, never a route or listener) signs that statement, after reading the key password from its
own console with echo off and refusing redirected input. The commit-msg hook then sends the key id,
fingerprint, the tree statement (a tree id, no repository content) and the signature to the fixed
`https://viberails.ai/public/api/v1/signatures/verify`: no API key, redirects or cookies, a 15-second
deadline and a 16-KiB response bound. It fails closed when the service cannot answer and never
relays exception text. A process that already runs as the user and knows the password, or drives
the popup through UI automation, is outside this boundary; see the
[VCA guide](VibeRails/Services/VCA/AGENTS.md#signed-commits-human-verified-and-signed-approval-2026-10-20).

### Review requests before a commit (VB-50, slice A)

Root-only `/api/v1/review-requests` requires both credentials and answers no-store. Creation reads
the project's staged index through the hook's snapshot reader (project derived server-side, never
from the request), bounds text at 256 KiB per file and 4 MiB per bundle, validates reviewer emails
(at most ten) and attached session ids (at most ten, looked up locally), and stores the bundle in
state.db (`review-requests/1`, additive). Bodies are capped at 16 KiB (create) and 256 KiB
(decisions: a trailer, a 4,000-character comment and, since slice F, up to 50 line comments of
1,000 characters holding at most 16,000 characters of paths and text together, each
`{ path, line, side, text }` with the path 1 to 4,096 characters without NUL, the line 1 to
1,000,000, the side `before` or `after`, and the text cleaned of control, format and separator
characters before storage); refusals carry fixed local wording and a code, never exception text. Of these routes
only the decisions route calls out, to the existing public signature verification above with a
tree id and no content, and an approval is stored only when that verification names a verified signer listed on the
request; the store records it and recomputes the status in one write transaction, so a cancel that
lands during verification wins and a cancelled request is never revived. Deleted files' base text
is read from the frozen base tree by path, under the same bounds, never from the working tree. The
commit-msg hook applies a stored, verified approval to the message before offering the
popup and then rechecks it like any other trailer; the ledger is resolved only when a signature is
pending, the read is bounded to three seconds and quiet on failure, so it can place a signature but
never pass a commit by itself. Hook test harnesses replace the ledger with a per-repository file
beside their job store, so no test opens the user's state.db. One MCP tool, `request_review`,
creates a request through this route with the inherited root credentials (slice E, below); no MCP
tool decides, sends or signs. Sending a request to viberails.ai and collecting its decisions is
[Review requests sent to viberails.ai](#review-requests-sent-to-viberailsai-vb-50-slice-c) below;
see [the component contract](VibeRails/Services/ReviewRequests/README.md).

## Outbound services and credentials

### Automatic conversation titles (VIBE-149)

The owner authorized automatic title requests on 2026-10-12. Only newly created,
eligible conversations enroll, and each makes **one** request (owner decision 2026-10-14): once it
has 300 cumulative user-input characters or two messages, or else when its session ends, with
whatever it has. There is no refresh. Root-only background work sends the user's own captured
messages (bare slash commands omitted unless they are all there is; at most 20,000 characters)
through the credential redactor to fixed `https://viberails.ai/api/v1/conversation-title`. It does
not send the screen transcript or agent output, so launch prompts such as the story-tracking nudge,
which are never captured as user input, cannot reach the API. Conversations linked to a Board card
are titled too: the request adds the primary card's display ID, title (at most 200 characters) and
description (at most 1,500), labeled as background other conversations may share, so the title
names the conversation rather than its story. It requires the saved API key and Remote Access;
hosted Integrations permission failures preserve existing labels.

Keys use only `X-Api-Key`. No redirects, cookies, HTTP body/error logging or new listener is added.
Requests have a 20-second deadline and 8-KiB response bound; titles are validated before saving.
Fresh settings are checked before sending and applying, with cancellation when they change.
Durable two-minute claims coordinate open roots. Rejected keys, rate limits, unavailable
endpoints and network failures back off every request for the key; a failure that belongs to one
conversation (a rejected title, a server error, a timeout) backs off only that conversation and
gives up after five tries. Explicit names cancel work; Automation/resume labels and historical
sessions are preserved. The user text and each story field are redacted whole before being bounded,
so a secret is never cut short of the context that lets the redactor recognize it. The whole
request stays under the hosted API's 25,000-character limit. The API's `New conversation` fallback
is never saved as a name; the request is spent either way. A finished request is closed for every
version, so a root still carrying the VIBE-158 refresh cannot make a second call; its
`LastExcerptHash` column is retained but no longer read or written. Only references and counts
are persisted locally, never a copy of the request. This does not alter the cached resume summary.

**Redaction is best effort, by owner decision (2026-10-13).** `RedactTranscript` masks the obvious
credential shapes and replaces each value with `XXXXX`: key/value fields (`password=`,
`"api_key":`, `DB_PASSWORD=`, connection-string `Pwd=`), credential CLI flags, `Authorization`,
`Bearer` and `Cookie` headers, URL user-info, PEM private keys, well-known token formats
(`sk-`, `ghp_`, `AKIA`, JWTs and similar) and the user's own API key. Labels and the rest of
the transcript are kept, so titles and resume summaries keep their context. Someone asking why
PBKDF2 beats Blowfish is far more likely than someone pasting a password, and a random password
in prose cannot be recognized. We do not chase every representation, and we do not degrade
titles, summaries or performance to try: a user who really wants to put a password in the input
can. The same masking applies to an Automation's stored failure reason (the last line its
Worker printed), which is served beyond the root backend. Every pattern is linear, so masking a
multi-megabyte transcript stays fast and never throws a regex timeout.

Credential cleaning lives in the pure static `VibeRails.Security.CredentialRedactor` in
`VibeRails.Data.Abstractions/Security`. Titles and summaries use `RedactTranscript`; backup
exports use its configuration-value, argument and property helpers inside their JSON/TOML
parsers. Search uses `ContainsKnownSecret`, preserving its existing filtering policy.
Configuration cleaning and search detection are not substitutes for outbound transcript redaction.

### Public session sharing links

`create_session_share_link` exposes the same operation to a managed terminal through MCP. It
takes a display name and only the inherited current session, never a caller-supplied target.
The stdio child calls the existing root route with its inherited scoped tool pair; it accepts
only loopback HTTP(S), disables redirects/proxies/cookies, and bounds the local hop to 30 seconds
and 16 KiB. Codex forwards environment variable names per launch, never credential values in argv
or shared config. No Board auto-grant is added. Callers without a terminal/root context fail;
tool descriptions disclose that creating the link grants public read access. With `emails`, the
tool first reads root-only `GET /api/v1/session-sharing/capabilities` (both credentials, no-store,
static answer, no outbound request) and creates nothing when an older running root lacks it; it
prints a URL or commit line only when the root confirmed the email audience with exactly those
people, so an older root can never publish the recording as a public link through the tool.

Root-only `POST /api/v1/sessions/{id}/sharing-links` requires both local credentials and validates
the exact local recording. For listed people it first reads the host's
`GET /api/v1/session-sharing-links/capabilities` and creates nothing when the host lacks it or
does not list `email`; a created link must still confirm the audience and people before any URL
is returned or a recording is queued. It creates the capability at fixed `https://viberails.ai` using only
header `X-Api-Key`, no redirects/cookies, 20-second requests and 16-KiB responses. Local JSON is
capped at 4 KiB. Returned session/key/path/expiry/upload-required fields are checked before a
fixed-origin public URL is returned; raw credentials and remote error prose never enter the modal.
Bounded, recognized error codes select fixed local messages; failure JSON may include the numeric
upstream HTTP status. Unknown codes, SQL, headers and exception text are never reflected or logged.
The sharing URL is itself a public-read capability; do not log it. Persistent upload requests
store a key fingerprint, and uploads/ACKs stay bound to that exact captured key when settings
change. They reuse the existing export protocol and locks. See the
[component contract](VibeRails/Services/Integrations/VibeCodeRemote/SessionSharing.md).

### Public card sharing

The saved card's Share card controls explicitly publish its complete allowed content to fixed
https://viberails.ai, to anyone with the link or only to listed email addresses (validated
locally; the host's `GET /api/v1/card-sharing-links/capabilities` is read before any restricted
publication so a server without the sharing update publishes nothing; the hosted response must
still confirm the audience and people, otherwise the link is revoked immediately).
Root/project-scoped `/api/v1/board/cards/{card}/sharing-links` CRUD, `PUT /{id}/access`
and manual refresh inherit both credentials, no-store responses and bounded (8 KiB) local requests.
The transport disables redirects/cookies, bounds response bytes/time, pins the operation's key,
and validates returned card/local identities, share fragments, expiry and linked recording IDs.
Account changes cannot retarget queued recordings. Uncertain creation is reconciled by listing,
never an automatic POST retry. Public links remain read-only capabilities, not Board membership
or terminal-control grants. Do not log links, keys, content or recordings.

Automatic updates discover only the current creator key's active publications, every 15 minutes
while any exist and backing off to two hours while none do, holding the Board sync lock only
during local capture. Their private local row identity and matching card key resolve stored
project ownership through IBoardStore; no remote path is accepted. Confirmed content hashes
persist in `.card-share-state.json` beside `state.db`: an account fingerprint, local row IDs,
publication numbers and hashes, never links, keys or content. Local deletion retains the last
public version until its links expire or are revoked. All capture is saved content, with a 64 MiB
whole-publication rejection bound. Archive queues preserve pending backoff, and card revocation
ends all reads through that link without independent session sharing links. See
[the card-sharing contract](VibeRails/Services/Board/Sharing/README.md).

### Review requests sent to viberails.ai (VB-50, slice C)

Sending is explicit and root-only. `GET /api/v1/review-requests/{id}/disclosure` (both credentials,
no-store, no outbound call) lists exactly what a send transfers: the destination, every changed
file with its change kind, line count and omission reason, whether its exact staged bytes travel in
place of text (VIBE-211: a binary, oversized or bundle-limit file carries base64 `afterBytes`, at
most 4 MiB per file and 8 MiB per bundle, so the reviewer's machine can rebuild the tree; the
disclosure counts them as `filesWithBytes` / `carriedBytes` and the card's confirm text names them)
and its git mode, the tree and base ids, each attached
session by name with the session-sharing disclosure (terminal output, prompts, saved code changes)
and whether it is still live, the reviewer addresses and the expiry. The capture also measures the
escaped size of every carried field and keeps the bundle under 20 MiB on the wire, inside the hosted
24 MiB body bound. `POST /{id}/send` (1 KiB
body) refuses without `{ "confirmed": true }` (400 `confirmation_required`), for a request that is
not open (409 `not_open`) and without a saved API key (409 `no_api_key`), and transfers nothing in
those cases. `ReviewRequestTransport` posts the stored bundle bytes as `{ requestId, bundle }` to
fixed `https://viberails.ai/api/v1/review-requests` with header `X-Api-Key` only, no redirects or
cookies, a 90-second deadline (30 seconds for polls and cancels) and a 1-MiB response bound; the
answer must name this request id and tree, a well-formed `rvw_` reference, a known status and a
plausible expiry before anything is recorded. There is no separate capability probe: a host
without the review-request update answers the create 404/405/501, reported as
`server_update_required`, and nothing is recorded as sent or queued for upload until the host has
confirmed the request by reference, so an older server receives nothing it could mishandle.
Failures carry fixed local wording selected by the HTTP status and a bounded JSON `code`; remote
prose, headers, redirect targets and exception text are never reflected or logged. Transport
failures answer 502 with the transport's code.

A confirmed send records the hosted reference, the send time, the hosted expiry and the SHA-256
fingerprint of the key that sent it (`review-requests/2`, additive nullable columns). Attached
sessions are queued through the existing `SessionShareUploads` table under that fingerprint and
uploaded by `SessionDataDrainJob` through the existing session ingest after each session ends: no
new upload path, the archive stays immutable, and a later account change cannot retarget them. The
queue is receipted (`SessionsQueuedUTC`, `review-requests/3`), so a send interrupted after the hosted
confirmation is finished by a retry or by the poller under the original key, never by posting the
bundle again. Reviewers are always listed email addresses; a request is never a public link.
Cancelling locally also `DELETE`s the hosted copy under the same key; a failed or deferred hosted
cancellation is retried by the poller and never blocks the local cancel. A local delete cancels the
hosted copy first and is refused (409 `remote_pending`) while it cannot, because the row is the only
thing that can still revoke the hosted copy; the request stays, cancelled, until the host confirms,
and an expired or already-cancelled hosted copy needs no host. A send that races a delete cancels
its own hosted copy: the confirmed transfer, its persistence and any recovery run on the
transport's own deadlines, never the caller's request token, so a disconnect after the host
accepted the bundle cannot lose the reference; the row is first put back, cancelled, with the
reference, the sending key's fingerprint and the expiry, and only then is the hosted copy
cancelled; a failed or timed-out cancellation leaves that
row for the poller's retry and a later delete waits for the host (409 `remote_pending`), while a
confirmed one deletes it again. The caller is never told the copy was cancelled when it was not
(Codex slice E review, F2). The reference is shown to the requester as a selector, never a credential:
every hosted route checks the caller's key.

A send whose answer is lost cannot orphan the hosted copy (Codex VIBE-209 fourth-pass review, F2;
VIBE-212). Before the bundle leaves, the sender claims the attempt on the ledger row
(`SendAttemptedUTC`, `review-requests/5`, nullable) together with the sending key's fingerprint; the
claim is refused for a request already sent or pinned to another key's fingerprint (409
`send_pending`), so a second send under a different account can never create a second hosted copy.
A refusal the host confirmed (any 4xx) clears the attempt and the request is unsent again; a lost,
unreadable or redirected answer and any 5xx keep it, because the host may hold the request. While
an attempt is unsettled the request is neither sent nor unsent: its summary carries
`sendAttemptedUtc` without a `remote`, Delete cancels it locally, keeps the row and refuses with 409
`send_pending` (the row is the only thing that can still find the copy), and Cancel keeps the attempt.
A retry with the same key posts the same bytes again, which the host answers with the stored request
(exact retries are idempotent, 200). The poller settles the rest before it polls decisions, for
attempts pinned to the current key only: it posts the stored bundle again, stores the recovered
reference, cancels the hosted copy at once when the request was cancelled meanwhile, and otherwise
polls it as an ordinary sent request. On that retry only a refusal decided by the content or the
route (400, 404/405/501, 409, 413, 422) proves the first POST never landed and clears the attempt; a
refused key or permission, a rate limit, an unreadable answer or a server error keeps it for the next
pass. Attempts are rows, so they survive restarts. Nothing new leaves the machine for this: the
re-POST carries exactly the bytes the person confirmed, under the key they confirmed them with.

Decisions are polled by the root that holds the Board sync lease, from `BoardSyncScheduler`, on a
self-paced cadence persisted in `.review-request-poll.json` beside state.db (account fingerprint,
next due time and backoff only): every 15 minutes while a sent request is open, doubling to a
two-hour ceiling while none is or the host is unreachable, never on the 60-second tick. A decided
request is still polled until its expiry at the backoff cadence, because the host keeps taking
decisions until cancellation or expiry; a successful send brings the next check forward to one
interval. Only requests sent with the current key's fingerprint are polled. Each hosted decision is validated
(at most 50, distinct ids, listed kinds, trailer grammar, comment bound, and since slice F at most 50
line comments each within the bounds above, 16,000 characters of paths and text together; the poll
answer is bounded at 8 MiB, which holds fifty maximal decisions once the host escapes HTML-sensitive
text as six bytes a character, and the bundle answer at 32 MiB) and then lands through
`ReviewRequestService.RecordDecisionAsync`: an approval's trailer must cover the request's tree and
is re-verified with the public signature endpoint, and its signer must be a listed reviewer; a
rejection must name a listed reviewer; line comments are cleaned and stored as JSON on the decision
row (`review-requests/4`, nullable) and shown as text on the card, grouped by the path the reviewer
named (the host does not check it against the bundle). The hosted decision id is stored with the row so a decision
lands once; a decision the ledger refuses is remembered for the process and not re-verified every
pass, while an unavailable verifier leaves it pending. Landed decisions publish the local
`review_request_updated` app event (id, status, card key, sent); no content leaves through it. In VS
Code the dashboard relays those four fields to the extension host over the webview `postMessage`
bridge (`__viberails_reviewRequestChanged__`, slice F), which re-reads the list from the root backend
with the inherited credentials and shows a notification; nothing travels back over that bridge. The
commit hook is unchanged: it applies a stored verified approval and rechecks the trailer itself.
Component contract: [Review requests](VibeRails/Services/ReviewRequests/README.md).

### Reviews requested of you (VB-50, slice D)

The reviewer's side. Root-only `/api/v1/review-inbox` (both local credentials, no-store): `GET`
answers the cached list, `POST /refresh` fetches it now, `POST /{reference}/reject` (32 KiB body,
`{ comment }`) posts a rejection, `POST /{reference}/approve` (optional 16 KiB `{ repositoryPath }`)
opens the signing popup. The list is
fetched with the saved account key from fixed `https://viberails.ai/api/v1/review-requests/inbox`
(header `X-Api-Key` only, no redirects or cookies, 30 s, 1 MiB; every item validated: a `rvw_`
reference, bounded text, a known status, a plausible expiry) on the Board sync cadence persisted in
`.review-inbox.json` beside state.db (the account fingerprint, due time, backoff and the last list as
the host gave it: repository names, requester addresses and references, never a key, file text or a
note): every 15 minutes while a listed request is open, doubling to two hours otherwise, never on the
60-second tick; "Check now" is a person's click and is answered at most once per 10 seconds. A key
change starts from an empty list. Rejections and approvals go to the host's own decision route under
the reviewer's key; the host's refusals keep their code (`not_a_reviewer`, `not_open`,
`signer_mismatch`, …) and status, and remote prose is never shown or logged.

**No route and no MCP tool signs.** Approve re-launches `vb --vca-hook sign-review --review
<reference> --workdir <clone> --console-window`, where `<clone>` is this root's project or, when the
body names one, a project another open root has (`JobProjectRoots` presence, VIBE-213 item 7), passed
as the listed spelling; any other folder is refused 400 before a launch, so the page cannot point the
popup at an arbitrary path. Through `VcaHookConsoleRespawn` (CREATE_NEW_CONSOLE;
Windows, an interactive desktop, not CI; refused with `popup_unavailable` otherwise), once per
reference while that window is open, and learns only its exit code. The popup reads the bundle itself
with the account key (`GET /{reference}/bundle`, 90 s, 25 MiB, validated the way the host validated
the desktop's bundle: ids, exact paths, change kinds, omissions, the 256 KiB per file and 4 MiB per
bundle text bounds, at most ten sessions and fifty decisions, and a statement equal to the staged-tree
statement), shows what is being approved with the requester's text cleaned
(`HumanSignaturePopup.Clean`; file counts computed from the carried text, never the requester's
numbers), refuses before any key prompt when the request is cancelled or expired, not addressed to
the account, already approved by this account or carries a file without content, and signs only after
`ReviewTreeRebuilder` rebuilt `treeId` from the reviewer's own clone (design decision 7: a temporary
index, no blob written, the real index untouched, a mismatch for any reason refused), required the
canonical transition shape (an added file carries no base text, a deleted file no staged text, only
a rename or copy names a distinct previous path, no path twice; `ReviewTreeRebuilder.ShapeProblem`,
also enforced by `ParseBundle` and the hosted contract) and bound every shown base text to the base
commit's own blob (a file listed as added, or the destination of a rename or copy, must be absent
from the base; a changed or deleted one present with exactly that text), and checked with
`diff-tree` that the rebuilt tree's per-path transition equals the listed one (no unlisted path, no
listed path left unchanged, no kind that differs: a path colliding with a base directory or file
would otherwise delete an unlisted entry, or one listed as modified, silently), so the diff the
reviewer read on viberails.ai is the real and whole change and not only the signed tree. Another reviewer's decision does not
close a request to this account, because viberails.ai keeps taking decisions. The password is
read key by key from the popup's own console with redirected input refused, the signature is confirmed
with the public verify endpoint before the approval is posted, and the statement is the staged-tree
one the dashboard Sign route and MCP refuse. The VS Code view calls the same local routes; its
"Read on viberails.ai" opens through the host's external-link allowlist, which admits only the sign-in
page and `https://viberails.ai/Reviews/requests/rvw_<32 hex>`.
Component contract: [Review requests](VibeRails/Services/ReviewRequests/README.md).

### Request review from a card and `request_review` (VB-50, slice E)

Two read-only root routes join the group (both credentials, no-store, no outbound call, nothing
stored). `GET /api/v1/review-requests/staged` captures the staged index exactly as create does and
answers what a request would carry: each file's change kind, `+added −removed` line counts
(`ReviewLineDiff`, a Myers edit-distance pass bounded at 20,000 edits), omission reason, the tree,
base and text-byte totals, the `Signed Approval` rules of the staged `vc.rules.md` files whose path
a staged file touches (their approvers are the suggested reviewers; the matching is the hook's
`PathLockRule` matching) and the fixed disclosure text; the same `nothing_staged` /
`not_a_repository` refusals apply. `GET /api/v1/review-requests/commit-approvals?sha=…` takes at
most 50 shas of 7 to 64 hexadecimal characters (anything else is 400 `invalid_request`), resolves
their trees with one `git log --no-walk=unsorted --ignore-missing --format=%H %T` through
`GitCli` (argv only, 64 KiB output bound; a git failure answers no trees) and reads verified
approvals per tree through the ledger's `FindVerifiedApprovalsAsync`, the commit hook's lookup. It
returns reviewer addresses, request ids and decision times, never signatures. The Rules page's
`POST /api/v1/hooks/preview` marks a deferred `Signed Approval` / `Human verified` finding with
`approvedBy` from `IStoredCommitApprovalSource` over the staged tree (`git write-tree`) with the
rule's approvers; the source is the hook's bounded, quiet `ReviewRequestApprovalSource`, resolved
only when such a finding exists, and its absence (tab children, tests) leaves the findings unchanged.

The card UI sends only through the existing send route after showing the disclosure in
`confirmDialog`; the backend's refusal wording (including `server_update_required` from an older
viberails.ai) is shown as the server gave it. Cancel and Delete also confirm through `confirmDialog`
(`window.confirm` remains banned). Every server string is escaped before it reaches markup. The MCP
tool is described under [MCP and agent launches](#mcp-and-agent-launches): it creates through the
root and never sends.

### Board sync, sharing and remote Start work

[SYNC.md](VibeRails/Services/Board/SYNC.md) defines the full content and hosted authorization
contract. A configured API key enables automatic Board publication and linked activity for
sync-enabled boards across local projects. New boards start local; existing boards retain sync
for compatibility. Disabling sync stops publication and remote Start work advertising, while
preserving the hosted copy and ledger. Older backends sharing the database may ignore the switch.
Read each board's content using its stored project scope. Portable text travels verbatim and can contain secrets. Activity
can include linked session metadata/outcomes, durable commit code, bounded attachment content
and card links; local project paths, environment IDs, launch settings and Automation definitions
are excluded. The repository folder name and source computer are published as owner-controlled
display labels, independently of the portable layout hash. Snapshot code comes from the saved
capture, not a fresh checkout read.

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

The root-only `/api/v1/board/remote` manager requires both local credentials and returns no-store
responses. It pages accessible remote metadata and labels matching local copies across projects.
Writes require the destination context captured when the list was loaded. Create publishes a
remote-only board using a stable retry identity; rename/delete recheck hosted ownership. Rename
updates matching local names. Delete takes the shared sync lock and pauses linked local copies
before the outbound request, retaining local cards/history even when the remote outcome is unknown.
Missing owned remote boards also pause background sync; only explicit re-enabling republishes them.
These protections apply to updated clients; older running versions may ignore the pause switch.

The owner's explicit host-wins policy permits automatic replacement of a **proven conflicting**
remote copy: validated history must contradict local acknowledgements, and the authenticated
descriptor must confirm ownership of the exact board before the existing owner-only DELETE.
A descriptor naming another owner or board pauses that board's sync and runs neither repair nor
deletion; only the owner's explicit re-enable publishes again.
The durable `BoardSyncRebuilds` intent pins the board and destination fingerprint; interruption
resumes delete confirmation, idempotent publication, then local-history upload. Imported boards,
ordinary 409 contention, network/auth failures and malformed responses never authorize deletion.
Replacement discards that remote board's content and sharing identity; local rows are retained.

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
origin's `/link`. The dashboard/VS Code bridge permits only `https://viberails.ai/link` and
its optional strict `#code=ABCD-EFGH` public-code fragment. The hosted page removes that fragment
before Auth0, retains it in same-tab storage for at most ten minutes, and requires explicit
approval with fresh antiforgery.

Serialize/throttle the root's single in-memory attempt; cancellation/replacement invalidates late
responses. Compare the original saved key before replacement so manual changes win. Account
email is response-only, written with the key and its full SHA-256 fingerprint; a mismatch hides
it. Never return the raw credential/fingerprint. See [account-link flow](docs/account-link-auth-flow.md).

### Jira (VB-40 / VIBE-102)

**Owner-requested automatic activity sharing (2026-10-09):** new Board comments on connected
Jira cards queue outbound comments by default; `syncToJira=false` retains an internal note.
New local session attachments also create public replay links and post them to the linked
issue, using the existing fixed-origin sharing service and upload policy. The Board UI and
agent prompts disclose both behaviors. This applies to existing Board write capabilities;
no MCP grant, listener or authentication exception is added. An additive local delivery ledger
pins the connection/issue, deduplicates events, excludes imports and copied discussion, and
never automatically retries an uncertain POST. Connection changes and delivery share the Jira
lock. Public links stay out of logs; old activity is not backfilled.

`add_jira_comment` is an explicit outbound MCP write on both transports, outside the local
Board auto-grants. It resolves the live card's retained issue link and original connected
site within the owning project through `IBoardStore`, never caller-supplied credentials,
site or issue ID. The existing Jira lock serializes with connection changes/unlink/pulls.
The saved-origin `POST /rest/api/3/issue/{numericIssueId}/comment` uses basic auth, no
redirects/cookies, a 30-second deadline and 256-KiB responses. Text and optional public
session replay URLs are sent as ADF; posting creates no public shares. Responses disclose
only fixed errors or a validated numeric comment ID. Unconfirmed delivery is never retried
automatically; Board receipt failure does not turn confirmed Jira delivery into a failed
post. See [Jira comments](VibeRails/Services/Jira/Comments.md).

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
and cookies disabled, a 30-second deadline covering headers and body, and a 4-MiB response cap.
Error responses use fixed wording; remote error prose is never reflected or persisted.
3xx is treated as failure: `/rest/api/3/myself`, `/rest/api/3/search/jql`,
`/rest/api/3/filter/{id}`, `/rest/agile/1.0/board/{id}` and its `/configuration`, plus
`/rest/software/1.0/board/{id}/issue` and `/issue/approximate-count`. Do not use the deprecated
agile board-issue endpoint or forward a saved token to another host.

Column mappings compare lane IDs only against the edited board's own lanes. The email suggestion
runs fixed `git config --get user.email` argv in the server-derived root, with a three-second
timeout and bounded output; no request value reaches the command. See
[JiraCloudClient](VibeRails/Services/Jira/JiraCloudClient.cs) and
[JiraBoardLink](VibeRails/Services/Jira/JiraBoardLink.cs).

### Azure DevOps (VIBE-165..168)

Azure DevOps Services mirrors the Jira contract above with its own client, lock, token file,
tables and delivery ledger; nothing here widens Jira's. New Board comments on a card linked to a
work item queue an outbound comment by default; `syncToRemote=false` (alias `syncToJira=false`,
stored as the same `syncToJira.to` key) keeps an internal note, and an older binary's `false`
suppresses Azure DevOps delivery too. New local session attachments create public replay links
through the existing sharing service and post them to the work item. The ledger pins the
connection and numeric work item id, deduplicates, excludes the pull's own notes, imports and
copied discussion, and never automatically retries an uncertain POST. Connection changes, pulls
and delivery share `.ado-pull.lock`. Dedicated Azure DevOps boards never upload to viberails.ai:
publication and manual sync are refused and the hosted-sync pass skips them.

`add_ado_comment` is an explicit outbound MCP write on both transports, outside the local Board
auto-grants (`BoardMcpAuthorization.ToolNames`). It resolves the live card's retained work item
link and saved connection within the owning project through `IBoardStore`, never a
caller-supplied organization, project, work item id or token, and posts no shares. Unconfirmed
delivery is never retried automatically; a Board receipt failure does not turn a confirmed post
into a failed one. See [Azure DevOps comments](VibeRails/Services/AzureDevOps/Comments.md).

The five root/project-scoped `/api/v1/board/boards/{boardId}/ado` GET/PUT/DELETE and `/test`,
`/pull` POST routes use both credentials. Personal access tokens are write-only: accepted on save,
stored in plain text in the private `~/.vibe_rails/ado-tokens.json`, never in Board storage,
responses or logs. A blank save keeps the token only within the same organization; a changed
organization requires the token again, mints a new connection id and deletes the old token.
The PAT needs **Work Items: Read & write** plus **Project and Team: Read** for Connect's
initial project-access check; Board Settings lists both scopes.

- **Host:** exactly `https://dev.azure.com`, default port, no user info. Links on
  `*.visualstudio.com`, Azure DevOps Server or any other host are refused before any request.
- **Paths:** the organization is 1 to 50 letters, digits and hyphens, starting and ending with a
  letter or digit; project, team and backlog level are validated segments (at most 128
  characters; no `/`, `\`, controls, `%`, `?`, `#`, leading/trailing dot or space) that are then
  percent-escaped. A saved query id must be a GUID.
  The work item id in a comment path is the stored positive integer.
- **Transport:** HTTP basic with an empty user and the token, `X-TFS-FedAuthRedirect: Suppress`,
  redirects and cookies disabled, one 30-second deadline covering headers and body, and a 4-MiB
  read-response cap. Error responses use fixed wording, never remote error prose. A 302, 203
  or non-JSON 2xx (the sign-in page) is a refused token. The token is never sent to another host.
- **WIQL:** literals come only from server-returned values with `'` doubled; the user's narrowing
  clause is wrapped in parentheses and may not contain `ORDER BY` or unbalanced parentheses.
- **Comments:** text is HTML-encoded; the only markup added is paragraphs, line breaks and one
  `<a>` per vetted public replay URL. 20,000 characters and 20 links per comment; 256-KiB
  responses; only a fixed message or a validated numeric comment id is returned.

The complete outbound endpoint list (api-version 7.1, comments 7.1-preview.4):
`GET {org}/_apis/projects/{project}`, `GET {org}/{project}/{team}/_apis/work/boards/{backlogLevel}`,
`GET {org}/{project}/{team}/_apis/work/teamsettings/teamfieldvalues`, `GET {org}/_apis/wit/fields`,
`POST {org}/{project}/{team}/_apis/wit/wiql`, `GET {org}/{project}/_apis/wit/wiql/{queryId}`,
`GET {org}/{project}/_apis/wit/queries/{queryId}` (the saved query's name, at Connect),
`POST {org}/{project}/_apis/wit/workitemsbatch` and
`POST {org}/{project}/_apis/wit/workItems/{id}/comments?format=html`. See
[AzureDevOpsClient](VibeRails/Services/AzureDevOps/AzureDevOpsClient.cs) and
[AdoBoardLink](VibeRails/Services/AzureDevOps/AdoBoardLink.cs).

### GitHub pull requests and review publishing (PR Reviews)

The [PR review tracker](VibeRails/Services/PrReviews/AGENTS.md) reads GitHub through the user's
own authenticated `gh` CLI. VibeRails holds no GitHub credential and stores no token.
Read calls are `gh pr list` (open PRs, at most 100), `gh pr view` (one PR's metadata
and current head when starting from its detail page), and a `gh api graphql` query for tracked
PRs' open/merged/closed state. All run through `ICliWrapper` as argv with an explicit
`-R owner/name` or GraphQL variables, `GH_HOST=github.com` overriding inherited host selection,
stdin closed for reads, prompts and update notices disabled and a
20-second timeout. Failures use fixed wording chosen by how `gh` ended (not started, timed out,
not signed in, or its exit code); `gh`'s stderr, which can carry GitHub's error prose, is never
reflected to the page or MCP. Optional machine-wide watched repositories accept validated GitHub
`owner/name` identifiers (up to 50, case-insensitive deduplication). With an empty watch list, the repository
is parsed from the dashboard project's `origin` remote and accepted only for github.com with
GitHub's owner/name character rules; the remote URL itself (which can embed a credential) is never echoed. PR links are built from that
parsed repository, never from CLI output. Titles, authors and feedback are untrusted text: the
page escapes them and the MCP tools return them as JSON data.

The `/api/v1/pr-reviews/**` routes are root-only, require both credentials and return no-store.
List, queue and lifecycle sync cover selected watches or the dashboard root's origin fallback.
Single-PR/pass operations accept a `repository` query argument selecting only a saved watch or
that origin; multiple watches require it (400 for ambiguous numbers, 409 for unselected repos).
Review ids are global, so reads and writes by id also match the selected repository and answer
404 for another repository's review. Explicit watches never require changing a checkout.
Records live in `pr_reviews.db` (`pr-reviews/1`, additive).

Settings > PR Reviewer explicitly enables reviewer work and outbound publishing together; off
by default. GET/PUT `/settings` under this group read/write the machine-wide switch through
`IPrReviewStore` (`pr-review-delivery/1`, additive). The optional `repositories` list lives in
`PrReviewerRepositories` (`pr-review-repositories/1`, additive) in the same file. Settings save
atomically; omission/null preserves watches for older clients, `[]` restores origin fallback.
Removing a watch retains its history, accessible again when re-added. Off blocks queue/start and
new POST reservations, preserving history and allowing in-progress local completion. A reserved POST may
still finish; this is disclosed on the page. Enabling does not backfill or start a background host.

Completion and `POST /{number}/reviews/{id}/publish` can publish through `gh api --hostname
github.com`: `GET user` verifies the authenticated identity; `GET
repos/{owner}/{name}/pulls/{number}/reviews?per_page=100&page=N` reconciles receipts (20-page cap);
`POST repos/{owner}/{name}/pulls/{number}/reviews` sends JSON on stdin with the saved full
40-character `commit_id`, feedback and a stable marker, and `event=REQUEST_CHANGES` for blockers
or request_changes verdicts, otherwise `COMMENT`. Before dispatch, `gh pr view` rechecks the
same PR's head; a changed head fails without substituting commits. GitHub decides write
permission, self-review restrictions and branch-protection enforcement. Command logs never
contain feedback or credentials. Each call has the existing 20-second deadline; errors use
fixed local wording, never remote error prose. No listener or auth exemption is added.

The immutable snapshot commits with local completion. SQLite atomically checks enablement and
reserves each delivery across processes before POST, persisting unknown first. Confirm only a
matching GitHub id, user, commit, body and submitted event. An uncertain POST is never resent:
retry only reconciles the original account's exact receipt, and absence remains unknown.
Pre-dispatch errors and confirmed 4xx refusals can retry safely. Delivery status/errors remain
distinct from local verdicts; local edits/deletions never edit/dismiss the remote review, and
receipts survive local deletion. Publishing/retry MCP tools use normal explicit tool grants on
both transports, outside Board auto-grants; callers may select a configured repository but
never supply a host, path or credential. Completion/retry remain pinned to that repository, PR
and immutable delivery snapshot.

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
  unknown arguments. Retain browser and argument round-trip coverage rather than treating a
  successful save as proof.

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
PR, Board comment or private `vibe-books` investigation. Never normalize rejected working-tree
changes into this approved contract.

Keep this file small:

- Edit the relevant current rule in place; merge superseded amendments instead of prepending
  another dated section. Link component contracts for implementation details.
- Add history only for a significant incident, accepted exposure, unresolved risk or new security
  decision. State what failed, the disposition and the rule that prevents recurrence.
- Do not append “checked, all good,” repeated test totals, route counts or unchanged listener
  results. Successful checks with no contract change require no edit here. Git history retains
  old audit notes; this document does not need an archive copy.
