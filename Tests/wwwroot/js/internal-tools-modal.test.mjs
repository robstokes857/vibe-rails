import test from 'node:test';
import assert from 'node:assert/strict';
import { showInternalToolsModal } from '../../../VibeRails/wwwroot/js/modules/internal-tools-modal.js';

function element() {
    return {
        textContent: '', innerHTML: '', hidden: false, disabled: false, value: '',
        attributes: {},
        setAttribute(key, value) { this.attributes[key] = value; },
        focus() {}, scrollIntoView() {}
    };
}

function harness(t, apiCall) {
    const previousDocument = globalThis.document;
    const previousFormData = globalThis.FormData;
    globalThis.document = { getElementById: () => null };
    globalThis.FormData = class {
        constructor(form) { this.form = form; }
        *[Symbol.iterator]() {
            for (const field of Object.entries(this.form.fields)) {
                if (!this.form.elements.namedItem(field[0]).disabled) yield field;
            }
        }
    };
    t.after(() => {
        globalThis.document = previousDocument;
        globalThis.FormData = previousFormData;
    });
    let cleanup;
    let markup;
    const modal = showInternalToolsModal({
        apiCall,
        showModal(_title, html, options) { cleanup = options.onClose; markup = html; }
    });
    const panels = new Map(['about', 'uploads', 'logs'].map(id => {
        const elements = new Map();
        const get = selector => {
            if (!elements.has(selector)) elements.set(selector, element());
            return elements.get(selector);
        };
        const form = get('form');
        const defaults = id === 'logs'
            ? { source: 'application', feature: '', level: '', status: '', operationId: '', search: '' }
            : { status: '', search: '' };
        form.fields = { ...defaults };
        form.reset = () => { form.fields = { ...defaults }; };
        form.elements = {
            namedItem: name => get(`[name="${name}"]`)
        };
        for (const name of Object.keys(defaults)) {
            const control = form.elements.namedItem(name);
            control.disabled = id === 'logs' && ['status', 'operationId'].includes(name);
            Object.defineProperty(control, 'value', {
                get: () => form.fields[name] || '',
                set: value => { form.fields[name] = value; }
            });
        }
        return [id, {
            querySelector: get,
            querySelectorAll: () => [get('[data-internal-action="previous"]'), get('[data-internal-action="next"]')]
        }];
    }));
    modal.root = {
        isConnected: true,
        querySelectorAll: () => [],
        querySelector: selector => panels.get(selector.replace('#vb-internal-panel-', ''))
    };
    return { modal, panels, cleanup, markup };
}

function deferred() {
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    return { promise, resolve };
}

test('tabs load only their own data, reuse loaded results, and close aborts work', async t => {
    const pending = deferred();
    const calls = [];
    const { modal, cleanup } = harness(t, (url, _method, _body, options) => {
        calls.push({ url, options });
        return pending.promise;
    });
    assert.equal(calls.length, 0);
    modal.selectSection('about');
    assert.deepEqual(calls.map(call => call.url), ['/api/v1/update/version']);
    modal.selectSection('uploads');
    assert.equal(calls[0].options.signal.aborted, true);
    assert.match(calls[1].url, /^\/api\/v1\/internal\/uploads\?/);
    assert.equal(calls[1].options.showLoading, false);
    cleanup();
    assert.equal(calls[1].options.signal.aborted, true);
    pending.resolve({ entries: [] });
    await pending.promise;
    assert.equal(modal.closed, true);
    assert.equal(modal.loaded.size, 0);
});

test('filters and pages go to the server and a successful tab does not refetch on reselect', async t => {
    const calls = [];
    const { modal, panels } = harness(t, async url => {
        calls.push(url);
        return { entries: [], features: ['feature-x'], hasMore: false };
    });
    modal.setLogSource('features');
    panels.get('logs').querySelector('form').fields = {
        source: 'features', feature: 'feature-x', level: 'Warning', status: 'failed', search: 'a & b', operationId: 'op-42'
    };
    await modal.loadEntries('logs', 100);
    const query = new URL(calls[0], 'http://localhost').searchParams;
    assert.deepEqual(Object.fromEntries(query), {
        source: 'features', feature: 'feature-x', level: 'Warning', status: 'failed', search: 'a & b',
        operationId: 'op-42', offset: '100', limit: '100'
    });
    assert.match(panels.get('logs').querySelector('[name="feature"]').innerHTML, /feature-x/);
    modal.selectSection('logs');
    assert.equal(calls.length, 1);
});

