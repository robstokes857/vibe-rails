import test from 'node:test';
import assert from 'node:assert/strict';
import { menuRow, toggleMessage, runFromMessageKey } from '../../../VibeRails/wwwroot/js/modules/launch-menu.js';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';

// VIBE-334: Quick actions rows stay compact with the optional message behind a link,
// and lane settings open with the shared default-lane switch.

const presentation = { logoHtml: '<i class="fa-solid fa-robot" aria-hidden="true"></i>', subtitle: 'Codex' };

test('agent rows put the message box behind a collapsed Add message link after Run', () => {
    const html = menuRow('Review <changes>', presentation, 'data-launch-id="review"', 'launch-message-0');
    const toggle = html.match(/<button[^>]*data-launch-message-toggle[^>]*>/s)?.[0];
    assert.ok(toggle, 'the row has a message toggle');
    assert.match(toggle, /class="btn btn-link btn-sm launch-menu-message-toggle"/);
    assert.match(toggle, /aria-expanded="false"/);
    assert.match(toggle, /aria-controls="launch-message-0-panel"/);
    assert.match(toggle, /aria-label="Add message for Review &lt;changes&gt;"/);
    assert.match(html, /fa-chevron-right/);
    assert.match(html, /<div class="launch-menu-message-panel" id="launch-message-0-panel" hidden>/);

    const textarea = html.match(/<textarea[^>]*>/s)?.[0];
    assert.ok(textarea, 'the message is a textarea, not a one-line input');
    for (const attribute of ['id="launch-message-0"', 'data-launch-message', 'rows="3"', 'maxlength="8000"',
        'aria-label="User message for Review &lt;changes&gt; (optional)"'])
        assert.ok(textarea.includes(attribute), attribute);
    assert.doesNotMatch(textarea, /required/);
    assert.match(html, /<label class="form-label" for="launch-message-0">User message <span class="text-muted">\(optional\)<\/span><\/label>/);
    assert.doesNotMatch(html, /launch-menu-message-help|Added to the prompt|aria-describedby/);
    assert.doesNotMatch(html, /<input/, 'no one-line field is left in the row');
    assert.doesNotMatch(html, /<changes>/, 'names are escaped');

    // The link follows Run in source order, so Tab goes Run -> Add message -> next row.
    assert.ok(html.indexOf('launch-menu-run') < html.indexOf('data-launch-message-toggle'));
    // The name and subtitle stay together; the message sits outside that block.
    const details = html.match(/<div class="launch-menu-details">.*?<\/div>/s)[0];
    assert.doesNotMatch(details, /data-launch-message/);
});

test('Action and script rows have no message link', () => {
    const html = menuRow('Build', { ...presentation, subtitle: 'Script' }, 'data-script-id="build"');
    assert.doesNotMatch(html, /data-launch-message/);
    assert.match(html, /aria-label="Run Build"/);
});

function messageHarness(value = '') {
    const attributes = { 'aria-controls': 'launch-message-0-panel', 'aria-expanded': 'false', 'aria-label': 'Add message for Review' };
    const span = { textContent: 'Add message' };
    const input = { value, focused: 0, focus() { this.focused++; } };
    const run = { position: 'header' };
    const wrapper = { before: element => { element.position = 'header'; } };
    const row = {
        classList: { toggle() {} },
        querySelector: selector => selector === '.launch-menu-run' ? run : null
    };
    const closest = selector => selector === '.launch-menu-row' ? row : selector === '.launch-menu-message' ? wrapper : null;
    const panel = {
        hidden: true,
        querySelector: selector => selector === '[data-launch-message]' ? input : null,
        closest,
        append: element => { element.position = 'footer'; }
    };
    const toggle = {
        getAttribute: name => attributes[name],
        setAttribute: (name, next) => { attributes[name] = next; },
        querySelector: selector => selector === 'span' ? span : null,
        closest
    };
    return { attributes, span, input, panel, toggle, run };
}

