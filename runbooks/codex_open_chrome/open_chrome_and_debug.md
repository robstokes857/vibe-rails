# Open Chrome and debug VibeRails

Verified on Windows, 2026-09-07. For UI work, use **visible Chrome controlled by the
persistent Node tool**. This gives the agent screenshots, clicks, DOM inspection,
console errors, and network responses while the user sees the same window.

**Start here: reuse the live session before launching anything.** In `mcp__node_repl__js`:

```javascript
nodeRepl.write({
  connected: typeof debugChrome !== 'undefined' && debugChrome.isConnected(),
  pageOpen: typeof debugChromePage !== 'undefined' && !debugChromePage.isClosed(),
  backendRunning: typeof debugVb !== 'undefined' && debugVb.exitCode === null,
  executable: typeof debugVb !== 'undefined' ? debugVb.spawnfile : null
});
```

If all three checks are true, go straight to **See and interact** below. Check the
executable before expecting working-tree edits to appear. Keep these handles across
turns; do not reset the Node session. Use top-level `var` in snippets so rerunning
declarations does not cause `Identifier has already been declared` errors.

Follow applicable browser/Chrome skills when provided in the current session.
The standalone Chrome recipe below worked when no connected browser plugin was
available. Once this workflow is established, reuse it rather than repeatedly
rediscovering the unavailable in-app browser.

## Fresh launch

### 1. Choose the executable

For a quick check of the installed app, use
`C:/Users/robst/.vibe_rails/vb.exe` (the `vb` command).

For **working-tree UI changes**, build an isolated copy from the repository root:

```powershell
dotnet build VibeRails\VibeRails.csproj --artifacts-path .codex-test-artifacts\chrome-ui --verbosity minimal
```

Then use
`C:/source/vibe-rails/.codex-test-artifacts/chrome-ui/bin/VibeRails/debug/vb.exe`.
Build once, not on every browser interaction.

**Launch `vb.exe`, not `dotnet vb.dll`.** The latter authenticates the root app but
currently breaks automatic terminal child creation: the child launcher uses
`Environment.ProcessPath`, which becomes `dotnet` without the DLL argument. This
caused HTTP 400 responses from `POST /api/v1/terminal/tabs` during our first attempts.

### 2. Open visible, controllable Chrome

Run in `mcp__node_repl__js`, not a short-lived shell script. The repository already
has Playwright in `UITests/node_modules`; do not reinstall it unless it is missing.

```javascript
var chromeRequire = (await import('node:module'))
  .createRequire('C:/source/vibe-rails/UITests/package.json');
var chromePlaywright = chromeRequire('@playwright/test');
var debugChrome = await chromePlaywright.chromium.launch({
  channel: 'chrome', headless: false
});
var debugChromeContext = await debugChrome.newContext({ viewport: null });
var debugChromePage = await debugChromeContext.newPage();
var debugChromeErrors = [];
var debugChromeApiFailures = [];
debugChromePage.on('pageerror', error => debugChromeErrors.push(error.message));
debugChromePage.on('response', response => {
  var pathname = new URL(response.url()).pathname;
  if (response.status() >= 400 && pathname.startsWith('/api/')) {
    debugChromeApiFailures.push({
      path: pathname, method: response.request().method(), status: response.status()
    });
  }
});
nodeRepl.write({ chromeConnected: debugChrome.isConnected() });
```

This opens installed Google Chrome with a debugging connection managed by
Playwright. No fixed remote-debugging port or user-profile changes are needed.
`vb --web` alone opens a normal browser window; it does not create this connection.

### 3. Start VibeRails and capture its login link in memory

Choose the executable from step 1. This example uses the working-tree build.
Run this block, then the next block promptly; do not print the bootstrap URL.

```javascript
var debugVbExe = 'C:/source/vibe-rails/.codex-test-artifacts/chrome-ui/bin/VibeRails/debug/vb.exe';
var debugVb = (await import('node:child_process')).spawn(
  debugVbExe, ['--vs-code-v1'], {
    cwd: 'C:/source/vibe-rails', windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe']
  }
);
var debugVbStartup = { ready: false, error: null };
var debugVbBootstrap = new Promise((resolve, reject) => {
  var buffer = '';
  var timer = setTimeout(() => reject(new Error('VibeRails startup timed out')), 90000);
  debugVb.once('error', error => { clearTimeout(timer); reject(error); });
  debugVb.once('exit', code => {
    clearTimeout(timer);
    reject(new Error('VibeRails exited during startup: ' + code));
  });
  debugVb.stderr.on('data', () => {});
  debugVb.stdout.on('data', chunk => {
    buffer = (buffer + chunk.toString()).slice(-16000);
    var match = buffer.match(/vs-code-v1=(\S+)/);
    if (match) { clearTimeout(timer); resolve(match[1]); }
  });
});
// Track readiness without blocking a tool call for the entire cold start.
void debugVbBootstrap.then(
  () => { debugVbStartup.ready = true; },
  error => { debugVbStartup.error = error.message; }
);
nodeRepl.write({ backendPid: debugVb.pid });
```

