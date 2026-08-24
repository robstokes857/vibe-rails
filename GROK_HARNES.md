# Native Grok CLI as a first-class VibeRails harness

This is the integration plan for launching the **Grok Build CLI** (`grok`) as its
own TUI in a VibeRails terminal tab — the same way Claude and Codex are launched
— instead of wrapping Grok inside OpenCode.

It exists because the 2026-08-15 attempt failed the production listener gate in
[`API_SEC.md`](API_SEC.md). Grok CLI now speaks the same proxy contract Claude
and Codex already use, so the sidecar is unnecessary.

This file is the how-to. It is not the implementation.

---

## Why Grok is OpenCode-backed today

`LLM.Grok46` is a **pseudo-CLI**: the dropdown says "Grok 4.6", the PTY runs
`opencode --model=xai/grok-4.6`, and Token Saver rides OpenCode's `xai` provider
override (`OPENCODE_CONFIG_CONTENT` → `{apiBase}/llm/xai/v1` → `api.x.ai`).

That shape was the fallback after this was rejected and removed:

- a second production request listener (`GrokLoopbackBridge`, `HttpListener` on
  `127.0.0.1` ports 6000–6999)
- a random path-capability instead of `viberails_session` / `viberails_tab`
- a `/llm/grok` sidecar route invented for that listener

Loopback is not authentication. A second acceptor is a second security boundary:
it does not pass through `CookieAuthMiddleware`, request logging/redaction, CORS,
or the main server's lifecycle. See `API_SEC.md` "Rejected listener incident —
Grok loopback bridge (2026-08-15)".

The native Grok CLI can now be pointed at the **already-approved** Kestrel tree
and can attach the same two headers every other CLI sends. That is the missing
piece from August. Do not revive the listener to "make Grok work".

---

## Frozen constraints (review gate)

Copied from `API_SEC.md`. A Grok harness PR that violates any of these is a STOP.

1. **One production listener.** The Kestrel host in `VibeRails/Program.cs`
   (`ListenLocalhost`). `PortFinder`'s transient `TcpListener` probe is not a
   serving surface. No `HttpListener`, no second `WebApplication`, no extra
   Kestrel host, no raw accept loop — including loopback-only and
   random-capability URLs.
2. **Do not reintroduce** `GrokLoopbackBridge`, `/llm/grok`, or any
   provider-specific sidecar.
3. **Do not grow** `CookieAuthMiddleware`'s skip list. `/llm/**` was removed
   from it on 2026-08-05 and must not return. The CLI authenticates with the
   same session/tab secrets as every other caller, sent as headers.
4. **Reuse `/llm/xai/{**rest}`** for the first slice. It is already a
   main-host mapping, gated by middleware + `ILlmProxyAuthGate`, and stripped of
   VibeRails headers before the upstream hop (`LlmProxyRelay.LocalOnlyHeaders`).
5. A **new** Kestrel path (for example to `cli-chat-proxy.grok.com`) is allowed
   only as an explicit topology/route amendment to `API_SEC.md` in the same PR.
   It is still mapped on the existing host. It is not a new listener. Do not
   name it `/llm/grok`.

---

## What Grok CLI actually supports

Verified against the Grok Build user guide (`~/.grok/docs/user-guide/`) and
`~/.grok/README.md`. These are Grok's knobs, not VibeRails inventions.

### Inference proxy URL

Grok's default inference host is `https://cli-chat-proxy.grok.com/v1`. Override
it without writing a config file:

```bash
# Unix
export GROK_CLI_CHAT_PROXY_BASE_URL="http://127.0.0.1:{port}/llm/xai/v1"
export GROK_MODELS_BASE_URL="http://127.0.0.1:{port}/llm/xai/v1"

# Windows PowerShell
$env:GROK_CLI_CHAT_PROXY_BASE_URL = "http://127.0.0.1:{port}/llm/xai/v1"
$env:GROK_MODELS_BASE_URL = "http://127.0.0.1:{port}/llm/xai/v1"
```

