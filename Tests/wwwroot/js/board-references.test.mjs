import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { findBoardReferenceToken, referenceSearchKind, canonicalSessionId, referenceCanBrowse, referenceEmptyNote, bindBoardReferences } from '../../../VibeRails/wwwroot/js/modules/board-references.js';
import { renderCommentHtml } from '../../../VibeRails/wwwroot/js/modules/board-text.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { bindComposerPreview, boardTextOptions } from '../../../VibeRails/wwwroot/js/modules/board-composer-preview.js';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

test('identifier routing skips unrelated searches, email and code', () => {
    const token = value => findBoardReferenceToken(value, value.length);
    const guid = '12345678-1234-1234-1234-123456789abc';
    assert.equal(canonicalSessionId(guid.replaceAll('-', '').toUpperCase()), guid);
    assert.equal(canonicalSessionId(guid), guid);
    for (const prefix of ['!', '@']) assert.equal(referenceSearchKind(token(prefix + guid)), 'session');
    for (const prefix of ['!', '@', '#']) assert.equal(referenceSearchKind(token(prefix + 'VIBE-34')), 'card');
    // `!`/`#` have no file search, so a partial card ID still goes to the card catalog.
    for (const prefix of ['!', '#']) assert.equal(referenceSearchKind(token(prefix + 'VIBE-')), 'card');
    assert.equal(referenceSearchKind(token('@VIBE-')), 'files-and-cards');
    assert.equal(referenceSearchKind(token('@src/file.cs')), 'files');
    assert.equal(referenceSearchKind(token('#abcdef0')), 'commit');
    assert.equal(token('rob@example.com'), null);
    assert.equal(token('@rob@example.com'), null);
    assert.equal(token('`!session'), null);
    assert.equal(token('```\n@VIBE-34'), null);
    assert.equal(token('## '), null);
    assert.equal(token('![image]'), null);
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
    const text = `!${guid} #abcdef0123 @[VIBE-34](card:card_1)`;
    assert.match(renderCommentHtml(text), /data-board-ref-session=/);
    assert.match(renderCommentHtml(text), /data-board-ref-commit=/);
    assert.match(renderCommentHtml(text), /data-board-ref-card=/);
    assert.doesNotMatch(renderCommentHtml('`' + text + '`'), /<button/);
    assert.doesNotMatch(renderCommentHtml('```\n' + text + '\n```'), /<button/);
});

