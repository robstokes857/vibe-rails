import test from 'node:test';
import assert from 'node:assert/strict';
import { openLaunchTerminal } from '../../../VibeRails/wwwroot/js/modules/launch-terminal.js';

test('queued tab is not adopted before its session starts', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const adopted = [];
    const app = { terminalController: { async adoptLaunchedTab(id) { adopted.push(id); return true; } } };
    let reads = 0;
    const api = { async call() { reads++; return { id: 'run', tabId: 'tab', sessionId: 'session', status: 'running' }; } };
    const opening = openLaunchTerminal(app, api, { id: 'run', tabId: 'tab', status: 'claimed' });
    assert.deepEqual(adopted, []);
    t.mock.timers.tick(1000);
    assert.equal(await opening, true);
    assert.deepEqual(adopted, ['tab']);
    assert.equal(reads, 1);
});

test('closing the owner cancels terminal follow-up without another request', async () => {
    const abort = new AbortController();
    const opening = openLaunchTerminal({}, { call() { assert.fail('read after close'); } },
        { id: 'run', status: 'queued' }, { signal: abort.signal });
    abort.abort();
    assert.equal(await opening, false);
});
