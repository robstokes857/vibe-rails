import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

function harness() {
    const requests = [], toasts = [];
    const controller = new BoardController({
        currentView: 'board',
        apiCall(url, method, body, options) {
            return new Promise((resolve, reject) => requests.push({ url, options, resolve, reject }));
        },
        showToast(...args) { toasts.push(args); }
    });
    controller.root = { isConnected: true };
    controller.queryAll = () => [];
    controller.renderLanes = () => {};
    controller.state.boardId = 'main';
    controller.state.cards = [{ id: 'a', columnId: 'done' }];
    controller.cardPage = {
        lanes: [{ columnId: 'done', totalCount: 120, filteredCount: 120, nextOffset: 30, hasMore: true,
            continuationToken: 'order-a' }],
        filteredCount: 120, blockedCount: 2, remainingPoints: 8, flaggedCount: 3,
        tags: ['tag-on-unloaded-card'], assignees: []
    };
    controller._cardPageGeneration = controller._refreshGeneration;
    return { controller, requests, toasts };
}

test('each lane renders flagged cards first while preserving order within each group', () => {
    const { controller } = harness();
    const host = { innerHTML: '' };
    controller.state.columns = ['open', 'done'].map((id, position) => ({ id, position, name: id, color: '#123456' }));
    controller.state.cards = controller.state.columns.flatMap(({ id }) => [
        { id: `${id}-plain-first`, columnId: id, position: 0 },
        { id: `${id}-flag-second`, columnId: id, position: 8, flagged: true },
        { id: `${id}-plain-second`, columnId: id, position: 1, flagged: false },
        { id: `${id}-flag-first`, columnId: id, position: 3, flagged: true }
    ]);
    controller.query = selector => selector === '[data-board-lanes]' ? host : null;
    controller.captureScroll = () => ({});
    controller.restoreScroll = () => {};
    controller.bindDragAndDrop = () => {};
    controller.bindLaneInfiniteScroll = () => {};
    controller.laneAgents = { button: () => '', mount() {}, updateActivity() {} };
    const rendered = [];
    controller.renderCard = card => { rendered.push(card.id); return card.id; };
    BoardController.prototype.renderLanes.call(controller);
    assert.deepEqual(rendered, ['open', 'done'].flatMap(id =>
        [`${id}-flag-first`, `${id}-flag-second`, `${id}-plain-first`, `${id}-plain-second`]));
});

test('scroll requests one page at a time, deduplicates activity, and advances server offset', async () => {
    const { controller, requests } = harness();
    const pending = controller.loadMoreCards('done');
    await controller.loadMoreCards('done');
    assert.equal(requests.length, 1);
    const query = new URL(requests[0].url, 'http://local').searchParams;
    assert.equal(query.get('pageSize'), '30');
    assert.equal(query.get('columnId'), 'done');
    assert.equal(query.get('offset'), '30');
    assert.equal(query.get('continuationToken'), 'order-a');
    requests[0].resolve({ cards: [{ id: 'a', title: 'updated' }, { id: 'b' }],
        lanes: [{ columnId: 'done', nextOffset: 60, hasMore: false, continuationToken: 'order-a' }] });
    await pending;
    assert.deepEqual(controller.state.cards.map(card => card.id), ['a', 'b']);
    assert.equal(controller.state.cards[0].title, 'updated');
    assert.equal(controller.cardPage.lanes[0].nextOffset, 60);
    await controller.loadMoreCards('done');
    assert.equal(requests.length, 1);
});

test('an ordering change restarts paging instead of hiding a promoted card behind the offset', async () => {
    const { controller, requests } = harness();
    let refreshes = 0;
    controller.refresh = async () => { refreshes += 1; };
    const pending = controller.loadMoreCards('done');
    requests[0].resolve({ cards: [{ id: 'promoted' }], lanes: [{ columnId: 'done', nextOffset: 30,
        hasMore: true, continuationToken: 'order-b', restartRequired: true }] });
    await pending;
    assert.equal(refreshes, 1);
    assert.deepEqual(controller.state.cards.map(card => card.id), ['a']);
});

