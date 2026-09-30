import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

function harness() {
    const requests = [];
    const controller = new BoardController({
        currentView: 'board',
        apiCall(url, method, body, options) { return new Promise(resolve => requests.push({ url, method, body, options, resolve })); }
    });
    controller.root = { isConnected: true, querySelectorAll: () => [] };
    controller.state.boardId = 'board';
    controller.state.cards = [{ id: 'card', title: 'Loaded card', activeTabId: null }];
    return { controller, requests };
}

test('the running robot opens its Automation, and never opens the card or the working agent', async () => {
    const { controller, requests } = harness();
    const focused = [];
    controller.focusSessionTab = async (card, session) => focused.push(session.id);
    controller.openCardEditor = () => assert.fail('robot click must not open the editor');
    const trigger = { dataset: { boardAction: 'go-to-automation' } };
    const tile = { dataset: { cardId: 'card' } };
    controller.onClick({
        target: { closest: selector => selector === '[data-board-action]' ? trigger : selector === '.board-card' ? tile : null },
        stopPropagation() {}
    });
    assert.equal(requests.length, 1);
    requests[0].resolve({ id: 'card', sessions: [
        { id: 'working', tabId: 'working-tab', active: true },
        { id: 'old-review', tabId: 'old', active: false, isAutomation: true },
        { id: 'review', tabId: 'review-tab', active: true, isAutomation: true }
    ] });
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(focused, ['review']);
    assert.match(controller.automationIndicator(), /<button.*data-board-action="go-to-automation"/);
});

test('a late robot lookup cannot navigate after switching boards', async () => {
    const { controller, requests } = harness();
    controller.focusSessionTab = () => assert.fail('stale response navigated');
    const pending = controller.goToCardAutomation('card');
    controller.state.boardId = 'other';
    requests[0].resolve({ sessions: [{ id: 'review', tabId: 'tab', active: true, isAutomation: true }] });
    await pending;
});

test('a finished Automation reports its recording location without reopening the card', async () => {
    const { controller, requests } = harness();
    const messages = [];
    controller.app.showToast = (...args) => messages.push(args.join(' '));
    controller.focusSessionTab = () => assert.fail('finished session was focused');
    const pending = controller.goToCardAutomation('card');
    requests[0].resolve({ sessions: [] });
    await pending;
    assert.match(messages[0], /finished.*recording/);
});

test('activity refresh preserves loaded pages and ignores results after a board switch or unload', async () => {
    const original = globalThis.document;
    globalThis.document = { querySelector: () => null };
    try {
        for (const leave of [c => { c.state.boardId = 'other'; }, c => c.disposeSessionActivity()]) {
            const { controller, requests } = harness();
            const pending = controller.refreshSessionActivity();
            leave(controller);
            requests[0].resolve({ cards: [{ id: 'card', activeTabId: 'late', hasActiveAutomation: true }] });
            await pending;
            assert.equal(controller.state.cards[0].activeTabId, null);
        }
        const { controller, requests } = harness();
        controller.state.cards.push({ id: 'loaded-done-card' });
        const pending = controller.refreshSessionActivity();
        await controller.refreshSessionActivity();
        assert.equal(requests.length, 1, 'polls do not overlap');
        assert.equal(requests[0].url, '/api/v1/board/cards/activity');
        assert.equal(requests[0].method, 'POST');
        assert.deepEqual(requests[0].body, { boardId: 'board', cardIds: ['card', 'loaded-done-card'] });
        requests[0].resolve({ cards: [{ id: 'card', key: 'VB-1', activeTabId: 'running', hasActiveAutomation: true }] });
        await pending;
        assert.equal(controller.state.cards.length, 2);
        assert.equal(controller.state.cards[0].title, 'Loaded card');
        assert.equal(controller.state.cards[0].hasActiveAutomation, true);
    } finally { globalThis.document = original; }
});

test('activity refresh batches only loaded IDs and stops batching after navigation', async () => {
    const original = globalThis.document;
    globalThis.document = { querySelector: () => null };
    try {
        for (const navigate of [false, true]) {
            const { controller, requests } = harness();
            controller.state.cards = Array.from({ length: 205 }, (_, i) => ({ id: `card-${i}` }));
            const pending = controller.refreshSessionActivity();
            assert.equal(requests.length, 1);
            assert.equal(requests[0].body.cardIds.length, 100);
            if (navigate) controller.state.boardId = 'other';
            requests[0].resolve({ cards: [] });
            await new Promise(resolve => setImmediate(resolve));
            if (!navigate) {
                assert.equal(requests.length, 2);
                assert.equal(requests[1].body.cardIds.length, 100);
                requests[1].resolve({ cards: [] });
                await new Promise(resolve => setImmediate(resolve));
                assert.equal(requests.length, 3);
                assert.deepEqual(requests[2].body.cardIds,
                    ['card-200', 'card-201', 'card-202', 'card-203', 'card-204']);
                requests[2].resolve({ cards: [] });
            } else assert.equal(requests.length, 1);
            await pending;
            assert.equal(controller.state.cards.length, 205);
        }
    } finally { globalThis.document = original; }
});

test('an activity response cannot update a replacement card editor', async () => {
    const original = globalThis.document;
    let editor = { dataset: { cardId: 'card' }, isConnected: true };
    globalThis.document = { querySelector: () => editor };
    try {
        const { controller, requests } = harness();
        controller.renderSessionsPanel = () => assert.fail('the old response must not touch a new editor');
        const pending = controller.refreshSessionActivity();
        editor = { dataset: { cardId: 'card' }, isConnected: true };
        requests[0].resolve({ cards: [] });
        requests[1].resolve({ id: 'card', sessions: [] });
        await pending;
    } finally { globalThis.document = original; }
});
