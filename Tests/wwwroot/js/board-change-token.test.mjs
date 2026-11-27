import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

// VIBE-322: the poll reads card activity first. Its change token says whether lanes and card pages
// changed since the last complete snapshot, and each card's stamp whether the open editor's card did.

const tick = () => new Promise(resolve => setImmediate(resolve));

function harness(t, editor = null) {
    const original = globalThis.document;
    globalThis.document = { querySelector: () => editor };
    t.after(() => { globalThis.document = original; });
    const requests = [];
    const controller = new BoardController({
        currentView: 'board',
        isUiHidden: () => false,
        apiCall(url, method, body, options) {
            return new Promise((resolve, reject) => requests.push({ url, method, body, options, resolve, reject }));
        }
    });
    controller.root = { isConnected: true, querySelectorAll: () => [] };
    controller.renderAll = () => {};
    controller.state.boardId = 'A';
    controller.state.boards = [{ id: 'A' }];
    controller.state.columns = [{ id: 'lane' }];
    controller.state.cards = [{ id: 'card', columnId: 'lane' }];
    controller._cardPageGeneration = controller._refreshGeneration;
    return { controller, requests };
}

const isActivity = request => request.url === '/api/v1/board/cards/activity';

// One poll round: answer the activity read with `token`, then the snapshot if one is requested.
async function round(h, changeToken) {
    const before = h.requests.length;
    const done = h.controller.refreshVisibleBoard();
    assert.ok(isActivity(h.requests[before]), 'every round starts with the activity read');
    assert.equal(h.requests.length, before + 1, 'nothing else is requested before the token is known');
    h.requests[before].resolve({ cards: [], changeToken });
    await tick();
    const snapshot = h.requests.slice(before + 1);
    for (const request of snapshot) {
        request.resolve(request.url.includes('/boards') ? { boards: [{ id: 'A' }] }
            : request.url.includes('/columns') ? { columns: [{ id: 'lane' }] }
            : { cards: [{ id: 'card', columnId: 'lane' }], lanes: [] });
    }
    await done;
    return snapshot.length > 0;
}

test('an unchanged change token skips the lane and card snapshot', async t => {
    const h = harness(t);
    assert.equal(await round(h, 't1'), true, 'the first round has nothing to compare');
    assert.equal(await round(h, 't1'), false);
    assert.equal(await round(h, 't1'), false);
    assert.equal(await round(h, 't2'), true, 'a moved token rereads the snapshot');
    assert.equal(await round(h, 't2'), false);
});

test('the manager chip is polled every round, with or without a snapshot', async t => {
    const h = harness(t);
    let polls = 0;
    h.controller.managerChip = { poll: async () => { polls++; } };
    await round(h, 't1');
    await round(h, 't1');
    await round(h, 't2');
    assert.equal(polls, 3);
});

test('the snapshot is still reread once a minute, without a token, and after a refresh or board switch', async t => {
    const h = harness(t);
    await round(h, 't1');
    h.controller._boardSnapshotSeen.at -= 60000;
    assert.equal(await round(h, 't1'), true, 'a minute-old snapshot is reread even with the same token');
    assert.equal(await round(h, null), true, 'a server that cannot tell gets the old behavior');
    await round(h, 't1');
    h.controller._refreshGeneration++;
    h.controller._cardPageGeneration = h.controller._refreshGeneration;
    assert.equal(await round(h, 't1'), true, 'a full refresh replaced the state the token was recorded for');
    h.controller.state.boardId = 'B';
    h.controller.state.boards = [{ id: 'A' }, { id: 'B' }];
    const before = h.requests.length;
    const done = h.controller.refreshVisibleBoard();
    h.requests[before].resolve({ cards: [], changeToken: 't1' });
    await tick();
    assert.ok(h.requests.length > before + 1, 'another board is never served from this board\'s snapshot');
    for (const request of h.requests.slice(before + 1)) {
        request.resolve(request.url.includes('/boards') ? { boards: h.controller.state.boards }
            : request.url.includes('/columns') ? { columns: [] } : { cards: [], lanes: [] });
    }
    await done;
    assert.equal(h.controller._boardSnapshotSeen.boardId, 'B');
});

test('an incomplete snapshot records no token', async t => {
    const h = harness(t);
    const done = h.controller.refreshVisibleBoard();
    h.requests[0].resolve({ cards: [], changeToken: 't1' });
    await tick();
    h.controller._boardDragging = true;
    for (const request of h.requests.slice(1)) request.resolve(request.url.includes('/boards') ? { boards: [{ id: 'A' }] } : {});
    await done;
    h.controller._boardDragging = false;
    assert.equal(h.controller._boardSnapshotSeen, undefined);
    assert.equal(await round(h, 't1'), true);
});

function openEditor() {
    return { dataset: { cardId: 'card' }, isConnected: true,
        _boardCard: { id: 'card', comments: [], notes: [], attachments: [], commits: [], sessions: [] },
        querySelector: () => null, querySelectorAll: () => [] };
}

