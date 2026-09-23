import test from 'node:test';
import assert from 'node:assert/strict';
import { TerminalManager, TerminalController, shouldCreateFreshTab } from '../../../VibeRails/wwwroot/js/modules/terminal-multitab.js';
import { automationEntries, automationStatus } from '../../../VibeRails/wwwroot/js/modules/terminal-automation-menu.js';

function managerWithSnapshot(snapshot) {
    const added = [];
    const manager = Object.assign(Object.create(TerminalManager.prototype), {
        _destroyed: false,
        automationTabs: new Map(),
        automationMenu: { refresh() {} },
        tabs: new Map(),
        tabOrder: [],
        app: { apiCall: async () => snapshot },
        getTabSelectionFromStorage: () => null,
        getTabTitleFromStorage: () => null,
        getTabMetaFromStorage: () => null,
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

test('running entries precede finished runs and UUID run ids sort by creation time', () => {
    const older = { tabId: 'older', jobRunId: 'ffff', hasActiveSession: true, createdUTC: '2026-09-23T10:00:00Z' };
    const newer = { tabId: 'newer', jobRunId: 'aaaa', hasActiveSession: true, createdUTC: '2026-09-23T11:00:00Z' };
    const finished = { tabId: 'done', jobRunId: 'bbbb', sessionId: 's1', createdUTC: '2026-09-23T12:00:00Z' };
    const rows = automationEntries(new Map([finished, older, newer].map(tab => [tab.tabId, tab])));
    assert.deepEqual(rows.map(row => row.tabId), ['newer', 'older', 'done']);
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
