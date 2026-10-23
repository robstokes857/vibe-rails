import test from 'node:test';
import assert from 'node:assert/strict';
import { PrReviewsController } from '../../../VibeRails/wwwroot/js/modules/pr-reviews-controller.js';

const pending = { id: 10, status: 'in_progress', feedback: 'saved feedback', verdict: 'comments', blocking: false };
const completed = { id: 9, status: 'completed', feedback: 'old review', verdict: 'comments', blocking: false };
const detail = {
    repository: 'octo/widgets', url: 'https://github.com/octo/widgets/pull/7',
    pullRequest: { number: 7, title: 'Widgets', author: 'octocat', summary: 'saved summary', status: 'in_review' },
    reviews: [pending, completed]
};

function harness(apiCall = async () => detail) {
    const body = { innerHTML: '' };
    const controls = ['button', 'input', 'textarea', 'select'].map(type => ({
        type, disabled: false, matches: () => false
    }));
    const controller = new PrReviewsController({ apiCall, showToast() {} });
    controller.root = {
        contains: () => true,
        querySelector: selector => selector === '[data-pr-detail-body]' ? body : null,
        querySelectorAll: () => controls
    };
    controller.state = { ...controller.emptyState(7), detail };
    return { controller, body, controls };
}

function form(selector, values, dataset = {}) {
    return {
        dataset,
        matches: candidate => candidate === selector,
        elements: Object.fromEntries(Object.entries(values).map(([key, value]) => [
            key, typeof value === 'boolean' ? { checked: value } : { value }
        ]))
    };
}

function inputIn(form) {
    return { closest: selector => selector === 'form' ? form : null };
}

function click(selector, dataset = {}, parentForm = null) {
    const button = {
        dataset,
        matches: candidate => candidate === selector,
        closest: candidate => candidate === 'form' ? parentForm : button
    };
    return { target: button };
}

function deferred() {
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    return { promise, resolve };
}

test('editing and cancelling history preserves unsaved pending feedback, summary and blocker', async () => {
    const { controller, body } = harness();
    const draft = form('[data-pr-pending-form]', {
        feedback: 'unsaved <finding>', summary: 'unsaved overview', verdict: 'request_changes', blocking: true
    }, { prPendingForm: '10' });
    controller.rememberForm(inputIn(draft));

    await controller.onClick(click('[data-pr-edit-review]', { prEditReview: '9' }));

    assert.match(body.innerHTML, /unsaved &lt;finding&gt;/);
    assert.match(body.innerHTML, /unsaved overview/);
    assert.match(body.innerHTML, /Complete as blocker/);
    assert.match(body.innerHTML, /data-pr-review-edit="9"/);
    await controller.onClick(click('[data-pr-cancel-edit]'));
    assert.match(body.innerHTML, /unsaved &lt;finding&gt;/);
    assert.match(body.innerHTML, /Complete as blocker/);
});

test('editing PR metadata preserves pending and history drafts across re-renders', async () => {
    const { controller, body } = harness();
    controller.state.editingReviewId = 9;
    controller.rememberForm(inputIn(form('[data-pr-review-edit]', {
        feedback: 'edited history', verdict: 'comments', blocking: true
    }, { prReviewEdit: '9' })));
    await controller.onClick(click('[data-pr-edit-pr]'));
    assert.match(body.innerHTML, /edited history/);
    controller.rememberForm(inputIn(form('[data-pr-pr-edit]', {
        title: 'draft title', author: 'draft author', summary: 'draft summary'
    })));
    controller.renderDetail();
    assert.match(body.innerHTML, /value="draft title"/);
    assert.match(body.innerHTML, /draft summary/);
    assert.match(body.innerHTML, /edited history/);
});

