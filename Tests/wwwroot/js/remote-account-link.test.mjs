import test from 'node:test';
import assert from 'node:assert/strict';
import { RemoteAccountLinkPanel, SIGN_IN_URL, isSignInUrl } from '../../../VibeRails/wwwroot/js/modules/remote-account-link.js';
import { SettingsController } from '../../../VibeRails/wwwroot/js/modules/settings-controller.js';

function deferred() {
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    return { promise, resolve };
}

function harness(t, apiCall = async () => ({ status: 'idle' })) {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_800_000_000_000 });
    const previousWindow = globalThis.window;
    const listeners = new Map();
    globalThis.window = {
        addEventListener: (type, fn) => listeners.set(type, fn),
        removeEventListener: type => listeners.delete(type)
    };
    const fields = new Map();
    for (const name of ['start', 'cancel', 'copy', 'open', 'pending', 'code', 'status', 'copy-status', 'countdown']) {
        const field = { textContent: '', hidden: false, disabled: false, listeners: {},
            addEventListener(type, fn) { this.listeners[type] = fn; },
            removeAttribute(name) { delete this[name]; }
        };
        fields.set(`[data-remote-link-${name}]`, field);
    }
    const root = { querySelector: selector => fields.get(selector) };
    const calls = [];
    const linked = [];
    let eventCallback;
    const app = {
        apiCall: (...args) => { calls.push(args); return apiCall(...args); },
        appEventClient: { on: (type, callback) => {
            assert.equal(type, 'remote-account-linked');
            eventCallback = callback;
            return () => { eventCallback = null; };
        } }
    };
    const panel = new RemoteAccountLinkPanel(app, root, { onLinked: state => linked.push(state) });
    t.after(() => { panel.unload(); globalThis.window = previousWindow; });
    return { panel, calls, linked, field: name => fields.get(`[data-remote-link-${name}]`), listeners,
        emit: state => eventCallback?.(state) };
}

function pending(overrides = {}) {
    return { status: 'pending', userCode: 'BXQK-2M7T', verificationUri: SIGN_IN_URL,
        expiresAt: new Date(Date.now() + 600_000).toISOString(), interval: 3, ...overrides };
}

test('start displays a code and waits the server interval, with at most one outstanding poll', async t => {
    const poll = deferred();
    const { panel, field, calls } = harness(t, (_url, method) => method === 'POST' ? Promise.resolve(pending({ interval: 6 })) : poll.promise);
    await panel.start();
    assert.equal(field('code').textContent, 'BXQK-2M7T');
    assert.equal(field('open').href, SIGN_IN_URL);
    assert.equal(field('countdown').textContent, 'Code expires in 10:00');
    t.mock.timers.tick(5999);
    assert.equal(calls.length, 1);
    t.mock.timers.tick(1);
    assert.equal(calls.length, 2);
    t.mock.timers.tick(30_000);
    assert.equal(calls.length, 2, 'slow polls cannot pile up');
    assert.equal(calls[1][3].showLoading, false);
    assert.ok(calls[1][3].signal instanceof AbortSignal);
    poll.resolve({ status: 'linked', keyHint: '••••••••abcd', account: { email: 'rob@example.com' } });
    await Promise.resolve();
    assert.match(field('status').textContent, /Connected as rob@example.com/);
    assert.equal(field('pending').hidden, true);
    assert.equal(field('open').href, undefined);
    t.mock.timers.tick(30_000);
    assert.equal(calls.length, 2);
});

test('double start is ignored and cancel suppresses the late start response', async t => {
    const start = deferred();
    const { panel, calls, field, linked } = harness(t, (_url, method) =>
        method === 'POST' ? start.promise : Promise.resolve({ status: 'cancelled' }));
    const request = panel.start();
    await panel.start();
    assert.equal(calls.length, 1);
    await panel.cancel();
    assert.equal(calls[0][3].signal.aborted, true);
    start.resolve(pending());
    await request;
    assert.equal(panel.state.status, 'cancelled');
    assert.equal(field('pending').hidden, true);
    assert.deepEqual(linked, []);
    t.mock.timers.tick(30_000);
    assert.equal(calls.length, 2);
});

test('cancel discards a late successful poll; a retry gets a fresh code', async t => {
    const poll = deferred();
    let starts = 0;
    const { panel, calls, linked } = harness(t, (_url, method) => {
        if (method === 'POST') return Promise.resolve(pending({ userCode: ++starts === 1 ? 'BXQK-2M7T' : 'ABCD-5678' }));
        if (method === 'DELETE') return Promise.resolve({ status: 'cancelled' });
        return poll.promise;
    });
    await panel.start();
    t.mock.timers.tick(3000);
    await panel.cancel();
    assert.equal(calls[1][3].signal.aborted, true);
    await panel.start();
    poll.resolve({ status: 'linked', keyHint: '••••••••old1' });
    await Promise.resolve();
    assert.equal(panel.state.userCode, 'ABCD-5678');
    assert.deepEqual(linked, []);
});

