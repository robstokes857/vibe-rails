import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { localCardEditorHtml } from '../../../VibeRails/wwwroot/js/modules/board-local-card.js';

test('a shared card locks editing but keeps session and history navigation available after rail refresh', () => {
    const previousDocument = globalThis.document;
    const previousObserver = globalThis.MutationObserver;
    let refresh, disconnected = false;
    globalThis.document = { createElement: () => ({}) };
    globalThis.MutationObserver = class {
        constructor(callback) { refresh = callback; }
        observe() {}
        disconnect() { disconnected = true; }
    };
    try {
        const field = { matches: () => false, disabled: false };
        const filter = { matches: () => true, disabled: false };
        const edit = { matches: () => false, closest: () => null, disabled: false, hidden: false };
        const replay = { matches: selector => selector.includes('[data-board-open-session]'), disabled: false };
        const history = { matches: () => false, closest: () => ({}), disabled: false };
        const buttons = [edit, replay, history];
        const editor = { prepend() {}, querySelectorAll: selector => selector === 'button' ? buttons : [field, filter] };
        const controller = { composerDisposers: [] };
        BoardController.prototype.lockSharedCardEditor.call(controller, editor);
        assert.equal(field.disabled, true);
        assert.equal(filter.disabled, false);
        assert.equal(edit.hidden, true);
        assert.equal(edit.disabled, true);
        assert.equal(replay.disabled, false);
        assert.equal(history.disabled, false);
        const replacement = { ...edit, hidden: false, disabled: false };
        buttons.push(replacement);
        refresh();
        assert.equal(replacement.disabled, true);
        controller.composerDisposers.forEach(dispose => dispose());
        assert.equal(disconnected, true);
    } finally {
        globalThis.document = previousDocument;
        globalThis.MutationObserver = previousObserver;
    }
});

test('a shared board never installs drag handlers', () => {
    let destroyed = false;
    BoardController.prototype.bindDragAndDrop.call({
        destroySortables() { destroyed = true; },
        currentBoard: () => ({ shared: true })
    });
    assert.equal(destroyed, true);
});

test('foreign shared cards describe read-only access', () => {
    const html = localCardEditorHtml({ card: { id: 'shared', title: 'Shared task' },
        projectPath: 'C:/other', boardName: 'Shared', columns: [], shared: true });
    assert.match(html, /Shared with you \(read-only\)/);
    assert.doesNotMatch(html, /Changes are saved/);
});
