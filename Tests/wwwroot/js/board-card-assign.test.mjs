import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardCardAssign, assignMenuItems, assignMenuMarkup, matchesAssignQuery, ASSIGN_ACTION } from '../../../VibeRails/wwwroot/js/modules/board-card-assign.js';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { LlmPickerController } from '../../../VibeRails/wwwroot/js/modules/llm-picker-controller.js';

// VIBE-145: assign an agent from the lane card without opening the editor.

const catalog = [
    { key: 'env:9:codex', kind: 'environment', label: 'Nightly review (codex)', cli: 'codex', environmentId: 9, enabled: true },
    { key: 'base:claude', kind: 'base', label: 'Claude', cli: 'claude', enabled: true },
    { key: 'env:7:claude', kind: 'environment', label: 'Card review (claude)', cli: 'claude', environmentId: 7, enabled: true },
    { key: 'base:Codex', kind: 'base', label: 'Codex', cli: 'codex', enabled: true },
    { key: 'env:5:claude', kind: 'environment', label: 'Unknown setup (claude)', cli: 'claude', environmentId: 5, enabled: true }
];
const environments = [
    { id: 9, cli: 'codex', customPrompt: '' },
    { id: 7, cli: 'claude', customPrompt: 'Review the card.' }
];

test('menu rows keep the picker order inside built-ins, Quick Launches, then Agents', () => {
    const rows = assignMenuItems(catalog, environments);
    assert.deepEqual(rows.map(row => [row.key, row.kind]), [
        ['base:claude', 'base'],
        ['base:codex', 'base'],
        ['env:9:codex', 'quick-launch'],
        ['env:7:claude', 'agent'],
        // A setup the app has not loaded counts as an Agent, the picker's conservative answer.
        ['env:5:claude', 'agent']
    ]);
    assert.deepEqual(assignMenuItems([{ key: '', label: 'nameless', cli: 'claude' }], []), []);
    assert.deepEqual(assignMenuItems(undefined, undefined), []);
});

test('the filter matches labels and CLI names without case', () => {
    const [row] = assignMenuItems([catalog[2]], environments);
    assert.equal(matchesAssignQuery(row, ''), true);
    assert.equal(matchesAssignQuery(row, ' REVIEW '), true);
    assert.equal(matchesAssignQuery(row, 'CLAUDE'), true);
    assert.equal(matchesAssignQuery(row, 'codex'), false);
});

test('the menu groups rows, marks the saved assignee and escapes labels', () => {
    const rows = assignMenuItems([...catalog, { key: 'base:grok', kind: 'base', label: '<b>Grok</b>', cli: 'grok', enabled: true }], environments);
    const html = assignMenuMarkup(rows, { current: 'base:Claude', avatar: row => `<i data-avatar="${row.cli}"></i>` });
    assert.match(html, /aria-label="Built-in CLIs"/);
    assert.match(html, /aria-label="Quick Launches"/);
    assert.match(html, /aria-label="Agents"/);
    assert.match(html, /data-assign-value="base:claude"[^>]*>/);
    assert.equal((html.match(/aria-checked="true"/g) || []).length, 1);
    assert.match(html, /is-current[^>]*aria-checked="true" data-assign-value="base:claude"/);
    assert.match(html, /data-avatar="codex"/);
    assert.match(html, /&lt;b&gt;Grok&lt;\/b&gt;/);
    assert.doesNotMatch(html, /<b>Grok<\/b>/);
    // Assigned cards can be unassigned from the same menu; it leads the list.
    assert.match(html, /^\s*<button[^>]*board-assign-unassign[^>]*data-assign-value=""/);
});

