import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { formatTokens, contextSectionMarkup, renderContext } from '../../../VibeRails/wwwroot/js/modules/board-card-context.js';

const controllerPath = path.resolve('VibeRails/wwwroot/js/modules/board-controller.js');
const apiPath = path.resolve('VibeRails/wwwroot/js/modules/board-api.js');
const indexPath = path.resolve('VibeRails/wwwroot/index.html');

// A two-property stand-in for the elements renderContext writes to.
function element() {
    return { textContent: '', title: '', innerHTML: '' };
}

test('formatTokens uses the k/M shape agents see in their status lines', () => {
    assert.equal(formatTokens(0), '0');
    assert.equal(formatTokens(980), '980');
    assert.equal(formatTokens(1000), '1k');
    assert.equal(formatTokens(12_400), '12k');
    assert.equal(formatTokens(1_450), '1.5k');
    assert.equal(formatTokens(1_200_000), '1.2M');
    assert.equal(formatTokens(undefined), '0');
    assert.equal(formatTokens(-5), '0');
});

test('the section markup waits for a saved card and exposes the hooks the binder needs', () => {
    const unsaved = contextSectionMarkup(false);
    assert.match(unsaved, /data-board-context-section/);
    assert.match(unsaved, /Save the card to measure/);
    assert.doesNotMatch(unsaved, /data-board-context>/);
    const saved = contextSectionMarkup(true);
    assert.match(saved, /data-board-context-total/);
    assert.match(saved, /data-board-context>/);
    assert.match(saved, /data-board-context-message/);
});

test('renderContext shows the total, each source, the breakdown groups and the last launch, escaped', () => {
    const host = element();
    const total = element();
    renderContext(host, total, {
        tokens: 12_400,
        chars: 49_600,
        method: 'chars/4',
        sources: [
            { key: 'prompt', label: 'Launch prompt', chars: 4000, tokens: 1000 },
            { key: 'cardRead', label: 'get_board_card', chars: 44_000, tokens: 11_000, note: '<b>not html</b>' },
            { key: 'lanes', label: 'list_board_columns', chars: 1600, tokens: 400 }
        ],
        contents: [{ key: 'comments', label: 'Comments', chars: 20_000, tokens: 5000, items: 7 }],
        extras: [{ key: 'trimmedActivity', label: 'Older activity not sent in full', chars: 8000, tokens: 2000, items: 3, note: 'previews only' }],
        lastLaunch: { tokens: 9800, measuredAt: '2026-09-28T12:00:00Z', cli: 'claude' }
    });
    assert.equal(total.textContent, '≈12k');
    assert.match(total.title, /49,600 characters/);
    assert.match(host.innerHTML, /data-context-part="prompt"[\s\S]*≈1k/);
    assert.match(host.innerHTML, /data-context-part="cardRead"[\s\S]*≈11k/);
    assert.match(host.innerHTML, /&lt;b&gt;not html&lt;\/b&gt;/);
    assert.doesNotMatch(host.innerHTML, /<b>not html<\/b>/);
    assert.match(host.innerHTML, /Comments <span class="board-side-sub">\(7\)<\/span>/);
    assert.match(host.innerHTML, /By content/);
    assert.match(host.innerHTML, /Available, not sent unless asked for/);
    assert.match(host.innerHTML, /Last launch ≈9\.8k tokens/);
    assert.match(host.innerHTML, /claude/);
    assert.match(host.innerHTML, /Tokens are an estimate \(chars\/4\)/);

    const empty = element();
    renderContext(empty, element(), { tokens: 0, chars: 0, method: 'chars/4', sources: [], contents: [], extras: [], lastLaunch: null });
    assert.match(empty.innerHTML, /No launch recorded yet\./);
    assert.doesNotMatch(empty.innerHTML, /Available, not sent/);
});

test('the card editor mounts the section, refreshes it after every rail change and disposes it on close', () => {
    const source = readFileSync(controllerPath, 'utf8');
    assert.match(source, /import \{ contextSectionMarkup, bindCardContext \} from '\.\/board-card-context\.js'/);
    const open = source.slice(source.indexOf('async openCardEditor'), source.indexOf('bindCardEditor(editor, card)'));
    // Right after the fields, before the linked-cards rail: the size is the first thing about the card.
    assert.match(open, /<\/div>\s*\$\{contextSectionMarkup\(Boolean\(card\)\)\}\s*\$\{renderCardLinksSection\(card\)\}/);
    assert.match(source, /this\.cardContextDispose\?\.\(\);\s*const cardContext = bindCardContext\(editor, card, \{ app: this\.app \}\);/);
    assert.match(source, /editor\._boardContext = cardContext;/);
    // One refresh point for comments, commits and sessions (they all reload the card), plus the
    // paths that mutate a rail without reloading: attachment add/delete and linked cards.
    const reload = source.slice(source.indexOf('async reloadEditingCard'), source.indexOf('async reloadEditingCard') + 600);
    assert.match(reload, /editor\._boardContext\?\.refresh\(\);/);
    const attach = source.slice(source.indexOf('async attachImages'), source.indexOf('async attachImages') + 2200);
    assert.match(attach, /addCardAttachmentAsync[\s\S]*_boardContext\?\.refresh\(\)/);
    assert.match(source, /deleteCardAttachmentAsync[\s\S]{0,600}_boardContext\?\.refresh\(\)/);
    assert.match(source, /onChanged: \(\) => editor\._boardContext\?\.refresh\(\)/);
    const links = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/board-card-links.js'), 'utf8');
    assert.match(links, /\{ openCard, showError, onChanged \}/);
    assert.match(links, /renderLinks\(\);\s*onChanged\?\.\(\);/);
    const close = source.slice(source.indexOf('onClose: () => {'), source.indexOf('onClose: () => {') + 400);
    assert.match(close, /this\.cardContextDispose\?\.\(\);/);
    assert.match(readFileSync(apiPath, 'utf8'), /getCardContextAsync[\s\S]*\/context`, 'GET'/);
});

test('the board template styles the context rows', () => {
    const html = readFileSync(indexPath, 'utf8');
    const start = html.indexOf('id="board-template"');
    const css = html.slice(start, html.indexOf('</template>', start));
    assert.match(css, /\.board-context-row\s*\{[^}]*grid-template-columns: minmax\(0, 1fr\) auto/);
    assert.match(css, /\.board-context-tokens\s*\{[^}]*white-space: nowrap/);
    assert.match(css, /\.board-context-note\s*\{[^}]*grid-column: 1 \/ -1/);
});
