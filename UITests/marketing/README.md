# Public screenshot capture

Render the actual frontend with fictional, hand-authored Trailhead data:

```powershell
node UITests/marketing/capture.cjs
```

Run from the repository root after installing the existing `UITests` npm dependencies and Playwright Chromium. This is a frontend capture fixture, not a VibeRails application mode. It starts no backend, opens no database, runs no CLI, and changes no normal application state.

`demo-data.cjs` supplies API responses. `capture.cjs` uses a fresh browser context, serves static files only from `VibeRails/wwwroot`, intercepts every HTTP request and WebSocket without forwarding, and blocks external hosts. It never reads browser profiles, environment credentials, terminal recordings, or user project data. The terminal receives an explicitly labeled sample transcript through its intercepted WebSocket.

Outputs go to `vscode-viberails/media/screenshots`: 15 PNGs, a local HTML gallery, a preview sheet, and a manifest with dimensions and image hashes. Shots use a 1600 × 900 viewport at 2× pixel density; close-ups use element bounds. The terminal uses a fresh context at 1× pixel density for its canvas renderer.

Before each capture, visible text and form values are checked for common private-data markers and incomplete-fixture messages. Unexpected fixture requests and JavaScript errors fail the run. Visually review every final image before publishing; the automated checks supplement the isolated data source.

To update the content, edit the fictional fixtures and rerun. Keep the production UI and its styles unchanged. Label quality metrics, agent activity, and terminal output as illustrative wherever the images are used.
