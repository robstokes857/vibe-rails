import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { mountBoardContext, mountLaneAutomation } from '../../../VibeRails/wwwroot/js/modules/board-settings.js';

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
