# UI Testing Suite

This directory contains the [Playwright](https://playwright.dev/) end-to-end testing suite for the VibeRails frontend, plus a small set of Node-native unit tests.

## Prerequisites

- [Node.js](https://nodejs.org/) installed on your system.

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
