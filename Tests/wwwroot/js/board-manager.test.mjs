import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { renderBoardManagerChip, mountBoardManagerChip, boardManagerChipState, boardManagerSummary } from '../../../VibeRails/wwwroot/js/modules/board-manager.js';
import { boardSettingsNavigation, mountBoardManager } from '../../../VibeRails/wwwroot/js/modules/board-settings.js';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { AiBoardManagerSettingsPanel, aiBoardManagerStatus, AI_BOARD_MANAGER_PATH } from '../../../VibeRails/wwwroot/js/modules/settings-ai-board-manager.js';

const tick = () => new Promise(resolve => setImmediate(resolve));

function fakeHost() {
    const listeners = new Map();
    return {
        innerHTML: '',
        hidden: false,
        isConnected: true,
        addEventListener: (name, listener) => listeners.set(name, listener),
        removeEventListener: name => listeners.delete(name),
        click: target => listeners.get('click')?.({ target })
    };
}

const button = action => ({ dataset: { boardManagerAction: action }, disabled: false, isConnected: true, closest: () => ({ dataset: { boardManagerAction: action }, disabled: false, isConnected: true }) });

test('the chip is hidden while the AI board manager is off, then renders setup, stopped and running states', () => {
    assert.equal(renderBoardManagerChip(null), '');
    assert.equal(boardManagerChipState({ enabled: false, selection: 'base:codex' }), 'hidden');
    assert.equal(boardManagerChipState({}), 'hidden', 'an answer without the switch shows nothing');
    assert.equal(renderBoardManagerChip({ enabled: false, selection: 'base:codex', active: { tabId: 't' } }), '');

    const unset = renderBoardManagerChip({ enabled: true, selection: null, managedCards: 2 });
    assert.match(unset, /data-board-manager-action="setup"/);
    assert.match(unset, /Set up board manager/);
    assert.match(unset, /class="board-ai-manager-chip is-unset"/);

    const stopped = renderBoardManagerChip({ enabled: true, selection: 'base:codex', active: null, managedCards: 1 }, () => ({ label: 'Codex' }));
    assert.match(stopped, /Codex/);
    assert.match(stopped, /Stopped/);
    assert.match(stopped, /· 1 Agent card</);
    assert.match(stopped, /data-board-manager-action="start"/);
    assert.doesNotMatch(stopped, /data-board-manager-action="stop"/);
    assert.doesNotMatch(stopped, /is-running/);

    const running = renderBoardManagerChip({ enabled: true, selection: 'base:codex', active: { tabId: 'tab-9' }, managedCards: 3, runningWorkers: 1, maxWorkers: 2, flaggedCards: 1 },
        () => ({ label: '<Codex>' }));
    assert.match(running, /is-running/);
    assert.match(running, /has-attention/);
    assert.match(running, /&lt;Codex&gt;/);
    assert.match(running, /Running/);
    assert.match(running, /3 Agent cards · 1 of 2 workers busy · 1 needs you/);
    assert.match(running, /data-board-manager-action="focus"/);
    assert.match(running, /data-board-manager-action="stop" aria-label="Stop board manager"/);
    // The chip's own class never reuses the card foot's per-card manager label.
    assert.doesNotMatch(running, /class="board-manager-chip/);
});

test('the summary counts cards, busy workers while running, and cards that need the user', () => {
    assert.equal(boardManagerSummary({ managedCards: 0 }), '0 Agent cards');
    assert.equal(boardManagerSummary({ managedCards: 1, active: null, runningWorkers: 3 }), '1 Agent card');
    assert.equal(boardManagerSummary({ managedCards: 2, active: { tabId: 't' }, runningWorkers: 0, maxWorkers: 1 }), '2 Agent cards · 0 of 1 worker busy');
    assert.equal(boardManagerSummary({ managedCards: 2, active: { tabId: 't' }, runningWorkers: 1, maxWorkers: 1, flaggedCards: 2 }), '2 Agent cards · 1 of 1 worker busy · 2 need you');
});

test('the chip refreshes for the current board, ignores stale boards, starts, focuses and stops the manager', async () => {
    const calls = [];
    const toasts = [];
    const focused = [];
    const settings = [];
    const confirms = [];
    let answer = false;
    let boardId = 'board-a';
    let status = { enabled: true, selection: 'base:codex', active: null, managedCards: 1 };
    BoardApi.attach({
        apiCall: async (path, method) => {
            calls.push({ path, method });
            if (method === 'POST' && path.endsWith('/launch')) { status = { ...status, active: { tabId: 'tab-7' } }; return { tabId: 'tab-7', sessionId: 's', selection: 'base:codex', reused: false }; }
            if (method === 'POST' && path.endsWith('/stop')) { status = { ...status, active: null }; return { stopped: true, sessionId: 's' }; }
            if (path.includes('board-slow')) { await new Promise(resolve => setTimeout(resolve, 20)); return { enabled: true, selection: 'base:claude', active: null, managedCards: 9 }; }
            return status;
        }
    });
    const host = fakeHost();
    const seen = [];
    const chip = mountBoardManagerChip({ showToast: (...args) => toasts.push(args) }, host, {
        boardId: () => boardId,
        describe: () => ({ label: 'Codex' }),
        openSettings: tab => settings.push(tab),
        focusTab: (tabId, selection) => focused.push([tabId, selection]),
        onStatus: value => seen.push(value),
        confirm: async options => { confirms.push(options); return answer; }
    });
    await tick();
    assert.equal(chip.status()?.selection, 'base:codex');
    assert.match(host.innerHTML, /Start manager/);
    assert.equal(host.hidden, false);
    assert.equal(seen.at(-1).managedCards, 1);

    // A board switch mid-flight: the slow reply for the old board never paints the new one.
    boardId = 'board-slow';
    const slow = chip.refresh();
    boardId = 'board-a';
    await chip.refresh();
    await slow;
    assert.match(host.innerHTML, /1 Agent card/);
    assert.doesNotMatch(host.innerHTML, /9 Agent cards/);

    await host.click(button('start'));
    assert.ok(calls.some(call => call.method === 'POST' && call.path.endsWith('/boards/board-a/manager/launch')));
    assert.equal(toasts.at(-1)[2], 'success');
    assert.match(host.innerHTML, /Go to manager/);
    await host.click(button('focus'));
    assert.deepEqual(focused.at(-1), ['tab-7', 'base:codex']);
    await host.click(button('setup'));
    assert.deepEqual(settings, ['manager']);

    // Stop asks first; declining sends nothing.
    await host.click(button('stop'));
    assert.equal(confirms.length, 1);
    assert.equal(confirms[0].confirmLabel, 'Stop manager');
    assert.equal(confirms[0].danger, true);
    assert.ok(!calls.some(call => call.path.endsWith('/stop')));
    answer = true;
    await host.click(button('stop'));
    assert.ok(calls.some(call => call.method === 'POST' && call.path.endsWith('/boards/board-a/manager/stop')));
    assert.equal(toasts.at(-1)[1], 'Board manager stopped.');
    assert.match(host.innerHTML, /Start manager/);

    // Turning the setting off hides the chip on the next refresh.
    status = { enabled: false, selection: null, active: null, managedCards: 0 };
    await chip.refresh();
    assert.equal(host.innerHTML, '');
    assert.equal(host.hidden, true);

    chip.dispose();
    status = { enabled: true, selection: 'base:codex', active: { tabId: 'x' }, managedCards: 1 };
    boardId = 'board-b';
    await chip.refresh();
    assert.equal(host.innerHTML, '', 'a disposed chip stops repainting');
});

test('the Manager settings tab mounts the picker, saves selection, options, workers and instructions, and clears', async () => {
    const calls = [];
    const pickers = [];
    const content = { innerHTML: '' };
    const fields = {
        '#board-manager-selection': { value: 'base:codex', addEventListener() {}, removeEventListener() {} },
        '[data-board-manager-workers]': { value: '2' },
        '[data-board-manager-instructions]': { value: 'Bugs before features.' },
        '[data-board-manager-options]': {
            innerHTML: '',
            // bindBoardLaunchOptions wires change listeners and reads <option>s; the fakes accept both.
            querySelector: selector => {
                const value = { '[data-board-launch-model]': 'gpt-6-astra', '[data-board-launch-effort]': 'high', '[data-board-launch-speed]': 'ultrafast' }[selector];
                return value === undefined ? null : { value, addEventListener() {}, removeEventListener() {}, querySelector: () => null };
            }
        },
        '[data-board-settings-content]': content
    };
    const element = {
        isConnected: true,
        querySelector: selector => fields[selector] || null,
        querySelectorAll: () => [],
        addEventListener: (name, listener) => { element[name] = listener; },
        removeEventListener() {}
    };
    let stored = { enabled: true, selection: null, baseLlmOptions: null, revision: 0, active: null, managedCards: 2, ownManagerCards: 1, maxWorkers: 1, instructions: null };
    BoardApi.attach({
        apiCall: async (path, method, body) => {
            calls.push({ path, method, body });
            if (method === 'PUT') {
                assert.equal(body.expectedRevision, stored.revision);
                stored = body.selection
                    ? { ...stored, selection: body.selection, baseLlmOptions: body.baseLlmOptions, maxWorkers: body.maxWorkers, instructions: body.instructions, revision: stored.revision + 1 }
                    : { ...stored, selection: null, baseLlmOptions: null, maxWorkers: 1, instructions: null, revision: 0 };
            }
            return stored;
        }
    });
    const toasts = [];
    const saved = [];
    const dispose = mountBoardManager({ showToast: (...args) => toasts.push(args) }, element, 'board-a', {
        onSaved: result => saved.push(result.revision),
        picker: (_app, select, options) => { pickers.push({ select, options }); return () => pickers.push('disposed'); }
    });
    await tick();
    assert.match(content.innerHTML, /No board manager yet\. Pick one below; it will manage 2 Agent cards/);
    assert.match(content.innerHTML, /1 card keeps its own manager/);
    assert.match(content.innerHTML, /<option value="1" selected>1 \(recommended for one checkout\)<\/option>/);
    assert.match(content.innerHTML, /maxlength="4000"/);
    assert.equal(pickers[0].options.selectedValue, '');
    assert.match(content.innerHTML, /data-settings-clear disabled/);

    await element.click({ target: { closest: selector => selector === '[data-settings-save]' ? { disabled: false, isConnected: true } : null } });
    const put = calls.find(call => call.method === 'PUT');
    assert.equal(put.path, '/api/v1/board/boards/board-a/manager');
    assert.deepEqual(put.body, {
        selection: 'base:codex',
        baseLlmOptions: { model: 'gpt-6-astra', effort: 'high', mode: '', yolo: false, speed: 'ultrafast' },
        instructions: 'Bugs before features.',
        maxWorkers: 2,
        expectedRevision: 0
    });
    assert.deepEqual(saved, [1]);
    assert.equal(toasts.at(-1)[1], 'Board manager saved.');
    // Re-rendered from the saved result: the picker is remounted with the stored selection.
    assert.equal(pickers.filter(entry => entry === 'disposed').length, 1);
    assert.equal(pickers.at(-1).options.selectedValue, 'base:codex');
    assert.match(content.innerHTML, /Not running · manages 2 Agent cards/);
    assert.match(content.innerHTML, /<option value="2" selected>2<\/option>/);
    assert.match(content.innerHTML, />Bugs before features\.<\/textarea>/);

    await element.click({ target: { closest: selector => selector === '[data-settings-clear]' ? { disabled: false, isConnected: true } : null } });
    const cleared = calls.filter(call => call.method === 'PUT').at(-1);
    assert.deepEqual(cleared.body, { selection: '', baseLlmOptions: null, expectedRevision: 1 });
    assert.deepEqual(saved, [1, 0]);
    dispose();
    assert.doesNotMatch(boardSettingsNavigation(), /data-board-settings-tab="manager"/);
    assert.match(boardSettingsNavigation({ manager: true }), /data-board-settings-tab="manager">Manager</);
});

test('the card editor sends no manager for a board-managed card and accepts it without one', () => {
    const controller = new BoardController({ showToast() {} });
    controller.state.boardManager = { enabled: true, selection: 'base:codex', active: null, managedCards: 0 };
    controller.state.aiBoardManager = true;
    const fields = {
        '#board-card-title': { value: 'Handled by the board', focus() {} },
        '#board-card-lane': { value: 'todo' },
        '#board-card-type': { value: 'task' },
        '#board-card-workflow': { value: 'agent' },
        '#board-card-use-board-manager': { checked: true },
        '#board-card-manager': { value: 'base:claude', focus() {} },
        '#board-card-assignee': { value: 'base:claude' },
        '[data-board-manager-launch-options]': { querySelector: () => null },
        '[data-board-worker-launch-options]': { querySelector: () => null }
    };
    const editor = { dataset: {}, querySelector: selector => fields[selector] || null };
    const payload = controller.readCardForm(editor);
    assert.equal(payload.workflowMode, 'agent');
    assert.equal(payload.managerSelection, '');
    assert.equal(payload.managerBaseLlmOptions, null);
    assert.equal(controller.validateCardTitle(editor, payload), true);

    fields['#board-card-use-board-manager'].checked = false;
    assert.equal(controller.readCardForm(editor).managerSelection, 'base:claude');

    // The setting is on but this board has no manager: point at Board settings.
    controller.state.boardManager = { enabled: true, selection: null, active: null, managedCards: 0 };
    const toasts = [];
    controller.app.showToast = (...args) => toasts.push(args);
    assert.equal(controller.validateCardTitle(editor, { ...payload, managerSelection: '' }), false);
    assert.match(toasts[0][1], /set up this board's manager in Board settings > Manager/);

    // The setting is off: exactly the message before board managers existed.
    controller.state.boardManager = null;
    controller.state.aiBoardManager = false;
    assert.equal(controller.validateCardTitle(editor, { ...payload, managerSelection: '' }), false);
    assert.equal(toasts[1][1], 'Choose a manager agent for the Agent workflow.');
});

test('a saved board-managed card keeps saving ordinary edits after the setting is off or its manager is removed', () => {
    const toasts = [];
    const controller = new BoardController({ showToast: (...args) => toasts.push(args) });
    const payload = { title: 'Renamed title', workflowMode: 'agent', managerSelection: '', assignee: 'base:claude' };
    const editor = {
        dataset: { cardId: 'card_saved' },
        _boardFormBaseline: { title: 'Old title', workflowMode: 'agent', managerSelection: '', assignee: 'base:claude' },
        querySelector: () => null
    };
    // Switch off, or on with the board's manager removed: the unchanged assignment is not re-checked.
    for (const boardManager of [null, { enabled: true, selection: null, active: null }]) {
        controller.state.boardManager = boardManager;
        controller.state.aiBoardManager = Boolean(boardManager);
        assert.equal(controller.validateCardTitle(editor, payload), true);
        assert.deepEqual(controller.cardChanges(editor, payload), { title: 'Renamed title' });
    }
    assert.equal(toasts.length, 0);
    // Making a Manual card an Agent card without a manager is still refused...
    editor._boardFormBaseline = { ...editor._boardFormBaseline, workflowMode: 'manual' };
    assert.equal(controller.validateCardTitle(editor, payload), false);
    // ...and so is a new card that would rely on a board manager it does not have.
    assert.equal(controller.validateCardTitle({ dataset: {}, querySelector: () => null }, payload), false);
    assert.equal(toasts.length, 2);
});

test('a board-managed card runs while the board manager runs, and only while the setting is on', () => {
    const controller = new BoardController({ showToast() {} });
    const card = { id: 'c1', workflowMode: 'agent', managerSelection: null, sessions: [] };
    assert.equal(controller.isBoardManaged(card), true);
    assert.equal(controller.isBoardManaged({ ...card, managerSelection: 'base:claude' }), false);
    assert.equal(controller.isBoardManaged({ ...card, workflowMode: 'manual' }), false);
    assert.equal(controller.hasRunningSession(card), false);
    controller.state.boardManager = { enabled: true, selection: 'base:codex', active: { tabId: 'tab-1' } };
    assert.equal(controller.hasRunningSession(card), true);
    assert.equal(controller.hasRunningSession({ ...card, managerSelection: 'base:claude' }), false);
    controller.state.boardManager = null;
    assert.equal(controller.hasRunningSession(card), false);
});

test('the Settings switch loads, saves at once and reports an unconfirmed save', async () => {
    const listeners = {};
    const toggle = { checked: false, disabled: true, addEventListener: (name, listener) => { listeners[name] = listener; } };
    const status = { textContent: '' };
    const reload = { addEventListener: (name, listener) => { listeners.reload = listener; } };
    const root = { querySelector: selector => ({ '[data-ai-board-manager-enabled]': toggle, '[data-ai-board-manager-status]': status, '[data-ai-board-manager-reload]': reload }[selector] || null) };
    const calls = [];
    let stored = false;
    let failNext = false;
    const app = {
        apiCall: async (path, method, body) => {
            calls.push({ path, method, body });
            if (failNext) { failNext = false; throw new Error('Network error.'); }
            if (method === 'PUT') stored = body.enabled;
            return { enabled: stored };
        }
    };
    const panel = new AiBoardManagerSettingsPanel(app, root);
    await panel.load();
    assert.deepEqual(calls[0], { path: AI_BOARD_MANAGER_PATH, method: 'GET', body: null });
    assert.equal(toggle.checked, false);
    assert.equal(toggle.disabled, false);
    assert.equal(status.textContent, aiBoardManagerStatus(false));

    toggle.checked = true;
    await listeners.change();
    assert.deepEqual(calls.at(-1), { path: AI_BOARD_MANAGER_PATH, method: 'PUT', body: { enabled: true } });
    assert.equal(toggle.checked, true);
    assert.match(status.textContent, /^Saved: The AI board manager is on/);

    failNext = true;
    toggle.checked = false;
    await listeners.change();
    assert.match(status.textContent, /Network error\. Reload to check the saved setting\./);
    assert.equal(toggle.disabled, true, 'an unconfirmed save waits for a reload');
    await listeners.reload();
    assert.equal(toggle.checked, true);
    panel.unload();
});