test('an unassigned card gets no Unassign row and a filter narrows or empties the list', () => {
    const rows = assignMenuItems(catalog, environments);
    const open = assignMenuMarkup(rows, { current: '' });
    assert.doesNotMatch(open, /board-assign-unassign/);
    assert.doesNotMatch(open, /aria-checked="true"/);
    const narrowed = assignMenuMarkup(rows, { current: 'base:claude', query: 'nightly' });
    assert.equal((narrowed.match(/data-assign-value="/g) || []).length, 1);
    assert.match(narrowed, /env:9:codex/);
    assert.doesNotMatch(narrowed, /board-assign-unassign/, 'a typed filter hides Unassign so Enter cannot pick it');
    assert.match(assignMenuMarkup(rows, { query: 'zzz' }), /No matching agents\./);
    assert.match(assignMenuMarkup([], {}), /No agents are available/);
});

// Rob: "Manage quick launches…" curates the launch picker; it must not limit assignment.
test('the menu, the chip label and the editor picker include targets hidden from the launch picker', t => {
    // getCliBrand resolves logo paths against window.
    const originalWindow = globalThis.window;
    globalThis.window = {};
    t.after(() => { globalThis.window = originalWindow; });
    const app ={ data: { environments: [{ id: 7, cli: 'claude', customPrompt: 'Review the card.' }] } };
    const picker = new LlmPickerController(app);
    picker.catalog = [
        { key: 'base:claude', kind: 'base', label: 'Claude', cli: 'claude', enabled: true },
        { key: 'base:codex', kind: 'base', label: 'Codex', cli: 'codex', enabled: false },
        { key: 'env:7:claude', kind: 'environment', label: 'Card review (claude)', cli: 'claude', environmentId: 7, enabled: false },
        { key: 'base:shell', kind: 'base', label: 'Terminal', cli: 'shell', enabled: false }
    ];
    app.llmPickerController = picker;

    assert.deepEqual(picker.getEnabledItems('assignee').map(item => item.key), ['base:claude'], 'launching still honours hiding');
    assert.deepEqual(new BoardCardAssign(app).rows().map(row => row.key), ['base:claude', 'base:codex', 'env:7:claude'],
        'hidden targets are assignable; the shell never is');
    assert.equal(new BoardController(app).assigneeInfo('env:7:claude').label, 'Card review (claude)',
        'a hidden assignee keeps its real name on the chip');

    const editor = picker._buildOptionItems({ context: 'assignee', includeHidden: true, includeGroups: true }, 'base:codex');
    assert.deepEqual(editor.map(option => option.label), ['Claude', 'Codex', 'Card review (claude)'],
        'the Assignee picker lists everything and marks nothing (hidden)');
    const launch = picker._buildOptionItems({ context: 'assignee', includeGroups: true }, 'base:codex');
    assert.deepEqual(launch.map(option => option.label), ['Claude', 'Codex (hidden)'],
        'without includeHidden only the saved hidden choice survives, marked');
});

function card(overrides = {}) {
    return { id: 'card-1', key: 'VB-1', displayId: 'VIBE-145', title: 'Vibe Rails Cards', description: '', type: 'task',
        priority: 'medium', columnId: 'backlog', tags: [], assignee: null, ...overrides };
}

// Rob: a chip that was both a big filter and a tiny selector made reassigning clicks filter the
// lane, so cards looked deleted. The whole chip is now the selector and nothing on it filters.
test('the whole assignee chip opens the menu, assigned or not, and never filters', () => {
    const controller = new BoardController({});
    controller.assigneeInfo = key => key ? { key, cli: 'codex', label: 'Codex', logo: '', logoFilter: '', color: '#70a597' } : null;

    const unassigned = controller.renderCard(card());
    assert.match(unassigned, new RegExp(`<button type="button" class="board-assignee-chip is-empty" data-board-action="${ASSIGN_ACTION}"`));
    assert.match(unassigned, /aria-label="Assign VIBE-145"/);
    assert.match(unassigned, /aria-haspopup="dialog"/);

    const assigned = controller.renderCard(card({ assignee: 'base:codex' }));
    assert.match(assigned, new RegExp(`<button type="button" class="board-assignee-chip" data-board-action="${ASSIGN_ACTION}"`));
    assert.match(assigned, /aria-label="Change assignee of VIBE-145 \(Codex\)"/);
    assert.match(assigned, /<span class="board-assignee-name">Codex<\/span>/);
    for (const html of [unassigned, assigned]) {
        assert.doesNotMatch(html, /data-assignee-id=|click to filter|board-assign-caret"/, 'no part of the card face filters the board');
        assert.equal((html.match(new RegExp(`data-board-action="${ASSIGN_ACTION}"`, 'g')) || []).length, 1,
            'one control, not a chip plus a caret');
    }
});

// A fake event whose closest() answers the selectors onClick asks about.
function clickOn({ action = null, cardId = null } = {}) {
    const cardEl = cardId ? { dataset: { cardId } } : null;
    const trigger = action ? { dataset: { boardAction: action } } : null;
    const stops = [];
    return {
        stops,
        event: {
            stopPropagation() { stops.push('stop'); },
            target: { closest: selector => selector === '[data-board-action]' ? trigger
                : selector === '.board-card' ? cardEl : null }
        }
    };
}

test('a chip click opens the menu for that card instead of filtering or opening the editor', () => {
    const controller = new BoardController({});
    const opened = [];
    const editors = [];
    controller.laneAgents = { close() {} };
    controller.cardAssign = { open: (trigger, target) => opened.push([trigger.dataset.boardAction, target.id]), close() {} };
    controller.openCardEditor = id => editors.push(id);
    controller.persistFilters = () => assert.fail('the card face must not touch the filters');
    controller.filtersChanged = () => assert.fail('the card face must not filter the board');
    controller.state.cards = [card({ assignee: 'base:codex' }), card({ id: 'card-2', key: 'VB-2' })];

    // Assigned and Unassigned chips are the same control.
    const assigned = clickOn({ action: ASSIGN_ACTION, cardId: 'card-1' });
    controller.onClick(assigned.event);
    controller.onClick(clickOn({ action: ASSIGN_ACTION, cardId: 'card-2' }).event);
    assert.deepEqual(opened, [[ASSIGN_ACTION, 'card-1'], [ASSIGN_ACTION, 'card-2']]);
    assert.deepEqual(assigned.stops, ['stop']);
    assert.equal(controller.state.filters.assignee || '', '');

    // The card body still opens the editor.
    controller.onClick(clickOn({ cardId: 'card-2' }).event);
    assert.deepEqual(editors, ['card-2']);

    // A card the board no longer lists opens nothing.
    controller.onClick(clickOn({ action: ASSIGN_ACTION, cardId: 'gone' }).event);
    assert.equal(opened.length, 2);
    assert.deepEqual(editors, ['card-2']);
});

function saveHarness({ fail = false } = {}) {
    const calls = [];
    const toasts = [];
    const app = {
        async apiCall(url, method, body) {
            calls.push({ url, method, body });
            if (fail) throw new Error('offline');
            return { id: 'card-1', assignee: body.assignee || null, baseLlmOptions: null };
        },
        showToast(...args) { toasts.push(args); }
    };
    const controller = new BoardController(app);
    controller.root = { isConnected: true };
    controller.state.cards = [card({ assignee: 'base:claude', baseLlmOptions: { model: 'opus', effort: 'high' } })];
    controller.assigneeInfo = key => key === 'base:codex' ? { key, label: 'Codex' } : { key, label: 'Claude' };
    let rendered = 0;
    let snapshots = 0;
    controller.renderAll = () => { rendered++; };
    controller.refreshBoardSnapshot = async () => { snapshots++; };
    return { controller, calls, toasts, counts: () => ({ rendered, snapshots }) };
}

test('choosing an agent patches only the assignee, repaints from the saved card and resyncs the board', async () => {
    const h = saveHarness();
    await h.controller.assignCardFromFace(h.controller.state.cards[0], 'base:Codex');
    assert.deepEqual(h.calls, [{ url: '/api/v1/board/cards/card-1', method: 'PUT', body: { assignee: 'base:codex' } }]);
    assert.equal(h.controller.state.cards[0].assignee, 'base:codex');
    assert.equal(h.controller.state.cards[0].baseLlmOptions, null, 'the server clears base options on a change; the lane shows the saved card');
    assert.deepEqual(h.toasts, [['Board', 'VIBE-145 assigned to Codex.', 'success']]);
    assert.deepEqual(h.counts(), { rendered: 1, snapshots: 1 });
});

test('Unassign sends an empty assignee and the same selection sends nothing', async () => {
    const h = saveHarness();
    await h.controller.assignCardFromFace(h.controller.state.cards[0], '');
    assert.deepEqual(h.calls.map(call => call.body), [{ assignee: '' }]);
    assert.equal(h.controller.state.cards[0].assignee, null);
    assert.deepEqual(h.toasts.at(-1), ['Board', 'VIBE-145 unassigned.', 'success']);

    const same = saveHarness();
    await same.controller.assignCardFromFace(same.controller.state.cards[0], 'base:Claude');
    assert.deepEqual(same.calls, []);
    assert.deepEqual(same.toasts, []);
});

// Review F1 (codex, f6cd93ad): an open menu must follow an assignment made elsewhere.
function openMenuHarness(cardId = 'card-1', assignee = 'base:codex') {
    const menu = new BoardCardAssign({}, { avatar: () => '' });
    const renders = [];
    menu.root = null;
    menu.panel = { id: 'board-assign-panel', remove() { this.removed = true; } };
    menu.card = card({ id: cardId, assignee });
    menu.render = () => renders.push(menu.card.assignee);
    menu.positionPanel = () => {};
    const attributes = {};
    const button = id => ({
        closest: selector => selector === '.board-card' ? { dataset: { cardId: id } } : null,
        setAttribute: (name, value) => { attributes[name] = value; },
        removeAttribute: name => { delete attributes[name]; },
        focus() {},
        isConnected: true
    });
    const root = buttons => ({ querySelectorAll: () => buttons });
    return { menu, renders, attributes, button, root };
}

test('a lane repaint rebinds the open menu to the refreshed card and re-renders only on a change', () => {
    const h = openMenuHarness();
    const root = h.root([h.button('card-9'), h.button('card-1')]);
    h.menu.root = root;
    const same = card({ assignee: 'base:codex' });
    h.menu.mount(root, [same]);
    assert.equal(h.menu.card, same, 'the latest summary replaces the retained one');
    assert.deepEqual(h.renders, [], 'an unchanged assignee needs no repaint');
    assert.equal(h.attributes['aria-expanded'], 'true');
    assert.equal(h.attributes['aria-controls'], 'board-assign-panel');

    const elsewhere = card({ assignee: 'base:claude' });
    h.menu.mount(root, [elsewhere]);
    assert.equal(h.menu.card, elsewhere);
    assert.deepEqual(h.renders, ['base:claude'], 'the current marker and Unassign row follow the saved assignee');

    // The card left the loaded lanes: the menu closes rather than pointing at nothing.
    h.menu.mount(root, []);
    assert.equal(h.menu.card, elsewhere, 'a missing summary keeps the last known one');
    h.menu.mount(h.root([h.button('card-9')]), [elsewhere]);
    assert.equal(h.menu.panel, null);
    assert.equal(h.menu.card, null);
});

test('a choice is compared with the board\'s current card, not the menu\'s stale copy', async () => {
    const h = saveHarness();
    // The menu opened while the card said Codex; another client saved Claude and the poll refreshed the lane.
    const stale = card({ assignee: 'base:codex' });
    h.controller.state.cards = [card({ assignee: 'base:claude' })];
    await h.controller.assignCardFromFace(stale, 'base:codex');
    assert.deepEqual(h.calls.map(call => call.body), [{ assignee: 'base:codex' }], 'Codex is no longer saved, so choosing it must save');
    assert.equal(h.controller.state.cards[0].assignee, 'base:codex');

    const unchanged = saveHarness();
    unchanged.controller.state.cards = [card({ assignee: 'base:claude' })];
    await unchanged.controller.assignCardFromFace(card({ assignee: 'base:codex' }), 'base:claude');
    assert.deepEqual(unchanged.calls, [], 'Claude is already saved, whatever the menu remembered');
});

test('a failed save reports once and rethrows so the menu stays open', async () => {
    const h = saveHarness({ fail: true });
    await assert.rejects(() => h.controller.assignCardFromFace(h.controller.state.cards[0], 'base:codex'), /offline/);
    assert.deepEqual(h.toasts, [['Board', 'offline', 'error']]);
    assert.equal(h.controller.state.cards[0].assignee, 'base:claude');
    assert.deepEqual(h.counts(), { rendered: 0, snapshots: 0 });
});
