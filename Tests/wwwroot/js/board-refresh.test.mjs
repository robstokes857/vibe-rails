import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

const boards = [{ id: 'A', position: 0 }, { id: 'B', position: 1 }];
const tick = () => new Promise(resolve => setImmediate(resolve));

function harness() {
    const requests = [], toasts = [];
    const controller = new BoardController({
        currentView: 'board',
        apiCall(url) {
            return new Promise((resolve, reject) => requests.push({ url, resolve, reject }));
        },
        showToast(...args) { toasts.push(args); }
    });
    let busy = false;
    controller.root = { isConnected: true };
    controller.renderAll = () => {};
    controller.setBusy = value => { busy = value; };
    controller.state.boards = boards;
    controller.state.boardId = 'A';
    controller.state.columns = [{ id: 'A-old-lane' }];
    controller.state.cards = [{ id: 'A-old-card' }];
    controller._loadedBoardId = 'A';
    return { controller, requests, toasts, get busy() { return busy; } };
}

async function loadLists(h, catalogRequest) {
    catalogRequest.resolve({ boards });
    await tick();
    return h.requests.slice(-2);
}

function finishLists(requests, board, suffix = '') {
    for (const request of requests) {
        assert.ok(request.url.endsWith(`boardId=${board}`));
        request.resolve(request.url.includes('/columns?')
            ? { columns: [{ id: `${board}-lane${suffix}`, position: 0 }] }
            : { cards: [{ id: `${board}-card${suffix}`, key: 'VB-1', position: 0 }] });
    }
}

test('a slower previous board cannot overwrite the selection or its actionable lanes', async () => {
    const h = harness();
    const first = h.controller.refresh();
    const firstLists = await loadLists(h, h.requests[0]);
    const second = h.controller.switchBoard('B');
    assert.deepEqual(h.controller.state.columns, []);
    assert.deepEqual(h.controller.state.cards, []);
    const secondLists = await loadLists(h, h.requests.at(-1));
    finishLists(secondLists, 'B');
    await second;
    finishLists(firstLists, 'A');
    await first;
    assert.equal(h.controller.state.boardId, 'B');
    assert.equal(h.controller.state.columns[0].id, 'B-lane');
    assert.equal(h.controller.state.cards[0].id, 'B-card');
    assert.equal(h.busy, false);
});

test('a stale catalog cannot replace newer metadata or start another board fetch', async () => {
    const h = harness();
    const first = h.controller.refresh();
    const second = h.controller.switchBoard('B');
    finishLists(await loadLists(h, h.requests[1]), 'B');
    await second;
    const count = h.requests.length;
    h.requests[0].resolve({ boards: [{ id: 'A', name: 'obsolete' }] });
    await first;
    assert.equal(h.requests.length, count);
    assert.equal(h.controller.state.boardId, 'B');
    assert.deepEqual(h.controller.state.boards, boards);
});

test('same-board refreshes accept only the latest response', async () => {
    const h = harness();
    const first = h.controller.refresh();
    const firstLists = await loadLists(h, h.requests[0]);
    const second = h.controller.refresh();
    const secondLists = await loadLists(h, h.requests.at(-1));
    finishLists(secondLists, 'A', '-new');
    await second;
    finishLists(firstLists, 'A', '-old');
    await first;
    assert.equal(h.controller.state.cards[0].id, 'A-card-new');
});

test('stale failures do not show a toast or clear the current loading state', async () => {
    const h = harness();
    const first = h.controller.refresh();
    const second = h.controller.switchBoard('B');
    h.requests[0].reject(new Error('obsolete failure'));
    await first;
    assert.deepEqual(h.toasts, []);
    assert.equal(h.busy, true);
    finishLists(await loadLists(h, h.requests[1]), 'B');
    await second;
    assert.equal(h.busy, false);
});

test('a response for an earlier mounted view cannot paint a replacement view', async () => {
    const h = harness();
    const first = h.controller.refresh();
    const lists = await loadLists(h, h.requests[0]);
    h.controller.root = { isConnected: true };
    h.controller.state.cards = [{ id: 'replacement' }];
    finishLists(lists, 'A');
    await first;
    assert.equal(h.controller.state.cards[0].id, 'replacement');
});

function jiraToolbar(h) {
    const button = { hidden: true };
    const status = { hidden: true, textContent: '' };
    h.controller.root = {
        isConnected: true,
        querySelector: selector => selector === '[data-board-action="jira-pull"]' ? button
            : selector === '[data-jira-pull-status]' ? status : null
    };
    return { button, status };
}

function pollHarness() {
    const h = harness();
    h.controller._cardPageGeneration = h.controller._refreshGeneration;
    h.controller.cardPage = { lanes: [{ columnId: 'done', nextOffset: 60, hasMore: true }] };
    h.firstPage = () => {
        h.requests[0].resolve({ boards });
        h.requests[1].resolve({ columns: [{ id: 'review' }, { id: 'done' }] });
        h.requests[2].resolve({ cards: [{ id: 'moved', columnId: 'review' }, { id: 'done-first', columnId: 'done' }],
            lanes: [{ columnId: 'done', nextOffset: 30, hasMore: true, continuationToken: 'fresh-order' }] });
    };
    return h;
}

