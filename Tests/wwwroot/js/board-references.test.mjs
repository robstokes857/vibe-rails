import test from 'node:test';
import assert from 'node:assert/strict';
import { findBoardReferenceToken, referenceSearchKind, canonicalSessionId, referenceCanBrowse, referenceEmptyNote, bindBoardReferences } from '../../../VibeRails/wwwroot/js/modules/board-references.js';
import { renderCommentHtml } from '../../../VibeRails/wwwroot/js/modules/board-text.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { bindComposerPreview, boardTextOptions } from '../../../VibeRails/wwwroot/js/modules/board-composer-preview.js';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

test('reference prefixes keep files separate and only complete GUIDs look up sessions', () => {
    const token = value => findBoardReferenceToken(value, value.length);
    const guid = '12345678-1234-1234-1234-123456789abc';
    assert.equal(canonicalSessionId(guid.replaceAll('-', '').toUpperCase()), guid);
    for (const id of [guid, guid.replaceAll('-', '').toUpperCase()])
        assert.equal(referenceSearchKind(token('!' + id)), 'session');
    for (const query of ['', 'login', 'fix login form', 'VIBE-', 'VIBE-34', guid.slice(0, -1), guid + 'x'])
        assert.equal(referenceSearchKind(token('!' + query)), 'card', query);
    for (const query of [guid, 'VIBE-34', 'board-api', 'src/file.cs', '"VIBE-34', '"docs/!notes here']) {
        assert.equal(referenceSearchKind(token('@' + query)), 'files', query);
        assert.equal(referenceCanBrowse(token('@' + query)), true);
        assert.equal(referenceEmptyNote(token('@' + query)), 'No matching files.');
    }
    assert.deepEqual(token('See !fix login form'), { start: 4, query: 'fix login form', kind: '!' });
    assert.equal(referenceSearchKind(token('#abcdef0')), 'commit');
    for (const query of ['VIBE-', 'VIBE-34']) assert.equal(referenceSearchKind(token('#' + query)), 'card');
    for (const value of ['rob@example.com', '@rob@example.com', '`!session', '```\n@VIBE-34', '## ',
        '![image]', '!'+guid+' ', '!'+guid+' more prose', '!'+ 'a'.repeat(257)])
        assert.equal(token(value), null, value);
});

test('Markdown is optional and user HTML stays inert across styles and links', () => {
    const source = '# Heading\n**bold** and *italic* and ~~old~~\n- [x] done\n> quote\n[site](https://example.test)\n<img src=x onerror=alert(1)>\n`**literal**`';
    const styled = renderCommentHtml(source);
    assert.match(styled, /<h1>Heading<\/h1>/);
    assert.match(styled, /<strong>bold<\/strong>/);
    assert.match(styled, /<em>italic<\/em>/);
    assert.match(styled, /<del>old<\/del>/);
    assert.match(styled, /aria-label="Checked"/);
    assert.match(styled, /<blockquote>quote<\/blockquote>/);
    assert.match(styled, /&lt;img/);
    assert.doesNotMatch(styled, /<img/);
    assert.match(styled, /<code class="board-inline-code">\*\*literal\*\*<\/code>/);
    const plain = renderCommentHtml(source, { markdown: false });
    assert.doesNotMatch(plain, /<h1>|<strong>|<em>|<blockquote>/);
    for (const attack of ['[x](javascript:alert(1))', '![x](https://evil.test/x)', '[x](data:text/html,evil)'])
        assert.doesNotMatch(renderCommentHtml(attack), /<img|href="(?:javascript|data):/);
});

test('references are inert buttons and never parsed inside code', () => {
    const guid = '12345678123412341234123456789abc';
    const text = `!${guid} #abcdef0123 @[VIBE-34](card:card_1) ![VIBE-35](card:card_2)`;
    assert.match(renderCommentHtml(text), /data-board-ref-session=/);
    assert.match(renderCommentHtml(text), /data-board-ref-commit=/);
    assert.equal((renderCommentHtml(text).match(/data-board-ref-card=/g) || []).length, 2);
    assert.doesNotMatch(renderCommentHtml('`' + text + '`'), /<button/);
    assert.doesNotMatch(renderCommentHtml('```\n' + text + '\n```'), /<button/);
});

class FakeElement {
    constructor(tagName = 'div') {
        Object.assign(this, { tagName: tagName.toUpperCase(), style: {}, dataset: {}, children: [], listeners: new Map(),
            offsetTop: 0, offsetLeft: 0, offsetWidth: 100, clientWidth: 500, scrollTop: 0, innerHTML: '', isConnected: true });
    }
    addEventListener(type, handler) { this.listeners.set(type, [...(this.listeners.get(type) || []), handler]); }
    fire(type, event = {}) { for (const handler of this.listeners.get(type) || []) handler(event); }
    dispatchEvent(event) { this.fire(event.type, event); return true; }
    appendChild(child) { child.parentElement = this; this.children.push(child); return child; }
    remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(child => child !== this); this.isConnected = false; }
    focus() {}
    setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; }
    setAttribute() {}
    querySelector() { return null; }
}

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

