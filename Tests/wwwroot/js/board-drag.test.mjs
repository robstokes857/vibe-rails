import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

function harness(t) {
    const originalWindow = globalThis.window;
    const options = [];
    globalThis.window = {
        matchMedia: () => ({ matches: false }),
        Sortable: { create: (_, value) => { options.push(value); return { destroy() {} }; } }
    };
    t.after(() => { globalThis.window = originalWindow; });
    const controller = new BoardController({});
    controller.root = { isConnected: true };
    controller.query = () => ({});
    controller.queryAll = () => [{ hidden: false }];
    controller.bindDragAndDrop();
    return { controller, options };
}

test('card and lane selection both suspend snapshots before drag start', async t => {
    const { controller, options } = harness(t);
    assert.equal(options.length, 2);
    for (const option of options) {
        controller._boardPollAbort = new AbortController();
        option.onChoose();
        assert.equal(controller._boardDragging, true);
        assert.equal(controller._boardPollAbort.signal.aborted, true);
        option.onUnchoose();
        await Promise.resolve();
        assert.equal(controller._boardDragging, false);
    }
});

test('lane rendering waits until a drag finishes, including a cancelled selection', async t => {
    const { controller, options } = harness(t);
    let renders = 0;
    controller.captureScroll = () => { throw new Error('Cannot replace the active drag DOM'); };
    options[0].onChoose();
    assert.equal(controller._boardDragging, true);
    controller.renderLanes();
    controller.renderLanes = () => { renders++; };
    options[0].onUnchoose();
    await Promise.resolve();
    assert.equal(controller._boardDragging, false);
    assert.equal(renders, 1);
});

test('unchoose does not release the drag guard while a drop is being saved', async t => {
    const { controller, options } = harness(t);
    let release, renders = 0;
    controller.onCardDropped = () => new Promise(resolve => { release = resolve; });
    controller.renderLanes = () => { renders++; };
    options[0].onChoose();
    controller._boardRenderPending = true;
    options[0].onUnchoose();
    const pending = options[0].onEnd({});
    await Promise.resolve();
    assert.equal(options[0].filter(), true, 'a second drag must wait for the first save');
    assert.equal(options[1].filter(), true);
    assert.equal(controller._boardDragging, true);
    assert.equal(renders, 0);
    release();
    await pending;
    assert.equal(options[0].filter(), false);
    assert.equal(controller._boardDragging, false);
    assert.equal(renders, 1);
});

test('failed lane drops release deferred rendering as well', async t => {
    const { controller, options } = harness(t);
    let renders = 0;
    controller.onLanesReordered = async () => { throw new Error('Save failed'); };
    controller.renderLanes = () => { renders++; };
    options[1].onChoose();
    controller._boardRenderPending = true;
    await assert.rejects(options[1].onEnd(), /Save failed/);
    assert.equal(controller._boardDragging, false);
    assert.equal(renders, 1);
});
