// Unit tests for the terminal auto-reconnect policy (terminal-reconnect.js).
//
// Context: navigation destroys every terminal socket and the restored manager
// used to reconnect only the active tab; every other tab sat on "Disconnected"
// until the user pressed Connect. These pin the pure policy pieces — close
// classification, backoff, the hidden-tab geometry rule, the kill switch — and
// the per-tab retry scheduler with injected timers. The browser integration
// (navigate away/back reconnects every tab, takeover never retries) lives in
// UITests/tests/terminal-reconnect.spec.js.
//
// Run with:  node --test Tests/wwwroot/js/terminal-reconnect.test.mjs

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import {
    AUTO_RECONNECT_FIRST_DELAY_MS,
    AUTO_RECONNECT_MAX_DELAY_MS,
    AUTO_RECONNECT_MAX_ATTEMPTS,
    AUTO_RECONNECT_STORAGE_KEY,
    CLOSE_KIND,
    TerminalAutoReconnect,
    classifySocketClose,
    computeReconnectDelayMs,
    isAutoReconnectEnabled,
    resolveHiddenConnectGeometry
} from '../../../VibeRails/wwwroot/js/modules/terminal-reconnect.js';

// ── Fake timers ──────────────────────────────────────────────────────────────
class FakeTimers {
    constructor() {
        this.now = 0;
        this.queue = [];
        this.nextId = 1;
    }

    setTimeout(fn, ms) {
        const id = this.nextId++;
        this.queue.push({ id, at: this.now + ms, fn });
        return id;
    }

    clearTimeout(id) {
        this.queue = this.queue.filter((entry) => entry.id !== id);
    }

    advance(ms) {
        this.now += ms;
        const due = this.queue.filter((entry) => entry.at <= this.now).sort((a, b) => a.at - b.at);
        this.queue = this.queue.filter((entry) => entry.at > this.now);
        due.forEach((entry) => entry.fn());
    }

    get pendingDelays() {
        return this.queue.map((entry) => entry.at - this.now);
    }
}

function makeScheduler(overrides = {}) {
    const timers = new FakeTimers();
    const attempts = [];
    let visible = true;
    let visibleCallback = null;

    const scheduler = new TerminalAutoReconnect({
        attempt: () => attempts.push(timers.now),
        canAttempt: () => true,
        isEnabled: () => true,
        isPageVisible: () => visible,
        subscribeVisible: (callback) => {
            visibleCallback = callback;
            return () => { visibleCallback = null; };
        },
        setTimeout: (fn, ms) => timers.setTimeout(fn, ms),
        clearTimeout: (id) => timers.clearTimeout(id),
        ...overrides
    });

    return {
        scheduler,
        timers,
        attempts,
        setVisible(value) {
            visible = value;
            if (value && visibleCallback) visibleCallback();
        },
        hasVisibleSubscription: () => visibleCallback !== null
    };
}

const retryClose = { code: 1006, reason: '', hasActiveSession: true };

// ── classifySocketClose ──────────────────────────────────────────────────────
test('a takeover close is never a retry, whichever viewer took the session', () => {
    assert.equal(classifySocketClose({ code: 1000, reason: 'Session taken over', hasActiveSession: true }), CLOSE_KIND.TAKEOVER);
    assert.equal(classifySocketClose({ code: 1000, reason: 'Session taken over by remote viewer', hasActiveSession: true }), CLOSE_KIND.TAKEOVER);
    assert.equal(classifySocketClose({ code: 1000, reason: 'Session taken over by local viewer', hasActiveSession: true }), CLOSE_KIND.TAKEOVER);
});

test('closes that say the session is gone are not retried', () => {
    for (const reason of ['Terminal session exited', 'Terminal session stopped', 'CLI session ended', 'No active terminal session']) {
        assert.equal(classifySocketClose({ code: 1000, reason, hasActiveSession: true }), CLOSE_KIND.SESSION_ENDED, reason);
    }
    assert.equal(classifySocketClose({ code: 1007, reason: '', hasActiveSession: true }), CLOSE_KIND.SESSION_ENDED, '1007 = InvalidPayloadData');
    assert.equal(classifySocketClose({ code: 1008, reason: 'Too many failed PIN attempts', hasActiveSession: true }), CLOSE_KIND.REJECTED);
    assert.equal(classifySocketClose({ code: 1000, reason: 'Done', hasActiveSession: false }), CLOSE_KIND.NO_SESSION);
});

