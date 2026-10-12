import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { renderBoardLaunchOptions } from '../../../VibeRails/wwwroot/js/modules/board-launch-options.js';

function optionFields(model, effort, yolo) {
    const fields = {
        '[data-board-launch-model]': { value: model },
        '[data-board-launch-effort]': { value: effort },
        '[data-board-launch-yolo]': { checked: yolo }
    };
    return { querySelector: selector => fields[selector] || null };
}

function harness() {
    const calls = [], toasts = [], focused = [];
    const label = { textContent: '' };
    const startButton = { disabled: false, querySelector: selector => selector === 'i' ? {} : label };
    const fields = {
        '#board-card-title': { value: 'Coordinate this work', focus() {} },
        '#board-card-lane': { value: 'todo' },
        '#board-card-type': { value: 'task' },
        '#board-card-workflow': { value: 'agent' },
        '#board-card-manager': { value: 'base:codex', focus() { focused.push('manager'); } },
        '#board-card-assignee': { value: 'base:claude' },
        '[data-board-manager-launch-options]': optionFields('gpt-6-astra', 'high', false),
        '[data-board-worker-launch-options]': optionFields('claude-sonnet-4-6', 'low', true),
        '[data-board-chat-agent]': { value: 'base:copilot' },
        '[data-board-start-work]': startButton,
        '[data-board-chat]': { disabled: false }
    };
    const card = { id: 'card-1', key: 'VB-1', title: 'Original', workflowMode: 'agent',
        managerSelection: 'base:codex', assignee: 'base:claude', sessions: [] };
    const editor = { dataset: { cardId: card.id }, _boardCard: card,
        querySelector: selector => fields[selector] || null };
    const app = {
        async apiCall(url, method, body) {
            calls.push({ url, method, body });
            return method === 'PUT' ? { ...card, ...body } : { tabId: 'manager-tab', selection: body?.selection };
        },
        showToast(...args) { toasts.push(args); },
        closeModal() {},
        terminalController: { rememberTabLaunch() {} }
    };
    const controller = new BoardController(app);
    controller.refresh = async () => {};
    return { app, controller, editor, card, fields, calls, toasts, focused, label };
}

test('manager and worker options read from their own controls', () => {
    const h = harness();
    const form = h.controller.readCardForm(h.editor);
    assert.equal(form.workflowMode, 'agent');
    assert.equal(form.managerSelection, 'base:codex');
    assert.equal(form.assignee, 'base:claude');
    assert.deepEqual(form.managerBaseLlmOptions, { model: 'gpt-6-astra', effort: 'high', mode: '', yolo: false, speed: '' });
    assert.equal(form.baseLlmOptions.model, 'claude-sonnet-4-6');
    assert.equal(form.baseLlmOptions.effort, 'low');
    assert.equal(form.baseLlmOptions.yolo, true);
    const worker = renderBoardLaunchOptions('base:claude');
    const manager = renderBoardLaunchOptions('base:codex', {}, { idPrefix: 'board-manager-launch' });
    assert.match(worker, /id="board-launch-yolo"/);
    assert.match(manager, /id="board-manager-launch-yolo"/);
    assert.match(manager, /for="board-manager-launch-yolo"/);
});

test('Agent workflow saves first and launches the manager while preserving the worker', async () => {
    const h = harness();
    let finishSave;
    const save = new Promise(resolve => { finishSave = resolve; });
    const apiCall = h.app.apiCall;
    h.app.apiCall = async (...args) => {
        const result = await apiCall(...args);
        if (args[1] === 'PUT') await save;
        return result;
    };
    const starting = h.controller.startWork(h.editor, h.card);
    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].body.assignee, 'base:claude');
    assert.equal(h.calls[0].body.managerSelection, 'base:codex');
    finishSave();
    await starting;
    assert.deepEqual(h.calls[1], { url: '/api/v1/board/cards/card-1/launch', method: 'POST',
        body: { selection: 'base:codex', intent: 'manager' } });
});

