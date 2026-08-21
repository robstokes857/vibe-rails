# Getting auth tokens from a running vb.exe (every time, without getting stuck)

Every VibeRails backend process mints **fresh, in-memory credentials at startup**. There is no
static API key, nothing persisted to disk, and no way to reuse yesterday's tokens: a new
`vb.exe` run = a new port + a new session token + a new tab token. Any cached credential from a
previous run will 401.

Use this runbook whenever you (human or agent) need the UI or API of a locally launched
instance — UI development, `curl`/`Invoke-RestMethod` poking, Playwright, WebSocket testing.

## The two credentials

Both are minted in the `AuthService` constructor (`VibeRails/Auth/AuthService.cs`) as URL-safe
base64 (`+` → `-`, `/` → `_`, no `=`) so they can double as WebSocket subprotocol values. They
live only in process memory and die with the process.

| Credential | How you send it | Notes |
|---|---|---|
| **Session (instance) token** | Cookie `viberails_session=<token>` **or** HTTP header `viberails_session: <token>` | Header form exists for cookieless clients (VS Code webview, server-to-server). |
| **Tab token** | HTTP header `viberails_tab: <token>`; for WebSockets, a subprotocol value | Despite the name it is **one token per process**, not per browser tab. The browser keeps it in `sessionStorage['viberails_tab']`. |

### What each route class requires (`VibeRails/Middleware/CookieAuthMiddleware.cs`)

| Surface | Session | Tab | On failure |
|---|---|---|---|
| `GET /auth/bootstrap`, `GET /health`, any `OPTIONS` | — | — | (frozen skip list — never extend it; `API_SEC.md` § 1 is the authority) |
| Page loads / static files (`/`, `/app.js`, …) | ✅ | — | 403 + "auth required" HTML page |
| `/api/**`, `/mcp`, `/llm/**` | ✅ | ✅ | 401 plain `Unauthorized` |
| Every WebSocket upgrade | ✅ (cookie, or token as a subprotocol) | ✅ (as a subprotocol) | 403. Query-string tokens are **deliberately rejected** (they end up in request logs). |

## The one-time bootstrap link

At startup the process generates a single-use code and prints
`http://localhost:<port>/auth/bootstrap?code=<code>` (`VibeRails/Program.cs` ~441–517):

| Launch | What stdout looks like | Browser auto-opens? |
|---|---|---|
| `vb` / `vb --web` / `vb --git-guard` | Human banner: `Open this URL to access the dashboard:` then the URL on its own line | Yes (`LaunchBrowser`) — waits for git detection first |
| `vb --vs-code-v1` | Machine line, flushed immediately: `vs-code-v1=<url>` | No — best mode for scripted capture |
| `vb --job-run … --vs-code-v1` | Same `vs-code-v1=<url>` line (child-tab bootstrap) | No |

`GET` that URL **once** and the response carries everything:

- `Set-Cookie: viberails_session=<sessionToken>; Path=/; HttpOnly; SameSite=Lax`
- Response header `viberails_tab: <tabToken>`
- An HTML page whose script stores the tab token in `sessionStorage` and redirects to `/`
  (`STRINGS.AUTH_BOOTSTRAP_HTML`; route: `VibeRails/Routes/AuthRoutes.cs`)

### The three rules that cause all the pain

1. **Single-use.** The first `GET` consumes the code (`ValidateAndConsumeBootstrapCode`). If
   `curl` eats it, your browser can't log in with the same link, and vice versa. Decide who
   consumes it *before* touching it.
2. **2-minute fuse, and the whole server dies.** If nobody consumes the code within 2 minutes,
   `UnconsumedBootstrapCodeShutdownWatchdog` calls `StopApplication()` — the process exits.
   A vb instance that "mysteriously quit" ~2 min after launch was never authenticated.
3. **No re-mint.** Nothing regenerates a bootstrap code at runtime. Consumed the code but lost
   the tokens (or opened a fresh browser tab with empty `sessionStorage`)? **Restart vb.** Don't
   go hunting for a recovery endpoint — there isn't one, by design.

## Recipe A — headless capture, PowerShell (the default for agent sessions)

