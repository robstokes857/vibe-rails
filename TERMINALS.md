# Terminal Known Issues & Notes

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
