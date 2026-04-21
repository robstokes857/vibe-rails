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

## Codex: Status-line cursor flash / cursor hop during TUI redraw

**Status:** Improved / mitigated — 2026-04-16

**Symptom:** In the Web UI terminal, Codex's visible cursor could appear to jump off the input line and flash on the bottom status/footer line while pressing arrow keys, space, or during active thinking/redraw. The flashed cursor sometimes took on the footer line's gray styling, which made it look like a browser caret or renderer bug.

**Correct term:** This is best described as a **VT cursor flash** or **cursor hop during TUI redraw**. More specifically, it was a **status-line cursor flash**: Codex's TUI briefly moved the real terminal cursor to its footer/status row during intermediate redraw steps, and xterm.js rendered that transient position.

**Root cause:** This turned out to be a combination of:
1. Codex splitting one visual redraw across multiple WebSocket messages, so xterm rendered intermediate states.
2. The server flushing some redraw fragments before synchronized output had fully settled.
3. xterm faithfully drawing the real VT cursor even when Codex briefly parked it on the footer/status line during redraw.

This was **not** the hidden helper textarea caret. That browser-caret issue had already been separately suppressed via CSS/runtime textarea patching. The remaining flash was the actual terminal cursor being shown at a transient position inside Codex's TUI.

**What changed:**
- `VibeRails/wwwroot/js/modules/terminal-multitab.js`
  - Replaced microtask-only output batching with a short timer-based coalesce window so adjacent WebSocket frames render together instead of as torn redraw fragments.
  - Reduced extra connect-time focus churn and follow-up fit churn that could amplify visible redraw noise.
  - Added a short output-driven cursor suppression window so the xterm cursor is hidden during inbound redraw bursts and restored after a brief idle period.
- `VibeRails/wwwroot/js/modules/vibe-terminal.js`
  - Added cursor suppression / restore helpers that temporarily hide the rendered xterm cursor by theme + CSS while output is actively streaming.
  - Increased browser-side xterm scrollback from `5000` to `20000`.
- `VibeRails/wwwroot/style.css`
  - Added a terminal state class that hides the xterm cursor layer during transient redraw bursts as a CSS-side fallback.
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs`
  - Added sync-output-aware batching for terminal output (`CSI ?2026 h/l`) with a fallback timeout so Codex redraw frames are less likely to flush mid-frame.
- `VibeRails/Services/Terminal/TerminalResizeCoordinator.cs`
  - Defers resize application while sync-output is active to avoid extra redraw churn from resize signals landing mid-frame.
- `VibeRails/Services/Terminal/Terminal.cs`
  - Increased C# emulator scrollback from `5000` to `20000` so reconnect snapshots and live browser history stay aligned.
- `VibeRails/Services/Terminal/SessionOutputWriter.cs`
  - Added sync-output-aware alternate-screen frame boundaries to improve replay/history capture for Codex sessions.

**Result:** The major visible footer/status-line cursor flash is now substantially reduced. A small residual flicker may still be visible when Codex updates its own placeholder/footer/status text during active thinking, because the TUI is still legitimately repainting that area. The mitigation here is to hide transient cursor positions, not to stop Codex from redrawing.

**Renderer note:** WebGL remains the global preferred renderer in the Web UI terminal. In testing, Codex looked better in WebGL than canvas, but no Codex-specific renderer override was added.

**Scrollback note:** The general Web UI terminal scrollback cap is now `20000` lines on both the browser xterm side and the C# emulator/reconnect side. However, Codex still uses the alternate screen for parts of its TUI, and alternate-screen scrollback remains inherently limited by terminal behavior.

**Key files touched:**
- `VibeRails/wwwroot/js/modules/terminal-multitab.js`
- `VibeRails/wwwroot/js/modules/vibe-terminal.js`
- `VibeRails/wwwroot/style.css`
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs`
- `VibeRails/Services/Terminal/TerminalResizeCoordinator.cs`
- `VibeRails/Services/Terminal/Terminal.cs`
- `VibeRails/Services/Terminal/SessionOutputWriter.cs`

