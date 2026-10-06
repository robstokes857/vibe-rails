import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { BoardSearch, searchResultsHtml, cardLocationHtml } from '../../../VibeRails/wwwroot/js/modules/board-search.js';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { localCardEditorHtml, readLocalCardForm, localCardChanges, openLocalCardEditor } from '../../../VibeRails/wwwroot/js/modules/board-local-card.js';

const deferred = () => {
    let resolve, reject;
    const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
};

function searchHarness() {
    const status = { textContent: '' }, results = { innerHTML: '' };
    const controls = [{ disabled: false, removeAttribute() { this.title = ''; } }];
    const host = { hidden: true, querySelector: selector => selector.includes('status') ? status : results, addEventListener() {} };
    const classes = new Set();
    const root = {
        querySelector: () => host, querySelectorAll: () => controls,
        classList: { toggle(name, on) { if (on) classes.add(name); else classes.delete(name); } }
    };
    const calls = [];
    const search = new BoardSearch(root, { currentBoardId: () => 'board-current', openCard() {},
        search(query, { signal }) { const request = deferred(); calls.push({ query, signal, ...request }); return request.promise; } });
    return { search, calls, status, results, host, controls, classes };
}

test('new search invalidates old results and failures immediately, and clearing restores lane filters', async () => {
    const h = searchHarness();
    const old = h.search.setQuery('old query', { immediate: true });
    const current = h.search.setQuery('semantic query', { immediate: true });
    assert.equal(h.calls[0].signal.aborted, true);
    assert.equal(h.controls[0].disabled, true);
    assert.equal(h.host.hidden, false);
    h.calls[1].resolve([{ id: 'new-card', title: 'New result', key: 'V-2', isCurrentProject: true }]);
    await current;
    h.calls[0].reject(new Error('outdated failure'));
    await old;
    assert.match(h.results.innerHTML, /New result/);
    assert.doesNotMatch(h.status.textContent, /outdated/);
    const late = h.search.setQuery('third', { immediate: true });
    h.search.setQuery('');
    h.calls[2].resolve([{ id: 'late', title: 'Late card' }]);
    await late;
    assert.equal(h.host.hidden, true);
    assert.equal(h.results.innerHTML, '');
    assert.equal(h.controls[0].disabled, false);
    assert.equal(h.classes.has('is-searching'), false);
    h.search.dispose();
});

test('unloading search aborts pending work and ignores its result', async () => {
    const h = searchHarness();
    const pending = h.search.setQuery('authentication', { immediate: true });
    h.search.dispose();
    assert.equal(h.calls[0].signal.aborted, true);
    h.calls[0].resolve([{ id: 'foreign', title: 'Late result' }]);
    await pending;
    assert.equal(h.results.innerHTML, '');
});

test('search and linked-card context keep server order and escape foreign names, paths and snippets', () => {
    const current = { id: 'a', title: 'Current', key: 'A-1', isCurrentProject: true, boardId: 'own', boardName: 'Own', columnName: 'Todo' };
    const foreign = { id: 'b', title: '<img src=x>', key: 'B-1', isCurrentProject: false,
        boardId: 'other', boardName: '<script>', columnName: 'Doing', projectPath: 'C:/<remote>', snippet: '<svg onload=alert(1)> comment' };
    const html = searchResultsHtml([current, foreign], 'own');
    assert.ok(html.indexOf('Current') < html.indexOf('B-1'));
    assert.match(html, /Another project/);
    assert.match(html, /C:\/&lt;remote&gt;/);
    assert.match(html, /&lt;svg onload=alert\(1\)&gt; comment/);
    assert.doesNotMatch(html, /<img|<script|<svg/i);
    assert.match(cardLocationHtml({ ...current, boardId: 'second' }, 'own'), /Another board in this project/);
});

test('local discovery, edit, discussion and link clients use explicit credential-bearing routes', async () => {
    const calls = [];
    const signal = new AbortController().signal;
    BoardApi.attach({ async apiCall(...args) { calls.push(args); return { cards: [] }; } });
    await BoardApi.searchBoardCardsAsync('comment & notes', { signal });
    await BoardApi.getLocalBoardCardAsync('card/one', { signal });
    await BoardApi.updateLocalBoardCardAsync('card/one', { title: 'Edit' });
    await BoardApi.addLocalBoardCommentAsync('card/one', { body: 'Comment' });
    await BoardApi.getLocalCardLinkCandidatesAsync('card/one', 'related');
    await BoardApi.linkLocalCardAsync('card/one', 'target');
    await BoardApi.unlinkLocalCardAsync('card/one', 'target');
    assert.equal(calls[0][0], '/api/v1/board/cards/search?q=comment%20%26%20notes');
    assert.equal(calls[0][3].signal, signal);
    assert.ok(calls.slice(1).every(call => call[0].startsWith('/api/v1/board/local-cards/card%2Fone')));
    assert.equal(calls[2][1], 'PUT');
    assert.deepEqual(calls[5][2], { card: 'target' });
    assert.equal(calls[6][1], 'DELETE');
    assert.ok(calls.every(call => call[3].preferErrorResponseMessage === true));
    assert.ok(calls.every(call => !JSON.stringify(call).includes('projectPath')));
});

