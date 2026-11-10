# UI Testing Suite

This directory contains the [Playwright](https://playwright.dev/) end-to-end testing suite for the VibeRails frontend, plus a small set of Node-native unit tests.

## Prerequisites

- [Node.js](https://nodejs.org/) installed on your system.
- .NET 11 SDK for the production snapshot fixtures used by the scrollback and Automation tests.

## Setup

Before running tests for the first time, install the dependencies:

```powershell
# Install npm packages
npm install

# Install browser binaries (with OS deps)
npx playwright install --with-deps chromium
```

## Running Tests

### All Tests
Run both the Node-native and Playwright suites (matches `npm test`):
```powershell
npm test
```

### Node-Native Unit Tests Only
Pure-Node tests (no browser) run via the built-in test runner:
```powershell
npm run test:node
```
This runs `tests/xterm-scrollback.spec.js` plus every pure frontend module regression under
`../Tests/wwwroot/js/*.test.mjs` using `node --test`.

### Playwright E2E Only
Run all E2E tests in headless mode (console output):
```powershell
npm run test:e2e
# or
npx playwright test
```

### UI Mode (Recommended for Debugging)
Open the interactive Playwright UI to see tests running step-by-step:
```powershell
npx playwright test --ui
```

### Focused Quality UX Tests (No Backend Required)

```powershell
npx playwright test --config playwright.quality.config.js
```

This starts a temporary local static server and runs `code-quality-ux.spec.js` against the real
frontend, combined code report viewer and shared agent picker, with mocked API responses and launch dispatch.
It needs no database or installed LLM CLI and does not change the standard suite's configuration.
Set `VIBERAILS_QUALITY_TEST_PORT` to use another local port if the default 18763 is occupied.
The tests cover compact actions, file-to-map selection, visible domain links and large overviews,
saved metrics, radar keyboard access, themes/reduced motion, independent scrolling, narrow layouts,
failure/empty states, request cancellation and cleanup, exclusion/restore flow, agent choice,
independent Rules/Quality checks, and responsive navigation with Settings in the account dropdown.
The file-details cases exercise the real Monaco reader, finding-line navigation, captured
excerpts, source fallbacks, ignore/cancel/rescan, file-scoped fix prompts, launch failure,
picker placement inside the native dialog, cleanup and 320–1440px layouts.

### Focused Page Header Tests (No Backend Required)

From `UITests`, run `npx playwright test --config playwright.page-headers.config.js`.
Also runs `design-system.spec.js` for button states/contrast, focus, fields, alerts,
modal chrome, touch sizing and the VS Code theme adapter. Uses the five production views that share the header, with intercepted APIs, to compare title typography and header
treatment at desktop, tablet and phone widths, exercise retained controls, and verify theme colors.
The static fixture never opens application databases or launches a CLI.

### Focused MCP remote frame test (No Backend Required)

From `UITests`, run `npx playwright test --config playwright.mcp-remote.config.js`.
Reuses the page-header static server. It connects the real MCP Explorer to an intercepted remote
endpoint and calls a tool in the real sandboxed `mcp-remote.html` frame. It also checks that the
only window message the frame receives is the port handshake. No MCP server is contacted.

### Focused Board UX Tests (No Backend Required)

```powershell
npx playwright test --config playwright.board.config.js
```

Uses the real frontend with mocked board APIs to verify compact board controls, description
image rendering/editing/upload/deletion, save/reopen, running-agent controls, and desktop/narrow layouts.
Description coverage includes shared inline reference highlighting, compact variable shortcuts,
caret placement and Undo, plus pending/saved image thumbnails and the attachment viewer.
It starts a temporary static server and never touches the machine's board database.

`board-picker.spec.js` holds a board-switch response and pauses the browser clock to cover
picker timing. Wait for the board's loading state to clear before changing a snapshot fixture;
the selected item updates before its board finishes loading. Wait for the search to be visible
and focused before sending keyboard input; Tom Select opens it asynchronously.

### Focused PR Reviews tests (No Backend Required)

From `UITests`, run `npx playwright test --config playwright.pr-reviews.config.js`.
Uses installed Chrome and the production frontend with intercepted APIs to check draft preservation
across detail actions, save-time input locking, and starting a pass at the current GitHub head.
No application database is opened and no GitHub request or CLI is launched.

### Focused public-card sharing tests

From the repository root, run `node UITests/node_modules/@playwright/test/cli.js test --config UITests/playwright.card-sharing.config.js`.
Uses the actual Board editor and synthetic API responses with installed Chrome. Covers desktop/mobile
create, rename, revoke, manual refresh, escaped link labels, draft preservation and uncertain/late results.
No application database is opened and no card is uploaded.

### Focused AI Agents editor tests (No Backend Required)

Run `node UITests/node_modules/@playwright/test/cli.js test --config UITests/playwright.ai-agents.config.js`
from the repository root. The production frontend uses intercepted typed definition,
binding, run and attachment APIs. Tests cover provider drafts, typed references, revision
conflicts, navigation guards, staged-upload retries, caret placement, history preservation,
Action fields, schedules and layouts at 320/375/768/1024px. Prompt-variable shortcuts cover
click/keyboard insertion, selection replacement, undo and saved literal placeholders.
Inline reference tests cover native caret alignment, source preservation, escaping, spelling,
composition and forced colors. Image previews cover pending files, saving/reopening and
the owner-specific attachment viewer on desktop and narrow screens.
The Board harness also covers
the new ordered lane and manual-card adapters alongside existing comments, attachment,
manager and navigation behavior. The older `playwright.agents-editor.config.js` fixtures
exercise the retired Environment/Job editor and are not the current acceptance harness.
No application database or CLI is used.

### Focused Automation terminal menu tests (No Backend Required)

```powershell
npx playwright test --config playwright.terminal-automations.config.js
```

Uses real terminal UI/xterm with intercepted API and WebSocket fixtures. Covers a long robot
menu, finished-agent removal, retained output through Board links and reload, launch events/remount, independent close/undo,
and narrow-window keyboard access. Does not launch a CLI or touch application state.
Snapshot fixtures are generated from current backend capture/serialization code by
`Tests/headless/TerminalSnapshots`. The headless xterm tests check every history/screen row,
repeat attach, cursor position and live input modes; browser tests use those same bytes and
cover switching away and back while a completed write is pending.
Fixture builds use `Tests/obj/terminal-snapshot-artifacts` so a running Debug app does not lock
their build output.

### Focused chat history tests (No Backend Required)

From `UITests`, run `npx playwright test --config playwright.history.config.js`.
This mounts the production sidebar and styles with paged API fixtures. It checks combined filters
against older sessions, Board titles, escaped metadata, narrow scrolling and remount behavior.
It opens no application database and launches no CLI.

### Focused file picker tests (No Backend Required)

From `UITests`, run `npx playwright test --config playwright.file-explorer.config.js`.
This mounts the production picker with filesystem API fixtures to check per-project folder
memory, project switching, legacy preferences, explicit starting paths and cancellation.
It opens no application database and selects only fixture files.

### Focused scripts layout tests (No Backend Required)

From `UITests`, run `npx playwright test --config playwright.scripts.config.js`.
Uses the production Automation page, Monaco and xterm with intercepted APIs/WebSockets.
Checks the terminal prompt stays inside desktop and short windows under the top navigation,
splitter resizing, navigation cleanup, scripts toolbar alignment and narrow-screen guidance.
Verbose Run/Close flows also check usable editor space and drawer scrolling at 1000×500
and 1000×400. A stored side-navigation preference must leave the top bar in place.
It never reads or changes real scripts, signing state, terminals or application databases.

### Focused account sign-in tests (No Backend Required)

From `UITests`, run `npx playwright test --config playwright.remote-link.config.js`.
This serves the real frontend with local API fixtures and an intercepted sign-in page. It covers
browser popup behavior, copy code, the VS Code bridge, narrow layout, completion alongside unsaved
settings, cancellation, and unavailable/denied/expired requests. It also checks the signed-out top-navigation link, the Settings account summary after reload, cross-tab completion and escaped
emails, signed-out Settings access from 320px to desktop widths, and the signed-in account chip: its popover's personal, key-only and managed-organization
states, Escape and outside-click closing, drafts kept by **Account settings**, the compact bar at
1280px and a 375px window. It opens no application database
and creates no account or API key.

### View Report
If a test fails, you can view the detailed HTML report:
```powershell
npx playwright show-report
```

## Configuration

- **Backend:** `global-setup.js` spawns the real VibeRails backend (`dotnet run --project ../VibeRails -c Debug -- --vs-code-v1`), consumes the one-time bootstrap URL to persist the auth cookie via `storageState` (the tab token lives in `sessionStorage`, which `storageState` does not persist, so it is captured separately for `fixtures.js` to re-inject), and writes the dynamic `baseURL` to `.playwright-runtime.json`. `global-teardown.js` kills the backend by PID.
- **Fake CLI:** `VIBERAILS_TEST_FAKE_CLI=1` (set by global-setup) makes `CommandService.PrepareSessionAsync` short-circuit to a portable echo+sleep so PTY+WS+xterm are exercised without a real LLM CLI.
- **Custom backend:** Set `VIBERAILS_E2E_BACKEND_DLL` to point the suite at an already-built isolated DLL instead of `dotnet run`.
- **Workers:** 1 (one backend instance per run, shared via saved `storageState`).
- **Tests:** Playwright specs live in `./tests`; `xterm-scrollback.spec.js` is excluded from Playwright and runs via `npm run test:node` with `node --test` + `@xterm/headless`. That command also runs every `../Tests/wwwroot/js/*.test.mjs` frontend module regression from the repository root.

## Adding New Tests

When adding features to app.js or index.html, add a corresponding spec file in ./tests/*.spec.js to ensure the UI interactions remain functional.

---

*Last checked: 2026-08-06T17:22:10Z by opencode (glm-5.2)*

### Private script terminal tests (No Backend Required)

From `UITests`, run `npx playwright test --config playwright.script-run.config.js`.
Uses the production xterm window with a simulated socket to check typing, Stop/Close, PIN
cancellation, narrow layout and minimize/restore across ordinary SPA navigation. The live fixture
also exposes a notes field to demonstrate that the page remains usable while minimized.
Real interpreter input, getpass, cleanup and approval checks are covered by the .NET
`PythonScriptTerminalTests` suite. Neither fixture records user input.