test('toggling the message opens and focuses the box, and a kept draft reads Edit message', t => {
    const harness = messageHarness();
    const oldDocument = globalThis.document;
    globalThis.document = { getElementById: id => id === 'launch-message-0-panel' ? harness.panel : null };
    t.after(() => { globalThis.document = oldDocument; });

    toggleMessage(harness.toggle, true);
    assert.equal(harness.panel.hidden, false);
    assert.equal(harness.attributes['aria-expanded'], 'true');
    assert.equal(harness.input.focused, 1);
    assert.equal(harness.span.textContent, 'Add message');

    harness.input.value = 'Focus on the parser';
    toggleMessage(harness.toggle, false);
    assert.equal(harness.panel.hidden, true);
    assert.equal(harness.attributes['aria-expanded'], 'false');
    assert.equal(harness.input.value, 'Focus on the parser', 'collapsing keeps the draft');
    assert.equal(harness.span.textContent, 'Edit message');
    assert.equal(harness.attributes['aria-label'], 'Edit message for Review');
    assert.equal(harness.input.focused, 1, 'collapsing leaves focus on the link');

    toggleMessage(harness.toggle, true);
    assert.equal(harness.span.textContent, 'Add message', 'an open box always offers to add');
    assert.equal(harness.attributes['aria-label'], 'Add message for Review');

    harness.input.value = '   ';
    toggleMessage(harness.toggle, false);
    assert.equal(harness.span.textContent, 'Add message', 'whitespace is not a message');
});

test('toggling ignores a link whose panel is gone', t => {
    const harness = messageHarness();
    const oldDocument = globalThis.document;
    globalThis.document = { getElementById: () => null };
    t.after(() => { globalThis.document = oldDocument; });
    toggleMessage(harness.toggle, true);
    assert.equal(harness.attributes['aria-expanded'], 'false');
});

function keyEvent(overrides = {}) {
    const runs = [];
    const run = { click: () => runs.push('run') };
    const row = { querySelector: selector => selector === '[data-launch-id]' ? run : null };
    const event = {
        key: 'Enter', shiftKey: false, isComposing: false, prevented: false,
        target: { matches: selector => selector === '[data-launch-message]', closest: selector => selector === '.launch-menu-row' ? row : null },
        preventDefault() { this.prevented = true; },
        ...overrides
    };
    return { event, runs };
}

test('Enter in the message box runs its row; Shift+Enter, composition and other fields do not', () => {
    const enter = keyEvent();
    assert.equal(runFromMessageKey(enter.event), true);
    assert.deepEqual(enter.runs, ['run']);
    assert.equal(enter.event.prevented, true);

    for (const overrides of [{ shiftKey: true }, { isComposing: true }, { key: 'a' },
        { target: { matches: () => false, closest: () => null } }]) {
        const other = keyEvent(overrides);
        assert.equal(runFromMessageKey(other.event), false, JSON.stringify(Object.keys(overrides)));
        assert.deepEqual(other.runs, []);
        assert.equal(other.event.prevented, false, 'the newline or keystroke is kept');
    }
});

function laneController(columns) {
    let modal = null;
    const calls = [];
    const app = {
        showModal: (title, html) => { modal = { title, html }; },
        closeModal() {}, showToast() {},
        async apiCall(url, method, payload) { calls.push({ url, method, payload }); return { id: 'col_ready', ...payload }; }
    };
    BoardApi.attach(app);
    const controller = new BoardController(app);
    controller.state.columns = columns;
    controller.state.boardId = 'brd_main';
    controller.refresh = async () => {};
    return { controller, calls, modal: () => modal };
}

