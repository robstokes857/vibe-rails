import test from 'node:test';
import assert from 'node:assert/strict';
import { setupVSCodeScriptImport } from '../../../VibeRails/wwwroot/js/modules/vscode-script-import.js';

function fixture(importScript) {
    const replies = [];
    const errors = [];
    let receive;
    const host = {
        __viberails_VSCODE__: true,
        addEventListener: (type, handler) => { assert.equal(type, 'message'); receive = handler; },
        __viberails_scriptImportReady__: () => assert.equal(typeof receive, 'function'),
        __viberails_scriptImportReceived__: (...args) => replies.push(args)
    };
    setupVSCodeScriptImport({ jobController: { pythonScripts: { importScript } }, showError: message => errors.push(message) }, host);
    return { replies, errors, send: data => receive({ data }) };
}

test('The bridge acknowledges delivery before the user finishes and rejects overlapping dialogs', async () => {
    let finish;
    const paths = [];
    const f = fixture(filePath => { paths.push(filePath); return new Promise(resolve => { finish = resolve; }); });
    const first = f.send({ command: 'importScript', requestId: '1', path: '/repo/a.py' });
    assert.deepEqual(f.replies, [['1']]);
    await f.send({ command: 'importScript', requestId: '2', path: '/repo/b.sh' });
    assert.match(f.replies[1][1], /Finish the open script dialog/);
    assert.deepEqual(paths, ['/repo/a.py']);
    finish();
    await first;
    const next = f.send({ command: 'importScript', requestId: '3', path: '/repo/c.ps1' });
    finish();
    await next;
    assert.deepEqual(paths, ['/repo/a.py', '/repo/c.ps1']);
});

test('The bridge ignores unrelated/malformed messages, reports failures, and recovers', async () => {
    const f = fixture(async () => { throw new Error('Import failed'); });
    for (const data of [null, {}, { command: 'close' }, { command: 'importScript', requestId: '1', path: 1 }]) await f.send(data);
    assert.equal(f.replies.length, 0);
    await f.send({ command: 'importScript', requestId: '2', path: '/repo/a.py' });
    await f.send({ command: 'importScript', requestId: '3', path: '/repo/a.py' });
    assert.deepEqual(f.replies, [['2'], ['3']]);
    assert.deepEqual(f.errors, ['Import failed', 'Import failed']);
});

test('Ordinary browsers and older extensions need no bridge', () => {
    setupVSCodeScriptImport({}, {});
    setupVSCodeScriptImport({}, { __viberails_VSCODE__: true });
});
