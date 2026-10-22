import test from 'node:test';
import assert from 'node:assert/strict';
import { SessionController } from '../../../VibeRails/wwwroot/js/modules/session-controller.js';

test('session load failures render error messages as escaped text', async t => {
    const original = globalThis.document;
    const list = {};
    globalThis.document = { querySelector: () => list };
    t.after(() => { globalThis.document = original; });
    const controller = new SessionController({ apiCall: async () => {
        throw new Error('<img src=x onerror="alert(1)"> & failed');
    } });
    await controller.fetchAndRenderSessions();
    assert.match(list.innerHTML, /&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt; &amp; failed/);
    assert.doesNotMatch(list.innerHTML, /<img/);
});
