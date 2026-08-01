import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/settings-controller.js');
const indexPath = path.resolve('VibeRails/wwwroot/index.html');
const { SettingsController } = await import(pathToFileURL(modulePath).href);

test('settings page exposes the default-on co-author removal control', () => {
    const html = readFileSync(indexPath, 'utf8');
    const source = readFileSync(modulePath, 'utf8');

    assert.match(html, /id="setting-remove-co-author-trailers"/);
    assert.match(html, /Remove co-author tags/);
    assert.match(html, /remove every <code>Co-authored-by:<\/code> trailer/);
    assert.match(source, /removeCoAuthorTrailers:\s*true/);
});

test('saving settings sends the co-author removal choice', async () => {
    globalThis.window = { VibeRailsPerformance: null };
    const calls = [];
    const app = {
        async apiCall(url, method, body) {
            calls.push({ url, method, body });
            return body;
        },
        setAppSettings() {},
        showToast() {},
        showError(message) { throw new Error(message); }
    };
    const controller = new SettingsController(app);

    await controller.saveSettings(
        false,
        '',
        false,
        true,
        '',
        false,
        'subscription',
        false,
        false,
        true,
        true,
        true,
        false,
        false,
        false);

    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, '/api/v1/settings');
    assert.equal(calls[0].method, 'POST');
    assert.equal(calls[0].body.removeCoAuthorTrailers, false);
});
