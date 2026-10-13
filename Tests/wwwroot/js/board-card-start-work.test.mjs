import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

globalThis.window = {};

function harness(overrides = {}) {
    const saved = { id: 'card-1', key: 'VB-1', displayId: 'VIBE-1', title: 'Saved work',
        assignee: 'base:codex', workflowMode: 'manual', sessions: [], ...overrides };
    const calls = [], toasts = [], remembered = [], focused = [], opened = [];
    const label = {}, icon = {};
    const button = { dataset: { boardAction: 'start-work' }, disabled: false,
        querySelector: selector => selector === 'i' ? icon : label,
        setAttribute(name, value) { this[name] = value; } };
    const tile = { dataset: { cardId: saved.id },
        querySelector: selector => selector === '[data-board-start-card-work]' ? button : null };
    const app = { currentView: 'board',
        async apiCall(url, method, body) {
            calls.push({ url, method, body });
            return url.endsWith('/launch') ? { tabId: 'tab-new', selection: body.selection, workingDirectory: 'C:/work' } : saved;
        },
        showToast: (...args) => toasts.push(args),
        closeModal() {},
        navigate: () => assert.fail('a launch must stay on the Board'),
        terminalController: {
            rememberTabLaunch: (tabId, metadata) => remembered.push({ tabId, metadata }),
            async adoptLaunchedTab(tabId) { focused.push(tabId); return true; }
        }
    };
    const controller = new BoardController(app);
    controller.root = { isConnected: true, querySelectorAll: () => [tile] };
    controller.state.boardId = 'board-1';
    controller.state.cards = [{ ...saved, assignee: 'base:claude' }];
    controller.refreshBoardSnapshot = async () => {};
    controller.openCardEditor = async id => {
        assert.equal(controller._startingCards.has(id), false, 'the editor must allow launching after assignment');
        opened.push(id);
    };
    return { controller, app, saved, calls, toasts, remembered, focused, opened, button, label };
}

test('the face button routes its click without opening the card editor', () => {
    const h = harness();
    const starts = [];
    let stopped = false;
    h.controller.startWorkFromFace = id => starts.push(id);
    const trigger = { dataset: { boardAction: 'start-work' } };
    h.controller.onClick({
        target: { closest: selector => selector === '[data-board-action]' ? trigger : { dataset: { cardId: 'card-1' } } },
        stopPropagation: () => { stopped = true; }
    });
    assert.deepEqual(starts, ['card-1']);
    assert.deepEqual(h.opened, []);
    assert.equal(stopped, true);
});

test('the face launches the latest saved assignee without saving or focusing a terminal', async () => {
    const h = harness({ baseLlmOptions: { model: 'gpt-6-astra', speed: 'ultrafast' } });
    await h.controller.startWorkFromFace('card-1');
    assert.deepEqual(h.calls.map(call => [call.method, call.url]), [
        ['GET', '/api/v1/board/cards/card-1'], ['POST', '/api/v1/board/cards/card-1/launch']
    ]);
    assert.deepEqual(h.calls[1].body, { selection: 'base:codex', intent: 'work' });
    assert.deepEqual(h.focused, []);
    assert.deepEqual(h.opened, []);
    assert.equal(h.remembered[0].metadata.label, 'VIBE-1 · Saved work');
    assert.equal(h.remembered[0].metadata.taskKey, 'board-card:card-1');
    assert.equal(h.remembered[0].metadata.workingDirectory, 'C:/work');
    assert.deepEqual(h.saved.baseLlmOptions, { model: 'gpt-6-astra', speed: 'ultrafast' });
    assert.equal(h.button.disabled, false);
});

test('Agent cards launch the manager alongside workers, desktop activity and Automations', async () => {
    const h = harness({ workflowMode: 'agent', managerSelection: 'base:claude',
        hasActiveDesktopAgent: true, hasActiveAutomation: true,
        sessions: [{ id: 'worker', tabId: 'worker-tab', active: true, origin: 'agent_worker' },
            { id: 'review', tabId: 'review-tab', active: true, isAutomation: true }] });
    await h.controller.startWorkFromFace('card-1');
    assert.deepEqual(h.calls[1].body, { selection: 'base:claude', intent: 'manager' });
    assert.deepEqual(h.focused, []);
});

