import test from 'node:test';
import assert from 'node:assert/strict';
import { PrReviewerSettingsPanel } from '../../../VibeRails/wwwroot/js/modules/settings-pr-reviewer.js';

function harness(apiCall) {
    const toggle = { checked: false, disabled: true, addEventListener() {} };
    const status = { textContent: '' };
    const repositoryInput = { value: '', disabled: true, focus() {} };
    const repositoryList = { hidden: true, innerHTML: '', addEventListener() {}, querySelectorAll: () => [] };
    const addButton = { disabled: true };
    const form = { addEventListener() {} };
    const reload = { addEventListener() {} };
    const fields = { enabled: toggle, status, repositories: repositoryList, repository: repositoryInput,
        'add-repository': addButton, 'repository-form': form, reload };
    const root = { querySelector: s => fields[s.slice('[data-pr-reviewer-'.length, -1)] };
    return { toggle, status, repositoryInput, repositoryList, addButton, panel: new PrReviewerSettingsPanel({ apiCall, updatePrReviewerNav() {} }, root) };
}

test('reviewer switch loads persisted state and saves changes through its dedicated API', async () => {
    let saved = true;
    const calls = [];
    const h = harness(async (path, method, body) => {
        calls.push([path, method, body]);
        if (method === 'PUT') saved = body.enabled;
        return { enabled: saved };
    });
    await h.panel.load();
    assert.equal(h.toggle.checked, true);
    h.toggle.checked = false;
    await h.panel.save();
    assert.equal(saved, false);
    assert.equal(h.toggle.disabled, false);
    assert.match(h.status.textContent, /Saved:.*off/);
    assert.deepEqual(calls[1], ['/api/v1/pr-reviews/settings', 'PUT', { enabled: false }]);
});

test('failed save never claims success and requires a reload of the actual persisted setting', async () => {
    const h = harness(async (_, method) => { if (method === 'PUT') throw Error('Connection lost.'); return { enabled: true }; });
    await h.panel.load();
    h.toggle.checked = false;
    await h.panel.save();
    assert.equal(h.toggle.disabled, true);
    assert.match(h.status.textContent, /Connection lost.*Reload/);
    await h.panel.load();
    assert.equal(h.toggle.checked, true);
    assert.equal(h.toggle.disabled, false);
});

test('unloading ignores late reads and aborts the request', async () => {
    let resolve, signal;
    const h = harness((_, __, ___, options) => { signal = options.signal; return new Promise(r => resolve = r); });
    const pending = h.panel.load();
    h.panel.unload();
    resolve({ enabled: true });
    await pending;
    assert.equal(signal.aborted, true);
    assert.equal(h.toggle.checked, false);
    assert.equal(h.toggle.disabled, true);
});

test('watch list adds and removes one repository at a time; switch saves preserve the typed entry', async () => {
    let saved = ['octo/widgets'];
    const writes = [];
    const h = harness(async (_, method, body) => {
        if (method === 'PUT') {
            writes.push(body);
            if (body.repositories) saved = [...new Set(body.repositories.map(s => s.toLowerCase()))];
        }
        return { enabled: true, repositories: saved };
    });
    await h.panel.load();
    assert.match(h.repositoryList.innerHTML, /octo\/widgets/);
    h.repositoryInput.value = ' else/tools ';
    await h.panel.save();
    assert.deepEqual(writes[0], { enabled: true });
    assert.equal(h.repositoryInput.value, ' else/tools ');
    await h.panel.addRepository();
    assert.deepEqual(saved, ['octo/widgets', 'else/tools']);
    assert.equal(h.repositoryInput.value, '');
    h.repositoryInput.value = 'OCTO/WIDGETS';
    await h.panel.addRepository();
    assert.match(h.status.textContent, /already in the list/);
    h.repositoryInput.value = 'https://github.com/else/tools';
    await h.panel.addRepository();
    assert.match(h.status.textContent, /owner\/repository/);
    assert.equal(writes.length, 2, 'duplicates and invalid input do not write settings');
    await h.panel.removeRepository(0);
    assert.deepEqual(saved, ['else/tools']);
    await h.panel.removeRepository(0);
    assert.deepEqual(saved, []);
    assert.equal(h.repositoryList.hidden, true);
    assert.equal(h.addButton.disabled, false);
});

test('an unconfirmed add preserves the saved list and typed repository until reload', async () => {
    let rejectWrite;
    const h = harness(async (_, method) => {
        if (method === 'PUT') return new Promise((_, reject) => { rejectWrite = reject; });
        return { enabled: true, repositories: ['octo/widgets'] };
    });
    await h.panel.load();
    h.repositoryInput.value = 'else/tools';
    const pending = h.panel.addRepository();
    assert.equal(h.toggle.disabled, true);
    assert.equal(h.addButton.disabled, true);
    await h.panel.removeRepository(0);
    rejectWrite(Error('Connection lost.'));
    await pending;
    assert.deepEqual(h.panel.repositories, ['octo/widgets']);
    assert.equal(h.repositoryInput.value, 'else/tools');
    assert.equal(h.repositoryInput.disabled, true);
    assert.match(h.status.textContent, /Connection lost.*Reload/);
    await h.panel.load();
    assert.equal(h.repositoryInput.disabled, false);
    assert.equal(h.repositoryInput.value, 'else/tools');
});
