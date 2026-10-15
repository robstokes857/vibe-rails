import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const panePath = path.resolve('VibeRails/wwwroot/js/modules/script-agent-pane.js');
const terminalPath = path.resolve('VibeRails/wwwroot/js/modules/terminal-multitab.js');
const { ScriptAgentPane, scriptAgentOwner, isOwnedTab, SCRIPT_AGENT_SELECTION_KEY } = await import(pathToFileURL(panePath).href);
const { TerminalController, TerminalManager } = await import(pathToFileURL(terminalPath).href);

const OWNER = scriptAgentOwner('reg-1');

function fakeElement() {
    return { hidden: false, disabled: false, textContent: '', value: '' };
}

/** The pane's markup as the handful of elements it reads; the status chip stays null (no DOM). */
function fakeHost() {
    const elements = {
        '[data-script-agent-terminal]': fakeElement(),
        '[data-script-agent-placeholder]': fakeElement(),
        '[data-script-agent-lifecycle]': fakeElement(),
        '[data-script-agent-select]': Object.assign(fakeElement(), { value: 'base:claude' }),
        '[data-script-agent-label]': fakeElement()
    };
    const buttons = {};
    for (const action of ['start', 'connect', 'restart', 'stop']) {
        buttons[action] = action === 'start' ? [fakeElement(), fakeElement()] : [fakeElement()];
    }
    const root = {
        listeners: [],
        querySelector: (selector) => elements[selector] || null,
        querySelectorAll: (selector) => buttons[/data-script-agent-action="([a-z]+)"/.exec(selector)?.[1]] || [],
        addEventListener(type, fn) { this.listeners.push({ type, fn }); },
        removeEventListener() {}
    };
    const host = {
        html: '',
        set innerHTML(value) { this.html = value; },
        querySelector: (selector) => (selector === '[data-script-agent]' ? root : null)
    };
    return { host, root, elements, buttons };
}

function fakeApp({ tabs = [], status = { hasActiveSession: true } } = {}) {
    const app = {
        calls: [],
        errors: [],
        registered: new Map(),
        data: { environments: [] },
        status,
        async apiCall(url, method = 'GET', body = null) {
            app.calls.push({ url, method, body });
            if (url === '/api/v1/terminal/tabs' && method === 'GET') return { tabs, maxTabs: 100 };
            if (url.startsWith('/api/v1/terminal/tabs?owner=') && method === 'POST') return { tabId: 'new-tab', hasActiveSession: false };
            if (url.endsWith('/status')) {
                if (app.status instanceof Error) throw app.status;
                return app.status;
            }
            return {};
        },
        showError(message) { app.errors.push(message); },
        terminalController: {
            registerOwnedTab: (id, tab) => app.registered.set(id, tab),
            unregisterOwnedTab: (id, tab) => { if (app.registered.get(id) === tab) app.registered.delete(id); }
        }
    };
    return app;
}

/** Stands in for TerminalTab: the pane only drives its session and socket lifecycle. */
function fakeTabFactory(log, manager) {
    return (state) => ({
        state,
        socketOpen: false,
        disposed: false,
        terminal: { options: {} },
        autoReconnect: { isPending: () => false, cancel() {} },
        setActive() {},
        hasOpenSocket() { return this.socketOpen; },
        // Like TerminalTab, an open socket repaints its manager's chrome.
        async connect() { log.connects.push(state.id); this.socketOpen = true; state.status = 'connected'; manager().updateUi(); return true; },
        async startSession(body) {
            log.starts.push({ tabId: state.id, body });
            state.hasActiveSession = true;
            state.status = 'connected';
            this.socketOpen = true;
            return { hasActiveSession: true };
        },
        async stopSession() { state.hasActiveSession = false; state.status = 'not-started'; this.socketOpen = false; return true; },
        dispose() { this.disposed = true; this.socketOpen = false; },
        scheduleFitPasses() {},
        focusInput() {}
    });
}

function withStorage(t) {
    const previous = globalThis.localStorage;
    const store = new Map();
    globalThis.localStorage = { getItem: key => store.get(key) ?? null, setItem: (key, value) => store.set(key, String(value)) };
    t.after(() => { if (previous === undefined) delete globalThis.localStorage; else globalThis.localStorage = previous; });
    return store;
}

function createPane(app, { getLaunchContext = () => ({ workingDirectory: '/scripts', initialPrompt: 'Read /scripts/nightly.py and wait.' }) } = {}) {
    const log = { connects: [], starts: [] };
    const pane = new ScriptAgentPane(app, { owner: OWNER, label: 'nightly.py', getLaunchContext });
    pane._createTab = fakeTabFactory(log, () => pane);
    return { pane, log };
}