```powershell
# 1) Launch and capture stdout. --vs-code-v1 prints the line immediately, no browser.
$out = "$env:TEMP\vb-out.txt"
$proc = Start-Process vb.exe -ArgumentList '--vs-code-v1' `
    -RedirectStandardOutput $out -PassThru -NoNewWindow
# (dev tree: Start-Process dotnet -ArgumentList 'run','--project','VibeRails','-c','Debug','--','--vs-code-v1')

# 2) Wait for the bootstrap line (be patient on cold starts; ~90 s ceiling like the e2e suite)
$bootstrapUrl = $null
foreach ($i in 1..450) {
    Start-Sleep -Milliseconds 200
    $m = Select-String -Path $out -Pattern 'vs-code-v1=(\S+)' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($m) { $bootstrapUrl = $m.Matches[0].Groups[1].Value; break }
}
if (-not $bootstrapUrl) { throw "No bootstrap URL — check $out (2-min fuse may have killed it)" }
$base = $bootstrapUrl -replace '/auth/bootstrap.*$',''

# 3) Consume the one-time code. -SessionVariable jars the cookie for you.
$resp = Invoke-WebRequest -Uri $bootstrapUrl -SessionVariable vbAuth
$tab  = [string]$resp.Headers['viberails_tab']
if (-not $tab) { throw 'Bootstrap succeeded but no viberails_tab header — should never happen' }

# 4) Call the API: cookie rides in the WebSession, tab token goes as a header.
Invoke-RestMethod "$base/api/v1/settings" -WebSession $vbAuth -Headers @{ viberails_tab = $tab }
```

No cookie jar available? Extract the raw token and send it as a **header** instead — the
middleware accepts `viberails_session` as a header everywhere it accepts the cookie:

```powershell
$setCookie = @($resp.Headers['Set-Cookie']) -match '^viberails_session=' | Select-Object -First 1
$session   = [uri]::UnescapeDataString(($setCookie -split ';')[0] -replace '^viberails_session=','')
Invoke-RestMethod "$base/api/v1/settings" -Headers @{ viberails_session = $session; viberails_tab = $tab }
```

(The unescape is belt-and-braces: tokens are URL-safe base64, but cookie parsers may encode.
The VS Code extension does the same — `fetchTokens` in `vscode-viberails/src/extension.ts`.)

## Recipe B — headless capture, bash/curl

```bash
vb.exe --vs-code-v1 > /tmp/vb-out.txt 2>&1 &
until grep -q 'vs-code-v1=' /tmp/vb-out.txt; do sleep 0.2; done
url=$(grep -m1 -o 'vs-code-v1=[^ ]*' /tmp/vb-out.txt | sed 's/^vs-code-v1=//' | tr -d '\r')
base=${url%/auth/bootstrap*}

curl -s -o /dev/null -D /tmp/vb-headers.txt "$url"       # consumes the one-time code
session=$(grep -i '^set-cookie: viberails_session=' /tmp/vb-headers.txt | sed 's/^[^=]*=//; s/;.*//' | tr -d '\r')
tab=$(grep -i '^viberails_tab:' /tmp/vb-headers.txt | sed 's/^[^:]*: *//' | tr -d '\r')