/** Binds the real typeahead (once per test) to a fake textarea; `data` feeds the stubbed searches. */
function bindPopup(t, { files = [], truncated = false, cards = [], sessions = [], session = null } = {}) {
    const saved = { document: globalThis.document, getComputedStyle: globalThis.getComputedStyle,
        searchFiles: BoardApi.searchFilesAsync, cards: BoardApi.getCardLinkCandidatesAsync,
        projectCards: BoardApi.getProjectCardCandidatesAsync, sessions: BoardApi.getCardSessionsAsync };
    const calls = { files: [], cards: [], projectCards: [], sessions: [], history: [], links: [] };
    const data = { files, truncated, cards, sessions, session };
    globalThis.document = { body: new FakeElement('body'), createElement: tag => new FakeElement(tag) };
    globalThis.getComputedStyle = () => ({ lineHeight: '20px', fontSize: '16px' });
    BoardApi.searchFilesAsync = async query => { calls.files.push(query); return { files: data.files, truncated: data.truncated }; };
    BoardApi.getCardLinkCandidatesAsync = async (_cardId, query) => { calls.cards.push(query); return data.cards.slice(0, 50); };
    BoardApi.getProjectCardCandidatesAsync = async query => {
        calls.projectCards.push(query); return data.cards.filter(card => card.isCurrentProject !== false).slice(0, 50);
    };
    BoardApi.getCardSessionsAsync = async id => { calls.sessions.push(id); return typeof data.sessions === 'function' ? data.sessions() : data.sessions; };
    const host = new FakeElement();
    const input = new FakeElement('textarea');
    input.parentElement = host;
    const dispose = bindBoardReferences(input, { app: {
        data: { configs: { rootPath: 'C:/repo' } },
        apiCall: async path => { calls.history.push(path); return data.session; }
    }, host, card: {}, onLink: async item => { calls.links.push(item); } });
    t.after(() => {
        dispose();
        BoardApi.searchFilesAsync = saved.searchFiles;
        BoardApi.getCardLinkCandidatesAsync = saved.cards;
        BoardApi.getProjectCardCandidatesAsync = saved.projectCards;
        BoardApi.getCardSessionsAsync = saved.sessions;
        for (const key of ['document', 'getComputedStyle']) {
            if (saved[key] === undefined) delete globalThis[key]; else globalThis[key] = saved[key];
        }
    });
    const type = async value => {
        input.value = value;
        input.selectionStart = input.selectionEnd = value.length;
        input.fire('input');
        await delay(260); // past the popup's debounce
        return host.children[0].innerHTML;
    };
    const pick = async (index = 0) => {
        for (let i = 0; i < index; i++) input.fire('keydown', { key: 'ArrowDown', preventDefault() {}, stopPropagation() {} });
        input.fire('keydown', { key: 'Enter', preventDefault() {}, stopPropagation() {} });
        await delay(260);
        return host.children[0]?.innerHTML || '';
    };
    return { calls, data, type, pick, input, dispose };
}

