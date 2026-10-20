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
    const host = { isConnected: true, querySelector: selector => nodes[selector] ?? null };
    const editor = {
        querySelector: selector => selector === '[data-board-automation-controls]' ? host : null,
        querySelectorAll: () => []
    };
    return { editor, select };
}

function fakeApp(state) {
    const app = {
        data: { configs: { rootPath: 'C:/repo' } },
        showToast() {},
        catalogCalls: 0,
        async apiCall(url) {
            if (url.startsWith('/api/v1/jobs?')) {
                app.catalogCalls++;
                if (state.catalogFails-- > 0) throw new Error('catalog unavailable');
                return { jobs: [{ id: 1, actions: [{ kind: 0, environmentId: 7 }] }, { id: 2, actions: [] }] };
            }
            return { jobs: state.jobs, runs: [], laneEntries: [] };
        }
    };
    BoardApi.attach(app);
    return app;
}

test('a refresh with the same jobs leaves the open picker alone and reads the catalog once', async () => {
    const state = { jobs: [{ id: 1, name: 'Review', enabled: true }, { id: 2, name: 'Deploy', enabled: true }], catalogFails: 0 };
    const app = fakeApp(state);
    const { editor, select } = automationEditor();
    const controls = bindCardAutomations(editor, { id: 'card_a' }, { app });
    await settle();
    assert.equal(select.writes, 1);
    assert.match(select.innerHTML, /<optgroup label="Agents"><option value="1">Review[\s\S]*<optgroup label="Automations"><option value="2">Deploy/);

    select.value = '2'; // the user has the dropdown open on Deploy
    await controls.refresh();
    await controls.refresh();
    assert.equal(select.writes, 1, 'an unchanged list is not rewritten, so the dropdown stays open');
    assert.equal(select.value, '2');
    assert.equal(app.catalogCalls, 1, 'the catalog is read once per editor open');

    state.jobs = [...state.jobs, { id: 3, name: 'Ship', enabled: true }];
    await controls.refresh();
    assert.equal(select.writes, 2, 'a changed list is rewritten');
    assert.match(select.innerHTML, /Ship/);
    assert.equal(select.value, '2', 'and keeps the selection');
    assert.equal(app.catalogCalls, 1);
    controls.dispose();
});

test('a failed catalog read only loses the grouping until a later refresh succeeds', async () => {
    const state = { jobs: [{ id: 1, name: 'Review', enabled: true }], catalogFails: 1 };
    const app = fakeApp(state);
    const { editor, select } = automationEditor();
    const controls = bindCardAutomations(editor, { id: 'card_a' }, { app });
    await settle();
    assert.match(select.innerHTML, /<optgroup label="Automations"><option value="1">Review/);

    await controls.refresh();
    assert.equal(app.catalogCalls, 2, 'the failure is retried');
    assert.match(select.innerHTML, /<optgroup label="Agents"><option value="1">Review/);
    await controls.refresh();
    assert.equal(app.catalogCalls, 2, 'and the success is kept');
    controls.dispose();
});
