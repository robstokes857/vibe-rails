import test from 'node:test';
import assert from 'node:assert/strict';
import { openLaunchTerminal, waitForNativeLaunch } from '../../../VibeRails/wwwroot/js/modules/launch-terminal.js';

test('native launch waits for execution acknowledgement and surfaces failures', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const states = ['claimed', 'failed'];
    const api = { async call(path) {
        assert.equal(path, 'runs/native');
        return { id: 'native', status: states.shift(), error: 'Terminal unavailable' };
    } };
    const waiting = waitForNativeLaunch(api, { id: 'native', status: 'queued' });
    t.mock.timers.tick(1000);
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(states, ['failed']);
    t.mock.timers.tick(1000);
    assert.equal((await waiting).error, 'Terminal unavailable');
});

test('native polling is bounded while capacity is occupied', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    let reads = 0;
    const queued = { id: 'native', status: 'queued' };
    const waiting = waitForNativeLaunch({ async call() { reads++; return queued; } }, queued);
    for (let i = 0; i < 30; i++) {
        t.mock.timers.tick(1000);
        await new Promise(resolve => setImmediate(resolve));
    }
    assert.equal(await waiting, queued);
    assert.equal(reads, 30);
});

test('acknowledged native runs need no further polling', async () => {
    for (const status of ['running', 'succeeded', 'cancelled', 'uncertain']) {
        const run = { id: 'native', status };
        assert.equal(await waitForNativeLaunch({ call() { assert.fail('unexpected poll'); } }, run), run);
    }
});

test('closing the owner stops native startup polling without another request', async () => {
    const abort = new AbortController();
    const queued = { id: 'native', status: 'queued' };
    const waiting = waitForNativeLaunch({ call() { assert.fail('read after close'); } }, queued, { signal: abort.signal });
    abort.abort();
    assert.equal(await waiting, queued);
});

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
