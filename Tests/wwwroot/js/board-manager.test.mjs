import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { renderBoardManagerChip, mountBoardManagerChip, boardManagerChipState, boardManagerSummary, BOARD_MANAGER_HIDDEN_POLL_MS } from '../../../VibeRails/wwwroot/js/modules/board-manager.js';
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

test('while the chip is hidden the board poll asks once a minute; shown, every tick; refresh always asks', async () => {
    let clock = 1_000_000;
    let reads = 0;
    let status = { enabled: false, selection: null, active: null, managedCards: 0 };
    BoardApi.attach({ apiCall: async () => { reads += 1; return status; } });
    const host = fakeHost();
    const chip = mountBoardManagerChip({ showToast() {} }, host, { boardId: () => 'board-a', now: () => clock });
    await tick();
    assert.equal(reads, 1, 'mounting reads the status once');
    assert.equal(host.hidden, true);

    // Ten-second ticks inside the minute send nothing.
    for (let i = 0; i < 5; i += 1) { clock += 10_000; await chip.poll(); }
    assert.equal(reads, 1);
    clock += BOARD_MANAGER_HIDDEN_POLL_MS - 50_000;
    await chip.poll();
    assert.equal(reads, 2, 'a minute after the last read the poll asks again');

    // An explicit refresh (board switch, launch, manager save) asks at once, off or not.
    clock += 1_000;
    await chip.refresh();
    assert.equal(reads, 3);

    // Once the switch reads on, every tick asks.
    status = { enabled: true, selection: 'base:codex', active: null, managedCards: 1 };
    clock += BOARD_MANAGER_HIDDEN_POLL_MS;
    await chip.poll();
    assert.equal(reads, 4);
    assert.equal(host.hidden, false);
    clock += 10_000;
    await chip.poll();
    clock += 10_000;
    await chip.poll();
    assert.equal(reads, 6);

    // A failed read hides the chip and drops back to once a minute.
    BoardApi.attach({ apiCall: async () => { reads += 1; throw new Error('offline'); } });
    clock += 10_000;
    await chip.poll();
    assert.equal(reads, 7);
    assert.equal(host.hidden, true);
    clock += 10_000;
    await chip.poll();
    assert.equal(reads, 7);
    chip.dispose();
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
            addEventListener() {}, removeEventListener() {},
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

// VIBE-262 -----------------------------------------------------------------------------------

test('a manager running in another window shows there, with nothing to focus, stop or start', () => {
    const status = { enabled: true, selection: 'base:claude', active: null, elsewhere: { sessionId: 's-other' }, managedCards: 2, runningWorkers: 1, maxWorkers: 1 };
    assert.equal(boardManagerChipState(status), 'elsewhere');
    const html = renderBoardManagerChip(status, () => ({ label: 'Claude' }));
    assert.match(html, /is-elsewhere/);
    assert.match(html, /Running in another window/);
    assert.match(html, /2 Agent cards · 1 of 1 worker busy/);
    assert.match(html, /from another VibeRails window on this project/);
    assert.doesNotMatch(html, /data-board-manager-action="(focus|stop|start)"/);
    // A live tab here always wins over another window's record.
    assert.equal(boardManagerChipState({ ...status, active: { tabId: 't' } }), 'running');
});

test('the chip repeats the manager tab status word and falls back to Running', () => {
    const status = { enabled: true, selection: 'base:claude', active: { tabId: 't', sessionId: 's' }, managedCards: 1 };
    const describe = () => ({ label: 'Claude' });
    assert.match(renderBoardManagerChip(status, describe), /board-ai-manager-word">Running</);
    assert.match(renderBoardManagerChip(status, describe, { statusWord: { status: 'thinking', text: 'Thinking' } }), /board-ai-manager-word">Thinking</);
    assert.match(renderBoardManagerChip(status, describe, { statusWord: { status: 'ready', text: 'Ready' } }), /board-ai-manager-word">Ready</);
    const waiting = renderBoardManagerChip(status, describe, { statusWord: { status: 'waiting', text: 'Waiting for user input' } });
    assert.match(waiting, /board-ai-manager-word">Waiting for user input</);
    assert.match(waiting, /is-waiting/);
    assert.match(waiting, /aria-label="Board manager Claude, running, Waiting for user input"/);
    // Connected and Disconnected say nothing about the agent, so the chip keeps Running.
    assert.match(renderBoardManagerChip(status, describe, { statusWord: { status: 'connected', text: 'Connected' } }), /board-ai-manager-word">Running</);
    // A stopped manager never shows a stale word.
    assert.match(renderBoardManagerChip({ ...status, active: null }, describe, { statusWord: { status: 'thinking', text: 'Thinking' } }), /Stopped/);
});

test('"needs you" is a button that lists the flagged Agent cards, escaped, with the rest counted', () => {
    const status = { enabled: true, selection: 'base:claude', active: { tabId: 't' }, managedCards: 4, flaggedCards: 3,
        flagged: [{ id: 'c1', key: 'VB-1', displayId: 'VIBE-1', title: '<b>Fix</b>', laneName: 'Review' }, { id: 'c2', key: 'VB-2', title: 'Two', laneName: 'Ready' }] };
    const closed = renderBoardManagerChip(status, () => ({ label: 'Claude' }));
    assert.match(closed, /data-board-manager-action="flagged"[^>]*aria-expanded="false"/);
    assert.match(closed, /aria-label="3 need you"/);
    assert.match(closed, /board-ai-manager-needs-count">3</);
    assert.match(closed, /id="board-ai-manager-flagged-menu"[^>]*hidden/);
    assert.match(closed, /data-board-manager-card="c1"/);
    assert.match(closed, /VIBE-1/);
    assert.match(closed, /&lt;b&gt;Fix&lt;\/b&gt;/);
    assert.match(closed, /VB-2/, 'a card without a display id shows its key');
    assert.match(closed, /\+1 more flagged card on this board/);
    // The inline summary leaves the count to the button; the tooltip keeps the whole sentence.
    assert.match(closed, /board-ai-manager-summary">· 4 Agent cards · 0 of 1 worker busy</);
    assert.match(closed, /title="Claude is managing this board: 4 Agent cards · 0 of 1 worker busy · 3 need you"/);
    const open = renderBoardManagerChip(status, () => ({ label: 'Claude' }), { menuOpen: true });
    assert.match(open, /aria-expanded="true"/);
    assert.doesNotMatch(open, /id="board-ai-manager-flagged-menu"[^>]*hidden/);
    assert.doesNotMatch(renderBoardManagerChip({ ...status, flaggedCards: 0, flagged: [] }), /data-board-manager-action="flagged"/);
});

function fakeEvents() {
    const handlers = new Map();
    return {
        on(type, fn) {
            handlers.set(type, [...(handlers.get(type) || []), fn]);
            return () => handlers.set(type, (handlers.get(type) || []).filter(other => other !== fn));
        },
        emit(type, payload) { for (const fn of handlers.get(type) || []) fn(payload); },
        count: () => [...handlers.values()].reduce((sum, list) => sum + list.length, 0)
    };
}

function eventHost() {
    const host = fakeHost();
    const listeners = new Map();
    host.addEventListener = (name, listener) => listeners.set(name, listener);
    host.removeEventListener = name => listeners.delete(name);
    host.click = clicked => listeners.get('click')?.({ target: clicked });
    host.fire = (name, event) => listeners.get(name)?.(event);
    return host;
}

const target = dataset => ({ dataset, disabled: false, isConnected: true, closest: () => ({ dataset, disabled: false, isConnected: true }) });

test('the status word follows the manager session app events and ignores every other session', async () => {
    let status = { enabled: true, selection: 'base:claude', active: { tabId: 'tab-1', sessionId: 's-1', cli: 'Claude' }, managedCards: 1 };
    BoardApi.attach({ apiCall: async () => status });
    const events = fakeEvents();
    const host = eventHost();
    const chip = mountBoardManagerChip({ showToast() {} }, host, { boardId: () => 'board-a', describe: () => ({ label: 'Claude' }), events });
    await tick();
    // Already running when the page loaded: no word until the session says something.
    assert.equal(chip.statusWord(), null);
    assert.match(host.innerHTML, /board-ai-manager-word">Running</);

    events.emit('session_idle', { sessionId: 's-other' });
    assert.equal(chip.statusWord(), null, 'another session never moves the word');

    // A quiet spell after unknown output reads as the turn ending.
    events.emit('session_idle', { sessionId: 's-1' });
    assert.equal(chip.statusWord()?.text, 'Ready');
    assert.match(host.innerHTML, /board-ai-manager-word">Ready</);

    events.emit('session_input', { sessionId: 's-1', kind: 'submit' });
    assert.equal(chip.statusWord()?.text, 'Thinking');
    assert.match(host.innerHTML, /board-ai-manager-word">Thinking</);

    events.emit('session_waiting_for_user', { sessionId: 's-1' });
    assert.equal(chip.statusWord()?.text, 'Waiting for user input');
    assert.match(host.innerHTML, /is-waiting/);

    // A new manager session starts from nothing again.
    status = { ...status, active: { tabId: 'tab-2', sessionId: 's-2', cli: 'Codex' } };
    await chip.refresh();
    assert.equal(chip.statusWord(), null);
    events.emit('session_waiting_for_user', { sessionId: 's-1' });
    assert.equal(chip.statusWord(), null);

    chip.dispose();
    assert.equal(events.count(), 0, 'dispose unsubscribes from the app events');
});

test('a manager this chip starts reads Thinking at once, as its tab does', async () => {
    let status = { enabled: true, selection: 'base:codex', active: null, managedCards: 1 };
    BoardApi.attach({
        apiCall: async (path, method) => {
            if (method === 'POST' && path.endsWith('/launch')) {
                status = { ...status, active: { tabId: 'tab-7', sessionId: 's-7', cli: 'Codex' } };
                return { tabId: 'tab-7', sessionId: 's-7', selection: 'base:codex', reused: false };
            }
            return status;
        }
    });
    const events = fakeEvents();
    const host = eventHost();
    const chip = mountBoardManagerChip({ showToast() {} }, host, { boardId: () => 'board-a', describe: () => ({ label: 'Codex' }), events });
    await tick();
    await host.click(target({ boardManagerAction: 'start' }));
    assert.equal(chip.statusWord()?.text, 'Thinking');
    assert.match(host.innerHTML, /board-ai-manager-word">Thinking</);
    events.emit('session_idle', { sessionId: 's-7' });
    assert.equal(chip.statusWord()?.text, 'Ready');
    chip.dispose();
});

test('the flagged list opens, opens a card and closes, and a refresh waits while it is open', async () => {
    let status = { enabled: true, selection: 'base:claude', active: { tabId: 't', sessionId: 's' }, managedCards: 2, flaggedCards: 1,
        flagged: [{ id: 'c1', key: 'VB-1', title: 'One', laneName: 'Review' }] };
    BoardApi.attach({ apiCall: async () => status });
    const opened = [];
    const host = eventHost();
    const chip = mountBoardManagerChip({ showToast() {} }, host, {
        boardId: () => 'board-a', describe: () => ({ label: 'Claude' }), openCard: id => opened.push(id), events: fakeEvents()
    });
    await tick();
    await host.click(target({ boardManagerAction: 'flagged' }));
    assert.match(host.innerHTML, /aria-expanded="true"/);

    // A poll while the list is open keeps the DOM (and keyboard focus) where it is.
    status = { ...status, managedCards: 5 };
    await chip.refresh();
    assert.match(host.innerHTML, /2 Agent cards/);
    assert.equal(chip.status().managedCards, 5);

    host.fire('keydown', { key: 'Escape', preventDefault() {} });
    assert.match(host.innerHTML, /aria-expanded="false"/);
    assert.match(host.innerHTML, /5 Agent cards/, 'closing repaints with what arrived meanwhile');

    await host.click(target({ boardManagerAction: 'flagged' }));
    await host.click(target({ boardManagerCard: 'c1' }));
    assert.deepEqual(opened, ['c1']);
    assert.match(host.innerHTML, /aria-expanded="false"/);
    chip.dispose();
});
