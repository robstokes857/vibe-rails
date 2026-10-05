import test from 'node:test';
import assert from 'node:assert/strict';
import { TerminalManager, TerminalController, shouldCreateFreshTab } from '../../../VibeRails/wwwroot/js/modules/terminal-multitab.js';
import { automationEntries, automationStatus } from '../../../VibeRails/wwwroot/js/modules/terminal-automation-menu.js';

function managerWithSnapshot(snapshot) {
    const added = [];
    const manager = Object.assign(Object.create(TerminalManager.prototype), {
        _destroyed: false,
        automationTabs: new Map(),
        closedAutomationTabs: new Set(),
        automationMenu: { refresh() {} },
        tabs: new Map(),
        tabOrder: [],
        app: { apiCall: async () => snapshot },
        getTabSelectionFromStorage: () => null,
        getTabTitleFromStorage: () => null,
        getTabMetaFromStorage: () => null,
        clearTabSelection() {},
        clearTabTitle() {},
        clearTabMeta() {},
        addLocalTab: info => added.push(info),
        updateUi() {}
    });
    return { manager, added };
}

test('restore keeps dozens of Automation runs as metadata without creating terminal viewers', async () => {
    const automations = Array.from({ length: 99 }, (_, index) => ({
        tabId: `auto-${index}`, jobRunId: `run-${index}`, automationName: `Check ${index}`,
        hasActiveSession: index < 10, sessionId: `session-${index}`
    }));
    const normal = { tabId: 'human', hasActiveSession: true };
    const { manager, added } = managerWithSnapshot({ maxTabs: 100, tabs: [normal, ...automations] });
    await manager.restoreTabs();
    assert.deepEqual(added, [normal]);
    assert.equal(manager.automationTabs.size, 99);
    manager.tabs.set(normal.tabId, {});
    assert.equal(manager.getTabCount(), 100);
    manager.tabs.set('auto-0', {}); // A selected run belongs to both registries.
    assert.equal(manager.getTabCount(), 100);
});

test('finished runs leave the group while running entries sort by creation time', () => {
    const older = { tabId: 'older', jobRunId: 'ffff', hasActiveSession: true, createdUTC: '2026-09-23T10:00:00Z' };
    const newer = { tabId: 'newer', jobRunId: 'aaaa', hasActiveSession: true, createdUTC: '2026-09-23T11:00:00Z' };
    const finished = { tabId: 'done', jobRunId: 'bbbb', hasActiveSession: false, sessionId: 's1', createdUTC: '2026-09-23T12:00:00Z' };
    const tabs = new Map([finished, older, newer].map(tab => [tab.tabId, tab]));
    const rows = automationEntries(tabs);
    assert.deepEqual(rows.map(row => row.tabId), ['newer', 'older']);
    assert.equal(tabs.get('done'), finished, 'retained output is still accessible');
    assert.equal(automationStatus(finished), 'Finished');
    assert.equal(automationStatus({ ...finished, statusAvailable: false }), 'Unavailable');
    assert.equal(automationStatus({}), 'Starting');
});

test('a finished Automation tab is never a reusable blank launch tab at the cap', () => {
    assert.equal(shouldCreateFreshTab({ forceNewTab: true }, { state: { jobRunId: 'run', hasActiveSession: false } }, 100, 100), true);
    assert.equal(shouldCreateFreshTab({ forceNewTab: true }, { state: { hasActiveSession: false } }, 100, 100), false);
});

test('a full browser permits server-side reclamation but never guesses an unavailable run finished', () => {
    const { manager } = managerWithSnapshot({});
    manager.maxTabs = 100;
    for (let i = 0; i < 99; i++) manager.tabs.set(`normal-${i}`, {});
    const run = { tabId: 'auto', jobRunId: 'run', hasActiveSession: false, sessionId: 'recording' };
    manager.automationTabs.set(run.tabId, run);
    assert.equal(manager.canCreateTab(), true);
    run.statusAvailable = false;
    assert.equal(manager.canCreateTab(), false);
    run.statusAvailable = true;
    run.hasActiveSession = true;
    assert.equal(manager.canCreateTab(), false);
});

