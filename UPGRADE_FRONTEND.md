# Frontend Upgrade Recommendation

## Recommendation

Do not do a full rewrite of the VS Code extension shell.

Best path:

- Keep the extension host thin and local-only
- Extract a shared frontend app with explicit platform adapters
- Use `React + TypeScript + Vite` for the shared UI if you want the best long-term result
- If you want lower risk first, do `TypeScript + Vite` first, then migrate to React incrementally

## Why

The main issue does not look like "HTML/CSS/JS is the wrong stack."

The main issue looks like fragile integration between:

- VS Code extension host
- webview wrapper
- packaged backend binary
- shared web app
- release pipeline

In the current code, the webview layer is doing runtime patching and injection:

- [vscode-viberails/src/webview-panel.ts](C:/source/VibeControl2/vscode-viberails/src/webview-panel.ts)
- [vscode-viberails/src/extension.ts](C:/source/VibeControl2/vscode-viberails/src/extension.ts)
- [vscode-viberails/src/backend-manager.ts](C:/source/VibeControl2/vscode-viberails/src/backend-manager.ts)

And the shared app branches on VS Code globals:

- [VibeRails/wwwroot/app.js](C:/source/VibeControl2/VibeRails/wwwroot/app.js)
- [VibeRails/wwwroot/index.html](C:/source/VibeControl2/VibeRails/wwwroot/index.html)

That architecture is what makes breakage hard to debug.

The UI is also already large enough that stronger structure would help:

- `terminal-multitab.js`: about 2900 lines
- `app.js`: about 1158 lines
- `index.html`: about 1236 lines
- `style.css`: about 5929 lines

That is where React starts to pay for itself.

## Conclusion

If you want the best long-term direction, choose:

- `React`
- `TypeScript`
- `Vite`
- a shared API/client layer
- a browser adapter and a VS Code adapter

Do not use:

- a full rewrite from scratch
- a true web-extension/browser-host target right now
- VS Code Webview UI Toolkit

The extension is not a good candidate for a pure web extension because it depends on local process spawning and local binaries. The extension uses `child_process.spawn` and local executable packaging, which ties it to the desktop/local host model.

## What To Do

### 1. Stop Release Breakage First

- Add a hard release gate before publishing the VSIX
- Run extension integration and smoke tests in CI before publish
- Test the packaged extension, not just source
- Remove dependence on a real installed `codex` for smoke coverage by adding a fake backend or stub CLI mode

### 2. Clean The Integration Boundary

- Introduce one explicit platform bridge:
  - browser bridge
  - VS Code bridge
- Replace scattered `window.__viberails_*` checks with a typed interface
- Replace global `fetch` and `WebSocket` monkey-patching with one explicit API client layer

### 3. Move The Frontend Onto A Real Build System

- Use Vite
- Keep source maps in dev
- Support a local dev server for faster webview debugging
- Bundle web assets cleanly for both the normal web app and the VS Code webview

### 4. Migrate UI Incrementally

- Start with the most complex and failure-prone surfaces:
  - terminal tabs
  - dashboard state
  - chat history/sidebar
- Keep the backend API stable while migrating the frontend piece by piece

### 5. Tighten VS Code-Specific Setup

- Add `"extensionKind": ["ui"]` to the extension manifest
- Revisit `onStartupFinished`; lazy activation on command may be safer if startup behavior is part of the problem
- Revisit `retainContextWhenHidden`; official docs warn it has high memory overhead

## What React Would Improve

React would help with:

- decomposing huge screens into components
- managing state flow more predictably
- making shared UI behavior easier to test
- improving debugging with modern tooling
- reducing manual DOM mutation bugs

React would not automatically fix:

- packaging bugs
- backend startup issues
- auth/token injection bugs
- webview/extension boundary issues
- broken release process

So React is a good move, but only if paired with an architecture cleanup.

## Suggested Migration Plan

### Phase 1: Stabilize

- Keep current UI
- Add CI smoke gate
- Create fake CLI/test backend
- Clean the VS Code bridge

### Phase 2: Replatform The Frontend

- Move frontend to `TypeScript + Vite`
- Keep behavior the same
- Introduce shared API client and adapter layer

### Phase 3: Migrate To React

- Migrate major screens to React one by one
- Keep the extension shell minimal

This gets better debugging and fewer broken releases without betting the product on one giant rewrite.

## Bottom Line

- Yes, move toward `React + TypeScript + Vite`
- No, do not rewrite the whole extension from scratch
- First fix the release and testing pipeline
- Then extract a shared frontend with explicit browser/VS Code adapters
- Then migrate incrementally

## Sources

- VS Code Webview API: https://code.visualstudio.com/api/extension-guides/webview
- VS Code Bundling Extensions: https://code.visualstudio.com/api/working-with-extensions/bundling-extension
- VS Code Testing Extensions: https://code.visualstudio.com/api/working-with-extensions/testing-extension
- VS Code Continuous Integration: https://code.visualstudio.com/api/working-with-extensions/continuous-integration
- VS Code Extension Host: https://code.visualstudio.com/api/advanced-topics/extension-host
- VS Code Remote Extensions: https://code.visualstudio.com/api/advanced-topics/remote-extensions
- VS Code Web Extensions: https://code.visualstudio.com/api/extension-guides/web-extensions
- VS Code Webview UI Toolkit archive: https://github.com/microsoft/vscode-webview-ui-toolkit
