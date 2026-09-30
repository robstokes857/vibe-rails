import test from 'node:test';
import assert from 'node:assert/strict';
import { mountBoardSync } from '../../../VibeRails/wwwroot/js/modules/board-settings.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';

test('successful sync still shows retained rejection identities and repair guidance safely', async () => {
    const handlers = {};
    const content = { innerHTML: '' };
    const element = { isConnected: true, querySelector: () => content,
        addEventListener: (name, action) => { handlers[name] = action; },
        removeEventListener: name => delete handlers[name] };
    const status = { published: true, enabled: true, configured: true, unsent: 0, lastError: null,
        rejected: 1, rejectedEntries: [{ cardKey: '<img src=x>', kind: 'change', entryId: '<script>bad()</script>' }] };
    BoardApi.attach({ apiCall: async () => status });
    const toasts = [];
    const dispose = mountBoardSync({ showToast: (...args) => toasts.push(args) }, element, 'board_a');
    await new Promise(resolve => setImmediate(resolve));
    assert.match(content.innerHTML, /data-board-sync-rejected/);
    assert.match(content.innerHTML, /1 rejected entries/);
    assert.match(content.innerHTML, /create a replacement card/);
    assert.match(content.innerHTML, /&lt;script&gt;/);
    assert.doesNotMatch(content.innerHTML, /<script>|<img/);
    const control = { dataset: { boardSyncAction: 'now' } };
    await handlers.click({ target: { closest: selector => selector === '[data-board-sync-action]' ? control : null } });
    assert.equal(toasts[0][2], 'warning');
    assert.match(content.innerHTML, /data-board-sync-rejected/);
    dispose();
});

test('skipped remote entries are counted and listed safely, with the latest-50 note', async () => {
    const content = { innerHTML: '' };
    const element = { isConnected: true, querySelector: () => content, addEventListener() {}, removeEventListener() {} };
    const status = { published: true, enabled: true, configured: true, unsent: 0, lastError: null, rejected: 0,
        skipped: 51, skippedEntries: [{ cardKey: 'VB-ABCDE-7', kind: 'restored', reason: '<b>cannot apply</b>', entryId: 'web_1', seq: 9 }] };
    BoardApi.attach({ apiCall: async () => status });
    const dispose = mountBoardSync({ showToast() {} }, element, 'board_a');
    await new Promise(resolve => setImmediate(resolve));
    assert.match(content.innerHTML, /data-board-sync-skipped/);
    assert.match(content.innerHTML, /51 changes from viberails\.ai could not be applied/);
    assert.match(content.innerHTML, /VB-ABCDE-7 · restored · &lt;b&gt;cannot apply&lt;\/b&gt;/);
    assert.match(content.innerHTML, /latest 50 skipped changes/);
    assert.doesNotMatch(content.innerHTML, /<b>cannot/);
    assert.doesNotMatch(content.innerHTML, /data-board-sync-rejected/);
    dispose();
});

test('configured boards offer automatic sync with no publication or activity switch', async () => {
    const handlers = {}, content = { innerHTML: '' }, writes = [];
    const element = { isConnected: true, querySelector: () => content,
        addEventListener: (name, action) => { handlers[name] = action; }, removeEventListener() {} };
    BoardApi.attach({ apiCall: async (path, method) => {
        if (method === 'POST') writes.push(path);
        return { configured: true, published: false, enabled: true };
    } });
    const dispose = mountBoardSync({ showToast() {} }, element, 'board_a');
    await new Promise(resolve => setImmediate(resolve));
    assert.match(content.innerHTML, /Automatic sync is on/);
    assert.match(content.innerHTML, /data-board-sync-action="now"/);
    assert.doesNotMatch(content.innerHTML, /type="checkbox"|action="publish"|action="activity"|action="unpublish"/);
    const control = { dataset: { boardSyncAction: 'now' } };
    await handlers.click({ target: { closest: selector => selector === '[data-board-sync-action]' ? control : null } });
    assert.equal(writes.length, 1);
    dispose();
});

test('without a key the board explains how to connect and offers no sync action', async () => {
    const content = { innerHTML: '' };
    const element = { isConnected: true, querySelector: () => content, addEventListener() {}, removeEventListener() {} };
    BoardApi.attach({ apiCall: async () => ({ configured: false }) });
    const dispose = mountBoardSync({ showToast() {} }, element, 'board_a');
    await new Promise(resolve => setImmediate(resolve));
    assert.match(content.innerHTML, /Sign in from the navigation/);
    assert.doesNotMatch(content.innerHTML, /data-board-sync-action/);
    dispose();
});
