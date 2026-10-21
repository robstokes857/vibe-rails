import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { boardTextOptions } from '../../../VibeRails/wwwroot/js/modules/board-composer-preview.js';
import { renderCommentHtml } from '../../../VibeRails/wwwroot/js/modules/board-text.js';
import { renderCodeLinks } from '../../../VibeRails/wwwroot/js/modules/board-code-links.js';

function harness() {
    const requests = [];
    const controller = new BoardController({
        currentView: 'board',
        isUiHidden: () => false,
        apiCall(url, method, body, options) { return new Promise(resolve => requests.push({ url, method, body, options, resolve })); }
    });
    controller.root = { isConnected: true, querySelectorAll: () => [] };
    controller.state.boardId = 'board';
    controller.state.cards = [{ id: 'card', title: 'Loaded card', activeTabId: null }];
    return { controller, requests };
}

function openEditor(t) {
    const original = globalThis.document;
    const editor = { dataset: { cardId: 'card' }, isConnected: true,
        _boardCard: { id: 'card', comments: [], notes: [], attachments: [], commits: [], sessions: [] },
        querySelector: () => null, querySelectorAll: () => [] };
    globalThis.document = { querySelector: () => editor };
    t.after(() => { globalThis.document = original; });
    return editor;
}

test('a card over the terminal refreshes linked sessions without loading Board activity', async t => {
    const editor = openEditor(t);
    const { controller, requests } = harness();
    controller.root = null;
    controller.app.currentView = 'terminal-focus';
    const rendered = [];
    controller.renderSessionsPanel = (_, card) => rendered.push(card.sessions.map(session => session.id));
    const poll = controller.refreshSessionActivity();
    assert.equal(requests.length, 1);
    assert.equal(requests[0].url, '/api/v1/board/cards/card');
    requests[0].resolve({ ...editor._boardCard,
        sessions: [{ id: 'discussion', tabId: 'tab', active: true, origin: 'chat' }] });
    await poll;
    assert.deepEqual(rendered, [['discussion']]);
    assert.equal(controller.hasRunningSession(editor._boardCard), false, 'a discussion does not become a work launch');
});

test('closing a card overlay aborts activity reads and ignores late sessions', async t => {
    const editor = openEditor(t);
    const { controller, requests } = harness();
    controller.root = null;
    controller.app.currentView = 'terminal-focus';
    controller.renderSessionsPanel = () => assert.fail('a closed overlay must not update');
    const poll = controller.refreshSessionActivity();
    controller.disposeSessionActivity();
    editor.isConnected = false;
    assert.equal(requests[0].options.signal.aborted, true);
    requests[0].resolve({ ...editor._boardCard, sessions: [{ id: 'late', active: true }] });
    await poll;
    assert.deepEqual(editor._boardCard.sessions, []);
});

test('a delayed activity read cannot hide a successfully posted comment', async t => {
    const editor = openEditor(t);
    const { controller, requests } = harness();
    const rendered = [];
    controller.renderCardDiscussion = (_, card) => rendered.push(card.comments.map(c => c.id));
    const older = structuredClone(editor._boardCard);
    const poll = controller.refreshSessionActivity();
    const post = controller.postComment(editor, 'Posted successfully');
    assert.equal(requests[2].method, 'POST');
    requests[2].resolve({});
    await new Promise(resolve => setImmediate(resolve));
    const comment = { id: 'new-comment', body: 'Posted successfully' };
    requests[3].resolve({ ...older, comments: [comment] });
    await post;
    assert.deepEqual(rendered, [['new-comment']]);
    requests[0].resolve({ cards: [] });
    requests[1].resolve(older);
    await poll;
    assert.deepEqual(editor._boardCard.comments, [comment]);
    assert.deepEqual(rendered, [['new-comment']], 'older poll never repaints the discussion');
});

test('activity adopts reference metadata with comments while retaining pending attachments and draft fields', async t => {
    const editor = openEditor(t);
    const pendingFiles = [{ id: 'pending-image', name: 'unsaved.png' }];
    editor._boardCard.pendingAttachments = pendingFiles;
    editor._boardCard.description = 'Original description';
    const { controller, requests } = harness();
    let html = '', attachments = 0, commits = 0;
    controller.renderAttachmentsPanel = () => attachments++;
    controller.renderCommitsPanel = () => commits++;
    controller.renderCardDiscussion = (_, card) => { html = renderCommentHtml(card.comments[0].body, boardTextOptions(card)); };
    const poll = controller.refreshSessionActivity();
    requests[0].resolve({ cards: [] });
    requests[1].resolve({ ...editor._boardCard, description: 'Remote description',
        comments: [{ id: 'remote', body: '![Screenshot](attachment:new-image) #abcdef0' }],
        attachments: [{ id: 'new-image', name: 'Screenshot.png', mimeType: 'image/png' }],
        commits: [{ sha: 'abcdef0123456', message: 'Remote commit' }] });
    await poll;
    assert.match(html, /<img[^>]+data-board-image="new-image"/);
    assert.match(html, /title="Remote commit"/);
    assert.equal(attachments, 1);
    assert.equal(commits, 1);
    assert.equal(editor._boardCard.pendingAttachments, pendingFiles);
    assert.equal(editor._boardCard.description, 'Original description');
});

