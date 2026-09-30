import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync('VibeRails/wwwroot/app.js', 'utf8');
const classSource = source.slice(source.indexOf('export class VibeControlApp'), source.indexOf('// Initialize the app'))
    .replace('export class VibeControlApp', 'class VibeControlApp');
const VibeControlApp = new Function(`${classSource}\nreturn VibeControlApp;`)();

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
    assert.deepEqual(app.appSettings, { apiKey: 'new-mask', remoteAccountEmail: 'new@example.com' });
});

test('unavailable account refresh retains the last known display', async () => {
    const app = appFixture();
    app.setAppSettings({ apiKey: 'saved-mask', remoteAccountEmail: 'rob@example.com' });
    app.apiCall = async () => { throw new Error('offline'); };
    await app.refreshAccountSettings();
    assert.deepEqual(app.appSettings, { apiKey: 'saved-mask', remoteAccountEmail: 'rob@example.com' });
});
