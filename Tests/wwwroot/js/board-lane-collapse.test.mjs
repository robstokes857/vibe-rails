import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

const key = 'viberails.board.collapsedLanes.v1';

function storageForTest(t, get) {
    const previous = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
    Object.defineProperty(globalThis, 'localStorage', { configurable: true, get });
    t.after(() => {
        if (previous) Object.defineProperty(globalThis, 'localStorage', previous);
        else delete globalThis.localStorage;
    });
}

function controller() {
    const board = new BoardController({});
    board.state.columns = [{ id: 'ready' }, { id: 'done' }];
    board.renderLanes = () => {};
    board.queryAll = () => [];
    return board;
}

test('collapsed lanes survive recreation and board switches without changing other lanes', t => {
    const storage = new Map();
    storageForTest(t, () => ({
        getItem: name => storage.get(name) ?? null,
        setItem: (name, value) => storage.set(name, value)
    }));
    const board = controller();
    board.toggleLane('done');
    assert.deepEqual(JSON.parse(storage.get(key)), ['done']);
    const reopened = controller();
    assert.ok(reopened.collapsedLaneIds.has('done'));
    reopened.state.columns = [{ id: 'another-board-lane' }];
    reopened.toggleLane('another-board-lane');
    reopened.state.columns = [{ id: 'done' }];
    reopened.toggleLane('done');
    assert.deepEqual(JSON.parse(storage.get(key)), ['another-board-lane']);
    reopened.toggleLane('unknown');
    assert.deepEqual(JSON.parse(storage.get(key)), ['another-board-lane']);
});

test('unavailable storage still allows collapsing and restoring lanes', t => {
    storageForTest(t, () => { throw new Error('Storage blocked'); });
    const board = controller();
    board.toggleLane('ready');
    assert.ok(board.collapsedLaneIds.has('ready'));
    board.toggleLane('ready');
    assert.equal(board.collapsedLaneIds.size, 0);
});

test('invalid saved preferences default to expanded lanes', t => {
    let saved = '{invalid';
    storageForTest(t, () => ({ getItem: () => saved }));
    assert.equal(controller().collapsedLaneIds.size, 0);
    saved = '{"ready":true}';
    assert.equal(controller().collapsedLaneIds.size, 0);
    saved = '["done",null,12]';
    assert.deepEqual([...controller().collapsedLaneIds], ['done']);
});
