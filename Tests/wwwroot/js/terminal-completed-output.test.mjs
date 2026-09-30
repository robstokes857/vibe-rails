import test from 'node:test';
import assert from 'node:assert/strict';
import { TerminalTab } from '../../../VibeRails/wwwroot/js/modules/terminal-tab.js';
import { TerminalManager } from '../../../VibeRails/wwwroot/js/modules/terminal-multitab.js';

const snapshot = {
    sessionId: 'recording', cols: 80, rows: 24,
    xterm_ui_bytes: { base64: btoa('final output'), includes_scrollback: true }
};

function viewer(apiCall = async () => snapshot, writeAsync) {
    const calls = [];
    const terminals = [];
    const createTerminal = () => ({
        resize: (...args) => calls.push(['resize', ...args]),
        resetForSnapshotReplay: () => calls.push(['reset']),
        async writeAsync(bytes) {
            calls.push(['write', new TextDecoder().decode(bytes)]);
            await writeAsync?.(this);
        },
        dispose() { this.disposed = true; },
        fit: () => calls.push(['fit']), scrollToBottom: () => calls.push(['scroll'])
    });
    const tab = Object.assign(Object.create(TerminalTab.prototype), {
        state: { id: 'auto', sessionId: 'recording', hasActiveSession: false },
        isActive: true,
        manager: { app: { apiCall }, updateUi() {} },
        autoReconnect: { cancel: () => calls.push(['cancel']) },
        disconnect: () => calls.push(['disconnect']),
        ensureTerminal() {
            if (this.vibeTerminal) return;
            this.vibeTerminal = createTerminal();
            terminals.push(this.vibeTerminal);
            this.terminal = { options: {} };
        },
        setupResizeHandling: () => calls.push(['resize-handling'])
    });
    return { tab, calls, terminals };
}

test('switching away and back during a completed write renders a fresh viewer', async () => {
    const writes = [];
    let fetches = 0;
    const { tab, calls, terminals } = viewer(async () => { fetches++; return snapshot; },
        () => new Promise(resolve => writes.push(resolve)));
    const first = tab.showCompletedOutput();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(writes.length, 1);
    tab.isActive = false;
    tab.disposeTerminalInstance();
    tab.isActive = true;
    const second = tab.showCompletedOutput();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(writes.length, 2);
    assert.equal(terminals[0].disposed, true);

    // An old write finishing must not release the newer request's deduplication guard.
    writes[0]();
    assert.equal(await first, false);
    const refresh = tab.showCompletedOutput();
    assert.equal(fetches, 2);
    writes[1]();
    assert.equal(await second, true);
    assert.equal(await refresh, true);
    assert.equal(tab.vibeTerminal, terminals[1]);
    assert.equal(calls.filter(([name]) => name === 'fit').length, 1);
});

test('switching away and back during snapshot fetch ignores the old activation', async () => {
    const fetches = [];
    const { tab, calls } = viewer(() => new Promise(resolve => fetches.push(resolve)));
    const first = tab.showCompletedOutput();
    tab.isActive = false;
    tab.disposeTerminalInstance();
    tab.isActive = true;
    const second = tab.showCompletedOutput();
    assert.equal(fetches.length, 2);
    fetches[0](snapshot);
    assert.equal(await first, false);
    const refresh = tab.showCompletedOutput();
    assert.equal(fetches.length, 2);
    fetches[1](snapshot);
    assert.equal(await second, true);
    assert.equal(await refresh, true);
    assert.equal(calls.filter(([name]) => name === 'write').length, 1);
});

for (const change of ['disposed', 'navigated', 'restarted', 'session-replaced', 'manager-destroyed']) {
    test(`completed write cannot finalize after the viewer is ${change}`, async () => {
        let finish;
        const { tab, calls } = viewer(undefined, () => new Promise(resolve => { finish = resolve; }));
        const pending = tab.showCompletedOutput();
        await new Promise(resolve => setImmediate(resolve));
        if (change === 'disposed') tab.disposeTerminalInstance();
        if (change === 'navigated') tab.isActive = false;
        if (change === 'restarted') tab.state.hasActiveSession = true;
        if (change === 'session-replaced') tab.state.sessionId = 'new';
        if (change === 'manager-destroyed') tab.manager._destroyed = true;
        finish();
        assert.equal(await pending, false);
        assert.equal(calls.some(([name]) => name === 'fit'), false);
        assert.notEqual(tab.state.status, 'finished');
    });
}

