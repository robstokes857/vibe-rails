# Contributing to the VibeRails VS Code Extension

Thanks for helping out. This document covers the extension in `vscode-viberails/`; the backend it embeds lives in `VibeRails/` in the same repository.

## Prerequisites

- **Node.js 24 LTS+** and npm (`nvm use` reads the repository-root `.nvmrc`; both local development and the release workflow use it)
- **PowerShell 7+** (`pwsh`) — every build/package/release script is PowerShell and runs on Windows, Linux, and macOS
- **.NET 10 SDK** — needed only when you build the bundled backend
- **VS Code 1.125+**

## Repository layout

| Path | What it is |
|---|---|
| `vscode-viberails/src/extension.ts` | Activation, commands, status bar, bootstrap/health handshake |
| `vscode-viberails/src/backend-manager.ts` | Spawns the backend, parses its `vs-code-v1=` line, owns the shutdown ladder |
| `vscode-viberails/src/webview-panel.ts` | Webview panel, CSP, asset rewriting, token injection |
| `vscode-viberails/src/constants.ts` | Shared strings and timings. **Must not import `vscode`.** |
| `vscode-viberails/src/test/` | `runTest.ts` + the Electron smoke suite |
| `vscode-viberails/bin/<target>/` | Staged backend binary + `wwwroot` (generated, not committed) |
| `deploy/` | `prepare-binaries.ps1`, `package-platforms.ps1`, `deploy.ps1` |
| `Scripts/test-vscode-extension-smoke.ps1` | One-shot build + smoke run |

## Development loop

```bash
cd vscode-viberails
nvm use
npm install
npm run watch              # or: npm run compile
```

Then open `vscode-viberails/` in VS Code and press **F5** to launch an Extension Development Host with the extension loaded. Restart the debug session to pick up changes.

The extension needs a staged backend at least once, or it throws "Bundled VibeRails backend is missing". `npm run prepare-binaries` only *copies* a published backend into `bin/<target>/` — publish it first:

```powershell
# from the repository root; -r matches your platform (win-x64 / linux-x64 / osx-arm64)
dotnet publish VibeRails/VibeRails.csproj -c Debug -r win-x64 --self-contained false -o Scripts/artifacts/aot/win-x64
cd vscode-viberails && npm run prepare-binaries
```

Backend-only changes need that publish + prepare pair again; TypeScript-only changes just need a recompile.

## Tests

```bash
npm test    # compiles, then runs the Electron smoke suite
```

Or the full path from the repository root, which publishes the backend, stages it, and then runs the same suite:

```powershell
.\Scripts\test-vscode-extension-smoke.ps1            # add -SkipPublish once the backend is staged
```

That script expects `code`, `dotnet`, `npm`, and `codex` on `PATH`.

The smoke test downloads a VS Code build, launches it **with no workspace folder**, and points the backend at a project via the `VIBERAILS_SMOKE_WORKSPACE` env var — which the extension honors only outside `ExtensionMode.Production`. It sets `VIBERAILS_TEST_FAKE_CLI=1`, which the *backend* reads to substitute cheap fakes for real AI CLIs, so a real `vb` binary is required but the actual CLIs are not.

## Packaging

```bash
npm run package:win32-x64     # or linux-x64 / darwin-arm64
npm run package               # all four
```

Each target gets its own VSIX with only that platform's backend bundled.

## Releasing

From the repository root:

```powershell
pwsh ./deploy/deploy.ps1
```

It syncs the backend and extension versions, commits and tags `vX.Y.Z`, triggers the GitHub Actions release build for all platforms, and publishes to the Marketplace. Publishing needs a Visual Studio Marketplace PAT in the `VS_PAT` environment variable.

Add user-visible changes to `CHANGELOG.md` under `[Unreleased]` as part of the PR that makes them.

## Pull request guidelines

- Keep `constants.ts` free of `vscode` imports.
- Keep the Node typings aligned with the minimum VS Code runtime declared in `package.json`.
- Run `npm run compile` before pushing; run the smoke test for anything touching the backend lifecycle, tokens, or the webview.
- **Never remove the `shift+enter` and `escape` keybinding entries in `package.json`** — including the one whose `command` is the empty string. They stop VS Code from swallowing those keys inside the webview terminal, and deleting them silently breaks multi-line input in every AI CLI.
- **Never re-fetch tokens from a consumed bootstrap URL.** The code is single-use with a two-minute expiry. Retry only `ECONNREFUSED`; a timeout, reset, or HTTP response may mean the server already consumed the code.
- Don't reintroduce DOM-scraping patches into the webview. The extension/dashboard contract is the injected `__viberails_VSCODE__`, `__viberails_close__`, and `__viberails_setTitle__` globals.
- The `fonts.googleapis.com` / `fonts.gstatic.com` CSP entries look unused but are not — `assets/bootstrap.min.css` `@import`s the Lato family.

## Reporting bugs

Open an issue at https://github.com/robstokes857/vibe-rails/issues. For extension bugs, include the **VibeRails Backend** output channel contents, your OS/VS Code version, and (for crashes) the relevant minidump from `~/.vibe_rails/crashdumps/`.