test('countdown expiry waits for an in-flight poll that may already have received approval', async t => {
    const poll = deferred();
    const { panel, calls, linked, field } = harness(t, (_url, method) => method === 'POST'
        ? Promise.resolve(pending({ expiresAt: new Date(Date.now() + 4000).toISOString() })) : poll.promise);
    await panel.start();
    t.mock.timers.tick(3000);
    t.mock.timers.tick(1000);
    assert.equal(panel.state.status, 'pending');
    assert.equal(calls[1][3].signal.aborted, false);
    assert.equal(field('countdown').textContent, 'Code expires in 0:00');
    poll.resolve({ status: 'linked', keyHint: '••••••••late' });
    await Promise.resolve();
    assert.equal(linked.length, 1);
    assert.match(field('status').textContent, /Connected/);
});

test('unload aborts pending requests, unsubscribes events and leaves shared backend state alone', async t => {
    const response = deferred();
    const { panel, calls, linked, emit } = harness(t, () => response.promise);
    const request = panel.mount();
    panel.unload();
    assert.equal(calls[0][3].signal.aborted, true);
    emit({ keyHint: '••••••••event' });
    response.resolve({ status: 'linked', keyHint: '••••••••late' });
    await request;
    assert.deepEqual(linked, []);
    t.mock.timers.tick(30_000);
    assert.deepEqual(calls.map(call => call[1]), ['GET']);
});

test('pagehide stops work and pageshow resumes the backend attempt', async t => {
    const { panel, calls, listeners } = harness(t, async () => pending());
    await panel.mount();
    listeners.get('pagehide')();
    t.mock.timers.tick(9000);
    assert.equal(calls.length, 1);
    listeners.get('pageshow')();
    await Promise.resolve();
    assert.equal(calls.length, 2);
    assert.equal(panel.state.status, 'pending');
});

test('an account event supersedes an older status response and stores only the display result', async t => {
    const response = deferred();
    let reads = 0;
    const { panel, calls, linked, emit, field } = harness(t, () => ++reads === 1 ? response.promise : Promise.resolve({
        status: 'linked', keyHint: '••••••••new1', account: { email: '<img src=x onerror=alert(1)>' }
    }));
    const request = panel.mount();
    emit({ keyHint: '••••••••old1', account: { email: 'stale@example.com' } });
    response.resolve(pending());
    await request;
    assert.equal(calls[0][3].signal.aborted, true);
    assert.equal(panel.state.status, 'linked');
    assert.equal(linked.length, 1);
    assert.equal(panel.state.keyHint, '••••••••new1');
    assert.match(field('status').textContent, /<img src=x/); // Assigned only via textContent.
    assert.equal(field('status').innerHTML, undefined);
});

test('a delayed linked event reads current status instead of restoring a manually replaced key', async t => {
    const { panel, emit, calls, linked, field } = harness(t, async () => ({ status: 'idle' }));
    await panel.mount();
    emit({ keyHint: '••••••••old1', account: { email: 'old@example.com' } });
    await Promise.resolve();
    assert.equal(calls.length, 2);
    assert.equal(panel.state.status, 'idle');
    assert.deepEqual(linked, []);
    assert.doesNotMatch(field('status').textContent, /old@example/);
});

test('terminal failures use local messages and keep the retry and paste workflow available', async t => {
    const { panel, field } = harness(t);
    for (const status of ['denied', 'expired', 'unavailable', 'error']) {
        panel._apply({ status, error: '<secret remote diagnostic>' });
        assert.equal(field('start').disabled, false);
        assert.equal(field('pending').hidden, true);
        assert.doesNotMatch(field('status').textContent, /secret remote diagnostic/);
    }
    panel._apply({ status: 'denied', error: 'key_limit' });
    assert.match(field('status').textContent, /key limit/);
    panel._apply({ status: 'unavailable' });
    assert.match(field('status').textContent, /add an API key in Settings/);
});

test('approved links awaiting a local save keep polling beyond device expiry without another approval', async t => {
    const { panel, calls, field } = harness(t, async () => ({ status: 'pending', error: 'save_failed', expiresAt: null, interval: 3 }));
    await panel.refresh();
    assert.match(field('status').textContent, /approved.*retry automatically/);
    assert.equal(field('pending').hidden, true);
    assert.equal(field('open').href, undefined);
    assert.equal(field('countdown').textContent, '');
    assert.equal(field('cancel').hidden, false);
    t.mock.timers.tick(660_000);
    await Promise.resolve();
    assert.equal(calls.length, 2);
    assert.equal(panel.state.status, 'pending');
});

test('a shared start with no code yet keeps polling; partially missing code data is rejected', async t => {
    const { panel, calls, field } = harness(t, async () => ({ status: 'pending', interval: 3 }));
    await panel.refresh();
    assert.equal(field('status').textContent, 'Preparing sign-in…');
    assert.equal(field('pending').hidden, true);
    assert.equal(field('cancel').hidden, false);
    t.mock.timers.tick(3000);
    await Promise.resolve();
    assert.equal(calls.length, 2);
    panel._apply({ status: 'pending', userCode: 'ABCD-1234' });
    assert.equal(panel.state.status, 'error');
});

