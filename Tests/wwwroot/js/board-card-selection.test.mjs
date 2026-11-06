import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardCardSelection, cardDeletionConfirmation } from '../../../VibeRails/wwwroot/js/modules/board-card-selection.js';

function harness({ confirm = async () => true, remove = async () => {} } = {}) {
    const board = {
        root: { isConnected: true }, state: { boardId: 'main', filters: { q: '' }, cards: [
            { id: 'a', key: 'VB-1', columnId: 'ready' }, { id: 'b', key: 'VB-2', columnId: 'done' }
        ] }, collapsedLaneIds: new Set(),
        currentBoard: () => ({ id: 'main' }), filteredCards() { return this.state.cards; },
        renderLanes() {}, async refresh() { this.refreshes = (this.refreshes || 0) + 1; }
    };
    const selection = new BoardCardSelection(board, { confirm, remove });
    selection.render = () => {};
    selection.active = true;
    return { board, selection };
}

test('only explicitly checked loaded cards enter selection, across lanes', () => {
    const { board, selection } = harness();
    selection.toggle('a', true);
    selection.toggle('unloaded', true);
    board.collapsedLaneIds.add('done');
    selection.toggle('b', true);
    assert.deepEqual([...selection.selected.keys()], ['a']);
    board.collapsedLaneIds.clear();
    selection.toggle('b', true);
    selection.toggle('a', true);
    assert.deepEqual([...selection.selected.keys()], ['a', 'b']);
    selection.toggle('a', false);
    assert.deepEqual([...selection.selected.keys()], ['b']);
});

test('cancel preserves selection and never sends deletes', async () => {
    const { selection } = harness({ confirm: async () => false, remove: () => assert.fail('cancelled') });
    selection.toggle('a', true);
    await selection.deleteSelected();
    assert.equal(selection.selected.size, 1);
    assert.equal(selection.busy, false);
});

test('partial failure is recoverable and successful IDs are never retried', async () => {
    const calls = [];
    let fail = true;
    const { board, selection } = harness({ remove: async id => {
        calls.push(id);
        if (id === 'b' && fail) throw new Error('Unavailable');
    } });
    selection.toggle('a', true);
    selection.toggle('b', true);
    await selection.deleteSelected();
    assert.deepEqual([...selection.selected.keys()], ['b']);
    assert.equal(selection.failures.get('b'), 'Unavailable');
    assert.deepEqual(board.state.cards.map(card => card.id), ['b']);
    assert.equal(board.refreshes, 1);
    fail = false;
    await selection.deleteSelected();
    assert.deepEqual(calls, ['a', 'b', 'b']);
    assert.equal(selection.selected.size, 0);
    assert.equal(selection.busy, false);
});

test('duplicate submissions are blocked from confirmation through the final request', async () => {
    let confirm, remove, confirmations = 0;
    const calls = [];
    const { selection } = harness({
        confirm: () => { confirmations++; return new Promise(resolve => { confirm = resolve; }); },
        remove: id => { calls.push(id); return new Promise(resolve => { remove = resolve; }); }
    });
    selection.toggle('a', true);
    const pending = selection.deleteSelected();
    await selection.deleteSelected();
    assert.equal(confirmations, 1);
    confirm(true);
    await Promise.resolve();
    await selection.deleteSelected();
    selection.toggle('b', true);
    assert.deepEqual(calls, ['a']);
    assert.equal(selection.selected.has('b'), false);
    remove();
    await pending;
    assert.equal(selection.busy, false);
});

test('changing context while confirming prevents writes; leaving during a batch stops unsent cards', async () => {
    for (const duringConfirmation of [true, false]) {
        let release;
        const calls = [];
        const wait = () => new Promise(resolve => { release = resolve; });
        const { board, selection } = harness({ confirm: duringConfirmation ? wait : async () => true,
            remove: id => { calls.push(id); return wait(); } });
        selection.toggle('a', true);
        selection.toggle('b', true);
        const pending = selection.deleteSelected();
        await Promise.resolve();
        selection.reset(); // Filters, board switch and unload use this same invalidation.
        release(true);
        await pending;
        assert.deepEqual(calls, duringConfirmation ? [] : ['a']);
        assert.equal(board.refreshes, undefined, 'stale work must not refresh a new board');
        assert.equal(selection.selected.size, 0);
    }
});

test('single and bulk confirmations share the deletion consequence', () => {
    const single = cardDeletionConfirmation([{ displayId: 'VIBE-2', title: 'A card' }]);
    const bulk = cardDeletionConfirmation([{}, {}]);
    assert.match(single.message, /VIBE-2 · A card/);
    assert.equal(single.message.split('? ')[1], bulk.message.split('? ')[1]);
    assert.equal(bulk.confirmLabel, 'Delete 2 cards');
});
