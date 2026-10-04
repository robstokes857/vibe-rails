import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/settings-controller.js');
const indexPath = path.resolve('VibeRails/wwwroot/index.html');
const { SettingsController } = await import(pathToFileURL(modulePath).href);

test('settings page has no trailer-removal option or empty Git tab', () => {
    const html = readFileSync(indexPath, 'utf8');
    const source = readFileSync(modulePath, 'utf8');

    assert.doesNotMatch(html, /setting-remove-co-author-trailers|Git Commit Settings|settings-(?:tab|panel)-git/);
    assert.doesNotMatch(source, /removeCoAuthorTrailers|setting-remove-co-author-trailers/);
});

test('Vibe AI has a default-off settings toggle and starts hidden in both navigation layouts', () => {
    const html = readFileSync(indexPath, 'utf8');
    const source = readFileSync(modulePath, 'utf8');
    const appSource = readFileSync(path.resolve('VibeRails/wwwroot/app.js'), 'utf8');

    assert.match(html, /id="setting-show-vibe-ai-ui"/);
    assert.match(html, /Show Vibe AI UI/);
    assert.match(source, /showVibeAiUi:\s*false/);
    assert.match(appSource, /applyVibeAiNavVisibility/);
    const links = html.match(/<button\b[^>]*data-view="vibe-rails-ai"[^>]*>/g);
    assert.equal(links.length, 2);
    for (const link of links) assert.match(link, /\shidden/);
    // Author display styles otherwise override the browser's hidden attribute rule.
    const css = readFileSync(path.resolve('VibeRails/wwwroot/style.css'), 'utf8');
    assert.match(css, /\.app-subnav-link\[hidden\][^{]*\{\s*display:\s*none\s*!important/);
    const initSource = readFileSync(path.resolve('VibeRails/Init.cs'), 'utf8');
    assert.doesNotMatch(initSource, /settings\.ShowVibeAiUi\s*=/);
});

test('saved visibility updates both navigation layouts and survives partial settings updates', (t) => {
    const source = readFileSync(path.resolve('VibeRails/wwwroot/app.js'), 'utf8');
    const classSource = source.slice(source.indexOf('export class VibeControlApp'), source.indexOf('// Initialize the app'))
        .replace('export class VibeControlApp', 'class VibeControlApp');
    const VibeControlApp = new Function('getTokenSaverEnabledSources', `${classSource}\nreturn VibeControlApp;`)(() => []);
    const app = Object.create(VibeControlApp.prototype);
    const links = [{ hidden: false }, { hidden: false }];
    const previousDocument = globalThis.document;
    t.after(() => { globalThis.document = previousDocument; });
    globalThis.document = {
        querySelectorAll(selector) {
            assert.equal(selector, '.app-subnav-link[data-view="vibe-rails-ai"]');
            return links;
        }
    };
    app.applyVsCodeThemePreference = () => {};
    app.updateAccountNav = () => {};

    app.setAppSettings({});
    assert.equal(app.appSettings.showVibeAiUi, false);
    assert.ok(links.every(link => link.hidden));
    app.setAppSettings({ showVibeAiUi: true });
    assert.ok(links.every(link => !link.hidden));
    app.setAppSettings({ apiKey: 'masked-key' });
    assert.ok(links.every(link => !link.hidden));
    app.setAppSettings({ showVibeAiUi: false });
    assert.ok(links.every(link => link.hidden));
});

test('visibility participates in dirty tracking and saved controls reflect the server value', (t) => {
    const previousWindow = globalThis.window;
    t.after(() => { globalThis.window = previousWindow; });
    globalThis.window = { VibeRailsPerformance: null };
    const controller = new SettingsController({});
    const toggle = { checked: false };
    const root = { querySelector: selector => selector === '#setting-show-vibe-ai-ui' ? toggle : null };
    controller._updateDataExportAvailability = () => {};
    assert.match(controller._trackedSettingsSelector(), /#setting-show-vibe-ai-ui/);
    const clean = controller._captureSettingsSnapshot(root);
    toggle.checked = true;
    assert.notEqual(controller._captureSettingsSnapshot(root), clean);
    controller._applySavedSettingsToControls(root, { showVibeAiUi: false });
    assert.equal(toggle.checked, false);
    controller._applySavedSettingsToControls(root, { showVibeAiUi: true });
    assert.equal(toggle.checked, true);
});

test('settings page always shares completed sessions and has no sharing switch', () => {
    const html = readFileSync(indexPath, 'utf8');
    const source = readFileSync(modulePath, 'utf8');

    assert.doesNotMatch(html, /id="setting-data-export-opt-in"/);
    assert.doesNotMatch(html, /Share session data/);
    assert.doesNotMatch(html, /id="settings-session-sharing-wrapper"/);
    assert.match(source, /dataExportOptIn:\s*true/);
    // The legacy one-shot export (Export Data button + modal) stays.
    assert.match(html, /id="settings-export-data-button"/);
});

test('saving settings omits the retired trailer option and preserves following arguments', async () => {
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
        /* routeThroughVibeRailsAi */ true,
        /* clearApiKey */ true,
        /* showVibeAiUi */ true);

    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, '/api/v1/settings');
    assert.equal(calls[0].method, 'POST');
    assert.equal(calls[0].body.removeCoAuthorTrailers, undefined);
    assert.equal(calls[0].body.routeThroughVibeRailsAi, true);
    assert.equal(calls[0].body.clearApiKey, true);
    assert.equal(calls[0].body.showVibeAiUi, true);
    assert.equal(calls[0].body.dataExportOptIn, true);

    await controller.saveSettings(false, '', false, true, '', false, 'subscription',
        false, false, false, 'subscription', true, true, true, true, false, false, false);
    assert.equal(calls[1].body.showVibeAiUi, false);
    assert.equal(calls[1].body.clearApiKey, false);
});

test('settings page groups its cards under a section tab bar', () => {
    const html = readFileSync(indexPath, 'utf8');

    assert.match(html, /class="settings-tabs" role="tablist" aria-label="Settings sections"/);
    for (const id of ['general', 'llm', 'keys', 'integrations']) {
        assert.match(html, new RegExp(`id="settings-tab-${id}"`));
        assert.match(html, new RegExp(`id="settings-panel-${id}" role="tabpanel"`));
    }
    // General is the default tab; the other panels start hidden.
    assert.match(html, /id="settings-tab-general"[\s\S]{0,120}aria-selected="true"/);
    assert.match(html, /id="settings-tab-llm"[\s\S]{0,120}aria-selected="false"/);
    assert.match(html, /id="settings-panel-llm"[\s\S]{0,120}hidden/);
    // Like settings stay on their own tabs.
    assert.match(html, /id="settings-panel-llm"[\s\S]*?Codex Settings[\s\S]*?Token Saver/);
    assert.match(html, /id="settings-panel-general"[\s\S]*?Remote PIN Lock[\s\S]*?Application Settings/);
});

test('every tracked settings control survives the tabbed layout', () => {
    const html = readFileSync(indexPath, 'utf8');
    const source = readFileSync(modulePath, 'utf8');

    const block = source.match(/_trackedSettingsSelector\(\)\s*\{[\s\S]*?\.join/)[0];
    const selectors = [...block.matchAll(/'([^']+)'/g)].map(match => match[1]);
    assert.ok(selectors.length >= 15, 'expected the full tracked selector list');

    for (const selector of selectors) {
        if (selector.startsWith('#')) {
            assert.ok(html.includes(`id="${selector.slice(1)}"`), `${selector} is missing from the page`);
        } else {
            const name = /name="([^"]+)"/.exec(selector)?.[1];
            assert.ok(name && html.includes(`name="${name}"`), `${selector} is missing from the page`);
        }
    }
});

