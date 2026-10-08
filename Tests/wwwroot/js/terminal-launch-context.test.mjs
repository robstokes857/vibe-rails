import test from 'node:test';
import assert from 'node:assert/strict';
import { TerminalManager } from '../../../VibeRails/wwwroot/js/modules/terminal-multitab.js';

function createLauncher({ cli = 'codex', environmentName = null, customPrompt, getLaunchContext } = {}) {
    const requests = [];
    let reconnects = 0;
    let active;
    const manager = Object.assign(Object.create(TerminalManager.prototype), {
        app: { data: {
            configs: { rootPath: '/project' },
            environments: environmentName ? [{ cli, name: environmentName, customPrompt }] : []
        } },
        options: { getLaunchContext },
        getActiveTab: () => active,
        getSelectionMeta: () => ({ cli, environmentName, displayName: environmentName || cli }),
        applySelection() {},
        async createAndActivateTab() {
            active = {
                state: { id: `tab-${requests.length}`, hasActiveSession: false },
                instance: {
                    async startSession(body) { requests.push(body); return { workingDirectory: body.workingDirectory }; },
                    hasOpenSocket: () => false,
                    async connect() { reconnects++; return true; }
                }
            };
            return active;
        },
        updateTabMetadata(tab, data) { Object.assign(tab.state, data); },
        _touchHistory() {},
        updateUi() {}
    });
    return { manager, requests, reconnectCount: () => reconnects };
}

test('Start sends host context to each agent using its selected environment and file directory', async () => {
    for (const cli of ['claude', 'codex', 'agy', 'copilot', 'opencode', 'grok']) {
        const { manager, requests } = createLauncher({
            cli,
            environmentName: 'Script helper',
            customPrompt: 'Explain your changes.',
            getLaunchContext: () => ({
                workingDirectory: 'C:\\My Scripts',
                initialPrompt: 'Read C:\\My Scripts\\deploy.ps1 and wait for my request.'
            })
        });
        await manager.startFromSelection('selected-env');
        assert.deepEqual(requests, [{
            cli,
            environmentName: 'Script helper',
            workingDirectory: 'C:\\My Scripts',
            title: 'Script helper Terminal',
            initialPrompt: 'Explain your changes.\n\nRead C:\\My Scripts\\deploy.ps1 and wait for my request.'
        }]);
    }
});

test('every fresh Start reads current context while reconnect leaves the running session alone', async () => {
    let context = { workingDirectory: '/first', initialPrompt: 'Read /first/one.py' };
    let contextReads = 0;
    const { manager, requests, reconnectCount } = createLauncher({
        getLaunchContext: () => { contextReads++; return context; }
    });
    await manager.startFromSelection('base:codex');
    const firstTab = manager.getActiveTab();
    context = { workingDirectory: '/second', initialPrompt: 'Read /second/two.sh' };
    await manager.reconnectActiveTab();
    assert.equal(reconnectCount(), 1);
    assert.equal(contextReads, 1);
    assert.equal(requests.length, 1);
    await manager.startFromSelection('base:codex');
    assert.notEqual(manager.getActiveTab(), firstTab, 'Start opens a new session rather than injecting into a running one');
    assert.equal(contextReads, 2);
    assert.deepEqual(requests.map(({ workingDirectory, initialPrompt }) => ({ workingDirectory, initialPrompt })), [
        { workingDirectory: '/first', initialPrompt: 'Read /first/one.py' },
        context
    ]);
});

test('plain shell launches get the file directory without the agent brief', async () => {
    for (const cli of ['shell', 'Shell']) {
        const { manager, requests } = createLauncher({
            cli,
            getLaunchContext: () => ({ workingDirectory: '/scripts', initialPrompt: 'Read /scripts/test.py' })
        });
        await manager.startFromSelection('base:shell');
        assert.equal(requests[0].workingDirectory, '/scripts');
        assert.equal(Object.hasOwn(requests[0], 'initialPrompt'), false);
    }
});

test('other terminal views keep the normal launch defaults', async () => {
    const { manager, requests } = createLauncher();
    await manager.startFromSelection('base:codex');
    assert.deepEqual(requests, [{ cli: 'codex', workingDirectory: '/project', title: 'codex Terminal' }]);
});

test('a canceled environment prompt or detached manager cannot start a contextual session', async () => {
    for (const detached of [false, true]) {
        const { manager, requests } = createLauncher({
            environmentName: 'Script helper',
            customPrompt: detached ? 'Explain your changes.' : 'Help with {{task}}',
            getLaunchContext: () => assert.fail('context must not be read after cancellation or navigation')
        });
        manager._destroyed = detached;
        // No modal host resolves the template as canceled, as a dismissed dialog does.
        await manager.startFromSelection('selected-env');
        assert.equal(requests.length, 0);
    }
});
