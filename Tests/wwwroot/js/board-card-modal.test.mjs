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
