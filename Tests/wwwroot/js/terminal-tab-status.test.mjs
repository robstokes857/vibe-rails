// Unit tests for TabStatusController state machine.
//
// Regression context: a Codex session was reported stuck on "Thinking" after
// the user answered the first selection prompt — the controller never came back
// to WAITING for the second prompt. These tests pin the transitions that the
// scenario depends on so we notice if the state machine breaks them.
//
// Run with:  node --test Tests/wwwroot/js/terminal-tab-status.test.mjs

import test from 'node:test';
import assert from 'node:assert/strict';

// ── Minimal DOM / globals so the module can be imported ──────────────────────
const fakeElement = () => ({
    className: '',
    style: {},
    textContent: '',
    innerHTML: '',
    classList: {
        _set: new Set(),
        add(...c) { c.forEach(x => this._set.add(x)); },
        remove(...c) { c.forEach(x => this._set.delete(x)); },
        toggle(c, on) { on ? this._set.add(c) : this._set.delete(c); },
        contains(c) { return this._set.has(c); },
    },
    appendChild() {},
    addEventListener() {},
    get offsetHeight() { return 0; },
});
globalThis.document = {
    createElement: () => fakeElement(),
};

// Stub utils.js — getCliBrand is the only symbol the module imports.
// We stage the real module into a tmp dir alongside a stub utils file, so its
// relative `./utils.js` import resolves to the stub (avoiding any DOM deps in
// utils.js itself).
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import { readFileSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import os from 'node:os';

const tmpDir = path.join(os.tmpdir(), 'viberails-tab-status-tests-' + process.pid);
mkdirSync(tmpDir, { recursive: true });
process.on('exit', () => { try { rmSync(tmpDir, { recursive: true, force: true }); } catch {} });

const sourceModule = path.resolve('VibeRails/wwwroot/js/modules/terminal-tab-status.js');
const stagedModule = path.join(tmpDir, 'terminal-tab-status.mjs');
const rewritten = readFileSync(sourceModule, 'utf8').replace("./utils.js", "./utils-stub.mjs");
writeFileSync(stagedModule, rewritten);
writeFileSync(path.join(tmpDir, 'utils-stub.mjs'), 'export function getCliBrand() { return {}; }\n');

const { TabStatusController, TAB_STATUS } = await import(pathToFileURL(stagedModule).href);

// ── Test helpers ─────────────────────────────────────────────────────────────
function makeController({ isActiveTab = () => true, cliKey = null } = {}) {
    const tabState = { label: 'Test', accentColor: null };
    const ui = { item: fakeElement(), button: fakeElement(), close: fakeElement() };
    const flashes = { ready: 0, waiting: 0 };
    const notifications = { ready: 0 };
    const progress = [];
    const controller = new TabStatusController(tabState, ui, {
        getCliKey: () => cliKey,
        isActiveTab,
        setProgress: (p) => progress.push(p),
        notifyReady: () => { notifications.ready++; },
    });
    // Patch flash methods so we can assert on them deterministically.
    controller._flashReady = () => { flashes.ready++; };
    controller._flashWaiting = () => { flashes.waiting++; };
    // Suppress the emoji animation timer during tests.
    controller._startEmojiCycle = () => {};
    controller._stopEmojiCycle = () => {};
    return { controller, flashes, notifications, progress, ui, tabState };
}

// ── Tests ────────────────────────────────────────────────────────────────────

test('socket open from fresh controller → CONNECTED', () => {
    const { controller } = makeController();
    controller.onSocketOpen();
    assert.equal(controller._status, TAB_STATUS.CONNECTED);
});

test('Enter from CONNECTED → THINKING, arms _awaitingFirstIdle', () => {
    const { controller, progress } = makeController();
    controller.onSocketOpen();
    controller.onTerminalData('\r');
    assert.equal(controller._status, TAB_STATUS.THINKING);
    assert.equal(controller._awaitingFirstIdle, true);
    assert.deepEqual(progress.at(-1), { state: 3, value: 0 });
});

test('ACTIVE state has been removed from the public enum', () => {
    assert.equal(TAB_STATUS.ACTIVE, undefined);
});

test('session idle from THINKING → READY and disarms _awaitingFirstIdle', () => {
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r'); // → THINKING
    controller.onSessionIdle();
    assert.equal(controller._status, TAB_STATUS.READY);
    assert.equal(controller._awaitingFirstIdle, false);
});

test('onWaitingForUserSelection from THINKING → WAITING (first prompt)', () => {
    const { controller, flashes } = makeController({ isActiveTab: () => false });
    controller.onSocketOpen();
    controller.onTerminalData('\r'); // → THINKING
    controller.onWaitingForUserSelection();
    assert.equal(controller._status, TAB_STATUS.WAITING);
    assert.equal(flashes.waiting, 1, 'background tab should flash on entering WAITING');
});

test('user answers first prompt, then second waiting event still transitions', () => {
    // This is the reported bug scenario.
    //
    //   1. CONNECTED
    //   2. user sends prompt          → THINKING
    //   3. backend fires waiting      → WAITING
    //   4. user presses Enter         → THINKING
    //   5. backend fires waiting      → must reach WAITING again
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('h');             // start typing
    controller.onTerminalData('i');
    controller.onTerminalData('\r');            // submit → THINKING
    controller.onWaitingForUserSelection();     // first waiting → WAITING
    assert.equal(controller._status, TAB_STATUS.WAITING, 'setup: first waiting should land in WAITING');

    controller.onTerminalData('\r');            // accept selection → THINKING
    assert.equal(controller._status, TAB_STATUS.THINKING, 'Enter from WAITING must move to THINKING');

    controller.onWaitingForUserSelection();     // second waiting
    assert.equal(
        controller._status,
        TAB_STATUS.WAITING,
        'second onWaitingForUserSelection must transition THINKING → WAITING',
    );
});

test('arrow-key navigation during WAITING leaves the state alone', () => {
    // Pre-fix: "\e[B" contained printable bytes ("[", "B") and the tab was
    // flipped to ACTIVE, which then suppressed onWaitingForUserSelection via
    // the ACTIVE guard — the actual mechanism behind the stuck-on-Thinking
    // report. With ACTIVE removed, menu navigation is a no-op here.
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r');            // → THINKING
    controller.onWaitingForUserSelection();     // → WAITING
    controller.onTerminalData('\x1b[B');        // user presses ↓ to navigate
    assert.equal(controller._status, TAB_STATUS.WAITING);
    controller.onTerminalData('\x1b[A');        // ↑
    assert.equal(controller._status, TAB_STATUS.WAITING);
});

test('xterm auto-reply (DSR / cursor position) during THINKING is a no-op', () => {
    // Pre-fix: when the TUI queries the terminal ("\e[6n"), xterm auto-
    // responded on the same onData channel with "\e[24;80R", flipping the
    // tab to ACTIVE and suppressing the next onWaitingForUserSelection.
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r'); // → THINKING
    controller.onTerminalData('\x1b[24;80R');
    assert.equal(controller._status, TAB_STATUS.THINKING);
});

test('focus-tracking reports are a no-op (\\e[I / \\e[O)', () => {
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen(); // CONNECTED
    controller.onTerminalData('\x1b[I');
    controller.onTerminalData('\x1b[O');
    assert.equal(controller._status, TAB_STATUS.CONNECTED);
});

test('typing during THINKING no longer flips the tab', () => {
    // ACTIVE is gone: typed chars do not change state. The user's own Enter
    // is the only thing that re-enters THINKING, and the backend's idle
    // signal is the only thing that moves us to READY.
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r');       // → THINKING
    controller.onTerminalData('hello');
    assert.equal(controller._status, TAB_STATUS.THINKING);
});

test('typing a printable char during WAITING clears it to CONNECTED', () => {
    // Reported by Rob in session e910eb94: Codex genuinely asked for permission,
    // tab landed in WAITING, user started typing a reply, and the indicator
    // stayed on "Waiting for user input" — but the user is no longer waiting,
    // they're engaging. Single printable bytes (0x20–0x7E) now clear WAITING
    // back to CONNECTED. CSI sequences and pastes are still ignored — see the
    // ACTIVE-state retirement note in terminal-tab-status.md for why.
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r');
    controller.onWaitingForUserSelection();
    assert.equal(controller._status, TAB_STATUS.WAITING, 'setup: should land in WAITING');
    controller.onTerminalData('h');
    assert.equal(controller._status, TAB_STATUS.CONNECTED, 'first typed char clears WAITING');
});

test('multi-byte data during WAITING does not clear it (paste / CSI)', () => {
    // Guard the carve-out: only single-byte printables clear WAITING. Paste
    // payloads, bracketed-paste wrappers, and CSI sequences must not trigger
    // the transition.
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r');
    controller.onWaitingForUserSelection();
    controller.onTerminalData('hello');                      // multi-char chunk (paste-shape)
    assert.equal(controller._status, TAB_STATUS.WAITING, 'multi-byte chunk must not clear');
    controller.onTerminalData('\x1b[200~pasted\x1b[201~');   // bracketed paste
    assert.equal(controller._status, TAB_STATUS.WAITING, 'bracketed paste must not clear');
    controller.onTerminalData('\x1b[B');                     // arrow key
    assert.equal(controller._status, TAB_STATUS.WAITING, 'CSI arrow must not clear');
    controller.onTerminalData('\x1b[24;80R');                // DSR auto-reply
    assert.equal(controller._status, TAB_STATUS.WAITING, 'DSR auto-reply must not clear');
});

test('typing a draft in READY does not flip the tab to WAITING (Rob, session dd6819b6)', () => {
    // Session dd6819b6: the user had finished a turn (tab READY) and was typing
    // their next message into the Codex composer. The composer repaints per
    // keystroke; the backend's waiting detector misread that slow repaint as an
    // idle keepalive and fired session_waiting_for_user — flipping the tab to
    // "Waiting for user input" while the user was plainly mid-sentence.
    //
    // A printable keystroke in a resting state (CONNECTED/READY) now marks the
    // user as composing, so a stale waiting signal is ignored. A genuine prompt
    // only appears after a submit (which clears the flag), so this can't eat a
    // real wait — that boundary is pinned by the next test.
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r');         // submit msg1 → THINKING
    controller.onSessionIdle();              // turn finishes → READY
    assert.equal(controller._status, TAB_STATUS.READY, 'setup: should be READY');

    controller.onTerminalData('a');          // start typing msg2 into the composer
    controller.onTerminalData('n');
    controller.onTerminalData('d');
    controller.onWaitingForUserSelection();  // backend false-fires mid-typing
    assert.equal(controller._status, TAB_STATUS.READY,
        'a waiting event while the user is composing must NOT flip to WAITING');
});

test('after submitting, a genuine waiting event still fires (composing flag cleared on submit)', () => {
    // Safety boundary for the fix above: typing then submitting (Enter →
    // THINKING) clears the composing flag, so the real prompt Codex shows
    // during its turn must still reach WAITING. This is the false-negative
    // guard — the failure mode we must never reintroduce.
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('y');          // typed chars (composing)
    controller.onTerminalData('e');
    controller.onTerminalData('s');
    controller.onTerminalData('\r');         // submit → THINKING (clears composing)
    controller.onWaitingForUserSelection();  // Codex asks for approval
    assert.equal(controller._status, TAB_STATUS.WAITING,
        'a prompt after submit must still reach WAITING');
});

test('type-ahead while THINKING does not arm the composing guard', () => {
    // A single printable byte typed *during* THINKING (type-ahead while the
    // agent works) must NOT mark the user as composing — otherwise a real
    // prompt arriving mid-turn would be suppressed. Only resting-state
    // (CONNECTED/READY) keystrokes count as composing a draft.
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r');         // → THINKING
    controller.onTerminalData('x');          // type-ahead during the turn
    controller.onWaitingForUserSelection();  // genuine prompt appears
    assert.equal(controller._status, TAB_STATUS.WAITING,
        'type-ahead during THINKING must not suppress a real prompt');
});

test('session idle while WAITING does NOT pop the tab to READY', () => {
    // Regression: Claude shows a selection prompt, PTY goes quiet, backend
    // fires session_idle after its 5s threshold — the previous "defensive
    // fallback" immediately flipped WAITING → READY, which is the opposite
    // of what the user wants. A waiting menu being quiet is the normal
    // state of a waiting menu.
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r');           // → THINKING
    controller.onWaitingForUserSelection();    // → WAITING
    controller.onSessionIdle();                // backend idle ping
    assert.equal(controller._status, TAB_STATUS.WAITING);
    controller.onSessionIdle();                // repeated
    assert.equal(controller._status, TAB_STATUS.WAITING);
});

test('Enter from WAITING → THINKING; backend pings + CSI do not leave WAITING', () => {
    // Single typed printables are intentionally NOT exercised here — they
    // clear WAITING (covered by "typing a printable char during WAITING
    // clears it to CONNECTED" above). This test pins the complement:
    // backend idle/busy pings and CSI sequences must leave WAITING alone,
    // and Enter is still the path to THINKING.
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r');
    controller.onWaitingForUserSelection();
    controller.onSessionBusy();
    controller.onSessionIdle();
    controller.onTerminalData('\x1b[B');       // arrow key
    assert.equal(controller._status, TAB_STATUS.WAITING, 'setup: none of those should leave WAITING');
    controller.onTerminalData('\r');
    assert.equal(controller._status, TAB_STATUS.THINKING);
});

test('onWaitingForUserSelection blocked while DISCONNECTED', () => {
    const { controller } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onSocketClose();
    controller.onWaitingForUserSelection();
    assert.equal(controller._status, TAB_STATUS.DISCONNECTED);
});

test('same-state WAITING re-entry re-flashes background tabs', () => {
    const { controller, flashes } = makeController({ isActiveTab: () => false });
    controller.onSocketOpen();
    controller.onTerminalData('\r');
    controller.onWaitingForUserSelection();
    const firstFlashes = flashes.waiting;
    controller.onWaitingForUserSelection();
    assert.ok(flashes.waiting > firstFlashes, 'repeated waiting signal should re-flash');
});

test('background tab → READY fires notifyReady (alongside the flash)', () => {
    // A different tab finishing its turn should both flash the tab and pop the
    // small "ready" toast — they share the same background-only branch.
    const { controller, flashes, notifications } = makeController({ isActiveTab: () => false });
    controller.onSocketOpen();
    controller.onTerminalData('\r'); // → THINKING
    controller.onSessionIdle();      // → READY (background)
    assert.equal(controller._status, TAB_STATUS.READY);
    assert.equal(flashes.ready, 1, 'background ready should flash');
    assert.equal(notifications.ready, 1, 'background ready should fire the toast notification');
});

test('active tab → READY does NOT fire notifyReady (nor flash)', () => {
    // The user is already looking at the active tab, so neither the flash nor
    // the toast should fire — only background completions are surfaced.
    const { controller, flashes, notifications } = makeController({ isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r'); // → THINKING
    controller.onSessionIdle();      // → READY (foreground)
    assert.equal(controller._status, TAB_STATUS.READY);
    assert.equal(flashes.ready, 0, 'active ready should not flash');
    assert.equal(notifications.ready, 0, 'active ready should not toast');
});

// ── Shell-tab backend busy/idle driving (CLI key 'shell') ─────────────────────
// A plain shell has no agent "turn", so PTY output IS the signal the user wants:
// running a dev server / build and watching the spinner track activity. Unlike
// agent tabs, the shell tab acts on backend session_busy / session_idle.

test('shell: session_busy from CONNECTED → THINKING (spinner + progress on)', () => {
    const { controller, progress } = makeController({ cliKey: 'shell', isActiveTab: () => true });
    controller.onSocketOpen();               // → CONNECTED
    controller.onSessionBusy();              // backend saw PTY output
    assert.equal(controller._status, TAB_STATUS.THINKING);
    assert.deepEqual(progress.at(-1), { state: 3, value: 0 }, 'busy should arm the indeterminate progress bar');
});

test('shell: session_idle from THINKING → CONNECTED (not READY), no flash/toast', () => {
    // Settle to CONNECTED so a dev server going quiet between requests does not
    // spam the "ready" flash + toast every cycle.
    const { controller, flashes, notifications, progress } = makeController({ cliKey: 'shell', isActiveTab: () => false });
    controller.onSocketOpen();
    controller.onSessionBusy();              // → THINKING
    controller.onSessionIdle();              // server went quiet
    assert.equal(controller._status, TAB_STATUS.CONNECTED);
    assert.equal(flashes.ready, 0, 'shell idle must not fire the ready flash');
    assert.equal(notifications.ready, 0, 'shell idle must not fire the ready toast');
    assert.deepEqual(progress.at(-1), { state: 0, value: 0 }, 'leaving THINKING should turn the progress bar off');
});

test('shell: busy → idle → busy toggles the spinner each cycle', () => {
    const { controller } = makeController({ cliKey: 'shell', isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onSessionBusy();
    assert.equal(controller._status, TAB_STATUS.THINKING, 'first request lights spinner');
    controller.onSessionIdle();
    assert.equal(controller._status, TAB_STATUS.CONNECTED, 'quiet drops spinner');
    controller.onSessionBusy();
    assert.equal(controller._status, TAB_STATUS.THINKING, 'next request lights spinner again');
});

test('shell: session_busy while already THINKING is a no-op (no churn)', () => {
    const { controller, progress } = makeController({ cliKey: 'shell', isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onSessionBusy();              // → THINKING
    const progressLen = progress.length;
    controller.onSessionBusy();              // continued output
    assert.equal(controller._status, TAB_STATUS.THINKING);
    assert.equal(progress.length, progressLen, 'repeated busy should not re-emit progress updates');
});

test('shell: session_busy while DISCONNECTED is ignored', () => {
    const { controller } = makeController({ cliKey: 'shell', isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onSocketClose();              // → DISCONNECTED
    controller.onSessionBusy();
    assert.equal(controller._status, TAB_STATUS.DISCONNECTED);
});

test('shell: session_idle while already CONNECTED (quiet) is a no-op', () => {
    const { controller } = makeController({ cliKey: 'shell', isActiveTab: () => true });
    controller.onSocketOpen();               // → CONNECTED
    controller.onSessionIdle();
    assert.equal(controller._status, TAB_STATUS.CONNECTED);
});

test('shell: Enter-armed idle still settles to CONNECTED, never READY', () => {
    // Even when the user's Enter armed _awaitingFirstIdle, a shell tab must take
    // the shell branch and land in CONNECTED rather than the agent READY path.
    const { controller } = makeController({ cliKey: 'shell', isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onTerminalData('\r');         // → THINKING, arms _awaitingFirstIdle
    assert.equal(controller._awaitingFirstIdle, true);
    controller.onSessionIdle();
    assert.equal(controller._status, TAB_STATUS.CONNECTED);
});

test('agent tab (non-shell): session_busy stays a no-op', () => {
    // Regression guard for the agent path: busy fires on the agent's own prompt
    // redraws, so it must NOT move an agent tab into THINKING.
    const { controller } = makeController({ cliKey: 'claude', isActiveTab: () => true });
    controller.onSocketOpen();               // → CONNECTED
    controller.onSessionBusy();
    assert.equal(controller._status, TAB_STATUS.CONNECTED, 'agent busy must remain inert');
});

test('shell: session_busy does NOT clobber WAITING', () => {
    // A user-attention prompt must outlive incidental PTY output. (Shells don't
    // enter WAITING today, so this guards the path defensively.)
    const { controller } = makeController({ cliKey: 'shell', isActiveTab: () => true });
    controller.onSocketOpen();
    controller.onWaitingForUserSelection();  // → WAITING
    assert.equal(controller._status, TAB_STATUS.WAITING);
    controller.onSessionBusy();              // incidental output
    assert.equal(controller._status, TAB_STATUS.WAITING, 'waiting must survive busy');
});

// ── session_completed: process exit ≠ "ready for input" ───────────────────────

test('shell: session_completed settles THINKING → CONNECTED, no flash/toast', () => {
    // A shell process exiting is "done", not "ready" — mirror the idle carve-out
    // so it never fires the READY flash/toast (the global "completed" toast covers
    // the exit).
    const { controller, flashes, notifications } = makeController({ cliKey: 'shell', isActiveTab: () => false });
    controller.onSocketOpen();
    controller.onSessionBusy();              // → THINKING
    controller.onSessionCompleted();         // process exited
    assert.equal(controller._status, TAB_STATUS.CONNECTED);
    assert.equal(flashes.ready, 0, 'shell completion must not flash READY');
    assert.equal(notifications.ready, 0, 'shell completion must not fire the ready toast');
});

test('agent tab: background session_completed flashes READY but does NOT toast', () => {
    // Completion still flashes the tab, but the in-panel "is ready" toast is
    // reserved for the idle (turn-finished) path — an exited process announcing
    // itself as "ready" would mislead and duplicate the global completed toast.
    const { controller, flashes, notifications } = makeController({ isActiveTab: () => false });
    controller.onSocketOpen();
    controller.onTerminalData('\r');         // → THINKING, arms _awaitingFirstIdle
    controller.onSessionCompleted();         // process exited
    assert.equal(controller._status, TAB_STATUS.READY);
    assert.equal(flashes.ready, 1, 'completion still flashes the tab');
    assert.equal(notifications.ready, 0, 'completion must not pop the ready toast');
});

test('agent tab: background session_idle (turn finished) still flashes AND toasts', () => {
    // Guard the contrast with completion: the idle path is the one that should
    // both flash and toast "is ready".
    const { controller, flashes, notifications } = makeController({ isActiveTab: () => false });
    controller.onSocketOpen();
    controller.onTerminalData('\r');         // → THINKING
    controller.onSessionIdle();              // → READY (turn finished)
    assert.equal(controller._status, TAB_STATUS.READY);
    assert.equal(flashes.ready, 1, 'idle ready flashes');
    assert.equal(notifications.ready, 1, 'idle ready toasts');
});

// ── Status display text — product voice (Rob, 2026-06-12) ─────────────────────
// "Thinking" / "Ready" / "Waiting for user input" are deliberate wording. A
// same-day attempt to rename THINKING's text to "Working" everywhere was
// vetoed; the rename is sanctioned ONLY for the shell tab. These tests pin the
// strings so a future refactor can't silently change the voice.

test('agent tab status text: Connected → Thinking → Waiting for user input → Ready', () => {
    const { controller } = makeController({ cliKey: 'claude', isActiveTab: () => true });
    controller._statusTextEl = fakeElement();
    controller.onSocketOpen();
    assert.equal(controller._statusTextEl.textContent, 'Connected');
    controller.onTerminalData('\r');            // → THINKING
    assert.equal(controller._statusTextEl.textContent, 'Thinking',
        'agent tabs must say "Thinking" — "Working" is shell-only');
    controller.onWaitingForUserSelection();     // → WAITING
    assert.equal(controller._statusTextEl.textContent, 'Waiting for user input',
        'the full waiting string is part of the product voice');
    controller.onTerminalData('\r');            // → THINKING
    controller.onSessionIdle();                 // → READY
    assert.equal(controller._statusTextEl.textContent, 'Ready');
});

test('shell tab status text: THINKING displays "Working" (the one sanctioned rename)', () => {
    const { controller } = makeController({ cliKey: 'shell', isActiveTab: () => true });
    controller._statusTextEl = fakeElement();
    controller.onSocketOpen();
    controller.onSessionBusy();                 // shell output → THINKING
    assert.equal(controller._statusTextEl.textContent, 'Working');
    controller.onSessionIdle();                 // quiet → CONNECTED
    assert.equal(controller._statusTextEl.textContent, 'Connected');
});
