import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

// VB-11: several boards per project, click-to-edit description, the marching "agent on this
// card" border, and image previews that work inside the VS Code webview.

const indexPath = path.resolve('VibeRails/wwwroot/index.html');
const controllerPath = path.resolve('VibeRails/wwwroot/js/modules/board-controller.js');
const apiPath = path.resolve('VibeRails/wwwroot/js/modules/board-api.js');
const attachmentsPath = path.resolve('VibeRails/wwwroot/js/modules/board-attachments.js');
const webviewPath = path.resolve('vscode-viberails/src/webview-panel.ts');

function boardTemplate() {
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

test('the board picker sits top-left of the title with an add button next to it', () => {
    const html = boardTemplate();
    const head = html.slice(html.indexOf('<header class="board-head">'), html.indexOf('</header>'));
    assert.match(head, /<select class="board-control board-picker-select" data-board-select/);
    assert.match(head, /data-board-action="new-board"/);
    assert.match(head, /data-board-action="edit-board"/);
    // The picker comes before the title in source order: top-left, title centred.
    assert.ok(head.indexOf('data-board-select') < head.indexOf('Vibe Board'));
    assert.match(rule(html, '.board-view .board-head'), /grid-template-columns:\s*1fr auto 1fr/);
    assert.match(rule(html, '.board-view .board-head-title'), /text-align:\s*center/);
});

test('the controller loads boards first, remembers the selection, and scopes lanes and cards to it', () => {
    const source = readFileSync(controllerPath, 'utf8');
    assert.match(source, /const BOARD_STORAGE_KEY = 'viberails\.board\.selected\.v1'/);
    assert.match(source, /boards = await BoardApi\.getBoardsAsync\(\);/);
    assert.match(source, /BoardApi\.getBoardColumnsAsync\(boardId\)/);
    assert.match(source, /BoardApi\.getBoardCardsAsync\(boardId\)/);
    assert.match(source, /BoardApi\.reorderBoardColumnsAsync\(orderedIds, this\.state\.boardId\)/);
    assert.match(source, /createBoardColumnAsync\(\{ name, wipLimit, color, boardId: this\.state\.boardId \}\)/);
    assert.match(source, /picker\?\.addEventListener\('change', \(\) => this\.switchBoard\(picker\.value\)\)/);
    assert.match(source, /case 'new-board':\s*this\.openBoardEditor\(null\)/);
    assert.match(source, /case 'edit-board':\s*this\.openBoardEditor\(this\.state\.boardId\)/);
    // Deleting a board is a confirmDialog, never window.confirm, and the last board is refused client-side too.
    const del = source.slice(source.indexOf('async deleteBoard('), source.indexOf('// Lane editor'));
    assert.match(del, /confirmDialog\(\{/);
    assert.doesNotMatch(del, /window\.confirm/);
    assert.match(del, /this\.state\.boards\.length <= 1/);
    // The card editor lists every board's lanes so a card can move between boards.
    assert.match(source, /laneOptionsHtml\(columnId\)/);
    assert.match(source, /<optgroup label="\$\{escapeHtml\(board\.name\)\}">/);
});

test('board-api.js exposes the boards endpoints and passes the board id through', () => {
    const source = readFileSync(apiPath, 'utf8');
    assert.match(source, /const withBoard = \(path, boardId\) => boardId \? `\$\{path\}\?boardId=\$\{enc\(boardId\)\}` : path;/);
    for (const name of ['getBoardsAsync', 'createBoardAsync', 'updateBoardAsync', 'deleteBoardAsync']) {
        assert.match(source, new RegExp(`^\\s+${name},?$`, 'm'), `BoardApi must export ${name}`);
    }
    assert.match(source, /async function getBoardColumnsAsync\(boardId = null\)/);
    assert.match(source, /async function getBoardCardsAsync\(boardId = null\)/);
    assert.match(source, /async function reorderBoardColumnsAsync\(orderedIds, boardId = null\)/);
});

test('clicking the rendered description opens it for editing', () => {
    const source = readFileSync(controllerPath, 'utf8');
    const composer = source.slice(source.indexOf('bindComposer(composer'), source.indexOf('async attachImages('));
    assert.match(composer, /preview\?\.addEventListener\('click', event => \{/);
    assert.match(composer, /if \(event\.target\.closest\('a, img, button'\)\) return;/);
    assert.match(composer, /setPreview\(false\);\s*input\.focus\(\);/);
    assert.match(source, /data-board-composer-preview title="Click to edit"/);
    assert.match(rule(boardTemplate(), '.board-description-preview'), /cursor:\s*text/);
});

test('a card with a live session marches its border and shows a larger dot', () => {
    const source = readFileSync(controllerPath, 'utf8');
    assert.match(source, /\$\{card\.activeTabId \? ' is-live' : ''\}/);
    const css = boardTemplate();
    const live = rule(css, '.board-view .board-card.is-live');
    assert.match(live, /repeating-linear-gradient\(-45deg, #06b6d4 0 10px/);
    assert.match(live, /background-size:\s*auto, 28px 28px/);
    assert.match(live, /animation:\s*board-march 1s linear infinite/);
    assert.match(css, /@keyframes board-march \{[\s\S]*to \{ background-position: 0 0, 28px 0; \}/);
    assert.match(rule(css, '.board-live-dot'), /width:\s*12px/);
    // Reduced motion switches both animations off.
    assert.match(css, /prefers-reduced-motion: reduce\) \{\s*\.board-view \.board-card\.is-live \{ animation: none; \}/);
});

test('image previews use tracked object URLs supported by the VS Code webview CSP', () => {
    const source = readFileSync(attachmentsPath, 'utf8');
    const image = source.slice(source.indexOf("if (kind === 'image')"), source.indexOf("} else if (kind === 'text')"));
    assert.match(image, /img\.src = objectUrl\(/);
    assert.doesNotMatch(image, /FileReader|readAsDataURL|blobToDataUrl/);
    // The webview CSP lets blob: images through as well, for everything else that paints one.
    assert.match(readFileSync(webviewPath, 'utf8'), /`img-src \$\{webview\.cspSource\} https: data: blob:`/);
    // The attachment list shows a thumbnail for small rasters (the same data: URL inline images use).
    const controller = readFileSync(controllerPath, 'utf8');
    assert.match(controller, /const RASTER_DATA_URL_RE = \/\^data:image\\\/\(\?:png\|jpeg\|gif\|webp\);base64,\/i;/);
    assert.match(controller, /<img class="board-attachment-thumb" src="\$\{escapeHtml\(attachment\.url\)\}"/);
});
