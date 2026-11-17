import test from 'node:test';
import assert from 'node:assert/strict';
import { columnStickOffset, keepShorterColumnInView } from '../../../VibeRails/wwwroot/js/modules/card-editor-columns.js';

test('a column shorter than the dialog stays at the top; a taller one holds its last screenful', () => {
    assert.equal(columnStickOffset(900, 500), 0);
    assert.equal(columnStickOffset(900, 900), 0);
    assert.equal(columnStickOffset(900, 1900), -1000);
    // Unmeasured values never push a column down the dialog.
    assert.equal(columnStickOffset(undefined, undefined), 0);
    assert.equal(columnStickOffset(0, 400), -400);
});

test('both columns get their own offset, refreshed on resize, and the observer is released', () => {
    const column = offsetHeight => {
        const values = {};
        return { offsetHeight, values, style: { setProperty(name, value) { values[name] = value; } } };
    };
    const main = column(1900), side = column(3100);
    const scroller = { clientHeight: 900, querySelector: selector => selector === '.board-editor-main' ? main : selector === '.board-editor-side' ? side : null };
    const editor = { querySelector: selector => selector === '.board-editor-scroll' ? scroller : null };
    const observed = [];
    let notify = null, disconnected = false;
    const previous = globalThis.ResizeObserver;
    globalThis.ResizeObserver = class { constructor(callback) { notify = callback; } observe(node) { observed.push(node); } disconnect() { disconnected = true; } };
    try {
        const dispose = keepShorterColumnInView(editor);
        assert.deepEqual(observed, [scroller, main, side]);
        assert.equal(main.values['--board-editor-stick'], '-1000px');
        assert.equal(side.values['--board-editor-stick'], '-2200px');
        // The card shrinks below the dialog height (comments filtered away): it now stays at the top.
        main.offsetHeight = 600;
        notify();
        assert.equal(main.values['--board-editor-stick'], '0px');
        dispose();
        assert.equal(disconnected, true);
        // An editor without the two-column layout is left alone.
        assert.equal(typeof keepShorterColumnInView({ querySelector: () => null }), 'function');
    } finally {
        globalThis.ResizeObserver = previous;
    }
});