`GROK_CLI_CHAT_PROXY_BASE_URL` is the inference base. Which path Grok appends is
**per model backend** (verified 1.0.5, 2026-08-23): grok-4.6 runs the Responses
backend and POSTs `{base}/responses`; grok-build POSTs `{base}/chat/completions`.
`GROK_MODELS_BASE_URL` is the `/v1/models` list Grok fetches at startup — setting
it also switches grok to API-key auth, and a signed-in `grok login` session still
outranks `XAI_API_KEY`, so API mode requires `grok logout` first.

Equivalent `config.toml` (do **not** bake the process port into a saved file;
the Kestrel port is per-process):

```toml
[endpoints]
models_base_url = "http://127.0.0.1:{port}/llm/xai/v1"

[model.grok-4.6]
base_url = "http://127.0.0.1:{port}/llm/xai/v1"
```

`GROK_CONFIG` / `GROK_CONFIG_PATH` cannot do this. That overlay is allowlisted
to soft settings (`models`, `features`, a narrowed `toolset`,
`shell_environment_policy` filters) and is **explicitly forbidden** from
redirecting network traffic. Do not try to inject the proxy URL through it.

### Forwarding VibeRails credentials (the "API key and secret")

The proxy does not want an xAI key pair. It wants the same two headers every
other CLI already sends:

| Header               | Env var Grok should read                         | Value |
|----------------------|--------------------------------------------------|--------|
| `viberails_session`  | `VIBERAILS_LLM_PROXY_SESSION_TOKEN`              | Instance session token (`IAuthService.GetInstanceToken()`) |
| `viberails_tab`      | `VIBERAILS_LLM_PROXY_TAB_TOKEN`                  | Tab token (`IAuthService.GetTabToken()`) |

Grok has no `GROK_CUSTOM_HEADERS` analogue of Claude's `ANTHROPIC_CUSTOM_HEADERS`.
It has Codex's shape: **`env_http_headers`** maps a request header to the *name*
of an environment variable. The value is read when Grok builds the HTTP client,
never written to disk. A blank/unset variable skips that header.

```toml
# ~/.grok/config.toml — header *names* and env-var *names* only. No token values.
# The dotted model name MUST be quoted: TOML splits unquoted dotted keys, so
# [model.grok-4.6] addresses the phantom table model."grok-4"."6" and grok silently
# never attaches the headers. This exact mistake shipped once (2026-08-23).
[model."grok-4.6"]
env_http_headers = { "viberails_session" = "VIBERAILS_LLM_PROXY_SESSION_TOKEN", "viberails_tab" = "VIBERAILS_LLM_PROXY_TAB_TOKEN" }

[model.grok-build]
env_http_headers = { "viberails_session" = "VIBERAILS_LLM_PROXY_SESSION_TOKEN", "viberails_tab" = "VIBERAILS_LLM_PROXY_TAB_TOKEN" }
```

A global `[models] env_http_headers` is **ignored** (only `extra_headers` has a global
form), and header injection is per-model **inference-only** — grok's ancillary GETs
(`/models`, `/settings`, `/bundle/archive`, `/feedback/config`, `/user`) never carry the
mapping, 401 at `CookieAuthMiddleware`, and grok tolerates that and still runs the
authenticated inference POST. All verified against grok 1.0.5 on 2026-08-23.

Launch-time environment (tokens in the process env, never on the command line):

```powershell
$env:VIBERAILS_LLM_PROXY_SESSION_TOKEN = "<session>"
$env:VIBERAILS_LLM_PROXY_TAB_TOKEN     = "<tab>"
$env:VIBERAILS_LLM_PROXY_BASE          = "http://127.0.0.1:{port}"   # MCP children; not Grok itself
$env:GROK_CLI_CHAT_PROXY_BASE_URL      = "http://127.0.0.1:{port}/llm/xai/v1"
$env:GROK_MODELS_BASE_URL              = "http://127.0.0.1:{port}/llm/xai/v1"
```

Do **not** put tokens in `query_params`. Grok persists query params in the
session log.

`extra_headers` is the static-value form of the same mechanism. Use it only for
non-secrets. Prefer `env_http_headers` for anything that rotates (session/tab).