test('late responses cannot overwrite a newer filter result or a closed modal', async t => {
    const first = deferred();
    const second = deferred();
    let call = 0;
    const { modal, panels, cleanup } = harness(t, () => ++call === 1 ? first.promise : second.promise);
    const stale = modal.loadEntries('uploads');
    const fresh = modal.loadEntries('uploads');
    second.resolve({ entries: [{ id: 'fresh', subject: 'Current session', status: 'succeeded' }] });
    await fresh;
    cleanup();
    first.resolve({ entries: [{ id: 'stale', subject: 'Old session', status: 'failed' }] });
    await stale;
    assert.equal(modal.lists.uploads.entries[0].id, 'fresh');
    const rendered = panels.get('uploads').querySelector('[data-results]').innerHTML;
    assert.match(rendered, /Current session/);
    assert.doesNotMatch(rendered, /Old session/);
});

test('event values are escaped and incomplete-history warnings remain visible', async t => {
    const payload = '<img src=x onerror=alert(1)>';
    const { modal, panels } = harness(t, async () => ({
        entries: [{ id: '1', subject: payload, message: payload, operationId: payload, status: 'failed' }],
        droppedCount: 2, writeFailures: 1, readFailures: 3
    }));
    await modal.loadEntries('uploads');
    const panel = panels.get('uploads');
    assert.doesNotMatch(panel.querySelector('[data-results]').innerHTML, /<img/);
    assert.match(panel.querySelector('[data-results]').innerHTML, /&lt;img/);
    modal.showDetails('uploads', modal.lists.uploads.entries[0], 0);
    assert.doesNotMatch(panel.querySelector('[data-detail]').innerHTML, /<img/);
    assert.match(panel.querySelector('[data-detail]').innerHTML, /&lt;img/);
    assert.equal(panel.querySelector('[data-warning]').hidden, false);
    assert.match(panel.querySelector('[data-warning]').textContent, /2 events dropped/);
    assert.match(panel.querySelector('[data-warning]').textContent, /1 events could not be written/);
    assert.match(panel.querySelector('[data-warning]').textContent, /3 log files or records could not be read/);
});

test('an upload opens Logs scoped to the same feature and operation', t => {
    const { modal, panels } = harness(t, async () => ({ entries: [] }));
    const form = panels.get('logs').querySelector('form');
    modal.activeSection = 'uploads';
    modal.lists.uploads.entries = [{ feature: 'data-upload', operationId: 'operation-42' }];
    modal.loaded.add('logs');
    modal.lists.logs.offset = 100;
    let selected;
    modal.selectSection = section => { selected = section; };
    const originalSelector = modal.root.querySelector;
    modal.root.querySelector = selector => selector === '#vb-internal-tab-logs' ? element() : originalSelector(selector);
    modal.handleClick({ target: { closest: selector => selector === '[data-internal-action]'
        ? { dataset: { internalAction: 'view-logs', entry: '0' } } : null } });
    assert.equal(selected, 'logs');
    assert.equal(form.elements.namedItem('source').value, 'features');
    assert.equal(form.elements.namedItem('feature').value, 'data-upload');
    assert.equal(form.elements.namedItem('operationId').value, 'operation-42');
    assert.equal(form.elements.namedItem('operationId').disabled, false);
    assert.equal(form.elements.namedItem('status').disabled, false);
    assert.equal(modal.lists.logs.offset, 0);
    assert.equal(modal.loaded.has('logs'), false);
});

test('read errors show an error rather than presenting an empty history as successful', async t => {
    const { modal, panels } = harness(t, async () => { throw new Error('Unavailable'); });
    await modal.loadEntries('uploads');
    assert.equal(panels.get('uploads').querySelector('[data-error]').hidden, false);
    assert.match(panels.get('uploads').querySelector('[data-error]').textContent, /Unavailable/);
    assert.equal(modal.loaded.has('uploads'), false);
});

