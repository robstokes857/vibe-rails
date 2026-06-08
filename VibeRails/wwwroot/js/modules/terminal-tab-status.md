# Terminal Tab Status State Machine

This document describes the state machine implemented in `terminal-tab-status.js`
(`TabStatusController`). It drives the small status label + icon on each terminal
tab button (e.g. `Connected`, `Thinking`, `Ready`, `Disconnected`).

## States

| State          | Meaning                                                                                  | Icon              | Text color       |
| -------------- | ---------------------------------------------------------------------------------------- | ----------------- | ---------------- |
| `CONNECTED`    | Socket is open, no user interaction yet.                                                 | link              | slate `#94a3b8`  |
| `THINKING`     | User pressed Enter; backend is working. Animated emoji cycle + indeterminate progress.   | cycling emoji     | shimmer gradient |
| `READY`        | Backend signaled idle/completion after a `THINKING` turn. Background tabs get a flash.   | circle-check      | slate `#94a3b8`  |
| `WAITING`      | Backend detected an interactive selection prompt (e.g. `•`/`◦` menu). Pulsing icon; background tabs get a yellow flash. | hand-point-up     | yellow `#f9e2af` |
| `DISCONNECTED` | WebSocket closed.                                                                        | plug-circle-xmark | peach `#fab387`  |

> **Note on the retired `ACTIVE` state.** An earlier version had a separate
> `ACTIVE` ("user is composing") state driven by a `_hasPrintableChar` heuristic
> over `onData` bytes. It was removed because (a) background tabs would mislead-
> ingly display "Active" while their terminal wasn't even on screen, and (b) the
> heuristic false-positived on arrow-key CSI sequences (`\x1b[B`) and xterm
> auto-responses to server queries (`\x1b[24;80R`), silently flipping the tab
> to ACTIVE and then suppressing the next `onWaitingForUserSelection` via the
> old ACTIVE guard — the root cause of a reported "stuck on Thinking after
> answering the first prompt" bug.

## Transitions

```
           ┌──────────────┐
           │ (no status)  │
           └──────┬───────┘
                  │ onSocketOpen()
                  ▼
           ┌──────────────┐
           │  CONNECTED   │
           └──────┬───────┘
                  │ Enter (\r)
                  ▼
           ┌──────────────┐
           │   THINKING   │
           └──┬───────┬───┘
              │       │
   Escape     │       │ onSessionIdle() / onSessionCompleted()
   (\x1b)     │       │
              │       ▼
              │   ┌──────────────┐
              │   │    READY     │
              │   └──────┬───────┘
              │          │ Enter (\r)
              │          ▼
              │   ┌──────────────┐
              └──▶│  CONNECTED   │
                  └──────────────┘
```

And on socket close from *any* state: → `DISCONNECTED`.

`onWaitingForUserSelection()` can move us into `WAITING` from any state except
`DISCONNECTED`. From `WAITING`:

- Enter (`\r`) → `THINKING`.
- A single printable byte (0x20–0x7E) → `CONNECTED` — the user has started
  typing a response, so the "Codex is waiting for you" signal is stale.
- Anything else (CSI sequences, multi-byte chunks, backend pings) leaves
  the state alone.

## Triggers

User input is routed into `onTerminalData(data)` from `terminal-multitab.js`,
which subscribes to xterm's `onData` (keystrokes + paste). Backend session
events are routed into `onSessionIdle()` / `onSessionCompleted()` /
`onSessionBusy()` / `onWaitingForUserSelection()`.

### `onTerminalData(data)`

1. `data === '\r'` (bare Enter) while in `CONNECTED` / `READY` / `WAITING`
   → `THINKING`.
2. `data === '\x1b'` (bare Escape) while in `THINKING` → `CONNECTED`
   (treated as abort).
3. A single printable byte (0x20–0x7E) while in `WAITING` → `CONNECTED`.
   Narrowly scoped to `data.length === 1` so the carve-out covers genuine
   keystrokes only: CSI sequences (arrow keys `\x1b[B`, function keys, focus
   reports `\x1b[I/O`, DSR auto-replies `\x1b[24;80R`), bracketed-paste
   wrappers, and clipboard pastes all arrive as multi-byte chunks and are
   excluded — re-introducing the false positives that retired the ACTIVE
   state would defeat the purpose. This is the only printable-byte transition
   in the machine; THINKING, CONNECTED, and READY all still ignore typing.
4. Everything else is ignored. Typed characters during `THINKING`, arrow
   keys, function keys, and xterm auto-responses to server queries all flow
   through without changing the state.

### `onSessionIdle()` / `onSessionCompleted()`

**Agent tabs (default).** Only transition to `READY` if `_awaitingFirstIdle`
is true — i.e. we had previously entered `THINKING` and have not already been
moved out of it. This prevents the backend's noisy idle pings from spuriously
flashing tabs.

Crucially, a `session_idle` arriving while the tab is parked in `WAITING`
is **ignored**. The backend idle threshold is only 5 seconds of no PTY
output, and a menu waiting for user input produces no PTY output by
definition — so treating idle as "prompt must have gone away" was firing
on the common case, not the edge case. `WAITING` only leaves on the user's
Enter press (→ `THINKING`) or socket close (→ `DISCONNECTED`).

**Shell tab (CLI key `shell`).** Idle settles `THINKING` → `CONNECTED`
(see the shell carve-out under `onSessionBusy()`). We deliberately go to
`CONNECTED`, **not** `READY`, so a dev server going quiet between requests
doesn't fire the `READY` flash + "is ready" toast on every cycle. Idle in any
other state (already `CONNECTED`/quiet) is a no-op. `onSessionCompleted()`
follows the same carve-out: a shell whose process *exits* settles `THINKING` →
`CONNECTED` rather than flashing `READY`.

