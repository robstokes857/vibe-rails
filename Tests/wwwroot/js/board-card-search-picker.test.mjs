import test from 'node:test';
import assert from 'node:assert/strict';
import { CardSearchPicker } from '../../../VibeRails/wwwroot/js/modules/board-card-search-picker.js';

function harness(options = {}) {
    const input = Object.assign(new EventTarget(), { value: '' });
    const results = Object.assign(new EventTarget(), { innerHTML: '', querySelectorAll: () => [] });
    const status = { textContent: '' };
    const host = Object.assign(new EventTarget(), {
        querySelector: selector => ({ '[data-board-link-search]': input, '[data-board-link-results]': results,
            '[data-board-link-status]': status })[selector]
    });
    const calls = [], selected = [];
    const picker = new CardSearchPicker(host, {
        ...options,
        search(query, { signal }) {
            return new Promise((resolve, reject) => calls.push({ query, signal, resolve, reject }));
        },
        onSelect: card => selected.push(card)
    });
    return { input, results, status, calls, selected, picker };
}

test('typing clears selectable results and cancels old work before the debounce expires', async t => {
    const h = harness();
    t.after(() => h.picker.dispose());
    const first = h.picker.load();
    h.calls[0].resolve([{ id: 'a', key: 'VIBE-68', title: 'Filters' }]);
    await first;
    assert.equal(h.picker.candidates.length, 1);
    h.input.value = 'new words';
    h.input.dispatchEvent(new Event('input'));
    assert.equal(h.calls[0].signal.aborted, true);
    assert.equal(h.results.innerHTML, '');
    assert.deepEqual(h.picker.candidates, []);
    const latest = h.picker.load();
    assert.equal(h.calls[1].query, 'new words');
    h.calls[1].resolve([]);
    await latest;
    assert.equal(h.status.textContent, 'No matching cards.');
});

test('query-only pickers do not load candidates for empty or whitespace-only searches', async t => {
    const h = harness({ searchOnEmpty: false });
    t.after(() => h.picker.dispose());
    for (const value of ['', '   ']) {
        h.input.value = value;
        const loading = h.picker.load();
        assert.equal(h.calls.length, 0);
        await loading;
        assert.equal(h.results.innerHTML, '');
        assert.match(h.status.textContent, /Type a card ID or title/);
        h.input.dispatchEvent(new Event('input'));
        assert.match(h.status.textContent, /Type a card ID or title/);
    }
    await new Promise(resolve => setTimeout(resolve, 250));
    assert.equal(h.calls.length, 0);
});

test('clearing a query-only picker cancels pending results and still allows the next search', async t => {
    const h = harness({ searchOnEmpty: false });
    t.after(() => h.picker.dispose());
    h.input.value = 'old';
    const old = h.picker.load();
    h.input.value = '';
    h.input.dispatchEvent(new Event('input'));
    assert.equal(h.calls[0].signal.aborted, true);
    h.calls[0].resolve([{ id: 'old', key: 'VB-1', title: 'Old result' }]);
    await old;
    await new Promise(resolve => setTimeout(resolve, 250));
    assert.equal(h.calls.length, 1);
    assert.deepEqual(h.picker.candidates, []);
    assert.equal(h.results.innerHTML, '');
    assert.match(h.status.textContent, /Type a card ID or title/);

    h.input.value = ' next ';
    const next = h.picker.load();
    assert.equal(h.calls[1].query, 'next');
    h.calls[1].resolve([{ id: 'next', key: 'VB-2', title: 'Next result' }]);
    await next;
    assert.match(h.results.innerHTML, /Next result/);
});

test('late results and failures cannot replace a newer search, and selection uses the returned identity', async t => {
    const h = harness();
    t.after(() => h.picker.dispose());
    const old = h.picker.load();
    h.input.value = 'VIBE-68';
    const fresh = h.picker.load();
    const card = { id: 'immutable', key: 'VB-PERM-1', displayId: 'VIBE-68', title: '<img src=x>',
        boardName: '<Board>', columnName: 'Review', isCurrentProject: false, projectPath: 'C:/<foreign>' };
    h.calls[1].resolve([card]);
    await fresh;
    h.calls[0].reject(new Error('Old failure'));
    await old;
    assert.match(h.results.innerHTML, /VIBE-68/);
    assert.match(h.results.innerHTML, /Another project/);
    assert.doesNotMatch(h.results.innerHTML, /<img|<Board>|<foreign>/);
    assert.doesNotMatch(h.status.textContent, /Old failure/);
    const click = new Event('click');
    Object.defineProperty(click, 'target', { value: { closest: () => ({ dataset: { boardLinkCard: 'immutable' } }) } });
    h.results.dispatchEvent(click);
    assert.deepEqual(h.selected, [card]);
});

test('search cancellation and disposal ignore late responses and remove input listeners', async () => {
    const h = harness();
    const closing = h.picker.load();
    h.picker.cancel();
    h.calls[0].resolve([{ id: 'stale' }]);
    await closing;
    assert.equal(h.results.innerHTML, '');
    const pending = h.picker.load();
    h.picker.dispose();
    assert.equal(h.calls[1].signal.aborted, true);
    h.calls[1].resolve([{ id: 'late' }]);
    await pending;
    h.status.textContent = 'disposed';
    h.input.dispatchEvent(new Event('input'));
    assert.equal(h.status.textContent, 'disposed');
    assert.equal(h.results.innerHTML, '');
});

test('current failures are visible and the next search can recover with a bounded-results hint', async t => {
    const h = harness();
    t.after(() => h.picker.dispose());
    const failed = h.picker.load();
    h.calls[0].reject(new Error('Offline. Try again.'));
    await failed;
    assert.equal(h.status.textContent, 'Offline. Try again.');
    const retry = h.picker.load();
    h.calls[1].resolve(Array.from({ length: 50 }, (_, i) => ({ id: String(i), key: `VIBE-${i}`, title: 'Card' })));
    await retry;
    assert.match(h.status.textContent, /Showing 50 cards. Refine your search/);
});
