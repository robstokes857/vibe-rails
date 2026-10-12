import test from 'node:test';
import assert from 'node:assert/strict';
import { cardReviewsSection, bindCardReviews } from '../../../VibeRails/wwwroot/js/modules/board-card-reviews.js';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

test('coding source excludes managers, planning workers and reviewers while retaining coding workers', () => {
    const sessions = [
        { id: 'manager', origin: 'agent_manager' },
        { id: 'planning-worker', origin: 'agent_worker_planning' },
        { id: 'coding-worker', origin: 'agent_worker' },
        { id: 'review-worker', origin: 'agent_worker', isReview: true },
        { id: 'manual-planning', origin: 'planning' },
        { id: 'manual-coding', origin: 'launch' },
        { id: 'manual-review', origin: 'code_review' },
        { id: 'discussion', origin: 'chat' },
        { id: 'attached-review', origin: 'mcp', isReview: true },
        { id: 'attached-manager', origin: 'mcp', isReview: false, isNonCoding: true },
        { id: 'attached-planner', origin: 'mcp', isReview: false, isNonCoding: true },
        { id: 'attached-coder', origin: 'mcp', isReview: false, isNonCoding: false }
    ];
    const html = cardReviewsSection({ id: 'card', sessions });
    assert.deepEqual([...html.matchAll(/<option value="session:([^"]+)"/g)].map(match => match[1]),
        ['coding-worker', 'manual-coding', 'attached-coder']);
});

test('planning workers retain their worker role in the session list', t => {
    const originalWindow = globalThis.window;
    globalThis.window = {};
    t.after(() => { globalThis.window = originalWindow; });
    const host = { innerHTML: '', querySelectorAll: () => [] };
    const editor = { querySelector: selector => selector === '[data-board-sessions]' ? host : null };
    new BoardController({}).renderSessionList(editor, { id: 'card' }, [
        { id: 'planner', origin: 'agent_worker_planning', displayName: 'Plan', cli: 'claude' }
    ], 'sessions');
    assert.match(host.innerHTML, /Worker \(planning\) · Claude · ended/);
});

test('loading a saved excluded source keeps it visible but unavailable for selection', async t => {
    const originalDocument = globalThis.document;
    globalThis.document = { createElement: () => ({}) };
    t.after(() => { globalThis.document = originalDocument; });
    const fields = new Map();
    const field = name => {
        if (!fields.has(name)) fields.set(name, { value: '', options: [], addEventListener() {}, removeEventListener() {},
            setAttribute() {}, append(option) { this.options.push(option); }, querySelector: () => null });
        return fields.get(name);
    };
    field('source').options = [{ value: 'unknown' }, { value: 'session:coder' }];
    field('target-host').querySelector = selector => field(selector);
    const host = { isConnected: true, addEventListener() {}, removeEventListener() {},
        querySelector(selector) {
            const name = selector.slice('[data-review-'.length, -1);
            return name === 'routing-editor' ? null : field(name);
        } };
    const calls = [];
    const app = {
        llmPickerController: { mount(picker, options) { picker.value = options.selectedValue; return () => {}; } },
        async apiCall(url) {
            calls.push(url);
            if (url.endsWith('/settings')) return { sourceKind: 'session', sourceSessionId: 'manager', scope: 'working-tree' };
            if (url.endsWith('/preview')) return { source: { kind: 'unknown' }, problem: 'Manager sessions cannot be coding evidence.' };
            return { reviews: [] };
        }
    };
    const binding = bindCardReviews({ querySelector: () => host }, { id: 'card' }, app);
    t.after(() => binding.dispose());
    await new Promise(resolve => setImmediate(resolve));
    const saved = field('source').options.find(option => option.value === 'session:manager');
    assert.equal(saved.disabled, true);
    assert.match(saved.textContent, /Unavailable coding session manager/);
    assert.equal(field('source').value, 'session:manager');
    assert.equal(field('run').disabled, true);
    assert.ok(calls.some(url => url.endsWith('/preview')));
});