test('background refresh replaces moved/deleted cards and preserves the number of loaded pages', async () => {
    const h = pollHarness();
    const pending = h.controller.refreshBoardSnapshot();
    h.firstPage();
    await tick();
    const continuation = h.requests[3];
    assert.match(continuation.url, /offset=30&continuationToken=fresh-order/);
    continuation.resolve({ cards: [{ id: 'done-second', columnId: 'done' }],
        lanes: [{ columnId: 'done', nextOffset: 60, hasMore: true, continuationToken: 'fresh-order' }] });
    await pending;
    assert.deepEqual(h.controller.state.cards.map(c => c.id), ['moved', 'done-first', 'done-second']);
    assert.equal(h.controller.cardPage.lanes[0].nextOffset, 60);
    assert.equal(h.requests.length, 4, 'unloaded history is not fetched');
    assert.equal(h.busy, false, 'a background refresh does not cover the board with a loading state');
});

test('background snapshots are discarded after navigation, paging, dragging or filter changes', async () => {
    for (const invalidate of [c => { c.state.boardId = 'B'; }, c => c.disposeSessionActivity(),
        c => { c._refreshGeneration++; }, c => { c._boardDragging = true; },
        c => { c._pageRequests.set('done', new AbortController()); }]) {
        const h = pollHarness();
        const pending = h.controller.refreshBoardSnapshot();
        invalidate(h.controller);
        h.firstPage();
        await pending;
        assert.equal(h.controller.state.cards[0].id, 'A-old-card');
        assert.equal(h.requests.length, 3);
    }
});

test('an ordering change during a background continuation keeps the last complete snapshot', async () => {
    const h = pollHarness();
    const pending = h.controller.refreshBoardSnapshot();
    h.firstPage();
    await tick();
    h.requests[3].resolve({ cards: [], lanes: [{ columnId: 'done', restartRequired: true }] });
    await pending;
    assert.equal(h.controller.state.cards[0].id, 'A-old-card');
    assert.equal(h.controller.cardPage.lanes[0].nextOffset, 60);
});

test('a Jira toolbar answer for the previous board cannot update the next board', async () => {
    const h = harness();
    const { button, status } = jiraToolbar(h);
    const stale = h.controller.refreshJiraPullButton();
    const request = h.requests.at(-1);
    assert.match(request.url, /\/boards\/A\/jira$/);
    h.controller.state.boardId = 'B';
    h.controller._refreshGeneration++;
    request.resolve({ hasToken: true, jql: 'project = A', lastReport: 'ok: board A' });
    await stale;
    assert.equal(button.hidden, true);
    assert.equal(status.textContent, '');
});

test('the current board shows its Jira pull button and last report', async () => {
    const h = harness();
    const { button, status } = jiraToolbar(h);
    const pending = h.controller.refreshJiraPullButton();
    h.requests.at(-1).resolve({ hasToken: true, jql: 'project = A', lastReport: 'ok: 1 created' });
    await pending;
    assert.equal(button.hidden, false);
    assert.equal(status.hidden, false);
    assert.equal(status.textContent, 'ok: 1 created');
});

test('a connection made from a Jira board link shows the pull button before Connect has read its filter', async () => {
    const h = harness();
    const { button } = jiraToolbar(h);
    const pending = h.controller.refreshJiraPullButton();
    h.requests.at(-1).resolve({ hasToken: true, jql: '', jiraBoardId: '7', lastReport: null });
    await pending;
    assert.equal(button.hidden, false);
});

for (const staleFails of [false, true]) {
    test(`an older Jira status ${staleFails ? 'failure' : 'response'} cannot overwrite the post-save toolbar`, async () => {
        const h = harness();
        const { button, status } = jiraToolbar(h);
        const initial = h.controller.refreshJiraPullButton();
        const initialRequest = h.requests.at(-1);
        const afterSave = h.controller.refreshJiraPullButton();
        h.requests.at(-1).resolve({ hasToken: true, jql: 'project = A', lastReport: 'fresh status' });
        await afterSave;
        if (staleFails) initialRequest.reject(new Error('old request failed'));
        else initialRequest.resolve({ hasToken: false, jql: '', lastReport: 'old status' });
        await initial;
        assert.equal(button.hidden, false);
        assert.equal(status.hidden, false);
        assert.equal(status.textContent, 'fresh status');
    });
}

test('the Jira toolbar refresh never rejects, even without a mounted toolbar', async () => {
    const h = harness();
    await h.controller.refreshJiraPullButton();
    const { button } = jiraToolbar(h);
    const pending = h.controller.refreshJiraPullButton();
    h.requests.at(-1).reject(new Error('offline'));
    await pending;
    assert.equal(button.hidden, true);
});