test('unopened and opened Automation terminals are excluded from background reconnect', () => {
    const normal = { state: { hasActiveSession: true }, instance: { hasOpenSocket: () => false } };
    const automation = { state: { hasActiveSession: true, jobRunId: 'run' }, instance: { hasOpenSocket: () => false } };
    const { manager } = managerWithSnapshot({});
    manager.tabs = new Map([['human', normal], ['auto', automation]]);
    manager.tabOrder = ['human', 'auto'];
    assert.deepEqual(manager._collectBackgroundReconnectCandidates(), [normal]);
});

test('refresh removes reclaimed runs but preserves list and viewers on request failure', async () => {
    const { manager } = managerWithSnapshot({ tabs: [{ tabId: 'new', jobRunId: 'new-run' }] });
    manager.automationTabs.set('old', { tabId: 'old', jobRunId: 'old-run' });
    const removed = [];
    manager.removeAutomationTab = id => removed.push(id);
    await manager.refreshAutomationTabs();
    assert.deepEqual(removed, ['old']);
    assert.deepEqual([...manager.automationTabs.keys()], ['new']);
    manager.app.apiCall = async () => { throw new Error('offline'); };
    await manager.refreshAutomationTabs();
    assert.deepEqual([...manager.automationTabs.keys()], ['new']);
});

test('stale refresh cannot populate a manager destroyed during navigation', async () => {
    let resolve;
    const { manager } = managerWithSnapshot({});
    manager.app.apiCall = () => new Promise(done => { resolve = done; });
    const pending = manager.refreshAutomationTabs();
    manager._destroyed = true;
    resolve({ tabs: [{ tabId: 'late', jobRunId: 'late-run' }] });
    await pending;
    assert.equal(manager.automationTabs.size, 0);
});

test('a completed workflow leaves the robot menu even while its wrapper shell remains active', () => {
    const finished = { tabId: 'finished', jobRunId: 'run', hasActiveSession: true, sessionId: 'outer', automationCompleted: true };
    const running = { tabId: 'running', hasActiveSession: true, automationCompleted: false };
    assert.equal(automationStatus(finished), 'Finished');
    assert.deepEqual(automationEntries(new Map([['finished', finished], ['running', running]])), [running]);
});

test('starting and unavailable agents stay visible until completion is confirmed', () => {
    const starting = { tabId: 'starting', hasActiveSession: false, sessionId: null };
    const unavailable = { tabId: 'unavailable', hasActiveSession: false, sessionId: 'session', statusAvailable: false };
    const unknown = { tabId: 'unknown', sessionId: 'session' };
    const tabs = new Map([starting, unavailable, unknown].map(tab => [tab.tabId, tab]));
    assert.equal(automationEntries(tabs).length, 3);
    unavailable.statusAvailable = true;
    unknown.hasActiveSession = false;
    assert.deepEqual(automationEntries(tabs).map(tab => tab.tabId), ['starting']);
});

test('the completion event removes an open Automation viewer and late refresh cannot resurrect it', async () => {
    const handlers = new Map();
    const { manager } = managerWithSnapshot({});
    manager.automationTabs.set('auto', { tabId: 'auto', jobRunId: 'run' });
    let disposed = 0;
    let removed = 0;
    manager.tabs.set('auto', {
        instance: { dispose() { disposed++; } },
        state: { ui: { item: { remove() { removed++; } }, panel: { remove() { removed++; } } } }
    });
    manager.tabOrder = ['auto'];
    for (const method of ['clearTabSelection', 'clearTabTitle', 'clearTabMeta']) manager[method] = () => {};
    const controller = Object.assign(Object.create(TerminalController.prototype), { manager });
    controller.bindSessionEvents({ on: (name, handler) => handlers.set(name, handler) });
    let resolve;
    manager.app.apiCall = () => new Promise(done => { resolve = done; });
    const pending = manager.refreshAutomationTabs();
    handlers.get('automation_terminal_closed')({ tabId: 'auto' });
    assert.equal(disposed, 1);
    assert.equal(removed, 2);
    assert.equal(manager.tabs.has('auto'), false);
    resolve({ tabs: [{ tabId: 'auto', jobRunId: 'run' }] });
    await pending;
    assert.equal(manager.automationTabs.size, 0);
});