test('An owner names one script\'s agent, and only a tab carrying one counts as owned', () => {
    assert.equal(OWNER, 'script-agent:reg-1');
    assert.equal(isOwnedTab({ tabId: 'a', owner: OWNER }), true);
    assert.equal(isOwnedTab({ tabId: 'b' }), false);
    assert.equal(isOwnedTab({ tabId: 'c', owner: '  ' }), false);
    assert.equal(isOwnedTab(null), false);
});

test('Start opens an owned tab in the script folder with the brief, and remembers the CLI', async (t) => {
    const store = withStorage(t);
    const app = fakeApp();
    const { pane, log } = createPane(app);
    const { host, elements, buttons } = fakeHost();
    await pane.mount(host);
    assert.match(host.html, /data-script-agent-terminal/);
    assert.doesNotMatch(host.html, /vb-terminal-panel|vb-terminal-tab/, 'no Terminals page strip or panel');
    assert.equal(elements['[data-script-agent-placeholder]'].hidden, false);
    assert.equal(elements['[data-script-agent-lifecycle]'].textContent, 'Not started');

    assert.equal(await pane.start(), true);

    const create = app.calls.find(call => call.method === 'POST');
    assert.equal(create.url, `/api/v1/terminal/tabs?owner=${encodeURIComponent(OWNER)}`);
    assert.deepEqual(log.starts, [{
        tabId: 'new-tab',
        body: {
            cli: 'claude',
            workingDirectory: '/scripts',
            title: 'Claude · nightly.py',
            initialPrompt: 'Read /scripts/nightly.py and wait.'
        }
    }]);
    assert.equal(app.registered.has('new-tab'), true, 'session events reach the pane');
    assert.equal(store.get(SCRIPT_AGENT_SELECTION_KEY), 'base:claude');
    assert.equal(elements['[data-script-agent-terminal]'].hidden, false);
    assert.equal(elements['[data-script-agent-placeholder]'].hidden, true);
    assert.ok(buttons.start.every(button => button.hidden), 'Start gives way to Restart and Stop');
    assert.equal(buttons.restart[0].hidden, false);
    assert.equal(buttons.stop[0].hidden, false);
    assert.equal(elements['[data-script-agent-select]'].disabled, true, 'the CLI is fixed while it runs');

    // A second Start while it runs does nothing; Stop keeps the host for the next Start.
    assert.equal(await pane.start(), false);
    assert.equal(await pane.stop(), true);
    assert.equal(buttons.start[0].hidden, false);
    assert.equal(app.calls.filter(call => call.method === 'DELETE').length, 0);
});

test('A plain shell start gets the folder but not the agent brief', async (t) => {
    withStorage(t);
    const app = fakeApp();
    const { pane, log } = createPane(app);
    const { host, elements } = fakeHost();
    await pane.mount(host);
    elements['[data-script-agent-select]'].value = 'base:shell';
    await pane.start();
    assert.equal(log.starts[0].body.cli, 'shell');
    assert.equal(log.starts[0].body.workingDirectory, '/scripts');
    assert.equal('initialPrompt' in log.starts[0].body, false);
});

test('Opening the workbench reattaches to this script\'s running agent and to nothing else', async (t) => {
    withStorage(t);
    const app = fakeApp({ tabs: [
        { tabId: 'plain', hasActiveSession: true },
        { tabId: 'other', owner: scriptAgentOwner('reg-2'), hasActiveSession: true },
        { tabId: 'mine-finished', owner: OWNER, hasActiveSession: false },
        { tabId: 'mine', owner: OWNER, hasActiveSession: true, cli: 'codex', sessionId: 's-1' }
    ] });
    const { pane, log } = createPane(app);
    await pane.mount(fakeHost().host);
    assert.equal(pane.tab.state.id, 'mine');
    assert.equal(pane.tab.state.cli, 'codex');
    assert.deepEqual(log.connects, ['mine']);
    assert.equal(app.calls.some(call => call.method === 'POST'), false, 'no new host for a running agent');

    // Leaving keeps a running agent for the next visit.
    const instance = pane.tab.instance;
    pane.dispose();
    assert.equal(instance.disposed, true);
    assert.equal(app.registered.size, 0);
    assert.equal(app.calls.some(call => call.method === 'DELETE'), false);
});

test('Leaving after the agent finished releases its host', async (t) => {
    withStorage(t);
    const app = fakeApp({ tabs: [{ tabId: 'mine-finished', owner: OWNER, hasActiveSession: false }] });
    const { pane, log } = createPane(app);
    await pane.mount(fakeHost().host);
    assert.equal(pane.tab.state.id, 'mine-finished');
    assert.deepEqual(log.connects, [], 'nothing to connect to');
    pane.dispose();
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(app.calls.filter(call => call.method === 'DELETE').map(call => call.url),
        ['/api/v1/terminal/tabs/mine-finished']);
});