test('Go to manager opens the manager instead of launching or selecting the worker', async () => {
    const h = harness({ workflowMode: 'agent', managerSelection: 'base:claude',
        sessions: [{ id: 'worker', tabId: 'worker-tab', active: true, origin: 'agent_worker' },
            { id: 'manager', tabId: 'manager-tab', active: true, origin: 'agent_manager' }] });
    await h.controller.startWorkFromFace('card-1');
    assert.deepEqual(h.calls.map(call => call.method), ['GET']);
    assert.deepEqual(h.focused, ['manager-tab']);
});

for (const overrides of [{ assignee: null }, { workflowMode: 'agent', managerSelection: null }]) {
    test(`a missing ${overrides.workflowMode ? 'manager' : 'assignee'} opens the editor`, async () => {
        const h = harness(overrides);
        await h.controller.startWorkFromFace('card-1');
        assert.deepEqual(h.calls.map(call => call.method), ['GET']);
        assert.deepEqual(h.opened, ['card-1']);
        assert.equal(h.button.disabled, false);
    });
}

test('duplicate clicks and an editor launch are blocked across lane repaints', async () => {
    const h = harness();
    let resolveRead;
    h.app.apiCall = async (url, method, body) => {
        h.calls.push({ url, method, body });
        if (method === 'GET') return new Promise(resolve => { resolveRead = resolve; });
        return { tabId: 'new-tab' };
    };
    const pending = h.controller.startWorkFromFace('card-1');
    assert.equal(h.button.disabled, true);
    assert.match(h.controller.renderCard(h.saved), /data-board-start-card-work[\s\S]*?disabled/);
    await h.controller.startWorkFromFace('card-1');
    await h.controller.startWork({}, h.saved);
    assert.equal(h.calls.length, 1);
    resolveRead(h.saved);
    await pending;
    assert.equal(h.calls.length, 2);
    assert.equal(h.button.disabled, false);
});

test('a failed launch restores the face button for retry', async () => {
    const h = harness();
    const api = h.app.apiCall;
    h.app.apiCall = async (...args) => {
        if (args[0].endsWith('/launch')) throw new Error('No terminal slots');
        return api(...args);
    };
    await h.controller.startWorkFromFace('card-1');
    assert.equal(h.button.disabled, false);
    assert.deepEqual(h.toasts, [['Board', 'No terminal slots', 'error']]);
    h.app.apiCall = api;
    await h.controller.startWorkFromFace('card-1');
    assert.equal(h.remembered.length, 1);
});

test('navigation during the saved-card read prevents a late launch', async () => {
    const h = harness();
    let resolveRead;
    h.app.apiCall = async (url, method) => {
        h.calls.push({ url, method });
        return new Promise(resolve => { resolveRead = resolve; });
    };
    const pending = h.controller.startWorkFromFace('card-1');
    h.controller.state.boardId = 'another-board';
    resolveRead(h.saved);
    await pending;
    assert.equal(h.calls.length, 1);
    assert.deepEqual(h.toasts, []);
    assert.deepEqual(h.remembered, []);
});

test('face controls track terminal activity without treating desktop work as a session', () => {
    const h = harness();
    h.controller.updateStartWorkButton({ querySelector: selector => selector === '[data-board-start-card-work]' ? h.button : null },
        { ...h.saved, activeSessionId: 'live-session', activeTabId: 'live-tab' });
    assert.equal(h.label.textContent, 'Go to agent');
    assert.equal(h.button['aria-label'], 'Go to agent on VIBE-1');
    h.controller.updateCardStartWorkButtons({ ...h.saved, hasActiveDesktopAgent: true });
    assert.equal(h.label.textContent, 'Start work');
    assert.equal(h.button['aria-label'], 'Start work on VIBE-1');
});
