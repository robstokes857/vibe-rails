import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/terminal-multitab.js');
const { TerminalController, shouldCreateFreshTab } = await import(pathToFileURL(modulePath).href);

function createManager({ selection = 'env:7:opencode', rememberedCli = 'opencode' } = {}) {
    const added = [];
    const focused = [];
    const manager = {
        tabs: new Map(),
        container: { isConnected: true },
        isDestroyed: () => false,
        getTabMetaFromStorage: () => ({
            label: 'Report',
            taskKey: 'python-script-run:report',
            customLabel: true,
            workingDirectory: 'C:/source/project'
        }),
        getTabSelectionFromStorage: () => selection,
        getSelectionMeta: () => ({ cli: rememberedCli }),
        getTabTitleFromStorage: () => 'Report title',
        addLocalTab(tabInfo, options) {
            added.push({ tabInfo, options });
            const tab = { state: { id: tabInfo.tabId } };
            this.tabs.set(tabInfo.tabId, tab);
            return tab;
        },
        async focusTab(tabId, options) {
            focused.push({ tabId, options });
            return true;
        }
    };
    return { manager, added, focused };
}

test('launchInFocus carries one-shot launch options into the dedicated terminal view', () => {
    const navigations = [];
    const controller = new TerminalController({
        navigate(view, data) {
            navigations.push({ view, data });
            return true;
        }
    });
    const options = {
        cli: 'claude',
        initialPrompt: 'Fix the rules',
        forceNewTab: true
    };

    assert.equal(controller.launchInFocus(options, { source: 'project-health' }), true);
    assert.deepEqual(navigations, [{
        view: 'terminal-focus',
        data: {
            source: 'project-health',
            launchOptions: options
        }
    }]);
    assert.notEqual(navigations[0].data.launchOptions, options);
});

test('forced launches reuse an active blank tab when the tab limit prevents a fresh one', () => {
    const blankTab = { state: { hasActiveSession: false } };
    const runningTab = { state: { hasActiveSession: true } };

    assert.equal(shouldCreateFreshTab({ forceNewTab: true }, blankTab, 7, 8), true);
    assert.equal(shouldCreateFreshTab({ forceNewTab: true }, blankTab, 8, 8), false);
    assert.equal(shouldCreateFreshTab({ forceNewTab: true }, runningTab, 8, 8), true);
    assert.equal(shouldCreateFreshTab({}, blankTab, 7, 8), false);
});

test('adoptLaunchedTab restores authoritative CLI and session identity', async () => {
    const { manager, added, focused } = createManager();
    const calls = [];
    const controller = new TerminalController({
        async apiCall(url, method, body, options) {
            calls.push({ url, method, body, options });
            return {
                tabId: 'tab-1',
                hasActiveSession: true,
                sessionId: 'session-1',
                cli: 'OpenCode',
                workingDirectory: 'C:/source/project/.workspace/run-1'
            };
        }
    });
    controller.manager = manager;

    assert.equal(await controller.adoptLaunchedTab('tab-1'), true);
    assert.deepEqual(calls, [{
        url: '/api/v1/terminal/tabs/tab-1/status',
        method: 'GET',
        body: null,
        options: { showLoading: false }
    }]);
    assert.deepEqual(added[0].tabInfo, {
        tabId: 'tab-1',
        hasActiveSession: true,
        sessionId: 'session-1',
        cli: 'OpenCode',
        workingDirectory: 'C:/source/project/.workspace/run-1'
    });
    assert.equal(added[0].options.selection, 'env:7:opencode');
    assert.equal(added[0].options.workingDirectory, 'C:/source/project/.workspace/run-1');
    assert.deepEqual(focused, [{ tabId: 'tab-1', options: { connectIfNeeded: true } }]);
});

test('adoptLaunchedTab falls back to the remembered selection when status is unavailable', async () => {
    const { manager, added } = createManager({ selection: 'base:codex', rememberedCli: 'codex' });
    const controller = new TerminalController({
        async apiCall() { throw new Error('status unavailable'); }
    });
    controller.manager = manager;

    assert.equal(await controller.adoptLaunchedTab('tab-2'), true);
    assert.equal(added[0].tabInfo.cli, 'codex');
    assert.equal(added[0].tabInfo.sessionId, null);
    assert.equal(added[0].tabInfo.hasActiveSession, true);
});