test('a stale page cannot append to another board or a refreshed filter result', async () => {
    for (const invalidate of [c => { c.state.boardId = 'other'; }, c => { c._refreshGeneration++; },
        c => { c.root = { isConnected: true }; }, c => c.cancelPageRequests()]) {
        const { controller, requests } = harness();
        const pending = controller.loadMoreCards('done');
        invalidate(controller);
        requests[0].resolve({ cards: [{ id: 'stale' }] });
        await pending;
        assert.deepEqual(controller.state.cards.map(card => card.id), ['a']);
    }
});

test('failed pages can be retried at the same offset', async () => {
    const { controller, requests, toasts } = harness();
    const first = controller.loadMoreCards('done');
    requests[0].reject(new Error('Temporary error'));
    await first;
    assert.equal(toasts.length, 1);
    const retry = controller.loadMoreCards('done');
    assert.equal(requests[1].url, requests[0].url);
    requests[1].resolve({ cards: [], lanes: [{ columnId: 'done', hasMore: false }] });
    await retry;
});

test('scroll cannot combine a new filter with the preceding page offset during debounce', async () => {
    const { controller, requests } = harness();
    controller.state.filters.q = 'new filter';
    controller.filtersChanged(true);
    clearTimeout(controller._filterTimer);
    await controller.loadMoreCards('done');
    assert.equal(requests.length, 0, 'wait for the first page of the new filter before loading more');
});

test('scroll cannot start an old-offset request while a full refresh is pending', async () => {
    const { controller, requests } = harness();
    controller.setBusy = () => {};
    controller.renderAll = () => {};
    controller.persistBoardSelection = () => {};
    controller._loadedBoardId = 'main';
    const refresh = controller.refresh();
    assert.equal(requests.length, 1, 'board catalog request is pending');
    await controller.loadMoreCards('done');
    assert.equal(requests.length, 1, 'no lane request may use the preceding full page');
    requests[0].resolve({ boards: [{ id: 'main' }] });
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    const columns = requests.find(request => request.url.includes('/columns'));
    const firstPage = requests.find(request => request.url.includes('/cards?'));
    assert.ok(columns && firstPage);
    columns.resolve({ columns: [{ id: 'done' }] });
    firstPage.resolve({ cards: [{ id: 'fresh' }], lanes: [{ columnId: 'done', nextOffset: 30, hasMore: true }] });
    await refresh;
    const more = controller.loadMoreCards('done');
    assert.equal(requests.length, 4, 'the newly installed first page may now load its continuation');
    requests[3].resolve({ cards: [{ id: 'next' }], lanes: [{ columnId: 'done', nextOffset: 31, hasMore: false }] });
    await more;
    assert.deepEqual(controller.state.cards.map(card => card.id), ['fresh', 'next']);
});

test('the header shows a labeled flagged count for open lanes, including unloaded cards', () => {
    const { controller } = harness();
    const stats = { innerHTML: '' };
    controller.query = selector => selector === '[data-board-stats]' ? stats : null;
    controller.renderToolbar();
    assert.match(stats.innerHTML, /<strong>3<\/strong> flagged/);
    assert.match(stats.innerHTML, /is-flagged/);
    assert.match(stats.innerHTML, /Cards that need your attention/);
    assert.doesNotMatch(stats.innerHTML, /pts/);

    controller.cardPage = null;
    controller.state.columns = [
        { id: 'open', name: 'In Progress' },
        { id: 'done', name: 'Done' }
    ];
    controller.state.cards = [
        { id: 'open-flag', columnId: 'open', flagged: true, blocked: false, points: 5 },
        { id: 'done-flag', columnId: 'done', flagged: true, blocked: false, points: 8 },
        { id: 'open-plain', columnId: 'open', flagged: false, blocked: true, points: 2 }
    ];
    // Same scope as the points total it replaced: the flag on the Done card is not counted.
    assert.deepEqual(controller.stats(), { cards: 3, blocked: 1, flagged: 1 });
    controller.renderToolbar();
    assert.match(stats.innerHTML, /<strong>1<\/strong> flagged/);
});

test('counts, filter options, and search results include unloaded cards', async () => {
    const { controller, requests } = harness();
    controller.state.filters.q = 'deep history';
    assert.deepEqual(controller.stats(), { cards: 120, blocked: 2, flagged: 3 });
    assert.equal(controller.filteredCards().length, 1, 'server-filtered results are not filtered a second time');
    const pending = controller.loadMoreCards('done');
    assert.equal(new URL(requests[0].url, 'http://local').searchParams.get('q'), 'deep history');
    requests[0].resolve({ cards: [] });
    await pending;
});
