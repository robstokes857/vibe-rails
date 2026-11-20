import test from 'node:test';
import assert from 'node:assert/strict';
import { AiAgentsController } from '../../../VibeRails/wwwroot/js/modules/ai-agents-controller.js';
import { LaunchApi } from '../../../VibeRails/wwwroot/js/modules/launch-api.js';
import { TerminalManager } from '../../../VibeRails/wwwroot/js/modules/terminal-multitab.js';
import { LlmPickerController } from '../../../VibeRails/wwwroot/js/modules/llm-picker-controller.js';

function fixture({ prompt = 'Review the changes.', status = 'running', tabStatuses = [], error = null,
    later = { status: 'running', tabId: 'tab', sessionId: 'session' }, cancelFails = false } = {}) {
    const requests = [], adopted = [], toasts = [], remembered = [], statusReads = [], cancels = [];
    const app = {
        data: {}, showToast: (...args) => toasts.push(args),
        async apiCall(path, method, body) {
            if (path === '/api/v1/presets/agent') return { name: 'Reviewer', cli: 1, initialPrompt: prompt, revision: 7 };
            if (path === '/api/v1/terminal/tabs/tab/status') {
                statusReads.push(adopted.length);
                // Scripted host replies first, then the run's session is live.
                return tabStatuses.length ? tabStatuses.shift() : { hasActiveSession: true, sessionId: 'session' };
            }
            // `later` is what every read after the enqueue reports.
            if (path === '/api/v1/runs/run') return { id: 'run', ...later };
            if (path === '/api/v1/runs/run/cancel') {
                assert.equal(method, 'POST');
                cancels.push(path);
                if (cancelFails) throw new Error('Not found');
                return null;
            }
            assert.equal(path, '/api/v1/runs');
            assert.equal(method, 'POST');
            requests.push(body);
            return { id: 'run', status, ...(error ? { error } : {}), ...(status === 'running' ? { tabId: 'tab', sessionId: 'session' } : {}) };
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
    return { controller, manager, requests, adopted, toasts, remembered, statusReads, cancels };
}

const settle = () => new Promise(resolve => setImmediate(resolve));
/** Lets `seconds` one-second polls elapse on mocked timers. */
async function elapse(t, seconds) {
    for (let i = 0; i < seconds; i++) {
        t.mock.timers.tick(1000);
        await settle();
    }
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

test('a picker launch says it is opening before the backend is asked', async () => {
    // The backend opens the tab before it answers, so a message after the answer comes too late.
    const f = fixture();
    const app = f.controller.app, answer = app.apiCall;
    let toastsWhenAsked = null;
    app.apiCall = async function (path, method, body) {
        if (path === '/api/v1/runs' && method === 'POST') toastsWhenAsked = f.toasts.map(t => t[1]);
        return answer.call(this, path, method, body);
    };
    await f.manager.startFromSelection('preset:agent:codex');
    assert.deepEqual(toastsWhenAsked, ['Opening Reviewer…']);
    assert.deepEqual(f.toasts.map(t => t[1]), ['Opening Reviewer…'], 'a tab that opened needs no second message');
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

test('a failed tab launch stays on the terminal surface and gives the reason the run recorded', async () => {
    const f = fixture({ status: 'failed', error: 'The terminal could not be created.' });
    await f.manager.startFromSelection('preset:agent:codex');
    assert.deepEqual(f.adopted, []);
    assert.deepEqual(f.cancels, []);
    assert.deepEqual(f.toasts.at(-1), ['AI Agents', 'Reviewer did not start. The terminal could not be created.', 'error']);

    // A run that ended without recording a reason still points somewhere useful.
    const silent = fixture({ status: 'interrupted' });
    await silent.manager.startFromSelection('preset:agent:codex');
    assert.deepEqual(silent.toasts.at(-1), ['AI Agents', 'Reviewer did not start. Check Run history for details.', 'error']);
});

test('the same saved launch opens again while its first tab is still starting', async t => {
    // Each picker launch is its own tab. The first run is claimed and its tab host has not
    // reported a session yet, which used to swallow every further Start for that launch.
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const f = fixture({ status: 'claimed', later: { status: 'claimed' } });
    const leave = new AbortController();
    const start = () => f.controller.launch('agent', 'preset', { terminalTab: true, signal: leave.signal });
    const first = start();
    await settle();
    assert.equal(f.requests.length, 1);
    const second = start();
    await settle();
    assert.equal(f.requests.length, 2, 'the second Start queued its own run');
    leave.abort();
    await Promise.all([first, second]);
    assert.equal(f.toasts.some(toast => toast[2] !== 'info'), false, 'leaving the terminal is not a failure');
});

test('a double click queues one run', async () => {
    const f = fixture();
    await Promise.all([
        f.manager.startFromSelection('preset:agent:codex'),
        f.manager.startFromSelection('preset:agent:codex')
    ]);
    assert.equal(f.requests.length, 1);
    assert.deepEqual(f.adopted, ['tab']);
});

test('a launch nothing picks up is cancelled after 30 seconds and says so', async t => {
    // Left queued, the run would open a tab whenever something finally claimed it.
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const f = fixture({ status: 'queued', later: { status: 'queued' } });
    const launching = f.manager.startFromSelection('preset:agent:codex');
    await settle();
    await elapse(t, 29);
    assert.deepEqual(f.cancels, [], 'still inside the wait');
    await elapse(t, 1);
    await launching;
    assert.deepEqual(f.cancels, ['/api/v1/runs/run/cancel']);
    assert.deepEqual(f.adopted, []);
    assert.deepEqual(f.toasts.at(-1),
        ['AI Agents', 'Reviewer was still waiting to start after 30 seconds, so it was cancelled.', 'warning']);
});

test('a queued launch that cannot be cancelled says where to cancel it', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const f = fixture({ status: 'queued', later: { status: 'queued' }, cancelFails: true });
    const launching = f.manager.startFromSelection('preset:agent:codex');
    await settle();
    await elapse(t, 30);
    await launching;
    assert.match(f.toasts.at(-1)[1], /Cancel it in Run history/);
});

test('a claimed launch is followed past 30 seconds and its tab is adopted when it starts', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const later = { status: 'claimed' };
    const f = fixture({ status: 'queued', later });
    const launching = f.manager.startFromSelection('preset:agent:codex');
    await settle();
    await elapse(t, 45);
    assert.deepEqual(f.adopted, []);
    assert.deepEqual(f.cancels, [], 'a run that is starting is never cancelled');
    Object.assign(later, { status: 'running', tabId: 'tab', sessionId: 'session' });
    await elapse(t, 1);
    await launching;
    assert.deepEqual(f.adopted, ['tab']);
    assert.equal(f.toasts.some(toast => toast[2] !== 'info'), false);
});

test('a launch that never finishes starting stops being followed after two minutes', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const f = fixture({ status: 'claimed', later: { status: 'claimed' } });
    const launching = f.manager.startFromSelection('preset:agent:codex');
    await settle();
    await elapse(t, 120);
    await launching;
    assert.deepEqual(f.cancels, []);
    assert.match(f.toasts.at(-1)[1], /^Reviewer is taking unusually long to start/);
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