test('finished output renders without a socket, disables typing and preserves scroll on repeat refresh', async () => {
    const { tab, calls } = viewer(async path => {
        assert.equal(path, '/api/v1/agent-tools/terminal/auto/snapshot');
        return snapshot;
    });
    assert.equal(await tab.showCompletedOutput(), true);
    assert.equal(tab.terminal.options.disableStdin, true);
    assert.equal(tab.state.status, 'finished');
    assert.deepEqual(calls.filter(([name]) => name === 'write'), [['write', 'final output']]);
    const count = calls.length;
    await tab.showCompletedOutput();
    assert.equal(calls.length, count);
});

for (const change of ['disposed', 'navigated', 'restarted', 'session-replaced']) {
    test(`late completed output is ignored after the viewer is ${change}`, async () => {
        let resolve;
        const { tab, calls } = viewer(() => new Promise(done => { resolve = done; }));
        const pending = tab.showCompletedOutput();
        if (change === 'disposed') tab._disposed = true;
        if (change === 'navigated') tab.isActive = false;
        if (change === 'restarted') tab.state.hasActiveSession = true;
        if (change === 'session-replaced') tab.state.sessionId = 'new';
        resolve(snapshot);
        assert.equal(await pending, false);
        assert.equal(calls.some(([name]) => name === 'write'), false);
    });
}

test('snapshot failure can be retried and a mismatched recording never replaces output', async () => {
    const { tab, calls } = viewer(async () => { throw new Error('offline'); });
    await assert.rejects(tab.showCompletedOutput(), /offline/);
    tab.manager.app.apiCall = async () => ({ ...snapshot, sessionId: 'other' });
    await assert.rejects(tab.showCompletedOutput(), /not available/);
    assert.equal(calls.some(([name]) => name === 'write'), false);
    tab.manager.app.apiCall = async () => snapshot;
    assert.equal(await tab.showCompletedOutput(), true);
});

test('opening a finished Automation activates its terminal instead of launching a Replay modal', async () => {
    const info = { tabId: 'auto', jobRunId: 'run', sessionId: 'recording', hasActiveSession: false };
    const calls = [];
    const manager = Object.assign(Object.create(TerminalManager.prototype), {
        automationTabs: new Map([['auto', info]]), tabs: new Map(),
        refreshAutomationTabs: async () => {},
        addLocalTab: value => calls.push(['add', value]),
        activateTab: async id => calls.push(['activate', id]),
        focusActiveTerminalInput: () => assert.fail('Finished output does not focus input')
    });
    assert.equal(await manager.openAutomationTab('auto'), true);
    assert.deepEqual(calls, [['add', info], ['activate', 'auto']]);
});

test('completion refresh retains an open viewer and loads its finished output', async () => {
    const info = { tabId: 'auto', jobRunId: 'run', sessionId: 'recording', hasActiveSession: false };
    let loaded = 0;
    const local = {
        state: { hasActiveSession: true, ui: { item: { classList: { add() {} } } } },
        instance: { autoReconnect: { cancel() {} }, showCompletedOutput: async () => { loaded++; } }
    };
    const manager = Object.assign(Object.create(TerminalManager.prototype), {
        app: { apiCall: async () => ({ tabs: [info] }), showError: assert.fail },
        tabs: new Map([['auto', local]]), automationTabs: new Map([['auto', info]]),
        closedAutomationTabs: new Set(), activeTabId: 'auto',
        automationMenu: { refresh() {} }, updateUi() {},
        removeAutomationTab: () => assert.fail('Completion must retain the viewer')
    });
    await manager.refreshAutomationTabs();
    assert.equal(manager.tabs.get('auto'), local);
    assert.equal(local.state.hasActiveSession, false);
    assert.equal(loaded, 1);
});
