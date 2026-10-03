import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// VIBE-36: a terminal tab whose session is linked to a Board card links back to that card from the
// controls bar, and the Board view opens the card it was sent.

const multitabPath = path.resolve('VibeRails/wwwroot/js/modules/terminal-multitab.js');
const { TerminalManager, readTabBoardCard } = await import(pathToFileURL(multitabPath).href);
const { BoardController } = await import(pathToFileURL(path.resolve('VibeRails/wwwroot/js/modules/board-controller.js')).href);

function fakeButton() {
    const classes = new Set(['d-none']);
    const attributes = new Map();
    return {
        title: '',
        classList: {
            toggle(name, force) { if (force) classes.add(name); else classes.delete(name); },
            contains: name => classes.has(name)
        },
        setAttribute: (name, value) => attributes.set(name, value),
        getAttribute: name => attributes.get(name)
    };
}

function managerWithLink(navigations = []) {
    const manager = Object.create(TerminalManager.prototype);
    manager.cardLinkBtn = fakeButton();
    manager.cardLinkLabel = { textContent: '' };
    manager.app = { navigate: (view, data) => { navigations.push({ view, data }); return true; } };
    return manager;
}

const card = { id: 'card-1', key: 'VB-8L17B-108', title: 'Link back to the story', displayId: 'VIBE-36' };

test('readTabBoardCard keeps the rendered fields and rejects a payload without an id', () => {
    assert.deepEqual(readTabBoardCard({ ...card, extra: 'ignored' }), card);
    assert.deepEqual(readTabBoardCard({ id: 'card-2', key: 'VB-2', title: 'Old card', displayId: null }),
        { id: 'card-2', key: 'VB-2', title: 'Old card', displayId: null });
    assert.equal(readTabBoardCard(null), null);
    assert.equal(readTabBoardCard({ key: 'VB-3' }), null);
    assert.equal(readTabBoardCard({ id: '  ' }), null);
});

test('the controls bar names the card of a live tab and hides without a session or card', () => {
    const manager = managerWithLink();
    manager.updateBoardCardLink({ hasActiveSession: true, boardCard: card });
    assert.equal(manager.cardLinkBtn.classList.contains('d-none'), false);
    assert.equal(manager.cardLinkLabel.textContent, 'VIBE-36');
    assert.equal(manager.cardLinkBtn.title, 'Open VIBE-36 · Link back to the story on the Board');
    assert.equal(manager.cardLinkBtn.getAttribute('aria-label'), 'Open Board card VIBE-36 · Link back to the story');

    // Older cards without a display ID fall back to the key, like every other card label.
    manager.updateBoardCardLink({ hasActiveSession: true, boardCard: { ...card, displayId: null } });
    assert.equal(manager.cardLinkLabel.textContent, 'VB-8L17B-108');

    manager.updateBoardCardLink({ hasActiveSession: false, boardCard: card });
    assert.equal(manager.cardLinkBtn.classList.contains('d-none'), true);
    manager.updateBoardCardLink({ hasActiveSession: true, boardCard: null });
    assert.equal(manager.cardLinkBtn.classList.contains('d-none'), true);
    manager.updateBoardCardLink(null);
    assert.equal(manager.cardLinkBtn.classList.contains('d-none'), true);

    // A finished Automation viewer still shows its read-only output, so it keeps the link.
    manager.updateBoardCardLink({ hasActiveSession: false, jobRunId: 'run-1', boardCard: card });
    assert.equal(manager.cardLinkBtn.classList.contains('d-none'), false);
});

test('clicking the link navigates to the Board with a one-shot card to open', () => {
    const navigations = [];
    const manager = managerWithLink(navigations);
    assert.equal(manager.openBoardCard(card), true);
    assert.deepEqual(navigations, [{ view: 'board', data: { openCardId: 'card-1' } }]);
    assert.equal(manager.openBoardCard(null), false);
    assert.equal(navigations.length, 1);
});