test('A dropped socket asks the server once: an exited CLI reads Finished, a live one offers Connect', async (t) => {
    withStorage(t);
    const app = fakeApp({ tabs: [{ tabId: 'mine', owner: OWNER, hasActiveSession: true, cli: 'claude' }] });
    const { pane } = createPane(app);
    const { host, elements, buttons } = fakeHost();
    await pane.mount(host);
    await pane._statusCheck;
    app.calls.length = 0;

    // Still running (a takeover): Connect is offered, and the check is not repeated.
    pane.tab.instance.socketOpen = false;
    pane.tab.state.status = 'disconnected';
    pane.updateUi();
    await pane._statusCheck;
    assert.equal(app.calls.filter(call => call.url.endsWith('/status')).length, 1);
    assert.equal(buttons.connect[0].hidden, false);
    pane.updateUi();
    assert.equal(app.calls.filter(call => call.url.endsWith('/status')).length, 1, 'one check per drop');

    // Reconnected, then the CLI exits: the last screen stays and Start is offered again.
    await pane.reconnect();
    pane.tab.instance.socketOpen = false;
    pane.tab.state.status = 'disconnected';
    app.status = { hasActiveSession: false };
    pane.updateUi();
    await pane._statusCheck;
    assert.equal(pane.tab.state.hasActiveSession, false);
    assert.equal(pane.tab.state.status, 'finished');
    assert.equal(pane.tab.instance.terminal.options.disableStdin, true);
    assert.equal(elements['[data-script-agent-terminal]'].hidden, false, 'the final screen stays readable');
    assert.match(elements['[data-script-agent-lifecycle]'].textContent, /^Finished/);
    assert.equal(buttons.start[0].hidden, false);
    assert.equal(buttons.stop[0].hidden, true);
});

test('A host that disappeared is dropped, and Start opens a new one', async (t) => {
    withStorage(t);
    const app = fakeApp({ tabs: [{ tabId: 'mine', owner: OWNER, hasActiveSession: false }] });
    const { pane, log } = createPane(app);
    await pane.mount(fakeHost().host);
    app.status = new Error('Terminal tab not found');
    await pane.start();
    assert.equal(pane.tab.state.id, 'new-tab');
    assert.deepEqual(log.starts.map(start => start.tabId), ['new-tab']);
});

test('The Terminals page restores ordinary and Automation tabs but never a view\'s own tab', async () => {
    const added = [];
    const automation = new Map();
    const manager = {
        _destroyed: false,
        closedAutomationTabs: new Set(),
        automationTabs: automation,
        automationMenu: { refresh() {} },
        app: { apiCall: async () => ({ tabs: [
            { tabId: 'plain', hasActiveSession: true },
            { tabId: 'agent', hasActiveSession: true, owner: OWNER },
            { tabId: 'run', hasActiveSession: true, jobRunId: 'r-1', automationName: 'Nightly' }
        ], maxTabs: 100 }) },
        getTabSelectionFromStorage: () => null,
        getTabTitleFromStorage: () => null,
        getTabMetaFromStorage: () => null,
        addLocalTab(info) { added.push(info.tabId); }
    };
    await TerminalManager.prototype.restoreTabs.call(manager);
    assert.deepEqual(added, ['plain']);
    assert.deepEqual([...automation.keys()], ['run']);
});

test('Session events for a view\'s own tab reach that view instead of a Terminals toast', () => {
    const handlers = new Map();
    const toasts = [];
    const controller = new TerminalController({ showToast: (...args) => toasts.push(args) });
    controller.bindSessionEvents({ on: (name, fn) => handlers.set(name, fn) });
    const idle = [];
    const classes = new Set();
    const tab = {
        state: { sessionId: 's-1', ui: { item: { classList: {
            add: c => classes.add(c), remove: (...cs) => cs.forEach(c => classes.delete(c)),
            toggle: (c, on) => (on ? classes.add(c) : classes.delete(c))
        }, querySelector: () => null } } },
        instance: { statusController: { onSessionIdle: () => idle.push('idle') } }
    };
    controller.registerOwnedTab('agent-tab', tab);
    handlers.get('session_idle')({ tabId: 'agent-tab', cli: 'Claude' });
    handlers.get('session_idle')({ sessionId: 's-1', cli: 'Claude' });
    assert.deepEqual(idle, ['idle', 'idle']);
    assert.equal(toasts.length, 0);

    // Unregistering by a stale tab object leaves the current one in place.
    controller.unregisterOwnedTab('agent-tab', { other: true });
    assert.equal(controller.ownedTabs.has('agent-tab'), true);
    controller.unregisterOwnedTab('agent-tab', tab);
    handlers.get('session_idle')({ tabId: 'agent-tab', cli: 'Claude' });
    assert.equal(toasts.length, 1, 'an unknown tab still gets the ordinary idle toast');
});