### Upstream xAI credentials (separate from the proxy gate)

VibeRails authenticates the *local* hop. xAI authenticates the *upstream* hop.
`LlmProxyRelay` strips `viberails_session`, `viberails_tab`, `Cookie`, and
`Host` before forwarding; `Authorization` is passed through.

Grok's credential order for the upstream request:

1. Per-model `api_key` / `env_key`
2. Signed-in session (`grok login` → `~/.grok/auth.json`)
3. `XAI_API_KEY`

For the first slice (reuse `/llm/xai` → `https://api.x.ai`), prefer
`XAI_API_KEY`. That is the same secret OpenCode's `xai` provider already uses.
`grok login` tokens are minted for `cli-chat-proxy.grok.com` and are not assumed
to work against `api.x.ai`.

Do not set `GROK_HOME` to isolate config. Grok stores `config.toml` **and**
`auth.json` under `$GROK_HOME` (default `~/.grok`). Relocating it isolates
credentials the way setting OpenCode's `XDG_DATA_HOME` would, which we
deliberately do not do. Leave `GROK_HOME` unset so `grok login` / `XAI_API_KEY`
keep working globally.

---

## Target architecture

Today:

```
dropdown "Grok 4.6"
    → PTY: opencode --model=xai/grok-4.6
    → OPENCODE_CONFIG_CONTENT remaps xai.baseURL + headers
    → Kestrel ANY /llm/xai/{**rest}
    → CookieAuthMiddleware (session)
    → ILlmProxyAuthGate (session + tab)
    → ZaiBodyTransform (Chat Completions minify, provider "xai")
    → https://api.x.ai/{rest}
```

Wanted:

```
dropdown "Grok 4.6"
    → PTY: grok [-m grok-4.6] [--yolo] …
    → GROK_CLI_CHAT_PROXY_BASE_URL + GROK_MODELS_BASE_URL
    → env_http_headers → viberails_session / viberails_tab
    → Kestrel ANY /llm/xai/{**rest}          ← same route, same gate, same relay
    → https://api.x.ai/{rest}
```

OpenCode can keep offering `xai/grok-4.6` in its own model list. That is a
different CLI. Native Grok is not a pseudo-CLI of OpenCode.

```
                    grok (native TUI)
                         |
                         |  POST /llm/xai/v1/chat/completions
                         |  Authorization: Bearer <XAI_API_KEY or grok session>
                         |  viberails_session: <instance>
                         |  viberails_tab: <tab>
                         v
              CookieAuthMiddleware  (session header)
                         |
                         v
              LlmXaiProxyRoutes     (feature flag → 404 if off)
                         |
                         v
              ILlmProxyAuthGate     (session + tab)
                         |
          +--------------+--------------+
          |                             |
   Token Saver minify            LlmProxyRelay
   (Chat Completions,            strips local headers,
    provider "xai")              forwards Authorization
          |                             |
          +--------------+--------------+
                         |
                         v
                   https://api.x.ai/v1/chat/completions
```

URL construction already exists: `LlmProxyXaiConfig.BuildXaiBaseUrl(apiBaseUrl)`
returns `{apiBase}/llm/xai/v1`. Grok's `GROK_CLI_CHAT_PROXY_BASE_URL` is that
string. OpenCode's `xai.options.baseURL` is the same string. Do not invent a
second path prefix.

---

## Manual proof (before any code)

With VibeRails running, Token Saver / OpenCode LLM proxy enabled, and the
instance + tab tokens in hand (see
`runbooks/automation_auth_tokens/auto_auth_runbook.md`):

```powershell
$port = 12345   # this process's Kestrel port
$base = "http://127.0.0.1:$port"

$env:GROK_CLI_CHAT_PROXY_BASE_URL = "$base/llm/xai/v1"
$env:GROK_MODELS_BASE_URL         = "$base/llm/xai/v1"
$env:VIBERAILS_LLM_PROXY_SESSION_TOKEN = "<session>"
$env:VIBERAILS_LLM_PROXY_TAB_TOKEN     = "<tab>"
$env:XAI_API_KEY = "<xai-...>"   # if grok login is not enough for api.x.ai

# One-time: the two env_http_headers blocks in ~/.grok/config.toml shown above.

grok -p "Reply with the single word pong."
```