test('foreign card markup labels the owning board and keeps card data inert', () => {
    const html = localCardEditorHtml({ card: { id: 'c', title: '<img>', description: '</textarea><script>', columnId: 'lane' },
        projectPath: 'C:/<repo>', boardName: '<board>', columns: [{ id: 'lane', name: '<lane>' }] });
    assert.match(html, /another project's board/);
    assert.match(html, /Changes are saved to that board/);
    assert.match(html, /&lt;\/textarea&gt;&lt;script&gt;/);
    assert.match(html, /C:\/&lt;repo&gt;/);
    assert.doesNotMatch(html, /<script>|<img>/i);
});

test('local card navigation resolves target ownership, and a late foreign read cannot replace a new card', async () => {
    const pending = deferred(), calls = [], opened = [];
    const controller = new BoardController({
        apiCall(url) { calls.push(url); return url.endsWith('/foreign') ? pending.promise : Promise.resolve({ isCurrentProject: true, card: { id: 'current' } }); },
        showModal() { assert.fail('Stale foreign editor must not open'); },
        showToast() { assert.fail('Stale request must not show errors'); }
    });
    controller.openCardEditor = async id => { opened.push(id); };
    const first = controller.openLocalCard('foreign');
    await controller.openLocalCard('current');
    pending.resolve({ isCurrentProject: false, card: { id: 'foreign' } });
    await first;
    assert.deepEqual(opened, ['current']);
    assert.deepEqual(calls, ['/api/v1/board/local-cards/foreign', '/api/v1/board/local-cards/current']);
});

function formHarness() {
    const fields = Object.fromEntries(Object.entries({ title: 'Original', description: 'Original description', columnId: 'foreign-lane',
        assignee: '', type: 'task' }).map(([name, value]) => [name, { value, focus() {} }]));
    fields.blocked = { checked: false };
    fields.flagged = { checked: false };
    const handlers = {};
    return { fields, handlers, elements: { namedItem: name => fields[name] }, addEventListener(type, handler) { handlers[type] = handler; } };
}

test('foreign editor saves only changed fields and preserves drafts after comment activity or a failed save', async t => {
    const form = formHarness(), commentForm = formHarness();
    commentForm.fields.body = { value: '**Comment draft** #abcdef0 <script>invalid</script>' };
    const comments = { innerHTML: '' }, status = { textContent: '' };
    const elements = { '[data-local-card-form]': form, '[data-local-comment-form]': commentForm,
        '[data-local-card-comments]': comments, '[data-local-card-status]': status };
    const editor = { isConnected: true, querySelector: name => elements[name] || null,
        querySelectorAll: () => [...Object.values(form.fields), commentForm.fields.body] };
    const oldDocument = globalThis.document;
    globalThis.document = { getElementById: () => ({ querySelector: () => editor }) };
    t.after(() => { globalThis.document = oldDocument; });
    const calls = [], errors = [];
    let fail = false;
    const app = {
        showModal() {}, showToast: (...args) => errors.push(args),
        llmPickerController: { mount(select, options) { select.value = options.selectedValue; return () => {}; } },
        async apiCall(url, method, payload) {
            calls.push({ url, method, payload });
            if (fail) throw new Error('Save failed');
            return url.endsWith('/comments') ? { id: 'new-comment', body: payload.body, createdAt: '2026-10-04T12:00:00Z' } : { id: 'foreign', ...payload };
        }
    };
    BoardApi.attach(app);
    const dispose = openLocalCardEditor(app, { card: { id: 'foreign', title: 'Original', comments: [] }, columns: [] }, { openCard() {}, onChanged() {} });
    const submit = { preventDefault() {} };
    form.fields.description.value = 'Keep this draft';
    await commentForm.handlers.submit(submit);
    assert.equal(form.fields.description.value, 'Keep this draft');
    assert.equal(commentForm.fields.body.value, '');
    assert.match(comments.innerHTML, /Comment draft/);
    assert.match(comments.innerHTML, /<strong>Comment draft<\/strong>/);
    assert.doesNotMatch(comments.innerHTML, /<script>|data-board-ref-commit/i);
    fail = true;
    await form.handlers.submit(submit);
    assert.equal(form.fields.description.value, 'Keep this draft');
    assert.equal(errors[0][1], 'Save failed');
    fail = false;
    await form.handlers.submit(submit);
    assert.deepEqual(calls.at(-1), { url: '/api/v1/board/local-cards/foreign', method: 'PUT', payload: { description: 'Keep this draft' } });
    const count = calls.length;
    await form.handlers.submit(submit);
    assert.equal(calls.length, count, 'saved baseline must not resend stale fields');
    dispose();
});

test('foreign form does not write retired priority, point or tag fields', () => {
    const form = formHarness();
    const baseline = readLocalCardForm(form);
    form.fields.title.value = 'Edited';
    assert.deepEqual(localCardChanges(baseline, readLocalCardForm(form)), { title: 'Edited' });
    for (const field of ['priority', 'points', 'tags']) assert.equal(Object.hasOwn(baseline, field), false);
});
