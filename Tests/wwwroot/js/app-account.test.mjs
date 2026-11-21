import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { readEntitlements, unknownEntitlements } from '../../../VibeRails/wwwroot/js/modules/pro-upgrade.js';

const source = readFileSync('VibeRails/wwwroot/app.js', 'utf8');
const classSource = source.slice(source.indexOf('export class VibeControlApp'), source.indexOf('// Initialize the app'))
    .replace('export class VibeControlApp', 'class VibeControlApp');
// The class body names these two imports; the badges need a page, so they stand still here.
const VibeControlApp = new Function('readEntitlements', 'updateProBadges', `${classSource}\nreturn VibeControlApp;`)(readEntitlements, () => {});

function deferred() {
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    return { promise, resolve };
}

function appFixture() {
    const app = Object.create(VibeControlApp.prototype);
    app.accountSettingsVersion = 1;
    app.appSettings = { apiKey: '', remoteAccountEmail: null };
    app.settingsController = {};
    app.setAppSettings = settings => {
        app.accountSettingsVersion++;
        app.appSettings = settings;
    };
    return app;
}

test('a delayed account event read cannot restore a key superseded by a settings save', async () => {
    const app = appFixture();
    const response = deferred();
    app.apiCall = () => response.promise;
    const refresh = app.refreshAccountSettings();
    app.setAppSettings({ apiKey: '', remoteAccountEmail: null });
    response.resolve({ apiKey: 'old-mask', remoteAccountEmail: 'old@example.com' });
    await refresh;
    assert.deepEqual(app.appSettings, { apiKey: '', remoteAccountEmail: null });
});

test('the latest account event wins even when the older read completes first', async () => {
    const app = appFixture();
    const old = deferred();
    const latest = deferred();
    let calls = 0;
    app.apiCall = () => ++calls === 1 ? old.promise : latest.promise;
    const first = app.refreshAccountSettings();
    const second = app.refreshAccountSettings();
    old.resolve({ apiKey: 'old-mask', remoteAccountEmail: 'old@example.com' });
    await first;
    latest.resolve({ apiKey: 'new-mask', remoteAccountEmail: 'new@example.com' });
    await second;
    assert.deepEqual(app.appSettings, { apiKey: 'new-mask', remoteAccountEmail: 'new@example.com', remoteAccountOrganization: null });
});

test('an account refresh replaces the organization with the saved one, or clears it', async () => {
    const app = appFixture();
    app.apiCall = async () => ({ apiKey: 'mask', remoteAccountEmail: 'rob@example.com', remoteAccountOrganization: 'Contoso' });
    await app.refreshAccountSettings();
    assert.equal(app.appSettings.remoteAccountOrganization, 'Contoso');
    app.apiCall = async () => ({ apiKey: 'other-mask', remoteAccountEmail: 'other@example.com' });
    await app.refreshAccountSettings();
    assert.equal(app.appSettings.remoteAccountOrganization, null);
});

// Another VibeRails window signed in or out: this page's saved-account copy is older than the file.
test('an account change made in another window redraws the open account dialog', async () => {
    const app = appFixture();
    const refreshes = [];
    app.remoteAccountPanel = { refresh: () => { refreshes.push(app.appSettings.apiKey); } };
    app.apiCall = async () => ({ apiKey: 'mask-from-another-window', remoteAccountEmail: 'rob@example.com' });
    await app.refreshSavedAccount();
    assert.deepEqual(refreshes, ['mask-from-another-window'], 'the dialog redraws after the saved account is read');

    await app.refreshSavedAccount();
    assert.equal(refreshes.length, 1, 'an unchanged account costs the dialog nothing');

    app.apiCall = async () => ({ apiKey: '', remoteAccountEmail: null });
    await app.refreshSavedAccount();
    assert.deepEqual(refreshes, ['mask-from-another-window', ''], 'signing out elsewhere redraws it too');

    app.remoteAccountPanel = null;
    app.apiCall = async () => ({ apiKey: 'later-mask', remoteAccountEmail: 'rob@example.com' });
    await app.refreshSavedAccount();
    assert.equal(app.appSettings.apiKey, 'later-mask', 'with no dialog open only the saved account updates');
});

test('a key removed in another window clears the open Settings box, a pasted one replaces it', async () => {
    const app = appFixture();
    const root = { querySelector: () => ({ dataset: { originalValue: 'old-mask' } }) };
    const applied = [];
    app.settingsController = {
        _settingsRoot: root,
        _applyLinkedApiKey: (target, mask) => applied.push(['linked', target === root, mask]),
        _applyRemovedApiKey: target => applied.push(['removed', target === root])
    };
    app.apiCall = async () => ({ apiKey: '', remoteAccountEmail: null });
    await app.refreshAccountSettings();
    app.apiCall = async () => ({ apiKey: 'new-mask', remoteAccountEmail: 'rob@example.com' });
    await app.refreshAccountSettings();
    assert.deepEqual(applied, [['removed', true], ['linked', true, 'new-mask']]);
});

test('unavailable account refresh retains the last known display', async () => {
    const app = appFixture();
    app.setAppSettings({ apiKey: 'saved-mask', remoteAccountEmail: 'rob@example.com' });
    app.apiCall = async () => { throw new Error('offline'); };
    await app.refreshAccountSettings();
    assert.deepEqual(app.appSettings, { apiKey: 'saved-mask', remoteAccountEmail: 'rob@example.com' });
});

