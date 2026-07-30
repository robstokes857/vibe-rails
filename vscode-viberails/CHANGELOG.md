# Changelog

All notable changes to the VibeRails VS Code extension are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `Ctrl+Alt+V` (`Cmd+Alt+V` on macOS) keybinding for **VibeRails: Open Dashboard**.
- `viberails.startupTimeoutMs` setting so a slow cold start can be given more than the previous hard-coded 30 seconds.
- Readiness probe against the backend's `/health` endpoint after bootstrap, so a backend that binds its port before it can serve requests surfaces as an error instead of a blank dashboard.
- Marketplace metadata: `AI` / `Machine Learning` categories, keywords, and declared `untrustedWorkspaces` / `virtualWorkspaces` capabilities.
- `CHANGELOG.md` and `CONTRIBUTING.md`; README now documents the bundled backend and common troubleshooting.

### Changed

- Development and extension-host baselines now use Node.js 24 LTS, VS Code 1.125+, and TypeScript 7.
- Backend shutdown now asks the backend to stop over HTTP (`POST /api/v1/shutdown`) before closing stdin and, only as a last resort, killing the process tree.
- Bootstrap token fetches retry up to three times only on `ECONNREFUSED`. Timeouts, connection resets, and HTTP responses are never retried because the one-time code may already be spent; an expired or reused code now reports a clear message.
- The backend manager is recreated when the bundled executable path changes (for example after a reinstall) instead of reusing a stale path.

### Removed

- The webview no longer injects its own Exit button by scraping dashboard markup. The dashboard ships and wires its own Exit buttons; the extension contract is the injected `__viberails_VSCODE__` / `__viberails_close__` / `__viberails_setTitle__` globals.
- Compiled test files are no longer packaged into the VSIX.

### Fixed

- Logging after the backend manager is disposed no longer risks throwing on a disposed output channel.
- The `VIBERAILS_SMOKE_WORKSPACE` test fallback is now ignored in production installs.

## [1.9.4] - 2026-07-29

### Added

- Cross-process data export and token publishing.

## [1.9.3] - 2026-07-28

### Added

- Proxy exchange capture for LLM traffic.

### Changed

- Hardened Git preflight checks.

## [1.9.0] - 2026-07-22

### Added

- VCA git-hook validation surfaced in a dedicated console window on Windows.

## [1.8.9] - 2026-07-17

### Added

- OpenCode support alongside Claude, Codex, Antigravity, and Copilot.

## [1.8.8] - 2026-07-15

### Added

- Local LLM proxy (v1) with token-saving stages and VCA rule updates.

## [1.8.0] - 2026-06-26

### Added

- Per-tab push notifications with terminal screenshots.
- Agent terminal tools and MCP settings propagation.

## [1.7.0] - 2026-06-02

### Added

- Encrypted remote debug bundles.
- Terminal text editor.
- Antigravity CLI environment (replacing the Gemini CLI), with improved terminal tab titles for custom and shell sessions.

Earlier history: see the `v1.x.x` git tags in the repository.
