import test from 'node:test';
import assert from 'node:assert/strict';
import { bindCardAutomations, currentEntryRuns } from '../../../VibeRails/wwwroot/js/modules/board-card-automations.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';

// VIBE-170 B4: the card editor refreshes its Automation picker every 10 s. Rewriting the
// options closed an open dropdown, and every tick refetched the project's job catalog.

const settle = () => new Promise(resolve => setImmediate(resolve));

function control() {
    const listeners = new Map();
    return {
        listeners,
        value: '',
        disabled: true,
        hidden: false,
        textContent: '',
        addEventListener(type, handler) { listeners.set(type, handler); },
        removeEventListener(type, handler) { if (listeners.get(type) === handler) listeners.delete(type); }
    };
}

function automationEditor() {
    let html = '';
    const select = Object.defineProperty(Object.assign(control(), { writes: 0 }), 'innerHTML', {
        get() { return html; },
        set(value) { html = value; this.writes++; }
    });
    const nodes = {
        '[data-board-automation-choice]': select,
        '[data-board-run-automation]': control(),
        '[data-board-automation-message]': control(),
        '[data-board-reload-automations]': control(),
        '[data-board-automation-runs]': Object.assign(control(), { innerHTML: '', querySelectorAll: () => [] })
    };
    const host = { ...control(), isConnected: true, querySelector: selector => nodes[selector] ?? null };
    const editor = {
        querySelector: selector => selector === '[data-board-automation-controls]' ? host : null,
        querySelectorAll: () => []
    };
    return { editor, select, runs: nodes['[data-board-automation-runs]'], host, message: nodes['[data-board-automation-message]'] };
}

function fakeApp(state) {
    const app = {
        data: { configs: { rootPath: 'C:/repo' } },
        showToast() {},
        catalogCalls: 0,
        urls: [],
        async apiCall(url, method = 'GET') {
            app.urls.push(url);
            const single = /^\/api\/v1\/runs\/([^/?]+)(\/retry)?$/.exec(url);
            if (single?.[2] && method === 'POST') {
                if (state.retryFails-- > 0) throw new Error('The launch service is busy.');
                state.retried = (state.retried || 0) + 1;
                return state.onRetry?.(decodeURIComponent(single[1]));
            }
            // One run by id is the read a current lane step may need; a page of runs is not.
            if (single && !single[2] && state.runsById) {
                const run = state.runsById[decodeURIComponent(single[1])];
                if (!run) throw new Error('Run not found in this project.');
                return run;
            }
            if (url === '/api/v1/launch-catalog') {
                app.catalogCalls++;
                if (state.catalogFails-- > 0) throw new Error('catalog unavailable');
                return { items: state.jobs.map(job => ({ ...job, id: String(job.id), kind: 'agent', definitionType: 'preset', canLaunch: true })) };
            }
            if (/^\/api\/v1\/board\/cards\/[^/]+\/launches\/runs$/.test(url)) return { runs: state.cardRuns || [], nextCursor: null };
            if (url.startsWith('/api/v1/runs')) throw new Error('the card dialog must not page every project run');
            return state.laneEntries || [];

        }
    };
    BoardApi.attach(app);
    return app;
}

test('fresh eligibility leaves an unchanged open picker alone', async () => {
    const state = { jobs: [{ id: 1, name: 'Review', enabled: true }, { id: 2, name: 'Deploy', enabled: true }], catalogFails: 0 };
    const app = fakeApp(state);
    const { editor, select } = automationEditor();
    const controls = bindCardAutomations(editor, { id: 'card_a' }, { app });
    await settle();
    assert.equal(select.writes, 1);
    assert.match(select.innerHTML, /<option value="1">Review[\s\S]*<option value="2">Deploy/);

    select.value = '2'; // the user has the dropdown open on Deploy
    await controls.refresh();
    await controls.refresh();
    assert.equal(select.writes, 1, 'an unchanged list is not rewritten, so the dropdown stays open');
    assert.equal(select.value, '2');
    assert.equal(app.catalogCalls, 3, 'eligibility is refreshed');

    state.jobs = [...state.jobs, { id: 3, name: 'Ship', enabled: true }];
    await controls.refresh();
    assert.equal(select.writes, 2, 'a changed list is rewritten');
    assert.match(select.innerHTML, /Ship/);
    assert.equal(select.value, '2', 'and keeps the selection');
    assert.equal(app.catalogCalls, 4);
    controls.dispose();
});

test('a failed catalog offers no unsafe launch until refresh succeeds', async () => {
    const state = { jobs: [{ id: 1, name: 'Review', enabled: true }], catalogFails: 1 };
    const app = fakeApp(state);
    const { editor, select } = automationEditor();
    const controls = bindCardAutomations(editor, { id: 'card_a' }, { app });
    await settle();
    assert.equal(select.writes, 0);

    await controls.refresh();
    assert.equal(app.catalogCalls, 2, 'the failure is retried');
    assert.match(select.innerHTML, /<option value="1">Review/);
    await controls.refresh();
    assert.equal(select.writes, 1, 'unchanged catalog is not repainted');
    controls.dispose();
});