test('lane settings open with the shared default-lane switch above Name', t => {
    const oldDocument = globalThis.document;
    globalThis.document = { getElementById: () => null };
    t.after(() => { globalThis.document = oldDocument; });
    const { controller, modal } = laneController([
        { id: 'col_ready', name: 'Ready', color: '#3b82f6', isDefaultForNewCards: true },
        { id: 'col_build', name: 'Build', color: '#10b981', isDefaultForNewCards: false }
    ]);

    controller.openLaneEditor('col_ready');
    const { title, html } = modal();
    assert.equal(title, 'Lane · Ready');
    const input = html.match(/<input[^>]*id="board-lane-default"[^>]*>/s)?.[0];
    assert.ok(input, 'the default-lane control is rendered');
    assert.match(input, /type="checkbox"/);
    assert.match(input, /role="switch"/);
    assert.match(input, /class="card-editor-switch"/);
    assert.doesNotMatch(input, /form-check-input/, 'not the bare checkbox');
    assert.match(input, /aria-describedby="board-lane-default-help"/);
    assert.match(input, /\schecked/);
    assert.match(html, /<label class="card-editor-switch-row" for="board-lane-default">\s*<span>Newly created cards land here<\/span>/);
    assert.match(html, /<div class="board-lane-default">/);
    assert.match(html, /id="board-lane-default-help">Used when no lane is chosen, even while this lane is collapsed\. Save to replace this board's current default\.<\/p>/);

    // First in the dialog: before Name, the colours and the Save/Delete bar.
    const at = html.indexOf('board-lane-default');
    for (const later of ['id="board-lane-name"', 'data-board-swatches', 'data-board-save-lane'])
        assert.ok(at < html.indexOf(later), later);
    assert.equal(html.match(/id="board-lane-default"/g).length, 1, 'rendered once, not also lower down');

    controller.openLaneEditor('col_build');
    assert.doesNotMatch(modal().html.match(/<input[^>]*id="board-lane-default"[^>]*>/s)[0], /\schecked/);
    controller.openLaneEditor(null);
    assert.equal(modal().title, 'Create lane');
    assert.match(modal().html, /role="switch" class="card-editor-switch" id="board-lane-default"/);
});

function laneEditor({ checked, baseline, name = 'Ready' }) {
    const fields = { '#board-lane-name': { value: name, focus() {} }, '#board-lane-default': { checked } };
    return { _boardDefaultLaneBaseline: baseline, querySelector: selector => fields[selector] || null };
}

test('Save lane sends the switch only when it changed', async () => {
    const { controller, calls } = laneController([{ id: 'col_ready', name: 'Ready', color: '#3b82f6' }]);
    controller.state.editingColumnId = 'col_ready';
    controller.state.editingColumnColor = '#3b82f6';

    await controller.saveLane(laneEditor({ checked: true, baseline: false }));
    await controller.saveLane(laneEditor({ checked: true, baseline: true }));
    await controller.saveLane(laneEditor({ checked: false, baseline: true }));
    assert.deepEqual(calls.map(call => [call.method, call.url.replace(/^.*\/board/, ''), call.payload]), [
        ['PUT', '/columns/col_ready', { name: 'Ready', color: '#3b82f6', isDefaultForNewCards: true }],
        ['PUT', '/columns/col_ready', { name: 'Ready', color: '#3b82f6' }],
        ['PUT', '/columns/col_ready', { name: 'Ready', color: '#3b82f6', isDefaultForNewCards: false }]
    ]);

    controller.state.editingColumnId = null;
    await controller.saveLane(laneEditor({ checked: true, baseline: false, name: 'New lane' }));
    assert.deepEqual(calls.at(-1).payload, { name: 'New lane', color: '#3b82f6', isDefaultForNewCards: true, boardId: 'brd_main' });
});

test('Save lane saves only the lane fields and closes; the run order saves on its own', async () => {
    const { controller, calls } = laneController([{ id: 'col_ready', name: 'Ready', color: '#3b82f6' }]);
    controller.state.editingColumnId = 'col_ready';
    controller.state.editingColumnColor = '#3b82f6';
    let closed = 0;
    controller.app.closeModal = () => { closed++; };
    const editor = laneEditor({ checked: true, baseline: false });

    assert.equal(await controller.saveLane(editor), true);
    assert.equal(closed, 1);
    // One column write: Save lane never writes the run order, which saves on each Add, Remove or move.
    assert.deepEqual(calls.map(call => [call.method, call.url.replace(/^.*\/board/, '')]), [['PUT', '/columns/col_ready']]);
    assert.equal(calls[0].payload.isDefaultForNewCards, true);
    // A save that keeps the dialog open no longer exists: the run order cannot fail after it.
    assert.equal(controller.saveLane.length, 1);
});
