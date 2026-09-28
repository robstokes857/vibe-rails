import test from 'node:test';
import assert from 'node:assert/strict';
import { mountHistory, historySection } from '../../../VibeRails/wwwroot/js/modules/board-history.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';

test('history is collapsed, fetches only on demand, escapes content and cancels on close', async () => {
    assert.doesNotMatch(historySection(), /<details[^>]*\bopen\b/);
    const handlers = {};
    const host = { html: '', replaceChildren() { this.html = ''; }, insertAdjacentHTML(_, text) { this.html += text; } };
    const more = { addEventListener() {}, removeEventListener() {} };
    const element = { open: false, querySelector: selector => selector.includes('entries') ? host : more,
        addEventListener: (name, fn) => { handlers[name] = fn; }, removeEventListener: name => delete handlers[name] };
    const calls = [];
    BoardApi.attach({ apiCall: async (...args) => {
        calls.push(args);
        return { entries: [{ id: 'one', body: '<script>bad()</script>', author: '<b>Agent</b>', createdUtc: '2026-09-27T12:00:00Z' }], hasMore: false, nextOffset: 1 };
    } });
    const dispose = mountHistory(element, 'board_a', 'card_a');
    assert.equal(calls.length, 0);
    element.open = true;
    handlers.toggle();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(calls.length, 1);
    assert.match(calls[0][0], /history\?offset=0&card=card_a/);
    assert.match(host.html, /&lt;script&gt;/);
    assert.doesNotMatch(host.html, /<script>/);
    handlers.toggle();
    assert.equal(calls.length, 1);
    dispose();
    assert.equal(handlers.toggle, undefined);
});

test('load more skips an entry already on screen and restores its label after a retry', async () => {
    const handlers = {};
    const moreHandlers = {};
    const host = { html: '', textContent: '', replaceChildren() { this.html = ''; }, insertAdjacentHTML(_, text) { this.html += text; } };
    const more = { textContent: 'Load older changes', hidden: true, disabled: false,
        addEventListener: (name, fn) => { moreHandlers[name] = fn; }, removeEventListener() {} };
    const element = { open: true, querySelector: selector => selector.includes('entries') ? host : more,
        addEventListener: (name, fn) => { handlers[name] = fn; }, removeEventListener() {} };
    const entry = (id, body) => ({ id, body, author: 'You', createdUtc: '2026-09-27T12:00:00Z' });
    // A sync tick between pages reorders the list, so the second page repeats an entry.
    const pages = [
        () => { throw new Error('offline'); },
        () => ({ entries: [entry('one', 'First'), entry('two', 'Second')], hasMore: true, nextOffset: 2 }),
        () => ({ entries: [entry('two', 'Second'), entry('three', 'Third')], hasMore: false, nextOffset: 4 })
    ];
    BoardApi.attach({ apiCall: async () => pages.shift()() });
    const dispose = mountHistory(element, 'board_a');
    handlers.toggle();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(more.textContent, 'Retry');
    assert.equal(more.hidden, false);
    await moreHandlers.click();
    assert.equal(more.textContent, 'Load older changes');
    assert.equal(more.hidden, false);
    await moreHandlers.click();
    assert.equal(more.hidden, true);
    assert.equal(host.html.match(/First/g).length, 1);
    assert.equal(host.html.match(/Second/g).length, 1);
    assert.equal(host.html.match(/Third/g).length, 1);
    dispose();
});
