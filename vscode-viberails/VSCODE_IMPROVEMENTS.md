# VS Code Extension — Improvement Backlog

Audit done 2026-07-21 against `extension.ts`, `backend-manager.ts`, `webview-panel.ts`, tests, `package.json`, `AGENTS.md`.

## High-value, low-effort

1. **Status bar shows no state** — it always says `$(rocket) VibeRails`. Add states: starting (sync~spin icon), ready (rocket), stopping, error. Track via `BackendManager.onPortDetected` + a new `onExit` event.
2. **Backend crash detection after startup** — `backend-manager.ts:120` only handles exit during startup. After ready, an exit goes to the output channel silently. Subscribe to `exit` post-ready and show a `showErrorMessage` with a "Restart" action.
3. **Fix `AGENTS.md` drift** — it says status bar uses `$(circuit-board)` but code uses `$(rocket)`; says "Close the panel or click Exit to stop" but closing the tab also stops the backend; doesn't mention the Stop status bar item at all. (Note: the bogus `viberails.executablePath` doc was already removed on 2026-07-21.)
4. **Add a `CHANGELOG.md`** — marketplace listing is bare without one. Standard for VS Code extensions.
5. **Expand `categories` and add `keywords`** in `package.json` — currently just `"Other"`. Add `"Machine Learning"`, `"Snippets"`, `"Education"` and keywords like `ai`, `llm`, `claude`, `codex`, `agents`.
6. **Strip unused font CSP entries** — `webview-panel.ts:108,110` allows `fonts.googleapis.com`/`fonts.gstatic.com` but the code at line 96-99 already removes those `<link>` tags. Tighten the CSP.
7. **Move `VIBERAILS_SMOKE_WORKSPACE` check out of production code** — `extension.ts:162` reads a test env var in the live workspace-folder resolver. Pass it through a test-only command or DI instead.

## Lifecycle / reliability

8. **Decouple backend lifecycle from panel visibility** — closing the webview tab kills the backend (and your terminal sessions). Many users will hit this accidentally. Add a setting `viberails.keepBackendAlive` (default `false` to preserve current behavior) so closing the panel just hides it; only the explicit Exit button or VS Code shutdown stops the backend.
9. **Health check after bootstrap** — `fetchTokens` (`extension.ts:211`) grabs the session cookie but never pings `/api/v1/IsLocal` to confirm the server is actually serving. Add one retry-with-backoff call before resolving.
10. **Retry `fetchTokens`** — single HTTP GET, 5s timeout. Transient failures (backend slow to bind socket) just fail the whole open. 2-3 retries with 500ms backoff would help on slow machines.
11. **Make the 30s startup timeout configurable** — `backend-manager.ts:131` hardcodes it. Cold AOT first-launch on a slow laptop can exceed that. Add `viberails.startupTimeoutMs` setting.
12. **Graceful shutdown via HTTP** — `backend-manager.ts:160` writes `\n` to stdin and hopes the CLI loop catches it. If the backend exposes a shutdown endpoint, POST to it first, then fall back to stdin/SIGTERM/taskkill.
13. **`cancellable: true` on the open progress** — `extension.ts:113` is non-cancellable. A hung start locks the user out for 30s.

## UX / commands

14. **Add a keybinding for `viberails.open`** — none defined. Something like `Ctrl+Shift+V` (when not in a chat panel) would be natural.
15. **"Open in Browser" command** — pop the dashboard out to the default browser for when you want it on a second monitor or outside VS Code.
16. **Multi-root workspace folder picker** — `getCurrentWorkspaceFolder` (`extension.ts:148`) silently picks the active editor's folder or `workspaceFolders[0]`. Add a "VibeRails: Switch Project…" command that shows a quick pick.
17. **Quick-pick commands for power users** — `VibeRails: Launch Environment…`, `VibeRails: Create Sandbox…`, `VibeRails: Recent Sessions` — invoke via API without opening the dashboard. Nice for keyboard-driven workflows.
18. **Sidebar tree view** — `contributes.views` with Agents / Environments / Sandboxes / Sessions. Click to open the dashboard pre-routed to that view.
19. **Code lens / decorators on `agent.md`** — show enforcement level (WARN/COMMIT/STOP) inline above each rule. Native VS Code feel the webapp can't replicate.
20. **Native VS Code terminal integration** — instead of running LLM CLIs in the webview xterm, offer an option to spawn real VS Code integrated terminals with the right env vars. Lets users use VS Code's terminal features (search, links, split).
21. **Walkthrough for onboarding** — `contributes.walkthroughs` is the modern VS Code way to onboard new users. "Open dashboard", "Create first environment", "Launch a CLI".

