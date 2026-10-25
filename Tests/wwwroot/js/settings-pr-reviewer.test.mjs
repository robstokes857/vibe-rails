import test from 'node:test';
import assert from 'node:assert/strict';
import { PrReviewerSettingsPanel } from '../../../VibeRails/wwwroot/js/modules/settings-pr-reviewer.js';

function harness(apiCall) {
    const toggle = { checked: false, disabled: true, addEventListener() {} };
    const status = { textContent: '' };
    const repositories = { value: '', disabled: true };
    const saveRepositories = { disabled: true, addEventListener() {} };
    const reload = { addEventListener() {} };
    const root = { querySelector: s => s.includes('enabled') ? toggle : s.includes('status') ? status : s === '[data-pr-reviewer-repositories]' ? repositories : s.includes('save-repositories') ? saveRepositories : reload };
    return { toggle, status, repositories, saveRepositories, panel: new PrReviewerSettingsPanel({ apiCall, updatePrReviewerNav() {} }, root) };
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

test('watch list supports adding editing and clearing; switch saves preserve pending repository edits', async () => {
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
    assert.equal(h.repositories.value, 'octo/widgets');
    h.repositories.value = 'octo/widgets\n else/tools \n\nOCTO/WIDGETS';
    await h.panel.save();
    assert.deepEqual(writes[0], { enabled: true });
    assert.match(h.repositories.value, /else\/tools/);
    await h.panel.save(true);
    assert.deepEqual(saved, ['octo/widgets', 'else/tools']);
    assert.equal(h.repositories.value, 'octo/widgets\nelse/tools');
    h.repositories.value = 'else/renamed';
    await h.panel.save(true);
    assert.deepEqual(saved, ['else/renamed']);
    h.repositories.value = '';
    await h.panel.save(true);
    assert.deepEqual(saved, []);
    assert.equal(h.saveRepositories.disabled, false);
});
