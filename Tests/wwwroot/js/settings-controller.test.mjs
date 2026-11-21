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

test('Vibe AI is hidden from navigation and routing and has no settings toggle', () => {
    const html = readFileSync(indexPath, 'utf8');
    const source = readFileSync(modulePath, 'utf8');
    const appSource = readFileSync(path.resolve('VibeRails/wwwroot/app.js'), 'utf8');

    assert.doesNotMatch(html, /setting-show-vibe-ai-ui|Show Vibe AI UI/);
    assert.doesNotMatch(source, /showVibeAiUi|setting-show-vibe-ai-ui/);
    assert.doesNotMatch(appSource, /showVibeAiUi|applyVibeAiNavVisibility/);
    assert.doesNotMatch(html, /<button\b[^>]*data-view="vibe-rails-ai"[^>]*>/);
    assert.doesNotMatch(appSource, /vibe-rails-ai|VibeRailsAiController|vibeRailsAiController/);
    // The retired preference stays in settings.json for older versions; startup never writes it.
    const initSource = readFileSync(path.resolve('VibeRails/Init.cs'), 'utf8');
    assert.doesNotMatch(initSource, /settings\.ShowVibeAiUi\s*=/);
});

test('the top navigation account sign-in link starts hidden', () => {
    const html = readFileSync(indexPath, 'utf8');
    const links = html.match(/<button\b[^>]*data-account-nav[^>]*>/g);
    assert.equal(links.length, 1);
    for (const link of links) assert.match(link, /\shidden/);
    // Author display styles otherwise override the browser's hidden attribute rule.
    const css = readFileSync(path.resolve('VibeRails/wwwroot/style.css'), 'utf8');
    assert.match(css, /\.app-subnav-link\[hidden\][^{]*\{\s*display:\s*none\s*!important/);
});

test('settings page always shares completed sessions and has no sharing switch', () => {
    const html = readFileSync(indexPath, 'utf8');
    const source = readFileSync(modulePath, 'utf8');

    assert.doesNotMatch(html, /id="setting-data-export-opt-in"/);
    assert.doesNotMatch(html, /Share session data/);
    assert.doesNotMatch(html, /id="settings-session-sharing-wrapper"/);
    assert.match(source, /dataExportOptIn:\s*true/);
    assert.doesNotMatch(html, /id="settings-export-data-button"|Export Data/);
    assert.doesNotMatch(source, /showDataExportModal|settings-export-data|\/settings\/db-size|_updateDataExportAvailability/);
});

test('dirty-state updates no longer call the removed export-availability method', () => {
    globalThis.window = { VibeRailsPerformance: null };
    const controller = new SettingsController({ setAppSettings() {} });
    const key = { value: '', dataset: { originalValue: '' } };
    const computerName = { value: 'Saved machine' };
    const saveState = { textContent: '', classList: { toggle() {} } };
    const saveButton = { disabled: true };
    const root = {
        querySelector(selector) {
            if (selector === '#setting-api-key') return key;
            if (selector === '#setting-computer-name') return computerName;
            if (selector === '#settings-save-state') return saveState;
            if (selector === '#settings-save-button') return saveButton;
            return null;
        }
    };

    controller._settingsRoot = root;
    controller._settingsSnapshot = controller._captureSettingsSnapshot(root);
    computerName.value = 'Unsaved machine';
    assert.doesNotThrow(() => controller._updateDirtyState(root));
    assert.equal(controller._settingsDirty, true);

    assert.doesNotThrow(() => controller._applyLinkedApiKey(root, '••••••••new1'));
    assert.equal(key.value, '••••••••new1');
    assert.equal(controller._settingsDirty, true);
});

test('saving settings omits retired options and preserves following arguments', async () => {
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
    controller._requestPresenceProof = async () => '1234';

    await controller.saveSettings(
        /* remoteAccess */ false,
        /* apiKey */ '',
        /* useVsCodeTheme */ false,
        /* mcpEnabled */ true,
        /* computerName */ '',
        /* codexEnterpriseNetworkProxy */ 'https://genai.example.test/v1',
        /* codexLlmProxyMode */ 'subscription',
        /* claudeEnterpriseNetworkProxy */ '',
        /* openCodeEnterpriseNetworkProxy */ '',
        /* grokEnterpriseNetworkProxy */ '',
        /* grokLlmProxyMode */ 'subscription',
        /* claudeTokenSaverEnabled */ true,
        /* codexTokenSaverEnabled */ true,
        /* openCodeTokenSaverEnabled */ true,
        /* grokTokenSaverEnabled */ true,
        /* clearApiKey */ true);

    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, '/api/v1/settings');
    assert.equal(calls[0].method, 'POST');
    assert.equal(calls[0].body.removeCoAuthorTrailers, undefined);
    assert.equal(calls[0].body.showVibeAiUi, undefined);
    assert.equal(calls[0].body.routeThroughVibeRailsAi, undefined);
    assert.equal(calls[0].body.codexEnterpriseNetworkProxy, 'https://genai.example.test/v1');
    assert.equal(calls[0].body.codexLlmProxyEnabled, undefined);
    assert.equal(calls[0].body.clearApiKey, true);
    assert.equal(calls[0].body.createVibeStoryTracking, undefined);
    assert.equal(calls[0].body.createVibeStoryTrackingCustomEnvs, undefined);
    assert.equal(calls[0].body.dataExportOptIn, true);

    await controller.saveSettings(false, '', false, true, '', '', 'subscription',
        '', '', '', 'subscription', true, true, true, true, false);
    assert.equal(calls[1].body.clearApiKey, false);
});

const templateOf = (html, id) => {
    const start = html.indexOf(`<template id="${id}">`);
    assert.ok(start >= 0, `${id} exists`);
    return html.slice(start, html.indexOf('</template>', start));
};

test('settings are two pages, each grouping its cards under a section tab bar', () => {
    const html = readFileSync(indexPath, 'utf8');
    const app = templateOf(html, 'settings-template');
    const account = templateOf(html, 'account-settings-template');

    assert.match(app, /<h1 class="vb-page-title">App settings<\/h1>/);
    assert.match(app, /class="settings-tabs" role="tablist" aria-label="App settings sections"/);
    for (const id of ['general', 'llm', 'context', 'setup', 'ai-board-manager']) {
        assert.match(app, new RegExp(`id="settings-tab-${id}"`));
        assert.match(app, new RegExp(`id="settings-panel-${id}" role="tabpanel"`));
    }
    assert.match(account, /<h1 class="vb-page-title">Account settings<\/h1>/);
    assert.match(account, /class="settings-tabs" role="tablist" aria-label="Account settings sections"/);
    for (const id of ['account', 'keys']) {
        assert.match(account, new RegExp(`id="settings-tab-${id}"`));
        assert.match(account, new RegExp(`id="settings-panel-${id}" role="tabpanel"`));
    }
    // The first tab is the default; the other panels start hidden.
    assert.match(app, /id="settings-tab-general"[\s\S]{0,120}aria-selected="true"/);
    assert.match(app, /id="settings-tab-llm"[\s\S]{0,120}aria-selected="false"/);
    assert.match(app, /id="settings-panel-llm"[\s\S]{0,120}hidden/);
    assert.match(account, /id="settings-tab-account"[\s\S]{0,120}aria-selected="true"/);
    assert.match(account, /id="settings-panel-keys"[\s\S]{0,120}hidden/);
    // Like settings stay on their own tabs, and each page links to the other.
    assert.match(app, /id="settings-panel-llm"[\s\S]*?Codex Settings[\s\S]*?Token Saver/);
    assert.match(account, /id="settings-panel-account"[\s\S]*?Account[\s\S]*?Remote access password[\s\S]*?id="setting-api-key"/);
    assert.doesNotMatch(app, /Remote access password|id="setting-api-key"|data-account-settings-action|data-settings-keys/);
    assert.doesNotMatch(account, /id="setting-remote-access"|id="setting-computer-name"|Codex Settings/);
    assert.match(app, /data-open-account-settings>Account settings</);
    assert.match(account, /data-open-app-settings>App settings</);
    // Each page's save bar submits its own form.
    assert.match(app, /form="app-settings-form"/);
    assert.match(account, /form="account-settings-form"/);
});

test('Context is an App settings tab, not a page of its own', () => {
    const html = readFileSync(indexPath, 'utf8');
    const source = readFileSync(modulePath, 'utf8');
    const appSource = readFileSync(path.resolve('VibeRails/wwwroot/app.js'), 'utf8');
    const app = templateOf(html, 'settings-template');

    assert.match(app, /id="settings-panel-context"[\s\S]*?data-context-project[\s\S]*?data-context-workspace/);
    assert.doesNotMatch(html, /id="context-template"|data-view="context"|data-open-context/);
    assert.match(source, /new ContextController\(this\.app\)/);
    // Old links and Board settings open the tab, keeping the board they asked for.
    assert.match(appSource, /'context': \(\) => this\.settingsController\.loadSettings\(\{ \.\.\.data, tab: 'context' \}\)/);
    assert.match(readFileSync(path.resolve('VibeRails/wwwroot/js/modules/board-controller.js'), 'utf8'),
        /navigate\('settings', \{ tab: 'context', boardId: board\.id \}\)/);
    assert.doesNotMatch(html, /setting-create-vibe-story-tracking/);
});

test('the account menu opens Account settings and the gear opens App settings', () => {
    const html = readFileSync(indexPath, 'utf8');
    const accountNav = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/account-nav.js'), 'utf8');
    const appSource = readFileSync(path.resolve('VibeRails/wwwroot/app.js'), 'utf8');

    assert.match(accountNav, /navigate\('account-settings', \{ tab: 'account' \}, \{ resetStack: true \}\)/);
    const gear = html.match(/<button\b[^>]*data-settings-nav[^>]*>/)[0];
    assert.match(gear, /data-view="settings"/);
    assert.match(gear, /aria-label="App settings"/);
    assert.doesNotMatch(gear, /\shidden/, 'the gear stays visible signed in and signed out');
    assert.doesNotMatch(appSource, /\[data-settings-nav\]/);
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

    const ids = ['general', 'llm', 'keys'];
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
            const match = /^#settings-panel-(general|llm|keys)$/.exec(selector);
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
    // Opening the page selects its first tab, which clears nothing that was ever entered.
    const clearsAtOpen = secretClears;

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

    // ArrowLeft wraps from General to KEYS.
    tablist.listeners.keydown.forEach(fn =>
        fn({ target: tabs[0], key: 'ArrowLeft', preventDefault() {} }));
    assert.equal(panels.keys.hidden, false);
    assert.equal(tabs[2].focusCount, 2);
    assert.equal(keysActivations, 2);
    tablist.listeners.click.forEach(fn => fn({ target: tabs[0] }));
    assert.equal(secretClears - clearsAtOpen, 2, 'leaving KEYS clears key passwords');
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

test('switching remote access on needs remote terminals, then a password; switching off always works', () => {
    globalThis.window = { VibeRailsPerformance: null };
    const features = available => ({ features: { remote_terminals: available } });
    const modals = [];
    const toasts = [];
    const app = {
        entitlements: features(false), appSettings: { apiKey: '' },
        showModal: (title, html) => { modals.push({ title, html }); },
        showToast: (...args) => toasts.push(args)
    };
    const controller = new SettingsController(app);
    const dirtyChecks = [];
    controller._updateDirtyState = root => dirtyChecks.push(root);
    const root = {};
    const toggle = { checked: true };

    // Signed out or Free: the shared prompt opens and the switch goes back to off.
    controller._pinIsSet = true;
    controller._guardRemoteAccess(root, toggle);
    assert.equal(toggle.checked, false);
    assert.equal(modals.length, 1);
    assert.equal(modals[0].title, 'Sign in to use remote terminals');
    assert.deepEqual(toasts, [], 'the prompt replaces the password reminder');
    assert.deepEqual(dirtyChecks, [root]);

    // Switching off is never intercepted, whatever the plan.
    controller._guardRemoteAccess(root, toggle);
    assert.equal(modals.length, 1);
    assert.equal(dirtyChecks.length, 1);

    // Available (Pro, or an unknown plan): the existing password rule still applies.
    for (const entitlements of [features(true), undefined]) {
        app.entitlements = entitlements;
        controller._pinIsSet = true;
        toggle.checked = true;
        controller._guardRemoteAccess(root, toggle);
        assert.equal(toggle.checked, true);
        controller._pinIsSet = false;
        controller._guardRemoteAccess(root, toggle);
        assert.equal(toggle.checked, false);
    }
    assert.equal(modals.length, 1);
    assert.equal(toasts.length, 2);
    assert.equal(toasts[0][0], 'Remote Access');
});

test('locked hosted features carry a hidden Pro badge that the app reveals', () => {
    const html = readFileSync(indexPath, 'utf8');
    const badge = feature => new RegExp(`<span class="badge pro-badge[^"]*" data-pro-badge="${feature}" title="Part of Vibe Rails Pro" hidden>Pro</span>`);
    const app = templateOf(html, 'settings-template');
    assert.match(app.slice(app.indexOf('id="setting-remote-access"'), app.indexOf('id="setting-performance-mode"')), badge('remote_terminals'));
    // Remote boards stays unlocked: listing and deleting hosted boards are free.
    const board = templateOf(html, 'board-template');
    assert.doesNotMatch(board.slice(board.indexOf('data-board-action="remote-boards"'), board.indexOf('data-board-stats')), /pro-badge/);
    const css = readFileSync(path.resolve('VibeRails/wwwroot/style.css'), 'utf8');
    assert.match(css, /\.badge-cli, \.pro-badge \{\s*color: var\(--tone-active-fg\);/, 'the badge uses the Active tone');
    assert.match(css, /\.pro-upgrade-dialog \{\s*max-width: 420px;/);
});