const card = (n, title) => ({ id: `card_${n}`, key: `VB-${n}`, title });
const rowCount = html => (html.match(/data-board-file-row="\d+"/g) || []).length;

test('@ only reads files, including full card IDs and GUIDs, and keeps Browse', async t => {
    const popup = bindPopup(t, { files: ['src/board-api.js'], cards: [card(7, 'Board API')] });
    for (const query of ['board-api', 'VB-', 'VB-ABCDE-107', '12345678-1234-1234-1234-123456789abc']) {
        const html = await popup.type('@' + query);
        assert.match(html, /src\/board-api.js/);
        assert.match(html, /data-board-file-browse/);
        assert.equal(rowCount(html), 1);
    }
    assert.equal(popup.calls.files.length, 4);
    assert.deepEqual(popup.calls.cards, []);
    assert.deepEqual(popup.calls.projectCards, []);
    assert.deepEqual(popup.calls.history, []);
});

test('! title and keyword searches never query history, including partial GUIDs', async t => {
    const popup = bindPopup(t, { cards: [card(12, 'Fix login form')] });
    for (const query of ['', 'login', 'fix login form', '12345678-1234-1234-1234-123456789ab', '12345678123412341234123456789abcX']) {
        assert.match(await popup.type('!' + query), /Fix login form · show sessions/);
        assert.equal(popup.calls.projectCards.at(-1), query);
    }
    assert.deepEqual(popup.calls.history, []);
    assert.deepEqual(popup.calls.sessions, []);
    assert.deepEqual(popup.calls.files, []);
});

