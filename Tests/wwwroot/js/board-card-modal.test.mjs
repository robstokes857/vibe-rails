import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

const indexPath = path.resolve('VibeRails/wwwroot/index.html');
const controllerPath = path.resolve('VibeRails/wwwroot/js/modules/board-controller.js');

function boardCss() {
    const html = readFileSync(indexPath, 'utf8');
    const start = html.indexOf('id="board-template"');
    const end = html.indexOf('</template>', start);
    assert.ok(start >= 0 && end > start, 'expected the board template in index.html');
    return html.slice(start, end);
}

function rule(css, selector) {
    const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const match = css.match(new RegExp(`${escaped}\\s*\\{[^}]*\\}`));
    assert.ok(match, `expected a CSS rule for ${selector}`);
    return match[0];
}

test('the card editor strips Bootstrap\'s extra scroller so the dialog has one bar', () => {
    const source = readFileSync(controllerPath, 'utf8');
    assert.match(source, /classList\.remove\('modal-lg', 'modal-dialog-scrollable'\)/);
    assert.match(source, /classList\.add\('modal-xl', 'board-card-modal-dialog'\)/);

    const open = source.slice(
        source.indexOf('async openCardEditor'),
        source.indexOf('bindCardEditor(editor, card)')
    );
    assert.match(open, /class="board-editor-scroll"/);
    // Save/Delete are a sibling of the scroller, not nested inside it.
    assert.match(
        open,
        /<div class="board-editor-scroll">[\s\S]*<\/aside>\s*<\/div>\s*<div class="board-editor-actions">/
    );
    assert.match(source, /querySelector\('\.board-editor-scroll'\)/);
});

test('opening a card keeps the id on the editor and ignores a stale fetch', () => {
    const source = readFileSync(controllerPath, 'utf8');
    const open = source.slice(
        source.indexOf('async openCardEditor'),
        source.indexOf('bindCardEditor(editor, card)')
    );
    assert.match(open, /const generation = \+\+this\._openCardGeneration/);
    assert.match(open, /new AbortController\(\)/);
    assert.match(open, /BoardApi\.getBoardCardAsync\(cardId, \{ signal: abort\.signal \}\)/);
    assert.match(open, /if \(generation !== this\._openCardGeneration\) return;/);
    assert.match(open, /data-card-id="\$\{escapeHtml\(card\?\.id \|\| ''\)\}"/);
    assert.doesNotMatch(open, /this\.state\.editingCardId/);
    assert.match(source, /cardIdFromEditor\(editor\)/);
    assert.match(source, /dataset\?\.cardId/);
    assert.doesNotMatch(source, /this\.state\.editingCardId = null/);
});

test('the card editor fills the viewport and only .board-editor-scroll overflows', () => {
    const css = boardCss();
    assert.doesNotMatch(css, /height:\s*min\(\s*74vh\s*,\s*700px\s*\)/);

    const dialog = rule(css, '.board-card-modal-dialog');
    assert.match(dialog, /height:\s*calc\(100% - 2rem\)/);
    assert.match(dialog, /max-height:\s*calc\(100% - 2rem\)/);

    const body = rule(css, '.board-card-modal-dialog .modal-body');
    assert.match(body, /overflow:\s*hidden/);

    const scroller = rule(css, '.board-editor-scroll');
    assert.match(scroller, /overflow-y:\s*auto/);

    assert.match(rule(css, '.board-card-editor .board-editor-main'), /overflow:\s*visible/);
    assert.match(rule(css, '.board-side-scroll'), /overflow:\s*visible/);
    assert.doesNotMatch(rule(css, '.board-comments'), /overflow-y:\s*auto/);
});

test('the new-card description stays in document flow while auto-growing', () => {
    const css = boardCss();
    const description = rule(css, '.board-block:first-of-type .board-composer-input');
    assert.match(description, /min-height:\s*240px/);
    assert.match(description, /max-height:\s*none/);
    assert.match(description, /overflow-y:\s*hidden/);
    assert.doesNotMatch(css, /\.board-card-editor\[data-card-id=""\][^{]*\{[^}]*flex:\s*1 1 auto/,
        'the create-card composer must grow with its text, not be constrained to the leftover viewport height');
});

test('card type is present in the editor, filters, tiles and save payload', () => {
    const source = readFileSync(controllerPath, 'utf8');
    const html = readFileSync(indexPath, 'utf8');
    assert.match(source, /const CARD_TYPES = \[[\s\S]*research-spike[\s\S]*Chore \/ tech debt/);
    assert.match(source, /id="board-card-type"/);
    assert.match(source, /type: value\('#board-card-type'\)/);
    assert.match(source, /class="board-type-chip" data-type=/);
    assert.match(source, /bindSelect\('\[data-board-filter-type\]', 'type'\)/);
    assert.match(html, /data-board-filter-type/);
    assert.match(html, /\.board-type-chip\[data-type="bug"\]/);
});

test('the card editor has a collapsed Agent notes rail that renders card.notes with the comment renderer', () => {
    const source = readFileSync(controllerPath, 'utf8');
    const open = source.slice(
        source.indexOf('async openCardEditor'),
        source.indexOf('bindCardEditor(editor, card)')
    );
    // Collapsed by default, with a count badge.
    assert.match(open, /<details data-board-notes-details>/);
    assert.match(open, /data-board-count="notes"/);
    assert.doesNotMatch(open, /data-board-history/);
    // Rendered on toggle, from the card response — no extra fetch — and escape-first.
    assert.match(source, /\[data-board-notes-details\]'\)\?\.addEventListener\('toggle'/);
    const render = source.slice(source.indexOf('renderNotesPanel(editor, card) {'), source.indexOf('renderCommentsPanel(editor, card) {'));
    assert.match(render, /const notes = card\?\.notes \|\| \[\];/);
    assert.match(render, /renderCommentHtml\(note\.body, \{ attachments \}\)/);
    assert.match(render, /class="board-comment board-note/);
    assert.doesNotMatch(render, /BoardApi\./);
    // The rail style narrows the avatar column for the side rail.
    const css = boardCss();
    assert.match(rule(css, '.board-comment.board-note'), /grid-template-columns: 24px minmax\(0, 1fr\)/);
});

test('board-api exposes the agent-notes routes', () => {
    const api = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/board-api.js'), 'utf8');
    assert.match(api, /async function getCardNotesAsync\(cardId\)[\s\S]*?\/notes`\)/);
    assert.match(api, /async function addCardNoteAsync\(cardId, body\)[\s\S]*?\/notes`, 'POST', \{ body \}\)/);
    assert.match(api, /getCardNotesAsync,\s*addCardNoteAsync,/);
});