test('a delayed poll cannot undo an upload that mutated the attachment array in place', async t => {
    const editor = openEditor(t);
    const { controller, requests } = harness();
    const older = structuredClone(editor._boardCard);
    const poll = controller.refreshSessionActivity();
    editor._boardCard.attachments.push({ id: 'local-upload' });
    requests[0].resolve({ cards: [] });
    requests[1].resolve(older);
    await poll;
    assert.equal(editor._boardCard.attachments[0].id, 'local-upload');
});

test('activity does not replace GitHub link controls while a link write is pending', async t => {
    const editor = openEditor(t);
    editor._boardCard.codeLinks = [];
    const { controller, requests } = harness();
    controller.renderCardDiscussion = () => assert.fail('pending link writes keep their controls');
    const poll = controller.refreshSessionActivity();
    editor._boardCodeLinkBusy = true;
    requests[0].resolve({ cards: [] });
    requests[1].resolve({ ...editor._boardCard, codeLinks: [{ id: 'old-link' }] });
    await poll;
    assert.deepEqual(editor._boardCard.codeLinks, []);
});

for (const removing of [false, true]) {
    test(`comment reload renders remotely ${removing ? 'removed' : 'added'} GitHub links before later polls`, async t => {
        const editor = openEditor(t);
        const list = {}, count = {};
        editor.querySelector = selector => selector === '[data-board-code-link-list]' ? list
            : selector === '[data-board-count="code-links"]' ? count : null;
        const link = { id: 'github-link', url: 'https://github.com/org/repo/pull/42', label: 'org/repo PR #42' };
        editor._boardCard.codeLinks = removing ? [link] : [];
        editor._boardCard.description = 'Unsaved description';
        renderCodeLinks(editor, editor._boardCard);
        const { controller, requests } = harness();
        controller.renderCardDiscussion = () => {};
        const post = controller.postComment(editor, 'Posted comment');
        requests[0].resolve({});
        await new Promise(resolve => setImmediate(resolve));
        const fresh = { ...editor._boardCard, description: 'Saved description',
            comments: [{ id: 'posted', body: 'Posted comment' }], codeLinks: removing ? [] : [link] };
        requests[1].resolve(fresh);
        await post;
        assert.deepEqual(editor._boardCard.codeLinks, fresh.codeLinks);
        for (let pollNumber = 0; pollNumber < 2; pollNumber++) {
            const offset = requests.length;
            const poll = controller.refreshSessionActivity();
            requests[offset].resolve({ cards: [] });
            requests[offset + 1].resolve(fresh);
            await poll;
        }
        assert.equal(count.textContent, removing ? '0' : '1');
        assert.equal(list.innerHTML.includes('data-board-remove-code-link="github-link"'), !removing);
        assert.equal(editor._boardCard.description, 'Unsaved description');
    });
}

test('card reload preserves pending or newer GitHub link writes', async t => {
    const editor = openEditor(t);
    for (const pendingWrite of [true, false]) {
        editor._boardCodeLinkBusy = false;
        const link = { id: 'new-link', url: 'https://github.com/org/repo/pull/42' };
        editor._boardCard.codeLinks = [];
        const older = structuredClone(editor._boardCard);
        const { controller, requests } = harness();
        const reload = controller.reloadEditingCard(editor);
        if (pendingWrite) editor._boardCodeLinkBusy = true;
        else editor._boardCard.codeLinks.push(link);
        requests[0].resolve({ ...older, codeLinks: pendingWrite ? [link] : [] });
        await reload;
        const expected = pendingWrite ? [] : [link];
        assert.deepEqual(editor._boardCard.codeLinks, expected);
        assert.deepEqual(controller.state.cards[0].codeLinks, expected);
    }
});

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

test('lane activity is refreshed even with no loaded cards, and is cleared on completion', async () => {
    const original = globalThis.document;
    globalThis.document = { querySelector: () => null };
    try {
        const { controller, requests } = harness();
        controller.state.cards = [];
        const lanes = [];
        controller.laneAgents.updateActivity = ids => lanes.push(ids);
        let pending = controller.refreshSessionActivity();
        assert.deepEqual(requests[0].body.cardIds, []);
        requests[0].resolve({ cards: [], activeAutomationColumnIds: ['review'] });
        await pending;
        pending = controller.refreshSessionActivity();
        requests[1].resolve({ cards: [], activeAutomationColumnIds: [] });
        await pending;
        assert.deepEqual(lanes, [['review'], []]);
    } finally { globalThis.document = original; }
});

test('a running Automation in another root is not reported as finished or focused through a stale tab', async () => {
    const { controller, requests } = harness();
    const messages = [];
    controller.app.showToast = (...args) => messages.push(args.join(' '));
    controller.focusSessionTab = () => assert.fail('cannot focus another root’s tab');
    const pending = controller.goToCardAutomation('card');
    requests[0].resolve({ hasActiveAutomation: true, sessions: [
        { id: 'shell', isAutomation: true, active: false, tabId: 'foreign-tab' }
    ] });
    await pending;
    assert.match(messages[0], /running in another VibeRails window/);
});
