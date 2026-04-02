# Terminal Known Issues & Notes

## Open Bugs

### Font size change causes incomplete TUI rendering / double print

**Status:** Open — 2026-04-01

**Symptom:** Changing xterm.js font size via the +/- buttons can cause two related rendering failures:
1. **Double print:** At the original font size, ConPTY emits a redundant full redraw, causing text to appear twice.
2. **Partial TUI rendering:** After a font size change clears and redraws, some font sizes produce a complete TUI (all conversation content visible), while others show only the header chrome (logo, version, model, workdir) and prompt — the conversation body is blank.

Observed with Claude Code's Ink/React TUI. Font size 10 renders fully; font size 11 renders only header + prompt. Increasing font size from the initial (broken) state cleared the double print but exposed the partial rendering issue.

**Reproduction:**
1. Launch Claude Code in VibeRails web terminal
2. Have an active conversation with tool calls visible
3. Click font size +/- buttons to change size
4. Observe: some sizes render the full TUI, others show only the top chrome and prompt area with blank space where conversation content should be

**Root cause (suspected):** The font-size-change flow in `applyFontSize()` (terminal-multitab.js:382) calls `resetDisplayOnly()` which calls `terminal.clear()` — wiping the xterm.js buffer — then sends `__resize__:cols,rows` to the backend. `TerminalResizeCoordinator.ApplyResize()` resizes the ConPTY, which triggers SIGWINCH to the TUI app. The TUI re-renders at new dimensions and that output flows back through PTY → WebSocket → xterm.js.

The problem is **ConPTY's redraw after `ResizePseudoConsole` is not always complete**. At certain col/row dimensions, the TUI content doesn't fully repaint. At others it works fine. The codebase already acknowledges ConPTY redraw unreliability: `TerminalResizeCoordinator` has "calling ResizePseudoConsole with the same size triggers a full ConPTY redraw, which produces duplicate output."

`EnableDebouncedRedrawOnResize` is `false`, so there is no Ctrl+L safety net to force a full TUI repaint after resize settles.

**Key files:**
- `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `applyFontSize()`, `resetDisplayOnly()`, `sendResizeToPty()`
- `VibeRails/wwwroot/js/modules/vibe-terminal.js` — `clearDisplay()`, `setFontSize()`, `fit()`
- `VibeRails/Services/Terminal/TerminalResizeCoordinator.cs` — `ApplyResize()`, `EnableDebouncedRedrawOnResize`
- `VibeRails/Services/Terminal/Terminal.cs` — `Resize()` → `_pty.Resize()` + `_emulator.Resize()`

**Current test:** Enabled `EnableDebouncedRedrawOnResize = true` in `TerminalResizeCoordinator.cs` (2026-04-01) to see if the debounced Ctrl+L forces a complete TUI repaint after font size changes.

**Possible fix directions:**
- Enable `EnableDebouncedRedrawOnResize = true` so a debounced Ctrl+L forces a full TUI repaint after resize settles. This already exists in `TerminalResizeCoordinator` — **now enabled for testing**.
- Remove or defer the `terminal.clear()` in the font-size path — let the ConPTY redraw overwrite stale content rather than clearing first and risking an incomplete repaint.
- Combine both: skip the preemptive clear and send a debounced Ctrl+L after resize, so the TUI app gets a chance to fully redraw without the user seeing a blank flash.
- Investigate whether the issue is ConPTY-specific (likely) or Ink/React layout-dependent at certain column widths.

---

## Closed / Informational

## Codex: Cannot scroll back during live session

**Symptom:** When a Codex terminal is running, scrolling up in xterm.js does nothing — prior output is inaccessible.

**Root cause:** Codex CLI uses the alternate screen buffer (`\x1b[?1049h`) for its TUI interface. Per the VT/xterm spec, alternate screen mode has no scrollback buffer. xterm.js enforces this: once `?1049h` is received, scrollback is disabled for the duration of the alternate screen session.

This is not a bug in VibeControl — it is standard terminal behavior. `vim`, `nano`, `htop`, and any other TUI application have the same behavior.

**What changed:** Codex CLI updated its UI to use an alternate-screen TUI (progress panels, status bars). Previously it used plain line output and scrollback worked fine.

**C# side detail:** `TerminalBuffer` only pushes rows into `_scrollback` when `!_usingAlternate` (line 344 in `TerminalBuffer.cs`). This is correct — alternate screen output should not pollute the normal scrollback.

**On reconnect:** The `TerminalGridSerializer` correctly replays the C# scrollback (rows accumulated before Codex entered alternate screen) followed by the current screen state. So reconnecting *does* restore pre-session history in xterm.js scrollback.

**Possible fix directions:**
- Before Codex enters alternate screen, snapshot the current xterm.js scrollback and offer a "view history" side panel or modal.
- Not fixable transparently — alternate screen without scrollback is spec behavior that xterm.js cannot override.

---

## Codex: Scrollback lost when it "stops thinking" (exits alternate screen)

**Symptom:** After Codex finishes a thinking phase and returns to normal output, the scrollback that existed before is gone.

**Expected behavior:** When an app exits alternate screen (`\x1b[?1049l`), xterm.js restores the normal screen AND its prior scrollback. Scrollback should survive the transition.

**Likely cause:** Codex (or its underlying framework) explicitly sends `\x1b[3J]` (erase scrollback) when transitioning between modes. This is a separate sequence from `?1049l` and can be sent at any time to wipe xterm's scrollback buffer. We use `\x1b[3J]` ourselves in `TerminalGridSerializer.cs:38`, but only during a reconnect replay — not during live streaming.

**To verify:** Inspect the raw PTY byte stream in `TerminalRunner` around the time Codex switches modes. Look for `\x1b[3J]` in Codex's output. If present, the wipe is coming from Codex itself.

**C# vs xterm.js split:** Our C# `TerminalBuffer` and xterm.js track state independently. During a live session, xterm.js is the truth — the C# emulator only matters on reconnect for replay. If Codex sends `\x1b[3J]` live, xterm.js clears its buffer immediately with no opportunity to intercept it cleanly.

**Possible fix directions:**
- Intercept `\x1b[3J]` in the PTY stream before forwarding to the browser WebSocket and suppress it (fragile — could affect other apps that legitimately want to clear scrollback).
- Not otherwise fixable without becoming a filtering proxy in the PTY pipeline.
