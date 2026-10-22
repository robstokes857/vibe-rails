import test from 'node:test';
import assert from 'node:assert/strict';
import { UiVisibility } from '../../../VibeRails/wwwroot/js/modules/ui-visibility.js';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { bindCardChecks } from '../../../VibeRails/wwwroot/js/modules/board-card-checks.js';
import { mountLaneAgentBody } from '../../../VibeRails/wwwroot/js/modules/board-lane-agent-body.js';
import { JobController } from '../../../VibeRails/wwwroot/js/modules/jobs-controller.js';

const settle = () => new Promise(resolve => setImmediate(resolve));

function surface(properties = {}) {
    const listeners = new Map();
    return Object.assign({
        listeners,
        addEventListener(type, listener) {
            if (!listeners.has(type)) listeners.set(type, new Set());
            listeners.get(type).add(listener);
        },
        removeEventListener(type, listener) { listeners.get(type)?.delete(listener); },
        emit(type, data, source = this.parent) { for (const listener of listeners.get(type) || []) listener({ data, source }); }
    }, properties);
}

function fixture(t, vscode = true) {
    const host = surface({ __viberails_VSCODE__: vscode, parent: {} });
    const page = surface({ hidden: false, querySelector: () => null });
    const visibility = new UiVisibility(host, page);
    const app = {
        currentView: 'board', data: { configs: {} },
        isUiHidden: () => visibility.isHidden(),
        onUiVisible: listener => visibility.onVisible(listener)
    };
    t.after(() => visibility.dispose());
    return { host, page, visibility, app,
        show(visible) { host.emit('message', { command: 'hostVisibility', visible }); } };
}

function installGlobals(t, values) {
    const originals = Object.fromEntries(Object.keys(values).map(key => [key, globalThis[key]]));
    Object.assign(globalThis, values);
    t.after(() => Object.assign(globalThis, originals));
}

function timers(t, host) {
    const callbacks = new Set();
    const setInterval = callback => { callbacks.add(callback); return callback; };
    const clearInterval = callback => callbacks.delete(callback);
    Object.assign(host, { setInterval, clearInterval });
    installGlobals(t, { setInterval, clearInterval });
    return { callbacks, tick() { for (const callback of callbacks) callback(); } };
}

test('host-hidden tabs pause while document-visible; both visibility sources must allow resume', t => {
    const f = fixture(t);
    let resumed = 0;
    const unsubscribe = f.app.onUiVisible(() => resumed++);
    f.show(false);
    assert.equal(f.page.hidden, false, 'VS Code keeps the retained document visible');
    assert.equal(f.app.isUiHidden(), true);
    f.host.emit('focus');
    assert.equal(resumed, 0);
    f.page.hidden = true;
    f.page.emit('visibilitychange');
    f.show(true);
    assert.equal(f.app.isUiHidden(), true, 'revealing a tab cannot override a hidden document');
    assert.equal(resumed, 0);
    f.page.hidden = false;
    f.page.emit('visibilitychange');
    assert.equal(f.app.isUiHidden(), false);
    assert.equal(resumed, 1);
    f.show(true);
    assert.equal(resumed, 1, 'duplicate host states do not refresh again');
    f.show(false);
    f.show(true);
    assert.equal(resumed, 2, 'a host-only reveal resumes immediately');
    unsubscribe();
    f.host.emit('focus');
    assert.equal(resumed, 2);
});

test('browser fallback ignores host messages; malformed messages and disposal are harmless', t => {
    const browser = fixture(t, false);
    browser.show(false);
    assert.equal(browser.app.isUiHidden(), false);
    browser.page.hidden = true;
    assert.equal(browser.app.isUiHidden(), true);
    const f = fixture(t);
    for (const data of [null, {}, { command: 'hostVisibility', visible: 'false' },
        { command: 'unrelated', visible: false }]) f.host.emit('message', data);
    assert.equal(f.app.isUiHidden(), false);
    f.visibility.dispose();
    assert.equal(f.host.listeners.get('message').size, 0);
    assert.equal(f.host.listeners.get('focus').size, 0);
    assert.equal(f.page.listeners.get('visibilitychange').size, 0);
});

