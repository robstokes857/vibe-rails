# Terminal change approval and review

Changes to the terminal require Rob's approval. The root [vc.rules.md](../vc.rules.md)
uses `Signed Approval(..., 'RobStokes857@gmail.com') (STOP)` for the terminal backend,
PTY provider, emulator, browser terminal, bundled xterm, terminal launchers, host
lifecycle, VS Code host, and the listed shared entry points. Core terminal regression
directories are covered too. Shared files are gated as whole files, including changes
to their non-terminal code.

## How approval works

Git Guard defers the check at pre-commit and requires Rob's signature at commit-msg.
Rob reviews the complete staged diff, then signs in the separate Git Guard popup
using a Settings > KEYS key registered to his verified viberails.ai account. One
signature covers all applicable approval rules for that staged tree. Changing staged
content afterward requires a new signature. Missing approval, an unapproved signer,
or unavailable signature verification blocks the commit; a textual acknowledgment
does not satisfy STOP.

The root policy protects itself and this checklist. Once committed, removing or
weakening an approval rule still requires approval under the committed policy.
Existing nested rules remain in force: notably `TerminalEmulator` has a directory
lock, and `Pty.Net` has additional STOP checks. A signature does not override them.

This is a Git Guard commit gate, not a filesystem edit lock or branch protection.
It requires active hooks and a VibeRails build supporting Signed Approval; skipping
hooks bypasses local enforcement. The hook installation in this checkout points to
the per-user installed `vb.exe`.

Approval is path-based. New files under protected directories are covered automatically.
For new terminal modules or dependencies outside those directories, extend the root
policy in the same change; literal approval paths do not support filename globs.
Review terminal-affecting changes in shared files even when their immediate purpose
is another feature. The separate remote relay repository is outside this policy.

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
