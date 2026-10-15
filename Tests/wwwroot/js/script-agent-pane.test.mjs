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
        disposals: 0,
        terminal: { options: {} },
        autoReconnect: { isPending: () => false, cancel() {} },
        setActive() {},
        hasOpenSocket() { return this.socketOpen; },
        // Like TerminalTab, an open socket repaints its manager's chrome.
        async connect() { log.connects.push(state.id); this.socketOpen = true; state.status = 'connected'; manager().updateUi(); return true; },
        async startSession(body) {
            log.starts.push({ tabId: state.id, body });
            // A test can hold /start open to leave the workbench while the launch lands.
            if (log.startGate) await log.startGate;
            state.hasActiveSession = true;
            state.status = 'connected';
            this.socketOpen = true;
            return { hasActiveSession: true };
        },
        async stopSession() { state.hasActiveSession = false; state.status = 'not-started'; this.socketOpen = false; return true; },
        dispose() { this.disposed = true; this.disposals += 1; this.socketOpen = false; },
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

// Review F3: leaving never closes the host. What the pane last saw may be stale, a launch may still
// be landing, and every window shares the owner's host, so a delete could stop a live agent.
test('Leaving never deletes the host, even after its agent finished', async (t) => {
    withStorage(t);
    const app = fakeApp({ tabs: [{ tabId: 'mine-finished', owner: OWNER, hasActiveSession: false }] });
    const { pane, log } = createPane(app);
    await pane.mount(fakeHost().host);
    assert.equal(pane.tab.state.id, 'mine-finished');
    assert.deepEqual(log.connects, [], 'nothing to connect to');
    pane.dispose();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(app.calls.some(call => call.method === 'DELETE'), false);
    assert.equal(app.registered.size, 0);
});

test('A host whose status is unavailable counts as live: Start never launches over it and leaving keeps it', async (t) => {
    withStorage(t);
    const app = fakeApp({ tabs: [{ tabId: 'mine', owner: OWNER, hasActiveSession: false, statusAvailable: false }] });
    const { pane, log } = createPane(app);
    const { host, buttons } = fakeHost();
    await pane.mount(host);
    assert.equal(pane.tab.state.hasActiveSession, true);
    assert.deepEqual(log.connects, ['mine']);
    assert.ok(buttons.start.every(button => button.hidden));
    assert.equal(await pane.start(), false);
    assert.equal(log.starts.length, 0);
    assert.equal(app.calls.some(call => call.method === 'POST'), false);

    // A status read that fails without "not found" keeps the tab and offers Connect.
    pane.tab.instance.socketOpen = false;
    pane.tab.state.status = 'disconnected';
    app.status = new Error('API call failed: Service Unavailable');
    pane.updateUi();
    await pane._statusCheck;
    assert.equal(pane.tab?.state.id, 'mine');
    assert.equal(buttons.connect[0].hidden, false);
    pane.dispose();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(app.calls.some(call => call.method === 'DELETE'), false);
});

test('Leaving while Start is waiting on /start keeps the launch and drops only the socket it opens', async (t) => {
    withStorage(t);
    const app = fakeApp();
    const { pane, log } = createPane(app);
    let release;
    log.startGate = new Promise(resolve => { release = resolve; });
    await pane.mount(fakeHost().host);
    const started = pane.start();
    while (log.starts.length === 0) await new Promise(resolve => setImmediate(resolve));
    const instance = pane.tab.instance;
    pane.dispose();
    release();
    assert.equal(await started, true);
    assert.equal(instance.socketOpen, false, 'no socket left behind by a pane that is gone');
    assert.equal(instance.disposals, 2);
    assert.equal(app.calls.some(call => call.method === 'DELETE'), false);
});