test('the link sits in the controls bar actions beside the retired Reconnect button', () => {
    const source = readFileSync(multitabPath, 'utf8');
    const start = source.indexOf('<div class="d-flex gap-2 align-items-center vb-terminal-controls-actions">');
    assert.ok(start >= 0, 'expected the controls bar actions markup');
    const actions = source.slice(start, source.indexOf('</div>', start));
    const at = actions.indexOf('id="terminal-card-link-btn"');
    assert.ok(actions.indexOf('id="terminal-reconnect-btn"') >= 0 && actions.indexOf('id="terminal-reconnect-btn"') < at);
    const openTag = actions.slice(actions.lastIndexOf('<button', at), actions.indexOf('>', at));
    assert.match(openTag, /\bd-none\b/, 'hidden until a tab with a linked card is active');
    assert.match(source, /this\.openBoardCard\(this\.getActiveTab\(\)\?\.state\?\.boardCard\)/);
});

test('refreshing the tab list updates the card only for the session the tab still runs', async () => {
    const manager = Object.create(TerminalManager.prototype);
    const same = { state: { id: 'same', sessionId: 'session-a', boardCard: null } };
    const moved = { state: { id: 'moved', sessionId: 'session-new', boardCard: null } };
    Object.assign(manager, {
        _destroyed: false,
        _automationRefresh: null,
        tabs: new Map([['same', same], ['moved', moved]]),
        automationTabs: new Map(),
        closedAutomationTabs: new Set(),
        automationMenu: { refresh() {} },
        updateUi() {},
        app: {
            apiCall: async () => ({ maxTabs: 100, tabs: [
                { tabId: 'same', hasActiveSession: true, sessionId: 'session-a', boardCard: card, needsAttention: true },
                { tabId: 'moved', hasActiveSession: true, sessionId: 'session-old', boardCard: card, needsAttention: true }
            ] })
        }
    });
    await manager.refreshAutomationTabs();
    assert.deepEqual(same.state.boardCard, card);
    assert.equal(same.state.needsAttention, true);
    assert.equal(moved.state.needsAttention, undefined);
    manager.app.apiCall = async () => ({ tabs: [{ tabId: 'same', sessionId: 'session-a', needsAttention: null }] });
    await manager.refreshAutomationTabs();
    assert.equal(same.state.needsAttention, true, 'unknown Board status preserves attention');
    manager.app.apiCall = async () => ({ tabs: [{ tabId: 'same', sessionId: 'session-a', needsAttention: false }] });
    await manager.refreshAutomationTabs();
    assert.equal(same.state.needsAttention, false, 'unflagging clears attention');
    assert.equal(moved.state.boardCard, null);
});

test('the Board consumes openCardId once, even when the view cannot mount', async () => {
    const controller = new BoardController({ currentView: 'board' });
    const previousDocument = globalThis.document;
    globalThis.document = { getElementById: () => null };
    try {
        const data = { openCardId: 'card-1', other: true };
        await controller.loadView(data);
        assert.deepEqual(data, { other: true });
    } finally {
        globalThis.document = previousDocument;
    }
});

test('opening a card from navigation selects its board before opening the editor', async () => {
    const calls = [];
    const app = {
        currentView: 'board',
        apiCall: async (url) => {
            calls.push(`GET ${url}`);
            return { id: 'card-1', boardId: 'board-2', title: 'Link back' };
        },
        showToast: (...args) => calls.push(['toast', ...args])
    };
    const controller = new BoardController(app);
    controller.root = { isConnected: true };
    controller.state.boardId = 'board-1';
    controller.refresh = async (options) => { calls.push(['refresh', controller.state.boardId, options]); };
    controller.openCardEditor = async (id) => { calls.push(['open', id]); };

    await controller.openCardFromNavigation('card-1');
    assert.deepEqual(calls, [
        'GET /api/v1/board/cards/card-1',
        ['refresh', 'board-2', { restoreSelection: false }],
        ['open', 'card-1']
    ]);
});

test('a card that can no longer be read still loads the remembered board', async () => {
    const calls = [];
    const app = {
        currentView: 'board',
        apiCall: async () => { throw new Error('Card not found'); },
        showToast: (...args) => calls.push(['toast', ...args])
    };
    const controller = new BoardController(app);
    controller.root = { isConnected: true };
    controller.refresh = async (options) => { calls.push(['refresh', options]); };
    controller.openCardEditor = async (id) => { calls.push(['open', id]); };

    await controller.openCardFromNavigation('gone');
    assert.deepEqual(calls, [
        ['toast', 'Board', 'Card not found', 'error'],
        ['refresh', { restoreSelection: true }]
    ]);
});