### 4. Authenticate in that same Chrome tab

Check startup in a short call. If still starting, check again shortly without
relaunching. An error requires diagnosis; do not consume the bootstrap until ready.

```javascript
nodeRepl.write(debugVbStartup);
```

Once ready, run the following two blocks in **separate calls**, each with tool
`timeout_ms: 60000`. This keeps their combined waits within each call's budget.

```javascript
await debugChromePage.goto(await debugVbBootstrap, {
  waitUntil: 'domcontentloaded', timeout: 15000
});
await debugChromePage.waitForFunction(
  () => !!sessionStorage.getItem('viberails_tab') && !!window.app?.terminalTokenCompression,
  null, { timeout: 30000 }
);
```

```javascript
// The initial terminal shell paints before its async setup finishes.
await debugChromePage.locator('[data-terminal-focus-content]:not([aria-busy="true"])')
  .waitFor({ state: 'visible', timeout: 30000 });
var debugAuthStatus = await debugChromePage.evaluate(async () =>
  (await fetch('/api/v1/settings', {
    headers: { viberails_tab: sessionStorage.getItem('viberails_tab') },
    signal: AbortSignal.timeout(10000)
  })).status
);
nodeRepl.write({ origin: new URL(debugChromePage.url()).origin, authStatus: debugAuthStatus });
```

Expected: API status **200**. Chrome holds the HttpOnly session cookie and the page
holds the tab token in `sessionStorage`; neither needs to be printed or saved.
The bootstrap link is single-use, expires in two minutes, and an unused link causes
the backend to shut down. **Do not consume it with curl before opening Chrome.**
Full authentication details: [auth runbook](../automation_auth_tokens/auto_auth_runbook.md).

## See and interact

Run these snippets in the same persistent Node session. Take a screenshot before
assessing appearance and after meaningful UI changes; DOM text alone is not visual QA.

```javascript
// Show the actual Chrome viewport to the agent.
await nodeRepl.emitImage({
  bytes: await debugChromePage.screenshot({ fullPage: false }), mimeType: 'image/png'
});
```

```javascript
// Example: use actual UI controls, then wait for the destination.
await debugChromePage.locator('.app-subnav-link[data-view="environments"]:visible').click();
await debugChromePage.locator('.view[data-view="environments"]').waitFor({ state: 'visible' });
nodeRepl.write({ pageErrors: debugChromeErrors, apiFailures: debugChromeApiFailures });
```

Use `debugChromePage.locator(...)`, `getByRole(...)`, `fill(...)`, and `click(...)`
for further work. Use `await debugChromePage.locator('body').innerText()` only when
the page text is useful; avoid dumping entire pages repeatedly. Attach targeted
`console`, `requestfailed`, or `response` listeners when investigating a specific
problem. Keep credentials and full bootstrap URLs out of diagnostics.

## Working-tree edits and session lifetime

- Static assets are served from **`wwwroot` beside the running executable**, not
  automatically from `VibeRails/wwwroot` in the checkout. For frontend-only edits,
  copy the changed files to that output `wwwroot`, preserving their relative paths,
  then `await debugChromePage.reload({ waitUntil: 'domcontentloaded' })`. If using
  the installed app, switch to an isolated working-tree build before editing assets.
- Reload keeps authentication but normally returns to the **Terminals** view.
  Wait for initialization, then navigate to the UI under test again.
- A fresh browser tab does not automatically inherit the tab token. Reuse
  `debugChromePage`. Restarting the backend requires a fresh bootstrap login.
- Leave the connected Chrome window and backend running between UI tasks.
  `debugChromeErrors` and `debugChromeApiFailures` accumulate; clear them with
  `.length = 0` when starting a new observation period, without adding duplicate listeners.
- To intentionally stop **only this session**, close `debugChrome` and kill the
  known `debugVb` process. Do not kill unrelated Chrome or VibeRails instances.
- Avoid background shell helpers for this workflow: a non-PTY `exec_command`
  session can close stdin and leave a browser the agent cannot command. The
  persistent Node tool keeps the page and process handles available directly.

Once Chrome is connected, auth returns 200, and one click works, start the requested
UI work. No repeated full smoke suite, extra headless browser, or port hunt is needed.