test('settings tabs switch panels without removing any controls', () => {
    globalThis.window = { VibeRailsPerformance: null };

    const ids = ['general', 'llm', 'keys', 'integrations'];
    const panels = Object.fromEntries(ids.map(id => [id, { hidden: id !== 'general' }]));
    const tabs = ids.map(id => {
        const tab = {
            dataset: { settingsTab: id },
            tabIndex: id === 'general' ? 0 : -1,
            selected: String(id === 'general'),
            focusCount: 0,
            listeners: {},
            getAttribute: name => name === 'aria-controls' ? `settings-panel-${id}` : null,
            setAttribute(name, value) { if (name === 'aria-selected') tab.selected = String(value); },
            addEventListener(type, fn) { (tab.listeners[type] ??= []).push(fn); },
            focus() { tab.focusCount += 1; },
            closest: () => tab
        };
        return tab;
    });
    const tablist = {
        listeners: {},
        addEventListener(type, fn) { (tablist.listeners[type] ??= []).push(fn); },
        querySelectorAll: () => tabs
    };
    const root = {
        querySelector(selector) {
            if (selector === '.settings-tabs') return tablist;
            if (selector === '[data-settings-keys]') return {};
            const match = /^#settings-panel-(general|llm|keys|integrations)$/.exec(selector);
            return match ? panels[match[1]] : null;
        }
    };

    const controller = new SettingsController({});
    let keysActivations = 0;
    let secretClears = 0;
    controller._keysPanel = {
        activate() { keysActivations += 1; },
        clearSecrets() { secretClears += 1; }
    };
    controller._initSettingsTabs(root);
    assert.equal(keysActivations, 0, 'keys are not fetched when settings initializes');

    // Clicking the LLMs tab (delegated through the tablist) shows only its panel.
    tablist.listeners.click.forEach(fn => fn({ target: tabs[1] }));
    assert.equal(panels.llm.hidden, false);
    assert.equal(panels.general.hidden, true);
    assert.equal(panels.keys.hidden, true);
    assert.equal(tabs[1].selected, 'true');
    assert.equal(tabs[1].tabIndex, 0);
    assert.equal(tabs[0].selected, 'false');
    assert.equal(tabs[0].tabIndex, -1);

    // ArrowRight from LLMs now moves directly to KEYS and lazily activates it.
    tablist.listeners.keydown.forEach(fn =>
        fn({ target: tabs[1], key: 'ArrowRight', preventDefault() {} }));
    assert.equal(panels.keys.hidden, false);
    assert.equal(panels.llm.hidden, true);
    assert.equal(tabs[2].focusCount, 1);
    assert.equal(keysActivations, 1);

    // ArrowLeft wraps from General to Integrations.
    tablist.listeners.keydown.forEach(fn =>
        fn({ target: tabs[0], key: 'ArrowLeft', preventDefault() {} }));
    assert.equal(panels.integrations.hidden, false);
    assert.equal(tabs[3].focusCount, 1);
    assert.equal(keysActivations, 1);
    tablist.listeners.click.forEach(fn => fn({ target: tabs[0] }));
    assert.equal(secretClears, 3, 'leaving KEYS clears key passwords');
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