test('drops, proxy "Done" closes and rejected upgrades are retried', () => {
    assert.equal(classifySocketClose({ code: 1006, reason: '', hasActiveSession: true }), CLOSE_KIND.RETRY, 'abnormal close');
    assert.equal(classifySocketClose({ code: 1000, reason: 'Done', hasActiveSession: true }), CLOSE_KIND.RETRY, 'proxy relay ended');
    assert.equal(classifySocketClose({ code: 1000, reason: 'Peer disconnected', hasActiveSession: true }), CLOSE_KIND.RETRY, 'forwarded close without reason');
    assert.equal(classifySocketClose({ code: 1001, reason: '', hasActiveSession: true }), CLOSE_KIND.RETRY, 'going away');
    assert.equal(classifySocketClose({ code: 1011, reason: 'boom', hasActiveSession: true }), CLOSE_KIND.RETRY, 'server error');
    assert.equal(classifySocketClose(), CLOSE_KIND.RETRY, 'no information at all');
});

// ── computeReconnectDelayMs ──────────────────────────────────────────────────
test('backoff doubles from the first delay and caps at the maximum', () => {
    assert.deepEqual(
        [0, 1, 2, 3, 4, 5, 6].map((attempt) => computeReconnectDelayMs(attempt)),
        [2000, 4000, 8000, 16000, 30000, 30000, 30000]
    );
    assert.equal(AUTO_RECONNECT_FIRST_DELAY_MS, 2000);
    assert.equal(AUTO_RECONNECT_MAX_DELAY_MS, 30000);
    assert.equal(AUTO_RECONNECT_MAX_ATTEMPTS, 5);
    assert.equal(computeReconnectDelayMs(-3), 2000, 'negative attempt clamps to the first delay');
    assert.equal(computeReconnectDelayMs(Number.NaN), 2000, 'NaN attempt clamps to the first delay');
    assert.equal(computeReconnectDelayMs(3, { firstDelayMs: 100, maxDelayMs: 500 }), 500);
});

// ── isAutoReconnectEnabled ───────────────────────────────────────────────────
test('auto-reconnect is on unless the storage key says off', () => {
    assert.equal(AUTO_RECONNECT_STORAGE_KEY, 'viberails_terminal_autoReconnect');
    assert.equal(isAutoReconnectEnabled(null), true, 'no storage at all');
    assert.equal(isAutoReconnectEnabled({ getItem: () => null }), true, 'unset');
    assert.equal(isAutoReconnectEnabled({ getItem: () => 'on' }), true);
    assert.equal(isAutoReconnectEnabled({ getItem: () => 'off' }), false);
    assert.equal(isAutoReconnectEnabled({ getItem: () => { throw new Error('blocked'); } }), true, 'storage errors default to on');
});

// ── resolveHiddenConnectGeometry ─────────────────────────────────────────────
test('a hidden tab only borrows geometry from a live, sanely-sized visible tab', () => {
    assert.equal(resolveHiddenConnectGeometry(undefined), null);
    assert.equal(resolveHiddenConnectGeometry({}), null, 'no geometry accessor');
    assert.equal(resolveHiddenConnectGeometry({
        hasOpenSocket: () => false,
        getLastResizeGeometry: () => ({ cols: 145, rows: 29 })
    }), null, 'socket closed: the signature is stale');
    assert.equal(resolveHiddenConnectGeometry({
        hasOpenSocket: () => true,
        getLastResizeGeometry: () => null
    }), null, 'nothing reported to the PTY yet');
    assert.equal(resolveHiddenConnectGeometry({
        hasOpenSocket: () => true,
        getLastResizeGeometry: () => ({ cols: 21, rows: 17 })
    }), null, 'pre-layout FitAddon grid is not trusted');
    assert.deepEqual(resolveHiddenConnectGeometry({
        hasOpenSocket: () => true,
        getLastResizeGeometry: () => ({ cols: 145, rows: 29 })
    }), { cols: 145, rows: 29 });
});

// ── TerminalAutoReconnect ────────────────────────────────────────────────────
test('the scheduler requires an attempt callback', () => {
    assert.throws(() => new TerminalAutoReconnect({}), TypeError);
});

test('an unexpected close arms one retry after the first delay and fires it on time', () => {
    const { scheduler, timers, attempts } = makeScheduler();

    const decision = scheduler.handleClose(retryClose);
    assert.deepEqual(decision, { kind: CLOSE_KIND.RETRY, scheduled: true, delayMs: 2000, exhausted: false });
    assert.equal(scheduler.isPending(), true);
    assert.equal(attempts.length, 0, 'never attempts synchronously');

    timers.advance(1999);
    assert.equal(attempts.length, 0);
    timers.advance(1);
    assert.deepEqual(attempts, [2000]);
    assert.equal(scheduler.isPending(), false);
});

