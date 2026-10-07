import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { mountBoardContext, mountBoardSync, mountLaneAutomation } from '../../../VibeRails/wwwroot/js/modules/board-settings.js';

test('failed publication reloads the saved sync choice', async () => {
    const listeners = new Map();
    const content = { innerHTML: '' };
    const element = {
        isConnected: true,
        querySelector: () => content,
        addEventListener: (name, listener) => listeners.set(name, listener),
        removeEventListener: name => listeners.delete(name)
    };
    const toasts = [];
    let reads = 0;
    BoardApi.attach({
        apiCall: (_path, method) => {
            if (method === 'GET') return Promise.resolve({ enabled: ++reads > 1, configured: true, lastError: reads > 1 ? 'Publication failed' : null });
            assert.equal(method, 'PUT');
            return Promise.reject(new Error('Publication failed'));
        }
    });
    const dispose = mountBoardSync({ showToast: (...args) => toasts.push(args) }, element, 'board-id');
    await new Promise(resolve => setImmediate(resolve));
    const target = { checked: true, disabled: false, matches: selector => selector === '[data-board-sync-enabled]' };

    await listeners.get('change')({ target });

    assert.equal(reads, 2);
    assert.match(content.innerHTML, /data-board-sync-enabled checked/);
    assert.match(content.innerHTML, /Publication failed/);
    assert.doesNotMatch(content.innerHTML, /This board stays local/);
    assert.equal(toasts[0][2], 'error');
    dispose();
});

test('closing settings suppresses a late board-status callback', async () => {
    let finish;
    let signal;
    const content = { innerHTML: 'loading' };
    const element = {
        isConnected: true,
        querySelector: () => content,
        addEventListener() {}, removeEventListener() {}
    };
    BoardApi.attach({ apiCall: (_path, _method, _body, options) => {
        signal = options.signal;
        return new Promise(resolve => { finish = resolve; });
    } });
    const dispose = mountBoardSync({}, element, 'closed-board', () => assert.fail('must not update a closed editor'));
    dispose();
    assert.equal(signal.aborted, true);
    finish({ isJiraBoard: true });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(content.innerHTML, 'loading');
});

for (const mount of [mountBoardContext, mountLaneAutomation]) {
    test(`${mount.name} aborts on close and ignores a late response`, async () => {
        let finish;
        let signal;
        const content = { innerHTML: 'loading' };
        const element = {
            isConnected: true,
            querySelector: () => content,
            querySelectorAll: () => [],
            addEventListener() {}, removeEventListener() {}
        };
        const app = {
            apiCall: (_path, _method, _body, options) => {
                signal = options.signal;
                return new Promise(resolve => { finish = resolve; });
            },
            showToast: () => assert.fail('a disposed modal must not show errors')
        };
        BoardApi.attach(app);
        const dispose = mount(app, element, 'scoped-id');
        dispose();
        assert.equal(signal.aborted, true);
        finish({ revision: 1, context: { defaultMessage: 'late', typeOverrides: [] }, jobId: 2, jobs: [] });
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(content.innerHTML, 'loading');
    });
}