test('a card choice opens its sessions with escaped agent/time metadata and links the chosen session', async t => {
    const id = '12345678-1234-1234-1234-123456789abc';
    const popup = bindPopup(t, { cards: [{ ...card(12, 'Fix login form'), displayId: 'LOGIN-2' }], sessions: [
        { id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', displayName: 'Same name', cli: 'claude', createdAt: '2026-10-04T10:00:00Z', active: false },
        { id, displayName: 'Same name', cli: 'codex<script>', createdAt: '2026-10-05T15:30:00Z', active: true, isReview: true }
    ] });
    await popup.type('See !fix login form');
    const html = await popup.pick();
    assert.equal(popup.input.value, 'See !VB-12');
    assert.deepEqual(popup.calls.sessions, ['card_12']);
    assert.equal(rowCount(html), 3);
    assert.match(html, /claude/);
    assert.match(html, /codex&lt;script&gt;/);
    assert.match(html, /Linked .*2026/);
    assert.match(html, /Review · Open/);
    assert.match(html, /Reference card LOGIN-2/);
    assert.doesNotMatch(html, /<script>/i);
    await popup.pick(1);
    assert.equal(popup.input.value, `See !${id} `);
    assert.equal(popup.calls.links[0].session.id, id);
    assert.deepEqual(popup.calls.history, []);
});

test('a card with no sessions explains the empty list and can be referenced with !', async t => {
    const popup = bindPopup(t, { cards: [card(12, 'No recordings')] });
    await popup.type('!recordings');
    assert.match(await popup.pick(), /No sessions linked to this card/);
    await popup.pick();
    assert.equal(popup.input.value, '![VB-12](card:card_12) ');
    assert.match(renderCommentHtml(popup.input.value), /data-board-ref-card="card_12"/);
    assert.deepEqual(popup.calls.links, []);
});

test('only an entire session GUID reads history and rejects a different project', async t => {
    const id = '12345678-1234-1234-1234-123456789abc';
    const popup = bindPopup(t, { session: { id, sessionDisplayName: 'Exact recording', cli: 'codex',
        startedUTC: '2026-10-05T15:00:00Z', workingDirectory: 'c:\\repo' } });
    assert.match(await popup.type('!' + id.replaceAll('-', '').toUpperCase()), /Exact recording/);
    assert.deepEqual(popup.calls.history, [`/api/v1/chatHistory/${id}`]);
    assert.deepEqual(popup.calls.projectCards, []);
    popup.data.session.workingDirectory = 'C:/other';
    assert.match(await popup.type('!' + id), /No session with that ID in this project/);
});

test('session and commit card searches scope before the result limit', async t => {
    const foreign = Array.from({ length: 50 }, (_, index) => ({ ...card(index + 100, 'Foreign candidate'), isCurrentProject: false }));
    const popup = bindPopup(t, { cards: [...foreign, { ...card(9, 'Local candidate'), isCurrentProject: true }] });
    assert.match(await popup.type('!candidate'), /Local candidate · show sessions/);
    assert.match(await popup.type('#VB-'), /Local candidate · show commits/);
    assert.deepEqual(popup.calls.projectCards, ['candidate', 'VB-']);
    assert.deepEqual(popup.calls.cards, []);
    assert.deepEqual(popup.calls.sessions, []);
});

test('editing during a card session lookup discards its stale dropdown', async t => {
    let resolve;
    const popup = bindPopup(t, { cards: [card(12, 'Slow lookup')], sessions: () => new Promise(done => { resolve = done; }) });
    await popup.type('!VB-12');
    await popup.type('@src');
    resolve([{ id: 'old', displayName: 'Stale session' }]);
    await delay(10);
    assert.doesNotMatch(await popup.type('@src'), /Stale session/);
    assert.deepEqual(popup.calls.links, []);
});

test('posted comments always render Markdown alongside session and commit references', t => {
    const savedFrame = globalThis.requestAnimationFrame;
    const savedStorage = globalThis.localStorage;
    globalThis.requestAnimationFrame = () => 0;
    globalThis.localStorage = { getItem: () => 'off' };
    t.after(() => { globalThis.requestAnimationFrame = savedFrame; globalThis.localStorage = savedStorage; });
    const guid = '12345678-1234-1234-1234-123456789abc';
    const body = `**bold** !${guid} #abcdef0`;
    const card = { id: 'card_1', comments: [{ id: 'c1', body, author: { kind: 'user' } }],
        sessions: [{ id: guid, displayName: 'Fix login' }], commits: [{ sha: 'abcdef0123456', message: 'Repair the form' }] };
    const host = { innerHTML: '', isConnected: false, querySelectorAll: () => [] };
    const editor = { querySelector: selector => selector === '[data-board-comments]' ? host : null, querySelectorAll: () => [] };
    Object.create(BoardController.prototype).renderCardDiscussion(editor, card);
    assert.ok(host.innerHTML.includes(renderCommentHtml(body, boardTextOptions(card))));
    assert.match(host.innerHTML, /<strong>bold<\/strong>/);
    assert.match(host.innerHTML, /! Fix login/);
    assert.match(host.innerHTML, /title="Repair the form"/);
});

test('comment previews update with typing and dispose their listener', () => {
    const live = { innerHTML: '', querySelectorAll: () => [] };
    const composer = { querySelector: selector => selector === '[data-board-composer-live]' ? live : null };
    const input = new EventTarget(); input.value = '**first**';
    const dispose = bindComposerPreview(composer, input, {});
    assert.match(live.innerHTML, /<strong>first<\/strong>/);
    input.value = '**next**'; input.dispatchEvent(new Event('input'));
    assert.match(live.innerHTML, /<strong>next<\/strong>/);
    dispose();
    input.value = '**disposed**'; input.dispatchEvent(new Event('input'));
    assert.doesNotMatch(live.innerHTML, /disposed/);
});