test('each failed attempt backs off further until the budget is exhausted', () => {
    const { scheduler, timers, attempts } = makeScheduler();
    const delays = [];

    for (let i = 0; i < AUTO_RECONNECT_MAX_ATTEMPTS; i += 1) {
        const decision = scheduler.handleClose(retryClose);
        assert.equal(decision.scheduled, true, `attempt ${i + 1} should be scheduled`);
        delays.push(decision.delayMs);
        timers.advance(decision.delayMs);
    }
    assert.deepEqual(delays, [2000, 4000, 8000, 16000, 30000]);
    assert.equal(attempts.length, AUTO_RECONNECT_MAX_ATTEMPTS);

    const exhausted = scheduler.handleClose(retryClose);
    assert.deepEqual(exhausted, { kind: CLOSE_KIND.RETRY, scheduled: false, delayMs: 0, exhausted: true });
    assert.equal(scheduler.isPending(), false);
    assert.equal(timers.pendingDelays.length, 0, 'nothing left armed');
});

test('an open socket alone does not restore the budget; the first data does', () => {
    const { scheduler, timers } = makeScheduler();

    scheduler.handleClose(retryClose);
    timers.advance(2000);
    scheduler.handleClose(retryClose);
    assert.equal(scheduler.attempts, 2);
    timers.advance(4000);

    // The root accepts a viewer socket before it has reached the tab child, so an open socket
    // proves nothing on its own — a dead child opens and closes again a moment later.
    scheduler.handleOpen();
    assert.equal(scheduler.attempts, 2, 'the budget survives an open that carried no data');
    assert.equal(scheduler.isPending(), false, 'but the armed retry is cancelled');
    assert.equal(scheduler.handleClose(retryClose).delayMs, 8000, 'backoff keeps growing');
    timers.advance(8000);

    scheduler.handleOpen();
    scheduler.handleHealthy();
    assert.equal(scheduler.attempts, 0);
    assert.equal(scheduler.handleClose(retryClose).delayMs, 2000);
});

test('a socket that opens and closes without ever carrying data still exhausts the budget', () => {
    const { scheduler, timers } = makeScheduler();

    // The regression this pins: resetting on open meant repeated upstream failures went back to
    // attempt zero every time and retried forever, despite the five-attempt limit.
    for (let i = 0; i < AUTO_RECONNECT_MAX_ATTEMPTS; i += 1) {
        const decision = scheduler.handleClose(retryClose);
        assert.equal(decision.scheduled, true, `attempt ${i + 1} should be scheduled`);
        timers.advance(decision.delayMs);
        scheduler.handleOpen();
    }
    assert.equal(scheduler.handleClose(retryClose).exhausted, true);
    assert.equal(timers.pendingDelays.length, 0, 'nothing left armed');
});

test('takeover, session-ended, rejected and no-session closes never arm a retry', () => {
    const { scheduler, timers } = makeScheduler();

    const cases = [
        { code: 1000, reason: 'Session taken over', hasActiveSession: true },
        { code: 1000, reason: 'Terminal session exited', hasActiveSession: true },
        { code: 1007, reason: 'No active terminal session', hasActiveSession: true },
        { code: 1008, reason: 'Too many failed PIN attempts', hasActiveSession: true },
        { code: 1000, reason: 'Done', hasActiveSession: false }
    ];
    for (const close of cases) {
        const decision = scheduler.handleClose(close);
        assert.equal(decision.scheduled, false, close.reason);
        assert.notEqual(decision.kind, CLOSE_KIND.RETRY, close.reason);
    }
    assert.equal(scheduler.isPending(), false);
    assert.equal(scheduler.attempts, 0, 'non-retry closes do not consume the budget');
    assert.equal(timers.pendingDelays.length, 0);
});

test('the kill switch stops retries from being armed', () => {
    const { scheduler, timers } = makeScheduler({ isEnabled: () => false });
    const decision = scheduler.handleClose(retryClose);
    assert.equal(decision.kind, CLOSE_KIND.RETRY);
    assert.equal(decision.scheduled, false);
    assert.equal(timers.pendingDelays.length, 0);
});

test('a close while a retry is already pending does not arm a second timer', () => {
    const { scheduler, timers } = makeScheduler();
    scheduler.handleClose(retryClose);
    const second = scheduler.handleClose(retryClose);
    assert.equal(second.scheduled, false);
    assert.equal(second.delayMs, 2000, 'reports the delay already pending');
    assert.equal(timers.pendingDelays.length, 1);
    assert.equal(scheduler.attempts, 1);
});