**Completion ≠ "ready" (all tabs).** `session_completed` means the process
*exited* — it carries an exit code, and the dispatcher already pops a global
"session completed" toast. So agent tabs still flash `READY` on completion but
pass `notify:false` to suppress the in-panel "is ready" toast; otherwise an
exited/crashed process would misleadingly announce itself as "ready" *and*
duplicate the global completion toast. The "is ready" toast is reserved for the
`session_idle` turn-finished path.

### `onSessionBusy()`

**Agent tabs (default): no-op.** The backend fires `session_busy` on *any* PTY
activity — including the agent's own prompt redraws — so we can't use it to
infer user intent. Enter detection via `onTerminalData` is the only trigger
for entering `THINKING`.

**Shell tab (CLI key `shell`): drives the spinner.** A plain shell has no agent
"turn" to misread, so PTY output *is* the signal the user wants — running a dev
server / build and watching the spinner track activity. For a shell tab,
`session_busy` moves `CONNECTED`/`READY` → `THINKING` (skipped when already
`THINKING`, `DISCONNECTED`, or `WAITING` — an attention prompt must outlive
incidental output), and `session_idle` settles it back to
`CONNECTED`. The backend emits these for *every* session — shell included,
no CLI gating (`TerminalStateService.RegisterSession` → `StartIdleMonitor`) —
so no backend change is needed; the controller just stops ignoring them for the
shell. `_isShellTab()` reads the CLI key from `options.getCliKey()`.

> **Caveat — the coarse 5s busy/idle signal.** The spinner rides the backend's
> PTY-activity heuristic, which has three rough edges, all currently accepted:
> - **Chatty servers never settle.** `session_idle` fires after 5s of no PTY
>   output (`TerminalStateService.s_idleThreshold`); a server logging more often
>   than every 5s (heartbeats, HMR pings) keeps `LastActivityUtc` fresh and never
>   crosses the threshold — so the spinner stays on continuously.
> - **Typing lights the spinner.** The *first* keystroke after an idle period
>   emits `session_busy` (`MarkInputActivity`), so merely starting to type the
>   next command at a quiet prompt flips the tab to `THINKING` before anything
>   actually runs.
> - **Long commands flicker.** A single command that pauses output for >5s
>   (compile/test phase, network wait) trips `session_idle` → `CONNECTED`
>   mid-run, then re-arms `THINKING` when output resumes.
>
> Separating "typing" from "command output", or "paused" from "finished", would
> need the busy event to carry its source (input vs output) and/or job-control
> awareness — a backend change deferred until the heuristic proves too noisy in
> practice.

### `onWaitingForUserSelection()`

Fired when the backend detects an interactive selection prompt in PTY output
(Claude-style `•`/`◦` menus or Codex-style "enter to submit / esc to cancel"
footers — see `WaitingForUserInputObserver`). The backend debounces this to
at most once per 30 seconds per session so a redrawing menu doesn't spam
events. Moves the tab into `WAITING` unless the tab is `DISCONNECTED`.

## The `_awaitingFirstIdle` flag

`_awaitingFirstIdle` is the bookkeeping bit that gates whether the next
backend idle event should auto-promote the tab to `READY`.

Set/cleared in `_transitionTo`:

| Entering state | `_awaitingFirstIdle` |
| -------------- | -------------------- |
| `THINKING`     | `true`               |
| `READY`        | `false`              |
| `CONNECTED`    | `false`              |
| `WAITING`      | `false`              |

## Side effects in `_transitionTo`

Beyond updating `_status` and calling `_applyVisuals`:

- **Progress bar.** Enters indeterminate (`state: 3`) on `THINKING`, turns
  off (`state: 0`) when leaving `THINKING`.
- **Ready flash.** When transitioning to `READY` on a *background* tab only,
  `_flashReady()` adds a one-shot `tab-status-ready-flash` class so the tab
  pulses with its accent color — notifying the user that a different tab
  has finished its turn.
- **Waiting flash.** When transitioning to `WAITING` on a *background* tab
  only, `_flashWaiting()` adds a one-shot `tab-status-waiting-flash` class.
  Unlike the ready flash, this uses a literal yellow `#f9e2af` (not the brand
  accent) so "waiting for you" looks the same on every tab — it's a
  per-user-attention signal, not a per-CLI one. The active tab gets the
  pulsing `hand-point-up` icon instead.
- **Thinking emoji cycle.** `_applyVisuals` stops any running emoji timer
  and, if the new state is `THINKING`, starts a 2-second cycle picking a
  random emoji from `THINKING_EMOJI` (never repeating the previous one
  back-to-back).

## CSS hooks

Each state adds the class `tab-status-<state>` to the tab item element
(e.g. `tab-status-waiting`). State-specific text/icon colors live in
`VibeRails/wwwroot/style.css` under the "Tab Status Overhaul" section.

The ready-flash animation uses a separate one-shot class,
`tab-status-ready-flash`, which is removed on `animationend`. The waiting-flash
uses `tab-status-waiting-flash` the same way — one-shot, removed on
`animationend` — but with a fixed yellow color instead of the brand accent.

## Files

- `terminal-tab-status.js` — this state machine (`TabStatusController`).
- `terminal-multitab.js` — wires xterm `onData` and backend session events
  into the controller; owns the DOM refs passed in as `ui`.
- `style.css` — `.tab-status-*` selectors and keyframes.
- `Tests/wwwroot/js/terminal-tab-status.test.mjs` — state machine unit tests
  (run with `node --test Tests/wwwroot/js/terminal-tab-status.test.mjs`).