test('card Automation rail displays the saved check outcome and summary', async () => {
    const state = { jobs: [], laneEntries: [{ current: true, processStatus: 'succeeded', verdict: 'passed',
        summary: '5 files analyzed; 7 skipped.' }] };
    const app = fakeApp(state);
    const { editor, runs } = automationEditor();
    const controls = bindCardAutomations(editor, { id: 'card_a' }, { app });
    await settle();
    // VIBE-338: one status pill per step instead of "Process: … · Result: …" block text.
    assert.match(runs.innerHTML, /board-step-pill is-success[^>]*><i[^>]*><\/i>Passed</);
    assert.match(runs.innerHTML, /5 files analyzed; 7 skipped/);
    assert.doesNotMatch(runs.innerHTML, /Process: succeeded/);
    controls.dispose();
});

// VIBE-296: the dialog read `runs?limit=100` for the whole project and filtered by card, so a
// card's runs vanished once 100 newer runs existed elsewhere. The server now scopes them.
test('card runs come from the card-scoped route and are shown as returned', async () => {
    const state = { jobs: [], cardRuns: [
        { id: 'run_new', status: 'running', sessionId: 'session-1', snapshot: { preset: { name: 'Reviewer' }, context: { cardId: 'card a/b' } } },
        { id: 'run_old', status: 'succeeded', snapshot: { action: { name: 'Lint' }, context: { cardId: 'card a/b' } } }
    ] };
    const app = fakeApp(state);
    const { editor, runs } = automationEditor();
    const controls = bindCardAutomations(editor, { id: 'card a/b' }, { app });
    await settle();
    assert.ok(app.urls.includes('/api/v1/board/cards/card%20a%2Fb/launches/runs'), 'the card id is encoded into the scoped route');
    assert.ok(!app.urls.some(url => url.startsWith('/api/v1/runs')), 'no project-wide run page is fetched');
    // VIBE-338: each run is a row with its name and a status pill.
    assert.match(runs.innerHTML, /Reviewer<\/span><span class="board-step-pill is-active">[\s\S]*Running[\s\S]*data-launch-replay="session-1"[\s\S]*Lint<\/span>[\s\S]*Finished/);
    controls.dispose();
});

// VIBE-372 review: the card rail is now the only place on the Board with a lane step's Retry and
// run error, so neither may depend on the card's newest-50 run page, and a refused request must
// leave the control usable.
test('a current step reads its run by id when the card run page no longer holds it', async () => {
    const calls = [];
    const api = { call: async (path) => {
        calls.push(path);
        if (path === 'runs/run_gone') throw new Error('Run not found in this project.');
        return { id: path.slice('runs/'.length), status: 'failed', error: 'boom' };
    } };
    const listed = { id: 'run_listed', status: 'succeeded' };
    const runs = await currentEntryRuns(api,
        [{ runId: 'run_listed' }, { runId: 'run_old' }, { runId: 'run_old' }, { runId: null }, { runId: 'run_gone' }], [listed]);
    assert.deepEqual(calls.sort(), ['runs/run_gone', 'runs/run_old'], 'each missing run is read once, and never a page');
    assert.equal(runs.get('run_listed'), listed);
    assert.equal(runs.get('run_old').error, 'boom');
    assert.equal(runs.has('run_gone'), false, 'an unreadable run is left out; the step keeps its recorded state');

    // In the rail: fifty newer runs fill the page, and the failed step still has its error and Retry.
    const state = { jobs: [], laneEntries: [{ id: 'entry_1', current: true, runId: 'run_old', processStatus: 'failed' }],
        cardRuns: Array.from({ length: 50 }, (_, index) => ({ id: `run_newer_${index}`, status: 'succeeded', snapshot: { action: { name: 'Check' } } })),
        runsById: { run_old: { id: 'run_old', status: 'failed', error: 'The build could not start.' } } };
    const app = fakeApp(state);
    const { editor, runs: list } = automationEditor();
    const controls = bindCardAutomations(editor, { id: 'card_a' }, { app });
    await settle();
    assert.match(list.innerHTML, /data-launch-retry="run_old"/);
    assert.match(list.innerHTML, /The build could not start\./);
    assert.deepEqual(app.urls.filter(url => url.startsWith('/api/v1/runs')), ['/api/v1/runs/run_old']);
    controls.dispose();
});

test('a refused Retry leaves its button usable, and the next press goes through', async () => {
    const run = { id: 'run_1', status: 'failed', error: 'The build could not start.' };
    const state = { jobs: [], laneEntries: [{ id: 'entry_1', current: true, runId: 'run_1', processStatus: 'failed' }],
        cardRuns: [run], retryFails: 1, onRetry: () => { run.status = 'queued'; run.error = null; return run; } };
    const app = fakeApp(state);
    const { editor, runs: list, host, message } = automationEditor();
    const controls = bindCardAutomations(editor, { id: 'card_a' }, { app });
    await settle();
    assert.match(list.innerHTML, /data-launch-retry="run_1"/);
    const before = list.innerHTML;
    const retry = { dataset: { launchRetry: 'run_1' }, disabled: false, isConnected: true };
    const press = () => host.listeners.get('click')({ target: { closest: () => retry } });

    press();
    assert.equal(retry.disabled, true, 'disabled while the request is in flight');
    await settle(); await settle();
    assert.equal(message.textContent, 'The launch service is busy.');
    assert.equal(list.innerHTML, before, 'nothing changed, so the list is not redrawn');
    assert.equal(retry.disabled, false, 'so the button has to be enabled again by hand');

    press();
    await settle(); await settle();
    assert.equal(state.retried, 1);
    assert.equal(message.textContent, '');
    assert.match(list.innerHTML, /Queued/);
    assert.doesNotMatch(list.innerHTML, /data-launch-retry/);
    controls.dispose();
});