Expected:

- Unauthenticated (no session/tab): `401` from middleware or the proxy gate,
  never the HTML auth page.
- Feature disabled, valid session: `404` from `LlmXaiProxyRoutes`.
- Happy path: Grok streams a reply; Token Saver's xAI exchange log records the
  POST; upstream is `api.x.ai`.
- `viberails_session` / `viberails_tab` do **not** appear on the outbound
  request to xAI (`LlmProxyRelay.LocalOnlyHeaders`).

If this proof fails, stop. Do not add a listener to "fix" it.

---

## VibeRails launch contract (what to implement)

Mirror Claude's env-var injection and Codex's `env_http_headers`, not OpenCode's
inline JSON and not a new listener.

### 1. Binary and enum

| Today | Wanted |
|-------|--------|
| `ResolveCliExecutable(LLM.Grok46)` → `opencode` | → `grok` |
| `IOpencodeLlmCliLauncher` reused | New `GrokLlmCliLauncher` (`CliExecutable = "grok"`) |
| `--model=xai/grok-4.6` prepended on base launches | `-m grok-4.6` (Grok's model id, not OpenCode's `provider/model`) |
| `isOpencodeBackedCli('grok-4.6')` true | false |

`LLM.Grok46` and the wire name `grok-4.6` can stay. Enum-lowercased `grok46` is
not the executable — same special case as `agy`. Do not add `LLM.Grok` unless
the dropdown grows a generic "Grok" entry with a model picker; the current
product is the pinned 4.6 entry.

`LaunchLLMService` must route `LLM.Grok46` to the new launcher, not
`_opencodeLauncher`.

### 2. `CommandService.PrepareSessionAsync`

New block next to the Claude / OpenCode proxy blocks:

- Condition: proxy enabled (reuse `OpenCodeLlmProxyEnabled` for the first slice
  — it already gates `/llm/xai` — **or** a dedicated `GrokLlmProxyEnabled` if
  the settings UI should split "OpenCode proxy" from "native Grok proxy".
  Splitting is nicer; sharing the flag is the smaller diff. Either way the
  **route** stays `OpenCodeLlmProxyEnabled` until `API_SEC.md` is amended,
  because `LlmXaiProxyRoutes` already keys off that setting.)
- Skip if the session env already has `GROK_CLI_CHAT_PROXY_BASE_URL` (same
  "don't clobber an explicit override" rule as Claude / OpenCode).
- Set:
  - `GROK_CLI_CHAT_PROXY_BASE_URL` = `LlmProxyXaiConfig.BuildXaiBaseUrl(apiBase)`
  - `GROK_MODELS_BASE_URL` = the same string
  - `AddProxyContactDetails(environment, …)` so MCP children see
    `VIBERAILS_LLM_PROXY_BASE` / `_SESSION_TOKEN` / `_TAB_TOKEN`

Put the builder in TokenSaver as `LlmProxyGrokConfig.BuildGrokProxyEnvironment`,
alongside `LlmProxyClaudeConfig.BuildClaudeProxyEnvironment`. Same inputs
(`apiBaseUrl`, session, tab). The tab/session values go into
`AddProxyContactDetails`; Grok reads them via `env_http_headers`, not via a
custom-headers env var.

`LlmProxyProvider` needs a `Grok` value if the token-saver pause tools / session
state distinguish providers. If native Grok and OpenCode-xAI share `/llm/xai`,
they share exchange-log key `"xai"` (that is what `LlmXaiProxyRoutes` already
passes to the relay). Do not invent `"grok"` as a relay provider string unless
the route splits.

### 3. Header mapping on disk

Grok will not send `viberails_*` until `env_http_headers` exists in
`~/.grok/config.toml` (project `.grok/config.toml` is documented as contributing
only `[mcp_servers]`, `[plugins]`, and `[permission]` — do not put the mapping
there).