test('a retry that fires when the tab can no longer attempt is dropped', () => {
    let allowed = true;
    const { scheduler, timers, attempts } = makeScheduler({ canAttempt: () => allowed });
    scheduler.handleClose(retryClose);
    allowed = false;
    timers.advance(2000);
    assert.equal(attempts.length, 0);
    assert.equal(scheduler.isPending(), false, 'the timer is consumed, not re-armed');
});

test('a retry that fires while the page is hidden waits for it to become visible', () => {
    const harness = makeScheduler();
    const { scheduler, timers, attempts } = harness;

    harness.setVisible(false);
    scheduler.handleClose(retryClose);
    timers.advance(2000);
    assert.equal(attempts.length, 0, 'no attempt against a hidden webview');
    assert.equal(harness.hasVisibleSubscription(), true);
    assert.equal(scheduler.isPending(), true, 'still pending while waiting for visibility');

    harness.setVisible(true);
    assert.deepEqual(attempts, [2000]);
    assert.equal(harness.hasVisibleSubscription(), false, 'one-shot subscription released');
    assert.equal(scheduler.isPending(), false);
});

test('cancel keeps the budget; reset clears it; both drop armed timers and visibility waits', () => {
    const harness = makeScheduler();
    const { scheduler, timers, attempts } = harness;

    scheduler.handleClose(retryClose);
    scheduler.cancel();
    assert.equal(scheduler.isPending(), false);
    assert.equal(timers.pendingDelays.length, 0);
    assert.equal(scheduler.attempts, 1, 'cancel keeps the attempt count');
    timers.advance(60000);
    assert.equal(attempts.length, 0);

    harness.setVisible(false);
    scheduler.handleClose(retryClose);
    timers.advance(4000);
    assert.equal(harness.hasVisibleSubscription(), true);
    scheduler.reset();
    assert.equal(harness.hasVisibleSubscription(), false);
    assert.equal(scheduler.attempts, 0);
    harness.setVisible(true);
    assert.equal(attempts.length, 0, 'a reset retry never fires');
});

// ── Source pins for the integration points ───────────────────────────────────
const tabSource = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/terminal-tab.js'), 'utf8');
const managerSource = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/terminal-multitab.js'), 'utf8');

test('plain tab selection still does not imply reconnect (2026-03-07 decision)', () => {
    // The tab button click must keep passing connectIfNeeded: false; the only
    // activation-time connect is the auto-reconnect debt via wantsConnectOnActivate.
    const clickAt = managerSource.indexOf("button.addEventListener('click', () => {");
    assert.ok(clickAt >= 0, 'tab button click handler present');
    const handler = managerSource.slice(clickAt, managerSource.indexOf('button.addEventListener(\'dblclick\'', clickAt));
    assert.ok(handler.includes('connectIfNeeded: false'), 'tab click passes connectIfNeeded: false');
    assert.ok(!handler.includes('connectIfNeeded: true'), 'tab click never passes connectIfNeeded: true');
    assert.ok(managerSource.includes('target.instance.wantsConnectOnActivate()'), 'activateTab honours the auto-reconnect debt');
});

test('a hidden tab reconnect pins geometry instead of fitting a 0x0 host', () => {
    const connectAt = tabSource.indexOf('async connect({ pinnedGeometry = null } = {})');
    assert.ok(connectAt >= 0, 'connect() accepts pinnedGeometry');
    const body = tabSource.slice(connectAt, tabSource.indexOf('async startSession(', connectAt));
    assert.ok(body.includes('this.vibeTerminal.resize(pinned.cols, pinned.rows)'), 'pinned tabs resize xterm');
    assert.ok(body.includes('this.autoReconnect?.handleOpen()'), 'onopen cancels the armed retry');
    assert.ok(body.includes('this.autoReconnect?.handleHealthy()'), 'the first data restores the retry budget');
    assert.ok(body.includes('this.autoReconnect?.handleClose({'), 'onclose consults the policy');
});

test('the restored manager schedules the background pass right after the active tab connects', () => {
    const initAt = managerSource.indexOf('async initialize() {');
    const activateAt = managerSource.indexOf('await this.activateTab(target, { connectIfNeeded: true });', initAt);
    const scheduleAt = managerSource.indexOf('this.scheduleBackgroundReconnect();', activateAt);
    assert.ok(initAt >= 0 && activateAt > initAt && scheduleAt > activateAt);
});