const FREE = { state: 'free', enforced: true, billingUrl: 'https://viberails.ai/Billing', checkedUtc: '2026-11-20T10:00:00Z',
    features: { remote_boards: false, remote_terminals: false, session_sharing: false, card_sharing: false } };

test('the entitlement read is unknown until it answers, and a failed read returns to unknown', async () => {
    const app = appFixture();
    app.entitlements = unknownEntitlements();
    const calls = [];
    app.apiCall = async (...args) => { calls.push(args.slice(0, 2)); return FREE; };
    assert.equal((await app.refreshEntitlements()).state, 'free');
    assert.equal(app.entitlements.features.remote_boards, false);
    await app.refreshEntitlements({ force: true });
    assert.deepEqual(calls, [['/api/v1/account/entitlements', 'GET'], ['/api/v1/account/entitlements/refresh', 'POST']]);
    assert.ok(Date.now() - app.entitlementsReadAt < 5000, 'the read time feeds the focus re-read');

    app.apiCall = async () => { throw new Error('offline'); };
    await app.refreshEntitlements();
    assert.equal(app.entitlements.state, 'unknown');
    assert.equal(app.entitlements.features.remote_boards, true, 'a failed read locks nothing');
});

test('the latest entitlement read wins even when an older one answers last', async () => {
    const app = appFixture();
    app.entitlements = unknownEntitlements();
    const old = deferred();
    const latest = deferred();
    let calls = 0;
    app.apiCall = () => ++calls === 1 ? old.promise : latest.promise;
    const first = app.refreshEntitlements();
    const second = app.refreshEntitlements({ force: true });
    latest.resolve({ ...FREE, state: 'pro', features: {} });
    await second;
    old.resolve(FREE);
    assert.equal((await first).state, 'pro', 'the superseded read reports the newer state');
    assert.equal(app.entitlements.features.remote_boards, true);
});

test('account events and a returning window read the entitlement again', () => {
    assert.match(source, /for \(const event of \['remote-account-linked', 'account_key_changed'\]\) \{\s*this\.appEventClient\.on\(event, \(\) => void this\.refreshEntitlements\(\)\);/);
    assert.match(source, /onUiVisible\(\(\) => \{\s*if \(Date\.now\(\) - this\.entitlementsReadAt > 60_000\) void this\.refreshEntitlements\(\);/);
    assert.match(source, /void this\.refreshEntitlements\(\);\s*await this\.fetchConfigs\(\);/, 'startup never waits for it');
});

function apiFixture(t, response) {
    const globals = { fetch: globalThis.fetch, window: globalThis.window, sessionStorage: globalThis.sessionStorage };
    t.after(() => Object.assign(globalThis, globals));
    t.mock.method(console, 'error', () => {});
    globalThis.window = {};
    globalThis.sessionStorage = { getItem: () => 'tab-token' };
    globalThis.fetch = async () => response;
    return appFixture();
}

const jsonResponse = (status, body) => ({ ok: false, status, statusText: 'Forbidden',
    headers: { get: name => name === 'content-type' ? 'application/json; charset=utf-8' : null }, json: async () => body });

test('an API error carries the denial code, feature, payer and status from its JSON body', async t => {
    const denial = { error: 'This Board needs Vibe Rails Pro.', code: 'pro_required', feature: 'remote_boards', payer: 'board_owner', status: 'paused' };
    const app = apiFixture(t, jsonResponse(403, denial));
    const preferred = await app.apiCall('/api/v1/board/remote', 'GET', null, { showLoading: false, preferErrorResponseMessage: true }).catch(error => error);
    assert.equal(preferred.message, 'This Board needs Vibe Rails Pro.');
    assert.deepEqual({ code: preferred.code, feature: preferred.feature, payer: preferred.payer, status: preferred.status },
        { code: 'pro_required', feature: 'remote_boards', payer: 'board_owner', status: 'paused' });
    // Without the option the message stays the stable status line, but the fields still arrive.
    const plain = await app.apiCall('/api/v1/board/remote', 'GET', null, { showLoading: false }).catch(error => error);
    assert.equal(plain.message, 'API call failed: Forbidden');
    assert.equal(plain.code, 'pro_required');
    assert.equal(plain.payer, 'board_owner');
});

test('an API error without a readable JSON body keeps its message and adds nothing', async t => {
    for (const response of [
        jsonResponse(400, { error: 'Nope' }),
        { ...jsonResponse(500, null), json: async () => { throw new SyntaxError('bad json'); } },
        { ok: false, status: 502, statusText: 'Bad Gateway', headers: { get: () => 'text/html' }, json: async () => assert.fail('not JSON') },
        { ok: false, status: 500, statusText: 'Server Error' }]) {
        const app = apiFixture(t, response);
        const error = await app.apiCall('/api/v1/settings', 'GET', null, { showLoading: false }).catch(failure => failure);
        assert.match(error.message, /^API call failed: /);
        for (const key of ['code', 'feature', 'payer', 'status']) assert.equal(key in error, false, key);
    }
});