test('completion during initial restore cannot restore the closed Automation', async () => {
    const { manager, added } = managerWithSnapshot({});
    let resolve;
    manager.app.apiCall = () => new Promise(done => { resolve = done; });
    const pending = manager.restoreTabs();
    manager.removeAutomationTab('auto');
    resolve({ tabs: [{ tabId: 'auto', jobRunId: 'run' }, { tabId: 'human' }] });
    await pending;
    assert.equal(manager.automationTabs.size, 0);
    assert.deepEqual(added, [{ tabId: 'human' }]);
});

for (const failedRead of [false, true]) {
    test(`completion during adoption cannot reopen an Automation after ${failedRead ? 'a failed' : 'a stale'} status read`, async () => {
        const { manager, added } = managerWithSnapshot({});
        manager.container = { isConnected: true };
        let resolve, reject;
        manager.app.apiCall = () => new Promise((done, fail) => { resolve = done; reject = fail; });
        const controller = new TerminalController(manager.app);
        controller.manager = manager;
        const pending = controller.adoptLaunchedTab('auto', { focus: false });
        manager.removeAutomationTab('auto');
        if (failedRead) reject(new Error('host closed'));
        else resolve({ tabs: [{ tabId: 'auto', jobRunId: 'run', hasActiveSession: true }] });
        assert.equal(await pending, false);
        assert.equal(manager.automationTabs.size, 0);
        assert.equal(added.length, 0);
        // A later launch notification must also respect the completed host.
        assert.equal(await controller.adoptLaunchedTab('auto', { focus: false }), false);
    });
}

test('agent closure removes only the calling tab, cancels undo, and leaves a clear replay notice', () => {
    const handlers = new Map();
    const { manager } = managerWithSnapshot({});
    let disposed = 0;
    let removed = 0;
    const notices = [];
    const history = [];
    manager.app.showToast = (...args) => notices.push(args);
    manager._touchHistory = id => history.push(id);
    manager.tabs.set('caller', {
        instance: { dispose() { disposed++; } },
        state: { sessionId: 'recording', pinned: true, ui: {
            item: { remove() { removed++; } }, panel: { remove() { removed++; } }
        } }
    });
    const ordinary = { state: { sessionId: 'unrelated' } };
    manager.tabs.set('ordinary', ordinary);
    manager.tabOrder = ['caller', 'ordinary'];
    manager.activeTabId = 'caller';
    manager._nextVisibleTabId = () => 'ordinary';
    manager.activateTab = id => { manager.activeTabId = id; };
    const timeoutId = setTimeout(() => assert.fail('a closed host must not be deleted again by undo'), 1000);
    manager._pendingCloses = new Map([['caller', { timeoutId }]]);
    let undoRefresh = 0;
    manager._refreshUndoControl = () => undoRefresh++;
    const controller = Object.assign(Object.create(TerminalController.prototype), { manager, app: manager.app });
    controller.bindSessionEvents({ on: (name, handler) => handlers.set(name, handler) });
    handlers.get('agent_terminal_closed')({ tabId: 'caller', sessionId: 'recording' });
    handlers.get('agent_terminal_closed')({ tabId: 'caller', sessionId: 'recording' });
    assert.equal(disposed, 1);
    assert.equal(removed, 2);
    assert.equal(manager.tabs.has('caller'), false);
    assert.equal(manager.tabs.get('ordinary'), ordinary);
    assert.equal(manager.activeTabId, 'ordinary');
    assert.deepEqual(manager.tabOrder, ['ordinary']);
    assert.equal(manager._pendingCloses.size, 0);
    assert.equal(undoRefresh, 1);
    assert.deepEqual(history, ['recording']);
    assert.equal(notices.length, 1);
    assert.match(notices[0][1], /Terminal closed because the agent called end_agent_session/);
    assert.match(notices[0][1], /Replay.*Board/);
    assert.equal(notices[0][3].requireDismiss, true);
});