test('a draft for a completed pass never overwrites a new pending pass', () => {
    const { controller, body } = harness();
    controller.state.pendingDraft = { id: 8, feedback: 'obsolete draft' };
    controller.renderDetail();
    assert.match(body.innerHTML, /saved feedback/);
    assert.doesNotMatch(body.innerHTML, /obsolete draft/);
});

test('a detail-page start requests the current GitHub head', async () => {
    const calls = [];
    const { controller } = harness(async (path, method, body) => {
        calls.push({ path, method, body });
        return detail;
    });
    controller.state.detail = { ...detail, reviews: [completed] };

    await controller.onClick(click('[data-pr-start-review]'));

    assert.deepEqual(calls[0], {
        path: '/api/v1/pr-reviews/7/reviews/start', method: 'POST', body: { useCurrentHead: true }
    });
});

test('saving disables form fields through the detail reload and clears only the submitted draft', async () => {
    const write = deferred();
    const read = deferred();
    const { controller, body, controls } = harness(async (_path, method) => method === 'POST' ? write.promise : read.promise);
    const draft = form('[data-pr-pending-form]', {
        feedback: 'new finding', summary: 'new summary', verdict: 'comments', blocking: false
    }, { prPendingForm: '10' });
    controller.rememberForm(inputIn(draft));
    controller.state.reviewDrafts[9] = { feedback: 'other unsaved form' };
    controller.state.editingReviewId = 9;

    const saving = controller.onClick(click('[data-pr-save-draft]', {}, draft));
    assert.ok(controls.every(control => control.disabled));
    write.resolve({});
    await new Promise(resolve => setImmediate(resolve));
    assert.ok(controls.every(control => control.disabled), 'inputs must remain disabled during the re-read');
    read.resolve({ ...detail, reviews: [{ ...pending, feedback: 'new finding' }, completed] });
    await saving;

    assert.ok(controls.every(control => !control.disabled));
    assert.equal(controller.state.pendingDraft, null);
    assert.match(body.innerHTML, /new finding/);
    assert.match(body.innerHTML, /other unsaved form/);
});

test('a failed save retains unsaved fields', async () => {
    const { controller, body } = harness(async () => { throw new Error('offline'); });
    const draft = form('[data-pr-pending-form]', {
        feedback: 'keep this', summary: '', verdict: 'request_changes', blocking: true
    }, { prPendingForm: '10' });
    controller.rememberForm(inputIn(draft));

    await controller.onClick(click('[data-pr-save-draft]', {}, draft));
    controller.renderDetail();

    assert.match(body.innerHTML, /keep this/);
    assert.match(body.innerHTML, /Complete as blocker/);
});

test('an old save cannot clear edit state after navigating to another PR', async () => {
    const write = deferred();
    const { controller } = harness(() => write.promise);
    controller.state.editingReviewId = 9;
    const edit = form('[data-pr-review-edit]', {
        feedback: 'old edit', verdict: 'comments', blocking: false
    }, { prReviewEdit: '9' });
    const saving = controller.onSubmit({ target: edit, preventDefault() {} });
    controller.generation += 1;
    controller.state = { ...controller.emptyState(8), editingReviewId: 25 };
    write.resolve({});
    await saving;

    assert.equal(controller.state.editingReviewId, 25);
});

test('a later refresh recovers when the initial local list failed', async () => {
    let listCalls = 0;
    let queueCalls = 0;
    const { controller } = harness(async path => {
        if (path.endsWith('/queue')) {
            queueCalls += 1;
            return { queue: [], skipped: [], counts: { open: 0 } };
        }
        listCalls += 1;
        if (listCalls === 1) throw new Error('temporary failure');
        return { repository: 'octo/widgets', pullRequests: [] };
    });
    controller.state = controller.emptyState(null);
    await controller.loadList(controller.generation);
    assert.equal(controller.state.list, null);

    await controller.refreshGitHub(controller.generation);

    assert.equal(queueCalls, 1);
    assert.equal(controller.state.listError, '');
    assert.equal(controller.pollPending, false);
});
