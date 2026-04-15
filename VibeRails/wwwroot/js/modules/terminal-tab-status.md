# Terminal Tab Status State Machine

This document describes the state machine implemented in `terminal-tab-status.js`
(`TabStatusController`). It drives the small status label + icon on each terminal
tab button (e.g. `Connected`, `Thinking`, `Ready`, `Active`, `Disconnected`).

## States

| State          | Meaning                                                                                  | Icon              | Text color       |
| -------------- | ---------------------------------------------------------------------------------------- | ----------------- | ---------------- |
| `CONNECTED`    | Socket is open, no user interaction yet.                                                 | link              | slate `#94a3b8`  |
| `ACTIVE`       | User is composing input (typing printable characters). Not yet submitted.                | keyboard          | green `#a6e3a1`  |
| `THINKING`     | User pressed Enter; backend is working. Animated emoji cycle + indeterminate progress.   | cycling emoji     | shimmer gradient |
| `READY`        | Backend signaled idle/completion after a `THINKING` turn. Background tabs get a flash.   | circle-check      | slate `#94a3b8`  |
| `WAITING`      | Backend detected an interactive selection prompt (e.g. `•`/`◦` menu). Pulsing icon; background tabs get a yellow flash. | hand-point-up     | yellow `#f9e2af` |
| `DISCONNECTED` | WebSocket closed.                                                                        | plug-circle-xmark | peach `#fab387`  |

## Transitions

```
           ┌──────────────┐
           │ (no status)  │
           └──────┬───────┘
                  │ onSocketOpen()
                  ▼
           ┌──────────────┐   any printable char    ┌──────────────┐
           │  CONNECTED   │ ───────────────────────▶│   ACTIVE     │
           └──────┬───────┘                         └──────┬───────┘
                  │                                        │
                  │ Enter (\r)                             │ Enter (\r)
                  ▼                                        ▼
           ┌──────────────┐                         ┌──────────────┐
           │   THINKING   │◀────────────────────────┤   (submits)  │
           └──┬───────┬───┘                         └──────────────┘
              │       │
   Escape     │       │ onSessionIdle() /
   (\x1b)     │       │ onSessionCompleted()
              │       ▼
              │   ┌──────────────┐  any printable char   ┌──────────────┐
              │   │    READY     │──────────────────────▶│    ACTIVE    │
              │   └──────┬───────┘                       └──────┬───────┘
              │          │ Enter (\r)                           │
              │          └──────────┐                           │
              │                     ▼                           │
              │              ┌──────────────┐                   │
              └─────────────▶│   CONNECTED  │                   │
                             └──────────────┘                   │
                                                                │
   THINKING can also be interrupted by typing — we drop         │
   straight into ACTIVE without waiting for the backend to      │
   go idle (see "typing during THINKING" below).                │
                                                                ▼
```

And on socket close from *any* state: → `DISCONNECTED`.

`onWaitingForUserSelection()` can move us into `WAITING` from any state
except `ACTIVE` and `DISCONNECTED`. From `WAITING`, Enter (`\r`) → `THINKING`
and typing a printable char → `ACTIVE`, same as from `READY`.

## Triggers

User input is routed into `onTerminalData(data)` from `terminal-multitab.js`,
which subscribes to xterm's `onData` (keystrokes + paste). Backend session
events are routed into `onSessionIdle()` / `onSessionCompleted()` /
`onSessionBusy()`.

### `onTerminalData(data)`

1. `data === '\r'` (bare Enter) while in `CONNECTED` / `READY` / `ACTIVE` /
   `WAITING` → `THINKING`.
2. `data === '\x1b'` (bare Escape) while in `THINKING`
   → `CONNECTED` (treated as abort).
3. Otherwise, if `_hasPrintableChar(data)` is true and we're not already in
   `ACTIVE` → `ACTIVE`.