## Tests

22. **Test coverage is minimal** — one 17-line unit test + one slow smoke test. Add unit tests for:
    - `fetchTokens` (cookie parsing, URL-encoded base64, missing tab header, non-2xx)
    - `rewriteAssetPaths` (leading `./`, `/`, `//`, `data:`, `http`)
    - `buildHtml` (nonce present in all `<script>`, CSP contains expected hosts, token injection)
    - `getSupportedExtensionTarget` (platform matrix)
    - `getCurrentWorkspaceFolder` (active editor, no editor, multi-root, env override)
23. **Drop the `(manager as any)` cast** in `backend-manager.test.ts:9` — expose a test-only static or use a small testable helper.
24. **Mock backend process in unit tests** — current tests don't exercise the spawn/parse/stop paths. A fake `cp.spawn` would let you test the `vs-code-v1=` line parsing, timeout, and shutdown sequence deterministically.
25. **Add a CI workflow for the extension** — `npm run compile && npm test` on Windows/macOS/Linux. The smoke test already supports `VIBERAILS_TEST_FAKE_CLI=1` and `VIBERAILS_VSCODE_CLI`.

## Code quality

26. **Add ESLint + Prettier** — TypeScript strict is on (good) but no linter. `npm i -D eslint @typescript-eslint/eslint-plugin prettier` + a minimal config.
27. **Centralize command/string constants** — `'viberails.open'`, `'viberails.stop'`, `'viberails._test.getConnectionInfo'`, `'vs-code-v1='`, `'viberails_session'`, `'viberails_tab'` are scattered. One `constants.ts` would help.
28. **Contract-driven exit button** — `webview-panel.ts:156-231` injects an Exit button by looking for `.nav-actions`. If the dashboard markup changes, the button vanishes silently. Have the dashboard itself emit a `vscode:ready` postMessage and let the extension ask the dashboard to add the button via a documented API.
29. **Guard `BackendManager.shutdown`** — `backend-manager.ts:255` disposes the output channel; any subsequent log call throws. Add a `disposed` guard on the log path.
30. **Remove dead "Runtime Install Flow"** — `ABOUT.md` says the extension installs VibeRails from GitHub releases when missing, but `resolveBundledAssets` just throws. Either implement it or remove the claim.
31. **`backendManager ??=` keeps stale exePath** — `extension.ts:111` reuses the manager across opens, so if the bundled path ever changes (different workspace, reinstalled extension) the old path is kept. Recreate when path differs.

## Marketplace polish

32. **Add marketplace badges** (version, downloads, license, build status) to README.
33. **Add a VS Code-specific screenshot/GIF** — current images are generic dashboard screenshots; show the dashboard *embedded in a VS Code tab*.
34. **Declare `capabilities`** — `untrustedWorkspaces`, `virtualWorkspaces`. Currently unspecified, which makes admins nervous.
35. **Add `preview: false`** explicitly to signal stable.
36. **Engines upper bound** — `^1.85.0` has no upper bound; VS Code 1.99+ may introduce breaking API. Pin to a tested max or use proposed-api only with explicit gating.

## Documentation

37. **README doesn't mention bundled backend** — users don't know the extension ships a full .NET binary per-platform. Mention install size (~50-100MB) and that no separate `vb` install is needed.
38. **README missing troubleshooting** — "dashboard won't open", "port already in use", "backend crashed" sections.
39. **CONTRIBUTING.md** missing.

## Top-5 if you only do a few

1. Backend crash detection + status bar state (#1, #2) — biggest perceived reliability win
2. Decouple backend from panel close (#8) — prevents accidental session loss
3. Add unit tests for `fetchTokens` / `rewriteAssetPaths` / `buildHtml` (#22) — fast, high coverage gain
4. Sidebar tree view + quick-pick commands (#17, #18) — turns the extension from "webview wrapper" into a real native citizen, which is the whole reason to ship a VS Code extension
5. Fix `AGENTS.md` drift (#3) — quick credibility fix
