# Terminal change review

The repository's terminal signing rules have been removed from
[vc.rules.md](../vc.rules.md). A narrower signing policy can be added later; this
checklist retains the terminal regression risks and review guidance.

Existing nested rules remain in force: notably `TerminalEmulator` has a directory
lock, and `Pty.Net` has additional STOP checks.

Review terminal-affecting changes in shared files when they alter launch arguments,
terminal service registration, process shutdown/ownership, input routing, terminal
visibility/size, or shared CSS that affects the terminal container. Any future signing
policy should focus on terminal runtime code rather than blanket-gating general
application, Board, Jobs, settings, documentation, or test files.

## Highest-risk changes

These priorities come from [TERMINAL.md](../../vibe-books/terminal/TERMINAL.md), including
its dated corrections. They describe regression risks, not a claim that every
historical incident remains open.

| Priority | Changes to scrutinize | Failure seen in the notes | Evidence to require |
| --- | --- | --- | --- |
| 1 | Resize, fit, reflow, tab activation, fonts and xterm/addon updates (`terminal-tab.js`, `vibe-terminal.js`, `TerminalResizeCoordinator`, `Pty.Net`) | Persistent stacked UI, stale rows, displaced cursor and lost scrollback. The August tab-activation case could occur on a single resize with large history. | Live slow/fast drag, hide/show, tab switching, font changes and long-history checks. Compare copied text, recorded bytes and rendered pixels to distinguish stream corruption from a renderer race. |
| 2 | Snapshot serialization, reset/mode restoration, attach ordering, reconnect and takeover (`TerminalGridSerializer`, `Terminal`, `TerminalSessionService`, reconnect/snapshot modules) | Lost or duplicated rows; mouse mode lost on reconnect, turning wheel input into prompt-history navigation; stale async completion blanking a new viewer. | Production snapshots replayed through xterm with every row and cursor checked; main/alternate screen, mouse and paste modes; repeated reconnect/takeover; switch/dispose during pending writes. Preserve atomic snapshot-then-live ordering. |
| 3 | Keyboard, Escape, paste, wheel and control-frame routing (`TerminalIoRouter`, `CodexWindowsInputRewriter`, `KeyTranslator`, paste/wheel modules) | A multiline paste submitted multiple turns; LF became Ctrl+Enter under ConPTY; wheel input edited the composer; rejected `__resize__` frames became literal typed text. | Real Windows ConPTY key probes plus byte-level tests for CRLF, bare LF, bracketed paste, Escape, modified Enter, wheel coordinates and malformed control frames. Keep rewrites limited to their verified CLI/platform cases. |
| 4 | Output scheduling, cursor suppression, native filtering and encoding (`WebSocketConsumer`, `ConsoleOutputConsumer`, `NativeConsoleOutputFilter`, `JobTerminalTabLauncher`, browser write loop) | Short browser timers caused persistent 1–3 second typing lag; cursor timers caused flicker; CP437 changed bullets to BEL and shifted entire rows. | Cold VS Code process and visibility checks; streaming/typing responsiveness; Unicode and split UTF-8 bytes; inner/outer Automation recording comparisons. Keep the browser receive flush on microtasks and output consumers non-blocking. |
| 5 | Session ownership, auto-reconnect, stop/dispose, child-process cleanup and Automation host reclamation (`TerminalTabHostService`, `TerminalRunner`, lifecycle and Job launch/cleanup code) | A viewer disconnect could kill a live session; takeover/retry races and cleanup can target a still-running or replacement session. | Disconnect without terminating the PTY, explicit takeover without retry theft, concurrent start/stop, exact-session identity checks, final-output drain and unavailable-status retention. |

## Review cautions from the history

- A small diff is not automatically low risk. The October 1 Automation corruption
  came from a missing UTF-8 initialization in a wrapper outside the terminal directory.
- Read corrections before following an older recommendation. The June cursor-suppression
  removal was later corrected to a Codex-only mitigation; the 2-second resize delay
  became opt-in while the default stayed 140 ms. The July snapshot fixes restored
  paste/mouse modes that older snapshot notes still describe as incomplete.
- Do not substitute blind Ctrl+L redraws, global input rewrites or longer browser
  timers for evidence of the cause. Native-console repaint handling has a separate,
  later history from the no-Ctrl+L-on-attach rule.
- Unit tests do not close a live rendering report. Record the tested CLI, platform,
  surface (native/browser/VS Code/Automation), build and exact scenario. Rob's live
  check is still required before calling an awaiting-verification incident resolved.
- Include renamed/deleted files, shared launch/CSS/configuration changes, and changes
  that weaken regression assertions in the diff Rob reviews.
