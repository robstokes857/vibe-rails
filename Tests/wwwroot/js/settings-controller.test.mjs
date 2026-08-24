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

test('settings page exposes the default-off Vibe AI nav control', () => {
    const html = readFileSync(indexPath, 'utf8');
    const source = readFileSync(modulePath, 'utf8');
    const appSource = readFileSync(path.resolve('VibeRails/wwwroot/app.js'), 'utf8');

    assert.match(html, /id="setting-show-vibe-ai-ui"/);
    assert.match(html, /Show Vibe AI UI/);
    assert.match(source, /showVibeAiUi:\s*false/);
    assert.match(appSource, /applyVibeAiNavVisibility/);
    assert.match(html, /data-view="vibe-rails-ai"[\s\S]{0,80}hidden/);
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
        /* remoteAccess */ false,
        /* apiKey */ '',
        /* useVsCodeTheme */ false,
        /* mcpEnabled */ true,
        /* computerName */ '',
        /* codexLlmProxyEnabled */ false,
        /* codexLlmProxyMode */ 'subscription',
        /* claudeLlmProxyEnabled */ false,
        /* openCodeLlmProxyEnabled */ false,
        /* grokLlmProxyEnabled */ false,
        /* grokLlmProxyMode */ 'subscription',
        /* claudeTokenSaverEnabled */ true,
        /* codexTokenSaverEnabled */ true,
        /* openCodeTokenSaverEnabled */ true,
        /* grokTokenSaverEnabled */ true,
        /* tokenSaverCaptureEnabled */ false,
        /* removeCoAuthorTrailers */ false,
        /* routeThroughVibeRailsAi */ false,
        /* showVibeAiUi */ false);

    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, '/api/v1/settings');
    assert.equal(calls[0].method, 'POST');
    assert.equal(calls[0].body.removeCoAuthorTrailers, false);
    assert.equal(calls[0].body.showVibeAiUi, false);
});

test('a dirty settings form blocks navigation, asks in-app, and replays on yes', async () => {
    globalThis.window = { VibeRailsPerformance: null };

    // window.confirm is a silent no-op in the VS Code webview; the old guard
    // bypassed itself there and silently discarded webview edits. Both the
    // call and the bypass must stay gone.
    const source = readFileSync(modulePath, 'utf8');
    assert.doesNotMatch(source, /window\.confirm\s*\(/);
    const appSource = readFileSync(path.resolve('VibeRails/wwwroot/app.js'), 'utf8');
    assert.match(appSource, /guard\(\{ from: this\.currentView, to: view, data, retry \}\)/);

    const controller = new SettingsController({});
    controller._settingsDirty = true;

    let retried = 0;
    const retry = () => {
        retried += 1;
        // The replayed navigation consults the guard again mid-retry; the
        // confirmed flag must let exactly that replay through.
        assert.equal(controller._guardSettingsNavigation({ from: 'settings', retry }), true);
    };

    controller.confirmLeave = async () => false;
    assert.equal(controller._guardSettingsNavigation({ from: 'settings', retry }), false);
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(retried, 0, 'declining the dialog must not replay the navigation');

    controller.confirmLeave = async () => true;
    assert.equal(controller._guardSettingsNavigation({ from: 'settings', retry }), false);
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(retried, 1, 'confirming replays the blocked navigation once');

    // The confirmed flag must not leak: the next dirty navigation blocks again.
    assert.equal(controller._guardSettingsNavigation({ from: 'settings', retry: () => {} }), false);
    await new Promise(resolve => setTimeout(resolve, 0));

    // Clean form or foreign view: pass-through.
    controller._settingsDirty = false;
    assert.equal(controller._guardSettingsNavigation({ from: 'settings', retry }), true);
    controller._settingsDirty = true;
    assert.equal(controller._guardSettingsNavigation({ from: 'dashboard', retry }), true);
});