---

## Startup prompt triple-print — WebFontsAddon / LigaturesAddon post-connect reflow

**Status:** Fixed — 2026-04-15

**Symptom:** On opening a fresh Web UI terminal, the initial shell prompt flashed 2–3 times within ~1.8s before the CLI took over. Reproduced in session `4a3386aa-a1e7-4337-8a34-f253f2ed75ac` where `SessionLogs` chunks 891 / 892 / 894 were byte-identical full-screen redraws of the PowerShell prompt at +0.09s / +1.31s / +1.81s. Only chunk 891 carried the ConPTY resize report `\e[8;31;122t`; the other two were pure redraws with no resize cause in the byte stream.

**Root cause:** `VibeTerminal` in `wwwroot/js/modules/vibe-terminal.js` loaded two xterm addons that each fired `scheduleFitPasses()` *after* their async load completed — after the PTY was already connected and the TUI running:
- `WebFontsAddon({ onLoaded: () => this.scheduleFitPasses() })` — fires when web fonts finish downloading
- `_loadLigaturesAddon()` dynamic import — fires when the ligatures module resolves

Each callback shifted xterm cell metrics (a real font is narrower/taller than the fallback), `fit()` recomputed cols/rows, a new `__resize__` went to the backend, ConPTY SIGWINCHed the shell, and PSReadLine redrew its entire prompt. Two addons → two extra full redraws.

Per the xterm.js docs: because xterm renders to `<canvas>` / WebGL, the browser does not download web fonts automatically, so custom fonts require the `addon-web-fonts` machinery specifically. Custom fonts and this bug are two faces of the same thing.

**Fix:** Switched the terminal to a cross-platform system monospace stack and removed the whole web-font pipeline.
- Default `fontFamily` now `Menlo, Monaco, Consolas, "Cascadia Mono", "Liberation Mono", "Courier New", monospace` (`vibe-terminal.js`)
- `fontLigatures: true` → `false` on the xterm `Terminal` constructor
- Removed `WebFontsAddon` loader + `<script src="addon-web-fonts.js">` from `index.html`
- Removed `_loadLigaturesAddon()` and the dynamic import
- Removed the font-family picker from Terminal Settings UI + its `localStorage` persistence (`terminal-multitab.js`)
- Removed `window.CXL_FONTS` from `terminal-themes.js`
- `session-viewer.js` (replay modal) updated to the same stack for consistency

Left on disk deliberately (may be used elsewhere in the app): all `.woff`/`.woff2`/`.ttf`/`.otf` asset files, all `@font-face` CSS rules, Monaco editor fonts. Backend has no font preference (confirmed).

**Key files touched:**
- `VibeRails/wwwroot/js/modules/vibe-terminal.js`
- `VibeRails/wwwroot/js/modules/terminal-multitab.js`
- `VibeRails/wwwroot/js/modules/session-viewer.js`
- `VibeRails/wwwroot/assets/xterm/terminal-themes.js`
- `VibeRails/wwwroot/index.html`

**Cross-reference:** The *existing* open bug "Font size change causes incomplete TUI rendering / double print" (above) is separate — that one is about the +/- font-size buttons triggering ConPTY resize partial redraws. It remains open, but this fix removes one compounding factor (no more font/ligature-load reflow firing on top of a user-initiated size change).

**Debug tool used:** `python-scripts/decode_session.py` + `python-scripts/analyze_doubleprint.py` — dump a session's raw `SessionLogs` BLOBs with ANSI escapes spelled out, then fingerprint chunks to detect identical full-screen redraws within a time window.

---

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

### Typing lag / stutter from WebSocket coalesce hold

**Status:** Open — 2026-04-20 — investigation complete, fix deferred

**Symptom:** Typing and held-down backspace feel laggy/stuttery in the xterm.js terminal. Stutter is perceptible rather than uniform — some frames pop instantly, others visibly lag.