for (const action of ['saveCard', 'startWork']) {
    test(`${action} requires a manager before saving an Agent workflow`, async () => {
        const h = harness();
        h.fields['#board-card-manager'].value = '';
        await h.controller[action](h.editor, h.card);
        assert.deepEqual(h.calls, []);
        assert.deepEqual(h.focused, ['manager']);
        assert.match(h.toasts[0][1], /Choose a manager/);
    });
}

test('a running worker does not prevent starting the manager', async () => {
    const h = harness();
    h.card.sessions = [{ id: 'worker', tabId: 'worker-tab', active: true, origin: 'agent_worker' }];
    h.controller.updateStartWorkButton(h.editor, h.card);
    assert.equal(h.label.textContent, 'Start Agent workflow');
    await h.controller.startWork(h.editor, h.card);
    assert.equal(h.calls.at(-1).body.intent, 'manager');
});

test('Go to manager selects the manager session ahead of a worker', async () => {
    const h = harness();
    h.card.sessions = [
        { id: 'worker', tabId: 'worker-tab', active: true, origin: 'agent_worker' },
        { id: 'manager', tabId: 'manager-tab', active: true, origin: 'agent_manager' }
    ];
    h.controller.focusSessionTab = async (_card, session) => h.focused.push(session.id);
    h.controller.updateStartWorkButton(h.editor, h.card);
    assert.equal(h.label.textContent, 'Go to manager');
    await h.controller.startWork(h.editor, h.card);
    assert.deepEqual(h.focused, ['manager']);
    assert.deepEqual(h.calls, []);
});

test('switching a manual card to Agent does not mistake its live worker for the manager', async () => {
    const h = harness();
    h.card.workflowMode = 'manual';
    h.card.activeSessionId = 'old-worker';
    h.card.sessions = [{ id: 'old-worker', tabId: 'old-worker-tab', active: true }];
    h.app.apiCall = async (url, method, body) => {
        h.calls.push({ url, method, body });
        return method === 'PUT' ? { ...h.card, ...body, activeSessionId: null }
            : { tabId: 'manager-tab', selection: body.selection };
    };
    await h.controller.startWork(h.editor, h.card);
    assert.equal(h.calls.length, 2);
    assert.equal(h.calls[1].body.intent, 'manager');
});

test('discussion remains independent of the workflow manager and worker', async () => {
    const h = harness();
    await h.controller.startWork(h.editor, h.card, 'chat');
    assert.equal(h.calls[0].body.managerSelection, 'base:codex');
    assert.equal(h.calls[0].body.assignee, 'base:claude');
    assert.deepEqual(h.calls[1].body, { selection: 'base:copilot', intent: 'chat' });
});

test('Agent tiles label the manager and escape catalog names', () => {
    const h = harness();
    h.controller.assigneeInfo = selection => ({ key: selection, label: '<img src=x onerror=bad()>', color: '#777' });
    const html = h.controller.renderCard(h.card);
    assert.match(html, /board-workflow-chip/);
    assert.match(html, /Manager · &lt;img/);
    assert.doesNotMatch(html, /<img src=x/);
});

test('closing the editor disposes both manager controls', () => {
    const h = harness();
    let disposed = 0;
    h.controller.managerPickerDispose = () => disposed++;
    h.controller.managerLaunchOptionsDispose = () => disposed++;
    h.controller.disposeCardPickers();
    h.controller.disposeCardPickers();
    assert.equal(disposed, 2);
});

test('linked sessions identify managers and workers in their metadata', t => {
    const originalWindow = globalThis.window;
    globalThis.window = {};
    t.after(() => { globalThis.window = originalWindow; });
    const h = harness();
    const host = { innerHTML: '', querySelectorAll: () => [] };
    h.editor.querySelector = selector => selector === '[data-board-sessions]' ? host : null;
    h.controller.renderSessionList(h.editor, h.card, [
        { id: 'manager', origin: 'agent_manager', displayName: 'Manager session', cli: 'codex' },
        { id: 'worker', origin: 'agent_worker', displayName: 'Worker session', cli: 'claude' }
    ], 'sessions');
    assert.match(host.innerHTML, /Manager · Codex · ended/);
    assert.match(host.innerHTML, /Worker · Claude · ended/);
});