test('a partly card-shaped @ token searches files too; an exact card ID does not', () => {
    const token = value => findBoardReferenceToken(value, value.length);
    for (const value of ['@VB-', '@vibe-books', '@board-api', '@VB-8QLDH-', '@VB-8QL']) {
        assert.equal(referenceSearchKind(token(value)), 'files-and-cards', value);
        assert.equal(referenceCanBrowse(token(value)), true, value);
        assert.doesNotMatch(referenceEmptyNote(token(value)), /Try a card ID/, value);
    }
    for (const value of ['@VB-12', '@VB-8QLDH-107', '@VIBE-35']) {
        assert.equal(referenceSearchKind(token(value)), 'card', value);
        assert.equal(referenceCanBrowse(token(value)), false, value);
        assert.equal(referenceEmptyNote(token(value)), 'No matching cards.', value);
    }
    for (const value of ['!board-api', '#board-api', '!VB-', '#VB-']) {
        assert.equal(referenceSearchKind(token(value)), 'card', value);
        assert.equal(referenceCanBrowse(token(value)), false, value);
    }
    // A quoted token is always a path, and a plain file token keeps Browse and its card/session hint.
    assert.equal(referenceSearchKind(token('@"VB-')), 'files');
    assert.equal(referenceCanBrowse(token('@src/a.cs')), true);
    assert.match(referenceEmptyNote(token('@src/a.cs')), /files/);
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
    setAttribute() {}
    querySelector() { return null; }
}

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

/** Binds the real typeahead (once per test) to a fake textarea; `data` feeds the stubbed searches. */
function bindPopup(t, { files = [], truncated = false, cards = [] } = {}) {
    const saved = { document: globalThis.document, getComputedStyle: globalThis.getComputedStyle,
        searchFiles: BoardApi.searchFilesAsync, cards: BoardApi.getCardLinkCandidatesAsync,
        projectCards: BoardApi.getProjectCardCandidatesAsync };
    const calls = { files: [], cards: [], projectCards: [] };
    const data = { files, truncated, cards };
    globalThis.document = { body: new FakeElement('body'), createElement: tag => new FakeElement(tag) };
    globalThis.getComputedStyle = () => ({ lineHeight: '20px', fontSize: '16px' });
    BoardApi.searchFilesAsync = async query => { calls.files.push(query); return { files: data.files, truncated: data.truncated }; };
    BoardApi.getCardLinkCandidatesAsync = async (_cardId, query) => { calls.cards.push(query); return data.cards.slice(0, 50); };
    BoardApi.getProjectCardCandidatesAsync = async query => {
        calls.projectCards.push(query); return data.cards.filter(card => card.isCurrentProject !== false).slice(0, 50);
    };
    const host = new FakeElement();
    const input = new FakeElement('textarea');
    input.parentElement = host;
    const dispose = bindBoardReferences(input, { app: { data: { configs: { rootPath: 'C:/repo' } } }, host, card: {}, onLink: async () => {} });
    t.after(() => {
        dispose();
        BoardApi.searchFilesAsync = saved.searchFiles;
        BoardApi.getCardLinkCandidatesAsync = saved.cards;
        BoardApi.getProjectCardCandidatesAsync = saved.projectCards;
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
    return { calls, data, type };
}

const card = (n, title) => ({ id: `card_${n}`, key: `VB-${n}`, title });
const rowCount = html => (html.match(/data-board-file-row="\d+"/g) || []).length;

test('@board-api lists matching files first, then cards, and still offers Browse', async t => {
    const { calls, type } = bindPopup(t, { files: ['VibeRails/wwwroot/js/modules/board-api.js'], cards: [card(7, 'Split board-api')] });
    const html = await type('see @board-api');
    assert.deepEqual(calls, { files: ['board-api'], cards: ['board-api'], projectCards: [] });
    assert.ok(html.indexOf('board-api.js') < html.indexOf('VB-7 · Split board-api'), 'files come before cards');
    assert.match(html, /data-board-file-browse/);
    assert.doesNotMatch(html, /No matches|Try a card ID/);
});

test('@VB- with no file matches shows cards; a miss on both says so and keeps Browse', async t => {
    const popup = bindPopup(t, { cards: [card(12, 'Fix login')] });
    const html = await popup.type('@VB-');
    assert.match(html, /VB-12 · Fix login · show sessions/);
    assert.match(html, /data-board-file-browse/);

    popup.data.cards = [];
    const none = await popup.type('@vibe-books');
    assert.match(none, /No matching files or cards\./);
    assert.match(none, /data-board-file-browse/);
});

test('an exact card ID skips the file index, and merged results stay bounded', async t => {
    const popup = bindPopup(t);
    const html = await popup.type('@VB-8QLDH-107');
    assert.deepEqual(popup.calls.files, []);
    assert.doesNotMatch(html, /data-board-file-browse/);
    assert.match(html, /No matching cards\./);

    Object.assign(popup.data, { files: Array.from({ length: 50 }, (_, i) => `src/vb-${i}.cs`), truncated: true,
        cards: Array.from({ length: 30 }, (_, i) => card(i + 1, `Card ${i + 1}`)) });
    const crowded = await popup.type('@vb-');
    assert.equal(rowCount(crowded), 50);
    assert.equal((crowded.match(/title="VB-\d+ · /g) || []).length, 10, 'room is kept for a few cards');
    assert.match(crowded, /Showing 50 matches/);
});

test('foreign card references are labeled and never read foreign sessions or commits through project routes', async t => {
    const popup = bindPopup(t, { cards: [{ id: 'outside', key: 'OTHER-ABCDE-1', title: 'Related work',
        boardName: 'Other board', projectPath: 'C:/other', isCurrentProject: false }] });
    const direct = await popup.type('@OTHER-ABCDE-1');
    assert.match(direct, /another project: Other board · C:\/other/);
    assert.doesNotMatch(direct, /show sessions|not been called/);
    const sessions = await popup.type('!OTHER-ABCDE-1');
    assert.match(sessions, /No matching cards/);
    const commits = await popup.type('#OTHER-ABCDE-1');
    assert.match(commits, /No matching cards/);
    assert.deepEqual(popup.calls.cards, ['OTHER-ABCDE-1']);
    assert.deepEqual(popup.calls.projectCards, ['OTHER-ABCDE-1', 'OTHER-ABCDE-1']);
});

test('session and commit card pickers scope before the limit while @ card references remain global', async t => {
    const foreign = Array.from({ length: 50 }, (_, index) => ({ ...card(index + 100, 'Foreign candidate'),
        isCurrentProject: false, boardName: 'Other', projectPath: 'C:/other' }));
    const popup = bindPopup(t, { cards: [...foreign, { ...card(9, 'Local candidate'), isCurrentProject: true }] });
    const sessions = await popup.type('!VB-');
    assert.match(sessions, /Local candidate · show sessions/);
    assert.doesNotMatch(sessions, /Foreign candidate/);
    const commits = await popup.type('#VB-');
    assert.match(commits, /Local candidate · show commits/);
    const cards = await popup.type('@VB-');
    assert.match(cards, /Foreign candidate/);
    assert.deepEqual(popup.calls.projectCards, ['VB-', 'VB-']);
    assert.deepEqual(popup.calls.cards, ['VB-']);
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