test('A create that answers after leaving leaves the host alone, even a running one', async (t) => {
    withStorage(t);
    const app = fakeApp();
    let answer;
    const listCall = app.apiCall;
    app.apiCall = async (url, method = 'GET', body = null) => {
        if (url.startsWith('/api/v1/terminal/tabs?owner=') && method === 'POST') {
            app.calls.push({ url, method, body });
            return new Promise(resolve => { answer = resolve; });
        }
        return listCall(url, method, body);
    };
    const { pane, log } = createPane(app);
    await pane.mount(fakeHost().host);
    const started = pane.start();
    while (!answer) await new Promise(resolve => setImmediate(resolve));
    pane.dispose();
    answer({ tabId: 'other-window', owner: OWNER, hasActiveSession: true });
    assert.equal(await started, false);
    assert.equal(pane.tab, null);
    assert.deepEqual(log.connects, []);
    assert.equal(log.starts.length, 0);
    assert.equal(app.calls.some(call => call.method === 'DELETE'), false);
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

// Review F1 (VIBE-159): a slow or failed look for the running agent must never start a second one.
test('Start waits for a slow discovery and attaches to the running agent instead of creating a host', async (t) => {
    withStorage(t);
    const app = fakeApp();
    let release;
    const pendingList = new Promise(resolve => { release = resolve; });
    const listCall = app.apiCall;
    app.apiCall = async (url, method = 'GET', body = null) => {
        if (url === '/api/v1/terminal/tabs' && method === 'GET') {
            app.calls.push({ url, method, body });
            return pendingList;
        }
        return listCall(url, method, body);
    };
    const { pane, log } = createPane(app);
    const { host, buttons, elements } = fakeHost();
    const mounted = pane.mount(host);
    assert.ok(buttons.start.every(button => button.disabled), 'Start waits for the lookup');
    assert.equal(elements['[data-script-agent-lifecycle]'].textContent, 'Looking for this script\'s agent…');
    assert.equal(elements['[data-script-agent-select]'].disabled, true);

    const started = pane.start();
    release({ tabs: [{ tabId: 'already-running', owner: OWNER, hasActiveSession: true, cli: 'claude' }] });
    assert.equal(await started, false);
    await mounted;
    assert.equal(pane.tab.state.id, 'already-running');
    assert.deepEqual(log.connects, ['already-running']);
    assert.equal(log.starts.length, 0);
    assert.equal(app.calls.some(call => call.method === 'POST'), false, 'no second host');
});

test('A failed discovery blocks Start until the list can be read', async (t) => {
    withStorage(t);
    const app = fakeApp();
    let listFails = true;
    const listCall = app.apiCall;
    app.apiCall = async (url, method = 'GET', body = null) => {
        if (url === '/api/v1/terminal/tabs' && method === 'GET' && listFails) {
            app.calls.push({ url, method, body });
            throw new Error('Host unreachable');
        }
        return listCall(url, method, body);
    };
    const { pane, log } = createPane(app);
    const { host, buttons } = fakeHost();
    await pane.mount(host);
    assert.equal(buttons.start[0].disabled, false, 'Start is offered once the lookup has finished');

    assert.equal(await pane.start(), false);
    assert.match(app.errors[0], /Could not check whether this script's agent is already running/);
    assert.equal(app.calls.some(call => call.method === 'POST'), false);

    listFails = false;
    assert.equal(await pane.start(), true);
    assert.deepEqual(log.starts.map(start => start.tabId), ['new-tab']);
});

test('When the server hands back this owner\'s running host, Start attaches instead of launching', async (t) => {
    withStorage(t);
    const app = fakeApp();
    const listCall = app.apiCall;
    app.apiCall = async (url, method = 'GET', body = null) => {
        if (url.startsWith('/api/v1/terminal/tabs?owner=') && method === 'POST') {
            app.calls.push({ url, method, body });
            return { tabId: 'other-window', owner: OWNER, hasActiveSession: true, cli: 'codex' };
        }
        return listCall(url, method, body);
    };
    const { pane, log } = createPane(app);
    await pane.mount(fakeHost().host);
    assert.equal(await pane.start(), false);
    assert.equal(pane.tab.state.id, 'other-window');
    assert.deepEqual(log.connects, ['other-window']);
    assert.equal(log.starts.length, 0, 'the running agent is not replaced');
});

// Review F4: two windows share an idle host; the other one started an agent in it meanwhile.
test('Start in a window whose shared host is now running attaches to that agent', async (t) => {
    withStorage(t);
    const app = fakeApp({ tabs: [{ tabId: 'shared', owner: OWNER, hasActiveSession: false }] });
    const { pane, log } = createPane(app);
    const { host, buttons } = fakeHost();
    await pane.mount(host);
    assert.equal(pane.tab.state.hasActiveSession, false);

    app.status = { hasActiveSession: true, sessionId: 'other-window-session', cli: 'codex' };
    assert.equal(await pane.start(), false);
    assert.equal(log.starts.length, 0, 'no /start into a host that already runs an agent');
    assert.deepEqual(log.connects, ['shared']);
    assert.equal(pane.tab.state.sessionId, 'other-window-session');
    assert.equal(pane.tab.state.cli, 'codex');
    assert.equal(buttons.stop[0].hidden, false, 'Stop and Restart are offered for the running agent');
    assert.ok(buttons.start.every(button => button.hidden));
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