test('Board timers and lifecycle events pause, resume without overlap, and unsubscribe', async t => {
    const f = fixture(t);
    installGlobals(t, { window: f.host, document: f.page });
    const clock = timers(t, f.host);
    const events = new Map();
    f.app.appEventClient = { on(name, callback) { events.set(name, callback); return () => events.delete(name); } };
    const board = new BoardController(f.app);
    let snapshots = 0, activity = 0, finish;
    board.refreshBoardSnapshot = async () => { snapshots++; await new Promise(resolve => { finish = resolve; }); };
    board.refreshSessionActivity = async () => { activity++; };
    f.show(false);
    board.bindSessionActivity();
    await settle();
    clock.tick();
    for (const callback of events.values()) callback();
    await settle();
    assert.equal(snapshots + activity, 0);
    f.show(true);
    assert.equal(snapshots, 1);
    f.host.emit('focus');
    clock.tick();
    assert.equal(snapshots, 1, 'reveal, focus and interval share the pending guard');
    finish();
    await settle();
    assert.equal(activity, 1);
    for (const callback of events.values()) callback();
    await settle();
    assert.equal(activity, 2, 'one microtask coalesces simultaneous lifecycle events');
    events.get('session_started')();
    board.disposeSessionActivity();
    await settle();
    f.show(false); f.show(true);
    assert.equal(activity, 2, 'queued events are discarded on unload');
    assert.equal(snapshots, 1);
    assert.equal(clock.callbacks.size, 0);
    assert.equal(f.visibility.listeners.size, 0);
});

test('Jobs pause while host-hidden and resume through the existing overlap guard', async t => {
    const f = fixture(t);
    f.app.currentView = 'ai-agents';
    const content = { querySelector: () => ({}) };
    f.page.getElementById = () => content;
    installGlobals(t, { window: f.host, document: f.page });
    const clock = timers(t, f.host);
    const jobs = new JobController(f.app);
    jobs.bindPageActions = () => {};
    jobs.refreshAll = async () => {};
    const pending = [];
    jobs.refreshRuns = jobs.refreshJobs = () => new Promise(resolve => pending.push(resolve));
    await jobs.loadView();
    f.show(false);
    clock.tick();
    assert.equal(pending.length, 0);
    f.show(true);
    assert.equal(pending.length, 2);
    clock.tick(); f.host.emit('focus');
    assert.equal(pending.length, 2);
    pending.forEach(resolve => resolve());
    await settle();
    jobs.unload();
    f.show(false); f.show(true);
    assert.equal(pending.length, 2);
    assert.equal(clock.callbacks.size, 0);
    assert.equal(f.visibility.listeners.size, 0);
});

test('card checks pause and refresh on reveal, then release their subscription', async t => {
    const f = fixture(t);
    const clock = timers(t, f.host);
    const nodes = new Map();
    const host = surface({ isConnected: true, querySelector(selector) {
        if (!nodes.has(selector)) nodes.set(selector, {});
        return nodes.get(selector);
    } });
    let reads = 0;
    f.app.apiCall = async () => { reads++; return { checks: [] }; };
    const binding = bindCardChecks({ querySelector: () => host }, { id: 'card' }, f.app);
    await settle();
    assert.equal(reads, 1);
    f.show(false); clock.tick();
    assert.equal(reads, 1);
    f.show(true);
    await settle();
    assert.equal(reads, 2);
    binding.dispose();
    f.show(false); f.show(true); clock.tick();
    assert.equal(reads, 2);
    assert.equal(f.visibility.listeners.size, 0);
});

test('lane activity pauses and refreshes on reveal, then releases its subscription', async t => {
    const f = fixture(t);
    installGlobals(t, { window: f.host, document: f.page });
    const clock = timers(t, f.host);
    const content = { removeAttribute() {}, querySelector: () => null, querySelectorAll: () => [] };
    const running = {};
    const panel = surface({ isConnected: true, classList: { toggle() {} },
        querySelector: selector => selector === '[data-lane-agents-content]' ? content : running });
    let reads = 0;
    f.app.apiCall = async url => {
        if (url.endsWith('/automation/running')) reads++;
        return { jobIds: [], jobs: [], environments: [], runningAgents: [], workflows: [] };
    };
    BoardApi.attach(f.app);
    const dispose = mountLaneAgentBody({ app: f.app, panel, column: { id: 'lane' },
        signal: new AbortController().signal, position() {}, updateCount() {} });
    await settle();
    f.show(false); clock.tick();
    assert.equal(reads, 0);
    f.show(true);
    await settle();
    assert.equal(reads, 1);
    dispose();
    f.show(false); f.show(true); clock.tick();
    assert.equal(reads, 1);
    assert.equal(f.visibility.listeners.size, 0);
});


test('foreign windows cannot change host visibility', t => {
    const f = fixture(t);
    for (const source of [{}, null, f.host]) {
        f.host.emit('message', { command: 'hostVisibility', visible: false }, source);
        assert.equal(f.app.isUiHidden(), false);
    }
    f.show(false);
    assert.equal(f.app.isUiHidden(), true);
});