curl -s "$base/api/v1/settings" -H "viberails_session: $session" -H "viberails_tab: $tab"
```

Smoke checks: `GET /health` needs nothing (readiness probe — the URL is printed a beat before
Kestrel is warm on cold AOT starts, so probe it first like the extension does). `GET
/api/v1/settings` with both headers → 200 JSON; drop `viberails_tab` → 401.

## Recipe C — UI development in a real browser

Let the **browser** consume the link: launch plain `vb` (auto-opens) or paste the printed URL.
The bootstrap page sets the cookie, stashes the tab token in `sessionStorage`, and redirects
to `/`. From then on `app.js` sends `viberails_tab` on every fetch.

- **Keep that browser tab alive.** F5/reload is fine (cookie + `sessionStorage` survive), and
  frontend-only edits can be copied into the running instance's `wwwroot` and picked up on
  reload — no re-auth, no rebuild.
- A **new** browser tab to the same `localhost:<port>` has the cookie (pages render) but an
  empty `sessionStorage` → every API call 401s and the app bounces you to a dead bootstrap
  link. Duplicating the authenticated tab copies `sessionStorage`; a fresh tab does not.
  When in doubt: restart vb, use the new link.
- Need browser **and** script access to one instance? Let the browser consume the link, then
  lift the values from DevTools: `document.cookie` won't show the session cookie (HttpOnly),
  but `sessionStorage.getItem('viberails_tab')` gives the tab token, and the Network panel
  shows the `viberails_session` cookie on any request. Or start from Recipe A and give the
  browser nothing — pick per task.

## Recipe D — Playwright / e2e (already solved, don't hand-roll)

`UITests/global-setup.js` is the canonical implementation of this entire runbook: spawns the
backend (`dotnet run … -- --vs-code-v1`, honoring `VIBERAILS_E2E_BACKEND_DLL` for an isolated
prebuilt DLL and setting `VIBERAILS_TEST_FAKE_CLI=1`), regexes `vs-code-v1=(\S+)` from stdout,
consumes the link in a throwaway Chromium context, and persists:

- cookies → Playwright `storageState` (`.playwright-auth.json`)
- tab token → captured separately, because **`storageState` does NOT persist
  `sessionStorage`**; `UITests/tests/fixtures.js` re-injects it on every new page via
  `addInitScript` and adds `viberails_tab` to `extraHTTPHeaders` so `context.request` works too.

One backend, one bootstrap consumption, `workers: 1`. If you're writing new specs, import
`{ test, expect }` from `UITests/tests/fixtures.js` and auth Just Works.

## Already inside a VibeRails-spawned child? You don't need any of this

The parent exports the live tokens into the child environment:

| Env var | Meaning |
|---|---|
| `VIBERAILS_TOOL_API_BASE` / `VIBERAILS_TOOL_SESSION_TOKEN` / `VIBERAILS_TOOL_TAB_TOKEN` | Agent-tool processes (`VibeRails/Services/AgentTools/LocalToolApiContext.cs`), plus `VIBERAILS_TOOL_CURRENT_TAB_ID` / `..._SESSION_ID` |
| `VIBERAILS_LLM_PROXY_SESSION_TOKEN` / `VIBERAILS_LLM_PROXY_TAB_TOKEN` | LLM-proxy callers (`VibeRails/Services/LlmProxy/LocalLlmProxyContext.cs`) |

Send them as the `viberails_session` / `viberails_tab` headers exactly as above.

## WebSockets

Tokens travel as **subprotocols** (both are URL-safe base64 precisely so they're valid RFC 6455
subprotocol tokens; query strings are rejected):

- Browser page: cookie carries the session; JS connects with `new WebSocket(url, [tabToken])`
  (`VibeRails/wwwroot/js/modules/base-websocket.js`).
- Cookieless client: offer both — `new WebSocket(url, [sessionToken, tabToken])`. The server
  validates each offered value against both tokens and echoes the tab token back as the
  accepted subprotocol.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `403` on `/auth/bootstrap` (invalid-code page) | Code expired (>2 min), already consumed, or from a previous process. Restart vb; use the fresh link. |
| Server exits ~2 min after launch | Bootstrap watchdog: nobody consumed the link. Authenticate faster (Recipe A) or relaunch when you're actually ready. |
| `401 Unauthorized` on `/api/**`, `/mcp`, `/llm/**` | Missing/stale `viberails_session` or `viberails_tab` (both are required there). Re-run the capture against the *current* process — tokens never survive a restart. |
| 403 HTML "auth required" page on `/` | No session cookie/header on a page load. |
| WS handshake 403 | Tab token missing from the subprotocol list, or you tried query-string auth. |
| Tokens suddenly wrong, nothing changed | The backend restarted (crash, watchdog, rebuild) — new port and new tokens every run. Always re-parse stdout; never hardcode the port. |
| Bootstrap URL printed but requests hang/refuse | Cold start: poll `GET /health` until 200 before consuming the link. |

## Hard rules (security posture — don't "fix" auth friction by weakening it)

- The middleware skip list (`GET /auth/bootstrap`, `GET /health`, `OPTIONS`) is **frozen**.
  Adding a path is a security regression; `API_SEC.md` § 1 is the authority.
- Never put either token in a URL/query string (they'd land in request logs).
- No second listener/port to dodge auth — the approved listener set in `API_SEC.md` is closed.
- Don't log tokens, don't write them to files in the repo, don't commit captured header dumps.
