import test from 'node:test';
import assert from 'node:assert/strict';
import { AiAgentsController } from '../../../VibeRails/wwwroot/js/modules/ai-agents-controller.js';
import { LaunchApi } from '../../../VibeRails/wwwroot/js/modules/launch-api.js';
import { TerminalManager } from '../../../VibeRails/wwwroot/js/modules/terminal-multitab.js';
import { LlmPickerController } from '../../../VibeRails/wwwroot/js/modules/llm-picker-controller.js';

function fixture({ prompt = 'Review the changes.', status = 'running', tabStatuses = [] } = {}) {
    const requests = [], adopted = [], toasts = [], remembered = [], statusReads = [];
    const app = {
        data: {}, showToast: (...args) => toasts.push(args),
        async apiCall(path, method, body) {
            if (path === '/api/v1/presets/agent') return { name: 'Reviewer', cli: 1, initialPrompt: prompt, revision: 7 };
            if (path === '/api/v1/terminal/tabs/tab/status') {
                statusReads.push(adopted.length);
                // Scripted host replies first, then the run's session is live.
                return tabStatuses.length ? tabStatuses.shift() : { hasActiveSession: true, sessionId: 'session' };
            }
            if (path === '/api/v1/runs/run') return { id: 'run', status: 'running', tabId: 'tab', sessionId: 'session' };
            assert.equal(path, '/api/v1/runs');
            assert.equal(method, 'POST');
            requests.push(body);
            return { id: 'run', status, ...(status === 'running' ? { tabId: 'tab', sessionId: 'session' } : {}) };
        },
        terminalController: {
            rememberTabLaunch(id, metadata) { remembered.push({ id, ...metadata }); },
            async adoptLaunchedTab(id) { adopted.push(id); return true; }
        }
    };
    const controller = Object.assign(Object.create(AiAgentsController.prototype), {
        app, api: new LaunchApi(app), launching: new Set(),
        openEditor() { assert.fail('launch must not open the definition editor'); }
    });
    app.aiAgentsController = controller;
    const manager = Object.assign(Object.create(TerminalManager.prototype), {
        app, getActiveTab: () => ({ state: { hasActiveSession: true } }),
        getSelectionMeta: () => ({ cli: 'codex' })
    });
    return { controller, manager, requests, adopted, toasts, remembered, statusReads };
}

test('a picker launch waits until its tab host reports the run session before adopting the tab', async () => {
    // The run row gets its session before the CLI is spawned and published (BeforeSpawnAsync).
    // Adopting then added a tab with no session: it showed "Select a LLM to continue" and
    // never connected. Each early reply below must hold the adoption back.
    const f = fixture({ tabStatuses: [
        { hasActiveSession: false, sessionId: null },
        { hasActiveSession: true, sessionId: 'an-earlier-session' }
    ] });
    await f.manager.startFromSelection('preset:agent:codex');
    assert.deepEqual(f.statusReads, [0, 0, 0], 'adopted only after the third status read');
    assert.deepEqual(f.adopted, ['tab']);
    assert.equal(f.remembered.length, 1);
    assert.equal(f.toasts.some(t => t[2] === 'warning'), false);
});

test('terminal picker opens Agents and Quick Launches as normal tabs with saved prompt and revision', async () => {
    for (const prompt of ['Review the changes.', '']) {
        const f = fixture({ prompt });
        await f.manager.startFromSelection('preset:agent:codex');
        assert.equal(f.requests.length, 1);
        assert.equal(f.requests[0].terminalTab, true);
        assert.equal(f.requests[0].nativeTerminal, false);
        assert.equal(f.requests[0].initialPrompt, prompt);
        assert.equal(f.requests[0].expectedRevision, 7);
        assert.deepEqual(f.adopted, ['tab']);
        assert.deepEqual(f.remembered, [{ id: 'tab', selection: 'preset:agent:codex', label: 'Reviewer', title: 'Reviewer' }]);
    }
});

test('catalog and legacy focus requests continue to open native windows', async () => {
    for (const options of [{}, { focusTerminal: true, nativeTerminal: false }]) {
        const f = fixture();
        await f.controller.launch('agent', 'preset', options);
        assert.equal(f.requests[0].nativeTerminal, true);
        assert.equal(f.requests[0].terminalTab, false);
        assert.deepEqual(f.adopted, []);
    }
});

test('Manage quick launches uses the same ordinary terminal destination', async () => {
    const f = fixture();
    let closed = false;
    const picker = Object.assign(Object.create(LlmPickerController.prototype), {
        app: f.controller.app,
        modalState: { items: [{ key: 'preset:agent:codex' }] },
        _closeCustomizationModal() { closed = true; }
    });
    await picker._launchModalItem('preset:agent:codex');
    assert.equal(closed, true);
    assert.equal(f.requests[0].terminalTab, true);
    assert.equal(f.requests[0].nativeTerminal, false);
    assert.equal(f.remembered[0].selection, 'preset:agent:codex');
    assert.deepEqual(f.adopted, ['tab']);
});

test('failed tab launches stay on the terminal surface and point to run history', async () => {
    const f = fixture({ status: 'failed' });
    await f.manager.startFromSelection('preset:agent:codex');
    assert.deepEqual(f.adopted, []);
    assert.match(f.toasts.at(-1)[1], /Check Run history/);
    assert.equal(f.toasts.at(-1)[2], 'warning');
});

test('leaving the terminal before prompt resolution does not enqueue a run', async () => {
    const f = fixture();
    const abort = new AbortController();
    abort.abort();
    await f.controller.launch('agent', 'preset', { terminalTab: true, signal: abort.signal });
    assert.deepEqual(f.requests, []);
    assert.deepEqual(f.adopted, []);
});