function editorHarness(t) {
    const editor = openEditor();
    const h = harness(t, editor);
    const counts = { automations: 0, reviews: 0, sessions: 0 };
    h.controller.cardAutomations = { refresh: () => counts.automations++ };
    h.controller.cardReviews = { refresh: () => counts.reviews++ };
    h.controller.renderSessionsPanel = () => counts.sessions++;
    return { ...h, editor, counts };
}

const isDetail = request => request.url === '/api/v1/board/cards/card';

// One activity refresh with the editor open: answer the activity read with the card's stamp, then
// the card read if one is requested. Returns whether the card was read and whether it was in parallel.
async function editorRound(h, stamp, options, activity = {}) {
    const before = h.requests.length;
    const done = h.controller.refreshSessionActivity(options);
    const parallel = h.requests.slice(before).some(isDetail);
    h.requests[before].resolve({ cards: [{ id: 'card', stamp, ...activity }], changeToken: 't' });
    await tick();
    const detail = h.requests.slice(before).find(isDetail);
    detail?.resolve(structuredClone(h.editor._boardCard));
    await done;
    return { read: Boolean(detail), parallel };
}

test('the open card is reread only when its stamp moves', async t => {
    const h = editorHarness(t);
    assert.deepEqual(await editorRound(h, 's1'), { read: true, parallel: true }, 'a new editor reads as before');
    assert.deepEqual(await editorRound(h, 's1'), { read: true, parallel: false }, 'the next round records the stamp');
    assert.deepEqual(await editorRound(h, 's1'), { read: false, parallel: false });
    assert.deepEqual(await editorRound(h, 's1'), { read: false, parallel: false });
    assert.equal(h.counts.automations, 2, 'an unchanged card with nothing running leaves its rails alone');
    assert.equal(h.counts.reviews, 2);
    assert.deepEqual(await editorRound(h, 's2'), { read: true, parallel: false });
    assert.deepEqual(await editorRound(h, 's2'), { read: false, parallel: false });
    h.editor._boardDetailSeen.at -= 60000;
    assert.deepEqual(await editorRound(h, 's2'), { read: true, parallel: false }, 'a minute-old card is reread');
    assert.deepEqual(await editorRound(h, null), { read: true, parallel: false }, 'no stamp, no skip');
});

// VIBE-327: the rails read launches.db and lane events, which the stamp does not cover.
test('an unchanged card refreshes its run and review rails while a run is live, and otherwise once a minute', async t => {
    const h = editorHarness(t);
    await editorRound(h, 's1');
    await editorRound(h, 's1');
    const rails = () => [h.counts.automations, h.counts.reviews];
    assert.deepEqual(rails(), [2, 2]);
    await editorRound(h, 's1');
    assert.deepEqual(rails(), [2, 2]);
    await editorRound(h, 's1', undefined, { hasActiveAutomation: true });
    await editorRound(h, 's1', undefined, { hasActiveAutomation: true });
    assert.deepEqual(rails(), [4, 4], 'a running Automation refreshes them every round');
    await editorRound(h, 's1', undefined, { hasWaitingAutomation: true });
    assert.deepEqual(rails(), [5, 5], 'so does a lane step waiting for its run');
    await editorRound(h, 's1');
    assert.deepEqual(rails(), [5, 5]);
    h.editor._boardRailsAt -= 60000;
    await editorRound(h, 's1');
    assert.deepEqual(rails(), [6, 6], 'minute-old rails are read again');
    await editorRound(h, 's1');
    assert.deepEqual(rails(), [6, 6]);
});

test('a session event rereads the open card at once', async t => {
    const h = editorHarness(t);
    await editorRound(h, 's1');
    await editorRound(h, 's1');
    assert.deepEqual(await editorRound(h, 's1', { forceDetail: true }), { read: true, parallel: true });
});

test('a card that changed while its read was blocked is not recorded as seen', async t => {
    const h = editorHarness(t);
    await editorRound(h, 's1');
    await editorRound(h, 's1');
    const before = h.requests.length;
    const done = h.controller.refreshSessionActivity();
    h.requests[before].resolve({ cards: [{ id: 'card', stamp: 's2' }], changeToken: 't' });
    await tick();
    h.editor._boardSaving = true;
    h.requests.at(-1).resolve(structuredClone(h.editor._boardCard));
    await done;
    h.editor._boardSaving = false;
    assert.equal(h.editor._boardDetailSeen.stamp, 's1');
    assert.equal((await editorRound(h, 's2')).read, true, 'the skipped update is fetched again');
});

test('the editor card is part of the activity read even when its tile is not loaded', async t => {
    const h = editorHarness(t);
    h.controller.state.cards = [{ id: 'other' }];
    const done = h.controller.refreshSessionActivity();
    assert.deepEqual(h.requests[0].body.cardIds, ['other', 'card']);
    h.requests[0].resolve({ cards: [] });
    h.requests[1].resolve(structuredClone(h.editor._boardCard));
    await done;
});
