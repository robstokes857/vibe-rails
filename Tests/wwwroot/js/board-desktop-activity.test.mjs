import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

function card(overrides = {}) {
    return { id: 'card', key: 'VB-1', title: 'Work', description: '', type: 'task',
        priority: 'medium', columnId: 'build', tags: [], ...overrides };
}

test('desktop activity has a purple border and accessible desktop indicator without a terminal target', () => {
    const controller = new BoardController({});
    const desktop = card({ hasActiveDesktopAgent: true });
    const html = controller.renderCard(desktop);

    assert.match(html, /class="board-card is-live is-desktop-live"/);
    assert.match(html, /class="board-desktop-active" title="Desktop app is working on this card"/);
    assert.match(html, /aria-label="Desktop app is working on this card"/);
    assert.match(html, /fa-desktop/);
    assert.doesNotMatch(html, /board-live-dot/);
    assert.equal(controller.hasRunningSession(desktop), false, 'desktop activity cannot make Go to agent target a nonexistent terminal');
});

test('simultaneous CLI and desktop activity preserves the CLI border and dot alongside the desktop icon', () => {
    const controller = new BoardController({});
    const both = card({ activeTabId: 'tab', activeSessionId: 'session', hasActiveDesktopAgent: true });
    const html = controller.renderCard(both);

    assert.match(html, /class="board-card is-live"/);
    assert.doesNotMatch(html, /is-desktop-live/);
    assert.match(html, /board-live-dot/);
    assert.match(html, /board-desktop-active/);
    assert.equal(controller.hasRunningSession(both), true);
    assert.doesNotMatch(controller.renderCard(card()), /is-live|is-desktop-live|board-desktop-active/);
});

test('activity polling adds and removes desktop styling while preserving CLI activity and card content', async t => {
    const originalDocument = globalThis.document;
    globalThis.document = { querySelector: () => null };
    t.after(() => { globalThis.document = originalDocument; });
    const requests = [];
    const classes = new Set();
    const indicators = new Set();
    const aside = {
        querySelector(selector) { return indicators.has(selector) ? { remove: () => indicators.delete(selector) } : null; },
        insertAdjacentHTML(_, html) {
            for (const name of ['board-live-dot', 'board-desktop-active'])
                if (html.includes(name)) indicators.add(`.${name}`);
        }
    };
    const tile = {
        dataset: { cardId: 'card' },
        classList: { toggle(name, enabled) { if (enabled) classes.add(name); else classes.delete(name); } },
        querySelector: selector => selector === '.board-card-aside' ? aside : null
    };
    const controller = new BoardController({ currentView: 'board',
        apiCall: () => new Promise(resolve => requests.push(resolve)) });
    controller.root = { isConnected: true, querySelectorAll: () => [tile] };
    controller.state.boardId = 'board';
    controller.state.cards = [card()];
    const refresh = async activity => {
        const pending = controller.refreshSessionActivity();
        requests.shift()({ cards: [{ id: 'card', ...activity }] });
        await pending;
    };

    await refresh({ hasActiveDesktopAgent: true });
    assert.equal(controller.state.cards[0].hasActiveDesktopAgent, true);
    assert.deepEqual([...classes], ['is-live', 'is-desktop-live']);
    assert.deepEqual([...indicators], ['.board-desktop-active']);

    await refresh({ activeTabId: 'tab', activeSessionId: 'session', hasActiveDesktopAgent: true });
    assert.deepEqual([...classes], ['is-live']);
    assert.ok(indicators.has('.board-live-dot'));
    assert.ok(indicators.has('.board-desktop-active'));

    await refresh({ activeTabId: 'tab', activeSessionId: 'session', hasActiveDesktopAgent: false });
    assert.deepEqual([...classes], ['is-live']);
    assert.deepEqual([...indicators], ['.board-live-dot']);

    await refresh({ hasActiveDesktopAgent: false });
    assert.equal(classes.size, 0);
    assert.equal(indicators.size, 0);
    assert.equal(controller.state.cards[0].title, 'Work');
});

test('desktop border uses the existing animation and reduced-motion rule with a separate purple color', () => {
    const source = readFileSync(new URL('../../../VibeRails/wwwroot/index.html', import.meta.url), 'utf8');
    assert.match(source, /\.board-view \.board-card\.is-desktop-live\s*\{\s*--board-live-color: #a855f7;/);
    assert.match(source, /\.board-desktop-active\s*\{\s*color: #a855f7;/);
    assert.match(source, /repeating-linear-gradient\(-45deg, var\(--board-live-color, #06b6d4\)/);
    assert.match(source, /prefers-reduced-motion: reduce\) \{\s*\.board-view \.board-card\.is-live \{ animation: none; \}/);
});
