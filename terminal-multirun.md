# Multi Run

A small UI that opens **two** terminal tabs back-to-back with the **same initial prompt**, each backed by a (possibly different) base CLI. Useful for side-by-side comparisons — "how does Claude vs Codex respond to this?"

## How it works

The "Multi Run" item lives in the kebab (`⋮`) menu in the terminal panel header. Clicking it opens a modal with:

- Two CLI pickers (defaults: Claude / Codex)
- A textarea for the initial prompt
- **Run** button — Cmd/Ctrl+Enter from the textarea also works

On Run, two tabs are created sequentially. Each tab is created via `manager.createAndActivateTab({ selection })` and started via `tab.instance.startSession({ cli, initialPrompt })`.

Sequential, not parallel: `createAndActivateTab` activates the new tab synchronously (deactivating the previous), so parallel calls would race on activation. Doing them in order means the user lands on the **second** tab — which they just selected as "second", so the focus matches expectation.

## Files

- `VibeRails/wwwroot/js/modules/terminal-multirun.js` — modal markup, mounting, and launch logic
- Mounted from `terminal-multitab.js` via the kebab menu's `terminal-multirun-btn` item (see `_showMultiRunModal`)

## Limitations / future work

The CLI pickers currently show **base CLIs only** — Claude, Codex, Gemini, Copilot. Custom environments and sandboxes are not selectable.

**Adding custom sandboxes is a planned extension, but not yet possible.** Sandboxes today don't have a deterministic CLI/env mapping — a sandbox is a working-directory checkout, not "run this CLI with this env." Before Multi Run can let the user pick a sandbox, sandboxes need to be tied to a specific CLI (and optionally a custom env) at creation time. Once that prerequisite ships, the picker would gain a "Sandboxes" group sourced from `manager.app.data.sandboxes` (or similar) and the launch logic would resolve to the sandbox's bound CLI/env.

Other things deliberately not supported today:

- **More than two tabs at once.** The "Multi" in the name is forward-compatible; bumping to N selects + N launches is a small change, but the comparison-style UX is the focus today.
- **Resume from session.** Multi Run always opens fresh sessions.
- **Working directory / title / extra args.** Both tabs use defaults.

## Why an `initialPrompt` body field, not a manual prompt-into-PTY write?

The `/api/v1/terminal/tabs/{id}/start` endpoint already accepts `InitialPrompt` (see `StartTerminalRequest` in `DTOs/ResponseRecords.cs`). The server resolves `request.InitialPrompt` first, then falls back to the env's `CustomPrompt` if the body field is empty (`Routes/TerminalRoutes.cs`). Multi Run sets `body.initialPrompt` directly — same path the env-fallback uses, just bypassing the env lookup since base CLIs don't have a `CustomPrompt`.
