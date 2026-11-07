import test from 'node:test';
import assert from 'node:assert/strict';
import { bindCardAutomations } from '../../../VibeRails/wwwroot/js/modules/board-card-automations.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';

// VIBE-170 B4: the card editor refreshes its Automation picker every 10 s. Rewriting the
// options closed an open dropdown, and every tick refetched the project's job catalog.

const settle = () => new Promise(resolve => setImmediate(resolve));

function control() {
    const listeners = new Map();
    return {
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
        '[data-board-automation-runs]': Object.assign(control(), { innerHTML: '' })
    };
    const host = { ...control(), isConnected: true, querySelector: selector => nodes[selector] ?? null };
    const editor = {
        querySelector: selector => selector === '[data-board-automation-controls]' ? host : null,
        querySelectorAll: () => []
    };
    return { editor, select, runs: nodes['[data-board-automation-runs]'] };
}

function fakeApp(state) {
    const app = {
        data: { configs: { rootPath: 'C:/repo' } },
        showToast() {},
        catalogCalls: 0,
        urls: [],
        async apiCall(url) {
            app.urls.push(url);
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
    assert.match(runs.innerHTML, /Process: succeeded · Result: passed/);
    assert.match(runs.innerHTML, /5 files analyzed; 7 skipped/);
    assert.doesNotMatch(runs.innerHTML, />Passed</);
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
    assert.match(runs.innerHTML, /Reviewer · running[\s\S]*data-launch-replay="session-1"[\s\S]*Lint · succeeded/);
    controls.dispose();
});
