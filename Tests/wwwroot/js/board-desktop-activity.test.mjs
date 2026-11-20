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

test('the card retains the app name and monitor after completion and after a CLI takes the assignment', t => {
    const originalWindow = globalThis.window;
    globalThis.window = {};
    t.after(() => { globalThis.window = originalWindow; });
    const controller = new BoardController({});
    const appCard = card({ desktopAppName: 'ChatGPT <Review>' });
    const html = controller.renderCard(appCard);
    assert.match(html, /aria-label="ChatGPT &lt;Review&gt; worked on this card"/);
    assert.match(html, /board-assignee-name">ChatGPT &lt;Review&gt;<\/span>/);
    assert.doesNotMatch(html, /ChatGPT <Review>|board-card is-live|Go to agent/);
    assert.equal(appCard.assignee, undefined);

    const reassigned = controller.renderCard({ ...appCard, assignee: 'base:codex' });
    assert.match(reassigned, /board-assignee-name">Codex<\/span>/);
    assert.match(reassigned, /aria-label="ChatGPT &lt;Review&gt; worked on this card"/);
    assert.doesNotMatch(reassigned, /board-card is-live/);
});

test('an unassigned desktop app named for a CLI wears that CLI mark, not its initials', t => {
    const originalWindow = globalThis.window;
    globalThis.window = {};
    t.after(() => { globalThis.window = originalWindow; });
    const controller = new BoardController({});
    const avatar = name => controller.assigneeChipFace(card({ desktopAppName: name })).content;

    for (const [name, logo] of [['Codex', 'openai.svg'], ['Claude Desktop', 'claude-color.svg'], ['ChatGPT', 'openai.svg'], ['codex-mcp-client', 'openai.svg']]) {
        const html = avatar(name);
        assert.match(html, new RegExp(`board-avatar has-logo" title="${name}"`), name);
        assert.match(html, new RegExp(`src="assets/img/${logo}"`), name);
        assert.doesNotMatch(html, new RegExp(`>${name.slice(0, 2).toUpperCase()}</span>`), name);
    }
    // No CLI word: the app keeps its initials on the primary colour, and "Recodex" is not Codex.
    for (const name of ['Zed', 'Recodex']) {
        const html = avatar(name);
        assert.doesNotMatch(html, /has-logo|<img/, name);
        assert.match(html, new RegExp(`background:var\\(--color-primary\\)[^>]*>${name.slice(0, 2).toUpperCase()}</span>`), name);
    }
});

test('activity polling adds and removes desktop styling while preserving CLI activity and card content', async t => {
    const originalDocument = globalThis.document;
    const originalWindow = globalThis.window;
    globalThis.document = { querySelector: () => null };
    globalThis.window = {};
    t.after(() => { globalThis.document = originalDocument; globalThis.window = originalWindow; });
    const requests = [];
    const classes = new Set();
    const indicators = new Map();
    const chip = { classList: { toggle() {} }, setAttribute(name, value) { this[name] = value; } };
    const aside = {
        querySelector(selector) { return indicators.has(selector) ? { remove: () => indicators.delete(selector) } : null; },
        insertAdjacentHTML(_, html) {
            for (const name of ['board-live-dot', 'board-desktop-active'])
                if (html.includes(name)) indicators.set(`.${name}`, html);
        }
    };
    const tile = {
        dataset: { cardId: 'card' },
        classList: { toggle(name, enabled) { if (enabled) classes.add(name); else classes.delete(name); } },
        querySelector: selector => selector === '.board-card-aside' ? aside : selector === '.board-assignee-chip' ? chip : null
    };
    const controller = new BoardController({ currentView: 'board',
        isUiHidden: () => false,
        apiCall: () => new Promise(resolve => requests.push(resolve)) });
    controller.root = { isConnected: true, querySelectorAll: () => [tile] };
    controller.state.boardId = 'board';
    controller.state.cards = [card()];
    const refresh = async activity => {
        const pending = controller.refreshSessionActivity();
        requests.shift()({ cards: [{ id: 'card', ...activity }] });
        await pending;
    };

    await refresh({ hasActiveDesktopAgent: true, desktopAppName: 'ChatGPT' });
    assert.equal(controller.state.cards[0].hasActiveDesktopAgent, true);
    assert.deepEqual([...classes], ['is-live', 'is-desktop-live']);
    assert.match(indicators.get('.board-desktop-active'), /ChatGPT is working on this card/);
    assert.match(chip.innerHTML, /board-assignee-name">ChatGPT<\/span>/);
    assert.equal(chip['aria-label'], 'Assign VB-1, desktop app ChatGPT');

    await refresh({ activeTabId: 'tab', activeSessionId: 'session', hasActiveDesktopAgent: true, desktopAppName: 'ChatGPT' });
    assert.deepEqual([...classes], ['is-live']);
    assert.ok(indicators.has('.board-live-dot'));
    assert.ok(indicators.has('.board-desktop-active'));

    await refresh({ activeTabId: 'tab', activeSessionId: 'session', hasActiveDesktopAgent: false, desktopAppName: 'ChatGPT' });
    assert.deepEqual([...classes], ['is-live']);
    assert.ok(indicators.has('.board-live-dot'));
    assert.match(indicators.get('.board-desktop-active'), /ChatGPT worked on this card/);

    await refresh({ hasActiveDesktopAgent: false, desktopAppName: 'ChatGPT' });
    assert.equal(classes.size, 0);
    assert.deepEqual([...indicators.keys()], ['.board-desktop-active']);
    assert.equal(controller.state.cards[0].title, 'Work');
});

test('desktop border uses the existing animation and reduced-motion rule with a separate purple color', () => {
    const source = readFileSync(new URL('../../../VibeRails/wwwroot/index.html', import.meta.url), 'utf8');
    assert.match(source, /\.board-view \.board-card\.is-desktop-live\s*\{\s*--board-live-color: #a855f7;/);
    assert.match(source, /\.board-desktop-active\s*\{\s*color: #a855f7;/);
    assert.match(source, /repeating-linear-gradient\(-45deg, var\(--board-live-color, #06b6d4\)/);
    assert.match(source, /prefers-reduced-motion: reduce\) \{[^{}]*\.board-view \.board-card\.is-live \{ animation: none; \}/);
});
