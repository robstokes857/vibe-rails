# Add a React/Vite Settings Island

> Deferred until after the next VibeRails release.

## Summary

- Migrate only the complete Settings page to React; keep the vanilla SPA shell, routing, terminals, shared CSS, toasts, and modal system unchanged.
- Preserve the current appearance, copy, DOM IDs, API behavior, navigation guards, PIN workflow, and data-export behavior.
- Pin the latest verified stable releases as of August 11, 2026: React/React DOM `19.2.8`, Vite `8.2.1`, and `@vitejs/plugin-react` `6.0.5`.
- Use Node 24 from the existing `.nvmrc`, TypeScript `7.0.2`, and a committed lockfile.

## Implementation Changes

- Add `VibeRails/Frontend` containing strict TSX source, Vite configuration, `tsconfig.json`, package scripts, and exact dependencies. Configure Vite library mode to produce one minified ESM bundle at `Frontend/dist/settings.js`, with React bundled, no CSS chunk, no source map, and no hashed or root-relative assets.
- Replace the large Settings template with a React `SettingsPage`. Retain `[data-view="settings"]` and all existing element IDs/classes so global styling and Playwright selectors remain valid.
- Reduce the existing Settings controller to a lifecycle bridge that:
  - Creates the Settings host under `#app-content`.
  - Lazily imports `wwwroot/generated/settings.js` only when Settings opens.
  - Prevents a late import from mounting after navigation.
  - Unmounts React and removes listeners on every route change.
  - Reuses the existing data-export and PIN modal implementations.
- React owns loading, form state, saving, PIN status, saved-state snapshots, dirty detection, `beforeunload`, and the asynchronous in-app navigation guard. Preserve masked API-key clearing semantics, PIN-required remote access, immediate Performance Mode changes, project rename integration, effective token-saver defaults, and `app.setAppSettings()` after load/save.
- Keep export-in-progress state in the lifecycle bridge and expose it to React as a subscribed external state, ensuring navigation away and back cannot start a duplicate export.

## Interfaces and Build Contract

- Export one internal bundle interface: `mountSettings(element, host): () => void`. Define a typed `SettingsHost` for authenticated API calls, global settings updates, project context, navigation guards, notifications, modal callbacks, Performance Mode, and export-state subscription.
- Make no backend route, DTO, database, or settings-file changes.
- Integrate the frontend into `VibeRails.csproj`:
  - Incrementally run `npm ci` when the lockfile changes.
  - Run TypeScript checking and Vite before normal build/publish, excluding design-time builds.
  - Link the fixed bundle explicitly into output/publish as `wwwroot/generated/settings.js`, even on a clean checkout.
  - Watch TS/TSX/config inputs during `dotnet watch`, clean only `Frontend/dist`, and support `SkipFrontendBuild=true` for prebuilt pipeline stages.
- Keep `Frontend/dist` and `node_modules` untracked.
- Add Node 24 setup and npm caching to release build jobs. Have the Linux Docker build generate the platform-neutral frontend bundle once on the host, then publish with `SkipFrontendBuild=true`.
- Change VSIX preparation to copy each downloaded publish artifact's `wwwroot`, rather than source `wwwroot`, and fail packaging if `index.html` or `generated/settings.js` is absent.
- Document Node 24, the integrated build, and the single-host `dotnet watch` workflow. Do not add a Vite HMR server or CSP/proxy changes.

## Test Plan

- Run frontend type-check and Vite build from a clean install; verify the build emits exactly the expected ESM bundle.
- Update the Settings controller unit test for lazy mount/unmount, navigation-race protection, host bridging, and persistent export state.
- Run and extend the existing Settings Playwright coverage for rendering, save payloads, masked-key replacement/clearing, dirty navigation, PIN-required remote access, PIN clearing, project identity, relay settings, data-export gating/progress/retry, and remounting during export.
- Run `dotnet build` and a clean publish, asserting `wwwroot/generated/settings.js` is present and served successfully.
- Exercise the Linux Docker AOT path and package one VSIX, asserting the compiled bundle is included under each packaged backend's `wwwroot`.
- Smoke-test Settings in both the authenticated browser host and VS Code webview to verify relative ESM loading and CSP compatibility.

## Assumptions

- This is a migration with exact behavioral and visual parity, not a Settings redesign.
- React remains an isolated first step; no other SPA view or router is migrated.
- Existing unrelated working-tree changes in `index.html`, `style.css`, and other files will be preserved.