test('Logs initially reads existing application files and offers all three sources', async t => {
    const calls = [];
    const { modal, panels, markup } = harness(t, async url => {
        calls.push(url);
        return { entries: [], features: ['Startup'] };
    });
    assert.match(markup, /name="source"[^>]*><option value="application">Application logs<\/option>/);
    assert.match(markup, /value="daemon">VibeRails Demon logs/);
    assert.match(markup, /value="features">Feature journal/);
    await modal.loadEntries('logs');
    const query = new URL(calls[0], 'http://localhost').searchParams;
    assert.equal(query.get('source'), 'application');
    assert.equal(query.has('status'), false);
    assert.equal(query.has('operationId'), false);
    assert.match(panels.get('logs').querySelector('[name="feature"]').innerHTML, /Startup/);
    assert.doesNotMatch(panels.get('logs').querySelector('[name="feature"]').innerHTML, /data-upload/);
});

test('changing log source clears incompatible filters and previous source rows and categories', async t => {
    const calls = [];
    const { modal, panels } = harness(t, async url => {
        calls.push(url);
        return { entries: [], features: ['Jobs'] };
    });
    const panel = panels.get('logs');
    const form = panel.querySelector('form');
    modal.setLogSource('features');
    modal.setFeatures(['data-upload']);
    Object.assign(form.fields, { feature: 'data-upload', status: 'failed', operationId: 'op-42', level: 'Error', search: 'failure' });
    modal.lists.logs = { offset: 100, entries: [{ message: 'Old journal entry' }], hasMore: true };
    modal.renderEntries('logs');
    modal.setLogSource('daemon');
    assert.equal(form.elements.namedItem('feature').value, '');
    assert.equal(form.elements.namedItem('status').value, '');
    assert.equal(form.elements.namedItem('operationId').value, '');
    assert.equal(form.elements.namedItem('status').disabled, true);
    assert.equal(form.elements.namedItem('operationId').disabled, true);
    assert.doesNotMatch(panel.querySelector('[name="feature"]').innerHTML, /data-upload/);
    assert.doesNotMatch(panel.querySelector('[data-results]').innerHTML, /Old journal entry/);
    await modal.loadEntries('logs');
    const query = new URL(calls[0], 'http://localhost').searchParams;
    assert.equal(query.get('source'), 'daemon');
    assert.equal(query.get('offset'), '0');
    assert.equal(query.get('level'), 'Error');
    assert.equal(query.get('search'), 'failure');
    assert.equal(query.has('status'), false);
    assert.equal(query.has('operationId'), false);
    assert.match(panel.querySelector('[name="feature"]').innerHTML, /Jobs/);
    assert.doesNotMatch(panel.querySelector('[name="feature"]').innerHTML, /data-upload/);
});

test('existing log details safely retain multiline messages, source files, and bounded-history notices', async t => {
    const { modal, panels } = harness(t, async () => ({
        entries: [{ id: 'line-42', source: 'application', sourceFile: '<script>.log', feature: 'Startup',
            level: 'Error', message: 'Connection failed\n  at <Service>.Connect()' }],
        truncated: true, readFailures: 1
    }));
    await modal.loadEntries('logs');
    const panel = panels.get('logs');
    assert.match(panel.querySelector('[data-results]').innerHTML, /&lt;script&gt;\.log/);
    modal.showDetails('logs', modal.lists.logs.entries[0], 0);
    const detail = panel.querySelector('[data-detail]').innerHTML;
    assert.match(detail, /Application logs/);
    assert.match(detail, /&lt;script&gt;\.log/);
    assert.match(detail, /Connection failed\n  at &lt;Service&gt;\.Connect\(\)/);
    assert.doesNotMatch(detail, /<script>|<Service>/);
    assert.equal(panel.querySelector('[data-warning]').hidden, false);
    assert.match(panel.querySelector('[data-warning]').textContent, /bounded window/);
    assert.match(panel.querySelector('[data-warning]').textContent, /1 log files or records/);
});