**Instrumentation added** (currently at `Log.Debug`, toggle to `Information` to re-capture):
- `TerminalStateService.RecordInput` — `[TypingLag] RecordInput chars=N elapsedMs=...`
- `TerminalStateService.LogOutput` — `[TypingLag] LogOutput bytes=N elapsedMs=...`
- `WebSocketConsumer.SendLoopAsync` — `[TypingLag] WS send frames=N bytes=N coalesceRounds=N holdMs=... sendMs=... syncOut=bool`

**Measurements** (2026-04-20 session, non-syncOutput path):
| Stage | Typical elapsed |
|---|---|
| `RecordInput` | 0.01–0.03 ms |
| `LogOutput` | 0.04–0.35 ms |
| WS `sendMs` | 0.01–0.2 ms |
| **WS `holdMs`** | **1–20 ms, clustered ~5–8 ms** |

**Root cause:** `WebSocketConsumer.SendLoopAsync` has a 4 ms `Task.Delay` coalesce window (`NormalCoalesceDelayMs`) gating every non-syncOutput send. On Windows, timer resolution (~15.6 ms) makes that sleep land at 5–16 ms. Logs show `coalesceRounds=1` on nearly every frame — the delay almost never actually merges anything, we just pay the tax. That hold is added to every keystroke echo. A burst of keystrokes produces multiple PTY echo chunks each paying their own hold, which reads as stutter.

**Why the delay exists** (do not remove blindly — commit `3ea2f30`):
> TUI apps (Codex, Copilot) split their screen updates across multiple small PTY writes that arrive 1-10ms apart — e.g. an erase sequence, then `?2026h` (sync-on), then the redrawn content, then `?2026l` (sync-off/render). If each arrives as a separate WebSocket frame, xterm.js may render intermediate torn states (blank cells) between the erase and the sync-on. Waiting 4 ms gives the PTY reader time to enqueue related writes so they coalesce into one frame.

The sync-output path (`?2026h`/`?2026l` bracketed) catches the *synced* portion. The 4 ms delay catches the *unsynced* preamble (e.g. an erase that lands just before sync-on).

**Paste test** (2338 chars input, ~6133 bytes echoed back): The big echo burst coalesced cleanly (2 frames → 1 WS send, 5.7 ms hold). Long trickle of 20–55 byte chunks afterward each pays its own ~8 ms hold. For paste, coalescing works; per-chunk holds on the tail are minor.

**Fast-typing test** (11 keys in 27 ms autorepeat burst): PTY serializes input→echo — first echo arrives ~7 ms after first keystroke with output for 1–2 keys, second echo ~40 ms later covering the rest. Each send adds a 5–17 ms hold. Cumulative hold on bursts matches the felt stutter.

**Fix options considered** (none applied):
1. **Remove the hold entirely.** Would re-introduce the Codex/Copilot tear-flicker the original commit fixed. Rejected.
2. **Shorten to true 1–2 ms.** Use `PeriodicTimer`/`SpinWait` to bypass Windows timer granularity. Keeps tear protection, cuts typing hold from ~8 ms to ~2 ms. Low risk.
3. **Adaptive: only coalesce while a previous `SendAsync` is in flight.** Self-tuning — idle keystrokes send instantly, burst output naturally batches during send roundtrip. Preserves tear protection under load. Most principled.
4. **Trust `?2026h`/`?2026l` only.** Drop unsynced delay. Requires confirming Codex/Copilot always bracket writes properly.

**Also missing before committing a fix:** client-side end-to-end timing (WS `onmessage` → post-`term.write` timestamps in `terminal-multitab.js`). Without that, fixing the server hold is blind to any client-side render/paint bottleneck.

**Files:**
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs` — `SendLoopAsync`, `NormalCoalesceDelayMs = 4`
- `VibeRails/Services/Terminal/Core/TerminalStateService.cs` — `RecordInput`, `LogOutput` instrumentation