Recommended: a small merge into the user's `~/.grok/config.toml` at first proxied
Grok launch, writing **only** the two `env_http_headers` keys on `grok-4.6` and
`grok-build`. Token values stay out of the file. When those env vars are unset
(Grok launched outside VibeRails), Grok skips the headers and talks to the
default cli-chat-proxy — so the mapping is safe to leave in place.

Do not write `base_url` / `cli_chat_proxy_base_url` into that file. The port dies
with the process; the env vars are the source of truth.

### 4. Initial prompt, YOLO, model

| VibeRails control | Grok flag | Notes |
|-------------------|-----------|--------|
| Model pin         | `-m grok-4.6` (or `--model`) | Not `xai/grok-4.6`. |
| YOLO              | `--yolo` / `--always-approve` | Alias of `--permission-mode bypassPermissions`. |
| Initial message   | **Verify before wiring.** `-p` / `--single` is **headless** (process exits). Do not use `-p` for a TUI tab. Confirm whether a trailing positional starts the TUI with that text or also goes headless. If there is no interactive prompt flag, skip `LlmPromptArgvBuilder` for Grok (empty TUI, user types). |
| Workdir           | `--cwd <path>` and/or the PTY cwd VibeRails already sets | Prefer the PTY cwd; add `--cwd` only if Grok ignores it. |
| MCP               | `grok mcp add --scope user viberails-mcp -- {vb} mcp` | Non-interactive. Pair with `grok mcp remove` like Claude. |

`LlmPromptArgvBuilder` currently sends `--prompt=` for `LLM.Grok46` because it is
OpenCode-backed. That flag is OpenCode's. Native Grok must leave that branch.

### 5. MCP

`CommandService.GetMcpCommands`:

```
grok mcp remove viberails-mcp     # suppress stdout like the other CLIs
grok mcp add --scope user viberails-mcp -- {vb} mcp
```

`--scope user` writes `~/.grok/config.toml`. The stdio server inherits the Grok
process environment, including `VIBERAILS_LLM_PROXY_*`, which is what
`TokenSaverTool` already reads. No extra MCP headers required.

Keep `LLM.Grok46` in `McpClis`.

### 6. Environments / settings UI

Grok is closer to Copilot/Antigravity/OpenCode (launch-flag-only) than to
Claude/Codex (settings files), **except** the one-time `env_http_headers` merge
above.

- Drop `grok-4.6` from `isOpencodeBackedCli`.
- New arg builder: model pin, `--yolo`, additional args. No `opencode.json`, no
  `XDG_CONFIG_HOME`.
- `LlmCliEnvironmentService` should not delegate `LLM.Grok46` to
  `OpencodeLlmCliEnvironment`. A Grok env dir can exist for future settings; it
  must not become `$GROK_HOME`.
- Environments table uniqueness stays `(CustomName, LLM)`. Existing OpenCode-backed
  Grok 4.6 rows keep `LLM = 8`; their `CustomArgs` will need a one-time rewrite
  off `--model=xai/grok-4.6` onto `-m grok-4.6` (or ignore leftover OpenCode
  flags, which native Grok will reject).

### 7. Token Saver

No new minify path for the first slice. Native Grok Chat Completions against
`/llm/xai` is the body shape `ZaiBodyTransform` / `ChatCompletionsRewriter`
already handle with `provider: "xai"`. The OpenCode Token Saver toggle already
gates that transform.

If the settings UI grows a "Grok" saver switch, it still has to ride
`LlmXaiProxyRoutes` until there is a separate Kestrel map.

---

## Phase 2 (only if `grok login` must hit cli-chat-proxy)

Native Grok's default upstream is `cli-chat-proxy.grok.com`, not `api.x.ai`.
OpenCode's xAI provider is `api.x.ai`. They are different products:

| | OpenCode `xai` / first-slice native Grok | Full-fidelity Grok CLI |
|---|---|---|
| Host | `api.x.ai` | `cli-chat-proxy.grok.com` |
| Auth | `XAI_API_KEY` | `grok login` session, or API key |
| Extra headers Grok sends | unused | `X-XAI-Token-Auth: xai-grok-cli`, `x-grok-model-override` |
| Route | `/llm/xai` (exists) | New Kestrel map, **not** `/llm/grok`, **not** a new listener |

