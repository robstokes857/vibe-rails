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
function makeController({ isActiveTab = () => true } = {}) {
    const tabState = { label: 'Test', accentColor: null };
    const ui = { item: fakeElement(), button: fakeElement(), close: fakeElement() };
    const flashes = { ready: 0, waiting: 0 };
    const progress = [];
    const controller = new TabStatusController(tabState, ui, {
        getCliKey: () => null,
        isActiveTab,
        setProgress: (p) => progress.push(p),
    });
    // Patch flash methods so we can assert on them deterministically.
    controller._flashReady = () => { flashes.ready++; };
    controller._flashWaiting = () => { flashes.waiting++; };
    // Suppress the emoji animation timer during tests.
    controller._startEmojiCycle = () => {};
    controller._stopEmojiCycle = () => {};
    return { controller, flashes, progress, ui, tabState };
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
