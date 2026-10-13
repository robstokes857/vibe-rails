import test from 'node:test';
import assert from 'node:assert/strict';
import { PythonRunWindow, isPythonRunOk, formatPythonRunOutput, pythonRunStatus } from '../../../VibeRails/wwwroot/js/modules/python-run-window.js';

function fixture(t, pin = undefined) {
    const sockets = [];
    class Socket {
        static OPEN = 1;
        readyState = 0;
        sent = [];
        constructor(url, protocols) { this.url = url; this.protocols = protocols; sockets.push(this); }
        send(data) { this.sent.push(data); }
        close() { this.readyState = 3; }
        connect() { this.readyState = 1; this.onopen?.(); }
        message(data) { this.onmessage?.({ data }); }
    }
    const previous = new Map();
    for (const [name, value] of Object.entries({
        WebSocket: Socket,
        window: { location: { origin: 'http://localhost:5000' }, removeEventListener() {} },
        document: { activeElement: null, removeEventListener() {} },
        sessionStorage: { getItem: () => 'tab-token==' },
        localStorage: { getItem() { throw Error('must not read inputs'); }, setItem() { throw Error('must not save inputs'); } }
    })) { previous.set(name, globalThis[name]); globalThis[name] = value; }
    t.after(() => { for (const [name, value] of previous) globalThis[name] = value; });
    const scripts = {
        runningNames: new Set(), requestRunPin: async () => pin,
        markRunning(name) { this.runningNames.add(name); }, clearRunning(name) { this.runningNames.delete(name); },
        recordRun() { assert.fail('terminal output must not be recorded'); },
        displayName: () => 'Test script', scriptByName: () => ({ fileName: 'test.py' })
    };
    const view = new PythonRunWindow({ showError: message => assert.fail(message) }, scripts);
    const writes = [];
    const statuses = [];
    view._mount = () => {
        view.layer = { remove() {}, querySelector() { return null; } };
        view._input = () => { view.inputDetached = true; };
        view.terminal = { cols: 80, rows: 24, reset() {}, fit() {}, focus() {}, dispose() {}, write(data) { writes.push(data); } };
    };
    view._paintState = (text, tone) => statuses.push({ text, tone });
    return { view, scripts, sockets, writes, statuses };
}
const tick = () => new Promise(resolve => setImmediate(resolve));

test('Run opens a private authenticated socket, streams output and sends input without storing it', async t => {
    const { view, scripts, sockets, writes } = fixture(t);
    const closed = view.open('script-id');
    await tick();
    const socket = sockets[0];
    assert.match(socket.url, /python-scripts\/run\/terminal\/ws$/);
    assert.deepEqual(socket.protocols, ['tab-token']);
    socket.connect();
    assert.deepEqual(JSON.parse(socket.sent[0]), { name: 'script-id', cols: 80, rows: 24 });
    socket.message('ready');
    socket.message(new TextEncoder().encode('Hey!\r\nEnter something').buffer);
    view._sendInput('a password\r');
    assert.equal(new TextDecoder().decode(socket.sent[1]), 'a password\r');
    assert.ok(socket.sent[1] instanceof Uint8Array);
    assert.equal(new TextDecoder().decode(writes[0]), 'Hey!\r\nEnter something');
    assert.ok(scripts.runningNames.has('script-id'));
    socket.message('exit:0');
    assert.equal(view.running, false);
    assert.equal(view.lastRun.exitCode, 0);
    assert.equal(scripts.runningNames.size, 0);
    view.close();
    assert.equal((await closed).exitCode, 0);
});

test('PIN is sent only in the initial message and a cancelled PIN starts no connection', async t => {
    const { view, scripts, sockets } = fixture(t, '1234');
    view.open('script-id'); await tick();
    sockets[0].connect();
    assert.equal(JSON.parse(sockets[0].sent[0]).pin, '1234');
    assert.equal(sockets[0].onopen, null, 'release the launch handler after sending the PIN');
    assert.equal(view.pin, undefined);
    view.close();
    scripts.requestRunPin = async () => null;
    view.open('script-id'); await tick();
    assert.equal(sockets.length, 1);
    assert.equal(view.running, false);
    view.close();
});

test('Close disposes the socket and renderer; late output cannot paint a new run', async t => {
    const { view, scripts, sockets, writes } = fixture(t);
    const closed = view.open('first'); await tick();
    sockets[0].connect(); sockets[0].message('ready');
    view.close(); await closed;
    assert.equal(sockets[0].readyState, 3);
    assert.equal(view.terminal, null);
    assert.equal(view.inputDetached, true);
    assert.equal(scripts.runningNames.size, 0);
    view.open('second'); await tick();
    sockets[0].message(new TextEncoder().encode('old output').buffer);
    sockets[0].onclose();
    assert.equal(writes.length, 0);
    assert.equal(view.running, true);
    view.close();
});

test('Stop waits for acknowledgment; Run again opens a fresh socket', async t => {
    const { view, sockets, statuses } = fixture(t);
    view.open('script-id'); await tick();
    sockets[0].connect(); sockets[0].message('ready');
    await view.execute();
    assert.equal(sockets.length, 1, 'no duplicate run');
    view.stop();
    assert.equal(sockets[0].sent.at(-1), 'stop');
    assert.equal(view.running, true);
    sockets[0].message('stopped');
    assert.equal(statuses.at(-1).text, 'Stopped');
    await view.execute();
    assert.equal(sockets.length, 2);
    view.close();
});

test('Lost connections never automatically run a script again', async t => {
    const { view, sockets, statuses } = fixture(t);
    view.open('script-id'); await tick();
    sockets[0].connect(); sockets[0].onclose();
    assert.equal(view.running, false);
    assert.match(statuses.at(-1).text, /Connection closed/);
    assert.equal(sockets.length, 1);
    view.close();
});

test('Closing before PIN entry finishes prevents a late launch', async t => {
    const { view, scripts, sockets } = fixture(t);
    let resolve;
    scripts.requestRunPin = () => new Promise(r => { resolve = r; });
    view.open('script-id'); view.close(); resolve('1234'); await tick();
    assert.equal(sockets.length, 0);
    assert.equal(scripts.runningNames.size, 0);
});

test('Large paste uses bounded binary frames with exact bytes', async t => {
    const { view, sockets } = fixture(t);
    view.open('script-id'); await tick(); sockets[0].connect(); sockets[0].message('ready');
    const input = '秘密'.repeat(50000);
    view._sendInput(input);
    const chunks = sockets[0].sent.slice(1);
    assert.ok(chunks.every(chunk => chunk.byteLength <= 65536));
    assert.equal(Buffer.concat(chunks).toString('utf8'), input);
    view.close();
});

test('Captured API summary helpers retain their compatibility behavior', () => {
    assert.equal(isPythonRunOk({ exitCode: 0 }), true);
    assert.equal(isPythonRunOk({ exitCode: 0, cancelled: true }), false);
    assert.equal(pythonRunStatus({ cancelled: true }), 'Stop requested');
    assert.equal(formatPythonRunOutput({ standardOutput: 'hello\n', standardError: 'oops' }), 'hello\n\n[stderr]\noops');
});