test('untrusted verification URLs and malformed code/expiry never become active links', async t => {
    const { panel, field } = harness(t);
    assert.equal(isSignInUrl(SIGN_IN_URL), true);
    for (const verificationUri of ['javascript:alert(1)', 'https://viberails.ai/link?code=BXQK-2M7T',
        'https://viberails.ai/link#key', 'https://evil.example/link', 'https://user@viberails.ai/link',
        'http://viberails.ai/link', 'https://viberails.ai:443/link', 'https://viberails.ai/link/']) {
        assert.equal(isSignInUrl(verificationUri), false);
        panel._apply(pending({ verificationUri }));
        assert.equal(panel.state.status, 'error');
        assert.equal(field('open').href, undefined);
    }
    for (const overrides of [{ userCode: '<img>' }, { expiresAt: 'never' }]) {
        panel._apply(pending(overrides));
        assert.equal(panel.state.status, 'error');
    }
});

test('opening requires a live code and a click; feature-detected VS Code bridge leaves a browser fallback', async t => {
    const { panel } = harness(t, async () => pending());
    const opened = [];
    let prevented = 0;
    const click = { preventDefault: () => { prevented++; } };
    window.__viberails_openExternal__ = url => opened.push(url);
    await panel.start();
    assert.deepEqual(opened, [], 'start never opens a delayed popup');
    panel.openSignInPage(click);
    assert.deepEqual(opened, [SIGN_IN_URL]);
    assert.equal(prevented, 1);
    delete window.__viberails_openExternal__;
    panel.openSignInPage(click);
    assert.equal(prevented, 1, 'ordinary browser follows the anchor');
    window.__viberails_openExternal__ = () => { throw new Error('old host'); };
    panel.openSignInPage(click);
    assert.equal(prevented, 1, 'a broken bridge leaves the same anchor click usable');
    panel.unload();
    panel.openSignInPage(click);
    assert.equal(prevented, 2);
});

test('clipboard copies only the user code and a late copy cannot repaint a cancelled attempt', async t => {
    const { panel, field } = harness(t, async (_url, method) => method === 'DELETE' ? { status: 'cancelled' } : pending());
    const previousClipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    const copy = deferred();
    const copied = [];
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: value => {
        copied.push(value); return copy.promise;
    } } });
    t.after(() => previousClipboard ? Object.defineProperty(navigator, 'clipboard', previousClipboard) : delete navigator.clipboard);
    await panel.start();
    const request = panel.copyCode();
    await panel.cancel();
    copy.resolve();
    await request;
    assert.deepEqual(copied, ['BXQK-2M7T']);
    assert.equal(field('copy-status').textContent, '');
    await panel.start();
    navigator.clipboard.writeText = async () => { throw new Error('Permission denied'); };
    await panel.copyCode();
    assert.match(field('copy-status').textContent, /Select the code/);
});

test('linking updates the key baseline while preserving unrelated settings drafts and their dirty state', t => {
    harness(t);
    const key = { value: '••••••••old1', dataset: { originalValue: '••••••••old1' } };
    const computerName = { value: 'Saved machine' };
    const root = { querySelector: selector => ({ '#setting-api-key': key, '#setting-computer-name': computerName })[selector] || null };
    const settings = [];
    const controller = new SettingsController({ setAppSettings: value => settings.push(value) });
    controller._settingsRoot = root;
    controller._settingsSnapshot = controller._captureSettingsSnapshot(root);
    computerName.value = 'Unsaved machine';
    controller._applyLinkedApiKey(root, '••••••••new1');
    assert.equal(key.value, '••••••••new1');
    assert.equal(key.dataset.originalValue, key.value, 'normal Save sends no replacement key');
    assert.equal(computerName.value, 'Unsaved machine');
    assert.equal(JSON.parse(controller._settingsSnapshot).computerName, 'Saved machine');
    assert.equal(controller._settingsDirty, true);
    computerName.value = 'Saved machine';
    controller._updateDirtyState(root);
    assert.equal(controller._settingsDirty, false, 'linking alone does not dirty Settings');
    assert.deepEqual(settings, [{ apiKey: '••••••••new1' }]);
    controller.unload();
    controller._applyLinkedApiKey(root, '••••••••late');
    assert.equal(key.value, '••••••••new1');
});

test('a concurrent manual clear or save uses the current saved mask instead of an earlier linked event', async t => {
    harness(t);
    for (const savedMask of ['', '••••••••manual']) {
        const calls = [];
        const controller = new SettingsController({ apiCall: async (...args) => {
            calls.push(args); return { apiKey: savedMask, computerName: 'Unrelated server value' };
        } });
        controller._linkedKeyVersion = 2;
        const savedSettings = { apiKey: '••••••••stale', computerName: 'Just saved' };
        await controller._reconcileSavedApiKey(savedSettings, 1);
        assert.equal(savedSettings.apiKey, savedMask);
        assert.equal(savedSettings.computerName, 'Just saved');
        assert.equal(calls.length, 1);
        assert.equal(calls[0][0], '/api/v1/settings');
        assert.equal(calls[0][1], 'GET');
    }
});
