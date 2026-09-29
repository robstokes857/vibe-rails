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

test('switching publishing on asks what leaves the machine; cancel sends nothing and pausing asks nothing', async () => {
    const handlers = {};
    const content = { innerHTML: '' };
    const element = { isConnected: true, querySelector: () => content,
        addEventListener: (name, action) => { handlers[name] = action; },
        removeEventListener: name => delete handlers[name] };
    let status = { published: false, enabled: false, configured: true, unsent: 0, lastError: null, rejected: 0 };
    const writes = [];
    BoardApi.attach({ apiCall: async (path, method, body) => {
        if (method === 'PUT') { writes.push(body); status = { ...status, published: true, enabled: !!body.enabled }; }
        return status;
    } });
    const prompts = [];
    let answer = false;
    const dispose = mountBoardSync({ showToast() {} }, element, 'board_a', { confirm: async options => { prompts.push(options); return answer; } });
    await new Promise(resolve => setImmediate(resolve));
    assert.match(content.innerHTML, /data-board-sync-action="publish"/);

    const control = { dataset: { boardSyncAction: 'publish' }, disabled: false };
    const clickOn = target => ({ defaultPrevented: false, preventDefault() { this.defaultPrevented = true; },
        target: { closest: selector => selector === '[data-board-sync-action]' ? target : null } });
    let event = clickOn(control);
    await handlers.click(event);
    assert.equal(event.defaultPrevented, true, 'the switch stays off until the dialog is confirmed');
    assert.equal(prompts.length, 1);
    assert.match(prompts[0].message, /Linked sessions, saved commit code, linked cards and attachments.*appear remotely/);
    assert.match(prompts[0].message, /Files over 1 MiB keep metadata only/);
    assert.match(prompts[0].message, /Launch options, Automation settings and environment definitions stay on this machine/);
    assert.match(prompts[0].message, /comments, agent notes and change history/);
    assert.deepEqual(writes, []);
    assert.equal(control.disabled, false);

    answer = true;
    event = clickOn(control);
    await handlers.click(event);
    assert.deepEqual(writes, [{ enabled: true, includeActivity: true }]);
    assert.match(content.innerHTML, /data-board-sync-action="unpublish" checked/);

    event = clickOn({ dataset: { boardSyncAction: 'unpublish' } });
    await handlers.click(event);
    assert.equal(event.defaultPrevented, false, 'pausing needs no confirmation');
    assert.equal(prompts.length, 2);
    assert.deepEqual(writes, [{ enabled: true, includeActivity: true }, { enabled: false, includeActivity: false }]);
    dispose();
});

test('an existing publication can enable linked activity after consent without pausing core sync', async () => {
    const handlers = {};
    const content = { innerHTML: '' };
    const element = { isConnected: true, querySelector: () => content,
        addEventListener: (name, action) => { handlers[name] = action; }, removeEventListener() {} };
    let status = { published: true, enabled: true, configured: true, activityEnabled: false };
    const writes = [];
    BoardApi.attach({ apiCall: async (path, method, body) => {
        if (method === 'PUT') { writes.push(body); status = { ...status, activityEnabled: body.includeActivity }; }
        return status;
    } });
    let consent = false;
    const dispose = mountBoardSync({ showToast() {} }, element, 'board_a', { confirm: async () => consent });
    await new Promise(resolve => setImmediate(resolve));
    assert.match(content.innerHTML, /Sync linked activity/);
    const control = { dataset: { boardSyncAction: 'activity' }, disabled: false };
    const event = () => ({ preventDefault() {}, target: { closest: selector => selector === '[data-board-sync-action]' ? control : null } });
    await handlers.click(event());
    assert.deepEqual(writes, []);
    assert.equal(status.enabled, true);
    consent = true;
    await handlers.click(event());
    assert.deepEqual(writes, [{ enabled: true, includeActivity: true }]);
    assert.equal(status.enabled, true);
    assert.doesNotMatch(content.innerHTML, /data-board-sync-action="activity"/);
    dispose();
});
