import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { formatTokens, contextSectionMarkup, renderContext, bindCardContext } from '../../../VibeRails/wwwroot/js/modules/board-card-context.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

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

test('the block exposes the hooks the binder needs', () => {
    const markup = contextSectionMarkup();
    assert.match(markup, /data-board-context-section/);
    assert.match(markup, /data-board-context-total/);
    assert.match(markup, /data-board-context>/);
    assert.match(markup, /data-board-context-message/);
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

// Behaviour, not source text: a fake editor around the real hooks, and a <details> stand-in.
function disclosure(open = false) {
    const listeners = new Map();
    return {
        open,
        addEventListener(type, handler) { listeners.set(type, handler); },
        removeEventListener(type, handler) { if (listeners.get(type) === handler) listeners.delete(type); },
        toggle(value) { this.open = value; listeners.get('toggle')?.(); },
        get listening() { return listeners.has('toggle'); }
    };
}

function contextEditor(details) {
    const nodes = {
        '[data-board-context]': { innerHTML: '' },
        '[data-board-context-total]': { textContent: '', title: '' },
        '[data-board-context-message]': { textContent: '' }
    };
    const section = { isConnected: true, closest: selector => selector === 'details' ? details : null, querySelector: selector => nodes[selector] ?? null };
    return { nodes, editor: { querySelector: selector => selector === '[data-board-context-section]' ? section : null } };
}

const settle = () => new Promise(resolve => setImmediate(resolve));
const estimate = { tokens: 1200, chars: 4800, method: 'chars/4', sources: [], contents: [], extras: [], lastLaunch: null };

test('inside the closed Advanced section the context is measured once it is opened, then after rail changes', async () => {
    const requests = [];
    BoardApi.attach({ apiCall: async url => { requests.push(url); return estimate; } });
    const details = disclosure(false);
    const { editor, nodes } = contextEditor(details);
    const context = bindCardContext(editor, { id: 'card_a' });
    await context.refresh();
    await context.refresh();
    assert.equal(requests.length, 0, 'rail changes while closed only mark the numbers stale');
    details.toggle(true);
    await settle();
    assert.deepEqual(requests, ['/api/v1/board/cards/card_a/context']);
    assert.equal(nodes['[data-board-context-total]'].textContent, '≈1.2k');
    await context.refresh();
    assert.equal(requests.length, 2, 'open: a rail change re-measures');
    details.toggle(false);
    details.toggle(true);
    await settle();
    assert.equal(requests.length, 2, 'nothing changed while it was closed');
    context.dispose();
    assert.equal(details.listening, false);
    await context.refresh();
    assert.equal(requests.length, 2);
});

test('a failed measurement reports inline, never as a toast', async () => {
    BoardApi.attach({ apiCall: async () => { throw new Error('Card was deleted'); } });
    const { editor, nodes } = contextEditor(disclosure(true));
    const context = bindCardContext(editor, { id: 'card_a' });
    await settle();
    await context.refresh();
    assert.equal(nodes['[data-board-context-total]'].textContent, '?');
    assert.equal(nodes['[data-board-context-message]'].textContent, 'Card was deleted');
    assert.equal(bindCardContext.length, 2, 'the binder takes no app, so it has no toast to raise');
    context.dispose();
    assert.equal(bindCardContext({ querySelector: () => null }, { id: 'card_a' }), null);
});

test('reloading the editing card re-measures its context', async () => {
    const controller = new BoardController({ apiCall: async () => ({ id: 'card_a', key: 'VB-1', title: 'Card' }) });
    BoardApi.attach(controller.app);
    let refreshed = 0;
    const editor = { dataset: { cardId: 'card_a' }, _boardContext: { refresh() { refreshed++; } } };
    await controller.reloadEditingCard(editor);
    assert.equal(refreshed, 1);
});

test('the Advanced section holds the display ID, the agent context and History; a new card has none', () => {
    const source = readFileSync(controllerPath, 'utf8');
    const open = source.slice(source.indexOf('async openCardEditor'), source.indexOf('bindCardEditor(editor, card)'));
    assert.match(open, /<summary class="board-side-label">Advanced<\/summary>[\s\S]*board-card-display-id[\s\S]*\$\{contextSectionMarkup\(\)\}\$\{historySection\(\)\}/);
    assert.doesNotMatch(open, /Card settings/);
    assert.equal((open.match(/contextSectionMarkup\(/g) || []).length, 1);
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