`_hasPrintableChar` scans `data` for any char with code ≥ `0x20` excluding
`0x7f` (DEL). This catches regular typing, pasted text, and also ANSI-prefixed
input like arrow keys (`\x1b[A`) — any deliberate user interaction counts.

Bare control codes like Ctrl+C (`\x03`) or Backspace (`\x7f`) do **not** flip
the state on their own.

**Exception: xterm focus in/out reports.** When the TUI has focus tracking
enabled (DEC mode 1004), xterm emits `\x1b[I` on focus and `\x1b[O` on blur
via `onData`. Those bytes contain `[` (0x5B) and `I`/`O` (0x49/0x4F), which
would otherwise be picked up as "printable input" by `_hasPrintableChar`.
That made clicking into a `READY` tab flip it straight to `ACTIVE`, which
is wrong — focus is not composing. `onTerminalData` filters `\x1b[I` and
`\x1b[O` as its very first step so they never reach the transition logic.

### `onSessionIdle()` / `onSessionCompleted()`

Only transition to `READY` if `_awaitingFirstIdle` is true — i.e. we had
previously entered `THINKING` and have not already been moved out of it.
This prevents the backend's noisy idle pings from spuriously flashing tabs.

### `onSessionBusy()`

No-op. The backend fires `session_busy` on *any* PTY activity (including
initial prompt output), so we can't use it to infer user intent. Enter
detection via `onTerminalData` is the only trigger for entering `THINKING`.

### `onWaitingForUserSelection()`

Fired when the backend detects an interactive selection prompt in PTY output
(currently: a chunk containing both `•` and `◦`, via `UserWaiting.Check`).
The backend debounces this to at most once per 30 seconds per session so a
redrawing menu doesn't spam events. Moves the tab into `WAITING` unless we're
already `ACTIVE` (user is composing) or `DISCONNECTED`.

## The `_awaitingFirstIdle` flag

`_awaitingFirstIdle` is the bookkeeping bit that gates whether the next
backend idle event should auto-promote the tab to `READY`.

Set/cleared in `_transitionTo`:

| Entering state | `_awaitingFirstIdle` |
| -------------- | -------------------- |
| `THINKING`     | `true`               |
| `READY`        | `false`              |
| `CONNECTED`    | `false`              |
| `ACTIVE`       | `false`              |
| `WAITING`      | `false`              |

Why `ACTIVE` clears it: see next section.

## Typing during `THINKING` (the "follow-up message" case)

A common case: the backend is still `THINKING` on the previous turn, and the
user starts typing their follow-up message. Two things matter here:

1. We want to **stop the loading indicator** — the user is clearly past the
   point of waiting for the previous turn. So typing during `THINKING`
   transitions straight to `ACTIVE` (step 3 of `onTerminalData`).

2. We do **not** want the tab to then flip to `READY` the moment the backend
   fires its next idle event, because that would yank the user out of
   `ACTIVE` while they're still mid-compose. Entering `ACTIVE` therefore
   clears `_awaitingFirstIdle`, so `onSessionIdle()` becomes a no-op until
   the user actually presses Enter again and re-enters `THINKING`.

The user's own Enter press is the only way out of `ACTIVE`.

## Side effects in `_transitionTo`

Beyond updating `_status` and calling `_applyVisuals`:

- **Progress bar.** Enters indeterminate (`state: 3`) on `THINKING`, turns
  off (`state: 0`) when leaving `THINKING`. `ACTIVE` has no progress bar.
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
(e.g. `tab-status-active`). State-specific text/icon colors live in
`VibeRails/wwwroot/style.css` under the "Tab Status Overhaul" section.

The ready-flash animation uses a separate one-shot class,
`tab-status-ready-flash`, which is removed on `animationend`.

## Files

- `terminal-tab-status.js` — this state machine (`TabStatusController`).
- `terminal-multitab.js` — wires xterm `onData` and backend session events
  into the controller; owns the DOM refs passed in as `ui`.
- `style.css` — `.tab-status-*` selectors and keyframes.