test('an agent closure for an older session leaves a replacement tab intact', () => {
    const { manager } = managerWithSnapshot({});
    const replacement = { state: { sessionId: 'new-session' } };
    manager.tabs.set('same-tab', replacement);
    assert.equal(manager.removeAgentTab('same-tab', 'old-session'), false);
    assert.equal(manager.tabs.get('same-tab'), replacement);
    assert.equal(manager.closedAutomationTabs.has('same-tab'), false);
});

test('agent closure during restore cannot resurrect an ordinary tab or alter another one', async () => {
    const { manager, added } = managerWithSnapshot({});
    let resolve;
    manager.app.apiCall = () => new Promise(done => { resolve = done; });
    const pending = manager.restoreTabs();
    manager.removeAgentTab('caller', 'recording');
    resolve({ tabs: [{ tabId: 'caller', sessionId: 'recording' }, { tabId: 'ordinary' }] });
    await pending;
    assert.deepEqual(added, [{ tabId: 'ordinary' }]);
    assert.equal(TerminalManager.prototype.addLocalTab.call(manager, { tabId: 'caller', sessionId: 'recording' }), null);
});

test('agent closure during adoption rejects its late list response', async () => {
    const { manager, added } = managerWithSnapshot({});
    manager.container = { isConnected: true };
    let resolve;
    manager.app.apiCall = () => new Promise(done => { resolve = done; });
    const controller = new TerminalController(manager.app);
    controller.manager = manager;
    const pending = controller.adoptLaunchedTab('caller', { focus: false });
    manager.removeAgentTab('caller', 'recording');
    resolve({ tabs: [{ tabId: 'caller', sessionId: 'recording', hasActiveSession: true }] });
    assert.equal(await pending, false);
    assert.equal(added.length, 0);
});

test('adding a viewer for a closed Automation never creates a terminal', () => {
    const { manager } = managerWithSnapshot({});
    manager.removeAutomationTab('auto');
    assert.equal(TerminalManager.prototype.addLocalTab.call(manager, { tabId: 'auto', jobRunId: 'run' }), null);
    assert.equal(manager.tabs.size, 0);
});

test('failed Automation close preserves its entry and reports the error', async () => {
    const { manager } = managerWithSnapshot({});
    manager.automationTabs.set('auto', { tabId: 'auto', jobRunId: 'run' });
    manager.app.apiCall = async () => { throw new Error('offline'); };
    const errors = [];
    manager.app.showError = error => errors.push(error);
    await manager.closeAutomationTab('auto');
    assert.equal(manager.automationTabs.size, 1);
    assert.match(errors[0], /offline/);
});

test('adopting a backend Automation without focus registers metadata only', async () => {
    const info = { tabId: 'auto', jobRunId: 'run', automationName: 'Check', hasActiveSession: true };
    const { manager, added } = managerWithSnapshot({ tabs: [info] });
    manager.container = { isConnected: true };
    manager.getSelectionMeta = () => ({ cli: 'shell' });
    const controller = new TerminalController(manager.app);
    controller.manager = manager;
    assert.equal(await controller.adoptLaunchedTab('auto', { focus: false }), true);
    assert.deepEqual(manager.automationTabs.get('auto'), info);
    assert.equal(added.length, 0);
});