A Phase-2 route would look like `LlmXaiProxyRoutes`: `app.Map(...)` on the main
host, same `ILlmProxyAuthGate`, same header strip, different `UpstreamHost`.
That PR must amend `API_SEC.md` (inventory count + listener-discovery pass) in
the same change. `LlmProxyRelay` already forwards unknown headers, so Grok's
cli-chat-proxy headers would survive the hop.

Do not start Phase 2 until the first slice is proven with `XAI_API_KEY` against
`/llm/xai`.

---

## Files that will have to move (when implementing)

Not a license to do them in this write-up; the list is so the work stays on the
existing seams.

| Area | Files |
|------|--------|
| TokenSaver builder | New `TokenSaver/LlmProxyGrokConfig.cs` (env dict). Tests next to `LlmProxyClaudeConfigTests`. |
| Launch | `CommandService.cs` (`ResolveCliExecutable`, proxy env block, `GetMcpCommands`, pinned model), `LaunchLLMService.cs`, `LlmPromptArgvBuilder.cs` |
| Launcher | New `GrokLlmCliLauncher.cs`; stop routing `Grok46` through OpenCode in `LlmCliEnvironmentService` / `LaunchLLMService` |
| Enum/docs | `DTOs/LLM.cs` comment, `DB/AGENTS.md`, `Launchers/AGENTS.md`, `runbooks/custom_envs/CLI_OPTIONS.md` |
| UI | `environment-controller.js` (`isOpencodeBackedCli`, `pinnedModelForCli`, arg builders), `utils.js` `getLlmName` if needed |
| Settings | Only if splitting `OpenCodeLlmProxyEnabled`; otherwise leave the flag and document that it also covers native Grok's use of `/llm/xai` |

---

## What not to do

- Do not add `GrokLoopbackBridge`, `HttpListener`, a second Kestrel host, or
  bind ports 6000–6999 "for Grok".
- Do not add `/llm/grok`.
- Do not put `/llm/**` back on the `CookieAuthMiddleware` skip list.
- Do not send VibeRails tokens as CLI argv (`grok --header …`). They show up in
  process listings. Env vars / `env_http_headers` only.
- Do not put tokens in `GROK_CONFIG`, `query_params`, or `config.toml`.
- Do not set `GROK_HOME` to the VibeRails env directory.
- Do not use `GROK_CONFIG` to set `base_url` / `GROK_CLI_CHAT_PROXY_BASE_URL`.
- Do not launch TUI tabs with `grok -p` (headless; the process exits).
- Do not keep prepending `--model=xai/grok-4.6` once the binary is `grok`.
- Do not treat this document as an `API_SEC.md` amendment. Phase 2 still needs
  that amendment in its own PR.

---

## Related reading

- [`API_SEC.md`](API_SEC.md) — listener freeze, `/llm/xai` auth shape, rejected bridge
- [`TokenSaver/README.md`](TokenSaver/README.md) — relay + Chat Completions minify
- [`TokenSaver/LlmProxyXaiConfig.cs`](TokenSaver/LlmProxyXaiConfig.cs) — URL builder reused here
- [`TokenSaver/LlmProxyClaudeConfig.cs`](TokenSaver/LlmProxyClaudeConfig.cs) — env-var injection twin
- [`TokenSaver/LlmProxyCodexConfig.cs`](TokenSaver/LlmProxyCodexConfig.cs) — `env_http_headers` twin
- [`runbooks/custom_envs/CLI_OPTIONS.md`](runbooks/custom_envs/CLI_OPTIONS.md) — current OpenCode-backed Grok 4.6
- [`runbooks/automation_auth_tokens/auto_auth_runbook.md`](runbooks/automation_auth_tokens/auto_auth_runbook.md) — how to mint the two headers
- Grok Build docs: `~/.grok/docs/user-guide/02-authentication.md`,
  `05-configuration.md`, `07-mcp-servers.md`, `11-custom-models.md`,
  `14-headless-mode.md`
