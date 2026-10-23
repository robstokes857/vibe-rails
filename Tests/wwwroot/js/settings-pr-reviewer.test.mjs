import test from 'node:test';
import assert from 'node:assert/strict';
import { PrReviewerSettingsPanel } from '../../../VibeRails/wwwroot/js/modules/settings-pr-reviewer.js';

function harness(apiCall) {
    const toggle = { checked: false, disabled: true, addEventListener() {} };
    const status = { textContent: '' };
    const reload = { addEventListener() {} };
    const root = { querySelector: s => s.includes('enabled') ? toggle : s.includes('status') ? status : reload };
    return { toggle, status, panel: new PrReviewerSettingsPanel({ apiCall }, root) };
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
