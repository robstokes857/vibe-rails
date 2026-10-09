import test from 'node:test';
import assert from 'node:assert/strict';
import { setupVSCodeLauncherBridge } from '../../../VibeRails/wwwroot/js/modules/vscode-launcher-bridge.js';

function fixture({ run = async () => {}, customize = async () => {}, refreshRuns = async () => {} } = {}) {
    const calls = [];
    const errors = [];
    const toasts = [];
    const navigations = [];
    let receive = null;
    const host = {
        __viberails_VSCODE__: true,
        addEventListener: (type, handler) => { assert.equal(type, 'message'); receive = handler; }
    };
    const app = {
        jobController: {
            pythonScripts: { run: async (name) => { calls.push(['run', name]); return run(name); } },
            refreshRuns: async (options) => { calls.push(['refreshRuns', options]); return refreshRuns(options); }
        },
        automationNavLauncher: { openCustomizationFromHost: async () => { calls.push(['customize']); return customize(); } },
        navigate: (view, data) => { navigations.push([view, data]); return true; },
        showToast: (title, message, type) => toasts.push([title, message, type]),
        showError: (message) => errors.push(message)
    };
    setupVSCodeLauncherBridge(app, host);
    assert.equal(typeof receive, 'function');
    return { calls, errors, toasts, navigations, send: (data) => receive({ data }) };
}

const settled = () => new Promise((resolve) => setImmediate(resolve));

test('each host command reaches the same code path the nav flyout uses', async () => {
    const f = fixture();
    f.send({ command: 'runScript', name: 'abc123' });
    f.send({ command: 'openLauncherCustomize' });
    f.send({ command: 'manageAutomations' });
    f.send({ command: 'automationQueued', jobId: 7, message: 'Automation queued.' });
    await settled();
    assert.deepEqual(f.calls, [['run', 'abc123'], ['customize'], ['refreshRuns', { quiet: true }]]);
    assert.deepEqual(f.navigations, [['jobs', undefined]]);
    assert.deepEqual(f.toasts, [['Automation', 'Automation queued.', 'success']]);
    assert.deepEqual(f.errors, []);
});

test('malformed and unrelated messages are ignored; failures become the usual error toast', async () => {
    const f = fixture({
        run: async () => { throw new Error('PIN required'); },
        customize: async () => { throw new Error('Modal host missing'); },
        refreshRuns: async () => { throw new Error('offline'); }
    });
    for (const data of [
        null, 'runScript', {}, { command: 'importScript', requestId: '1', path: '/a.py' },
        { command: 'runScript' }, { command: 'runScript', name: 7 }, { command: 'runScript', name: '' }
    ]) f.send(data);
    await settled();
    assert.deepEqual(f.calls, []);

    f.send({ command: 'runScript', name: 'abc123' });
    f.send({ command: 'openLauncherCustomize' });
    f.send({ command: 'automationQueued' });
    await settled();
    assert.deepEqual(f.errors, ['PIN required', 'Modal host missing']);
    assert.deepEqual(f.toasts, [['Automation', 'Queued.', 'success']]);
    assert.deepEqual(f.calls.map(([name]) => name), ['run', 'customize', 'refreshRuns']);
});

test('script list changes that touch the launcher reach the extension; identical refreshes stay quiet', () => {
    let listener = null;
    let notified = 0;
    const host = {
        __viberails_VSCODE__: true,
        addEventListener: () => {},
        __viberails_launcherChanged__: () => { notified += 1; }
    };
    const scripts = { state: null, onStateChange: (fn) => { listener = fn; return () => {}; } };
    setupVSCodeLauncherBridge({ jobController: { pythonScripts: scripts } }, host);
    assert.equal(typeof listener, 'function');

    const unsigned = { name: 'abc123', displayName: 'Backup', fileName: 'backup.py', status: 'unapproved' };
    listener({ scripts: [unsigned] });
    assert.equal(notified, 1, 'first load');
    listener({ scripts: [{ ...unsigned, approvedUtc: null, lastRun: { exitCode: 0 } }] });
    assert.equal(notified, 1, 'a focus refresh with the same list');
    listener({ scripts: [{ ...unsigned, status: 'approved' }] });
    assert.equal(notified, 2, 'signed');
    listener({ scripts: [{ ...unsigned, status: 'approved', displayName: 'Nightly backup' }] });
    assert.equal(notified, 3, 'renamed');
    listener({ scripts: [{ ...unsigned, status: 'modified', displayName: 'Nightly backup' }] });
    assert.equal(notified, 4, 'edited after signing');
    listener({ scripts: [] });
    assert.equal(notified, 5, 'deleted');
    listener(null);
    assert.equal(notified, 6, 'list unknown again');

    // The baseline is whatever the controller already holds when the bridge is installed.
    let late = 0;
    let lateListener = null;
    setupVSCodeLauncherBridge(
        { jobController: { pythonScripts: { state: { scripts: [unsigned] }, onStateChange: (fn) => { lateListener = fn; } } } },
        { __viberails_VSCODE__: true, addEventListener: () => {}, __viberails_launcherChanged__: () => { late += 1; } }
    );
    lateListener({ scripts: [{ ...unsigned }] });
    assert.equal(late, 0);
    lateListener({ scripts: [{ ...unsigned, fileName: 'backup.ps1' }] });
    assert.equal(late, 1);
});

test('plain browsers get no bridge and a dashboard without the controllers tolerates every command', async () => {
    let installed = false;
    setupVSCodeLauncherBridge({}, {});
    setupVSCodeLauncherBridge({}, { __viberails_VSCODE__: true, addEventListener: () => { installed = true; } });
    assert.equal(installed, true);

    let receive = null;
    setupVSCodeLauncherBridge({}, { __viberails_VSCODE__: true, addEventListener: (_type, handler) => { receive = handler; } });
    for (const command of ['runScript', 'openLauncherCustomize', 'manageAutomations', 'automationQueued']) {
        receive({ data: { command, name: 'x' } });
    }
    await settled();
});
