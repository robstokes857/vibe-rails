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
`DISCONNECTED`. From `WAITING`, Enter (`\r`) → `THINKING`. Typed characters do
not change the state.

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
3. Everything else is ignored. Typed characters, arrow keys, function keys,
   and xterm auto-responses to server queries all flow through without
   changing the state. This is a deliberate change from the previous
   `_hasPrintableChar` heuristic — see the ACTIVE retirement note above.

### `onSessionIdle()` / `onSessionCompleted()`

Only transition to `READY` if `_awaitingFirstIdle` is true — i.e. we had
previously entered `THINKING` and have not already been moved out of it.
This prevents the backend's noisy idle pings from spuriously flashing tabs.

Crucially, a `session_idle` arriving while the tab is parked in `WAITING`
is **ignored**. The backend idle threshold is only 5 seconds of no PTY
output, and a menu waiting for user input produces no PTY output by
definition — so treating idle as "prompt must have gone away" was firing
on the common case, not the edge case. `WAITING` only leaves on the user's
Enter press (→ `THINKING`) or socket close (→ `DISCONNECTED`).

### `onSessionBusy()`

No-op. The backend fires `session_busy` on *any* PTY activity (including
initial prompt output), so we can't use it to infer user intent. Enter
detection via `onTerminalData` is the only trigger for entering `THINKING`.

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
