import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { boardSettingsNavigation, contextEditorSection, mountAppContext, mountBoardLevelContext, mountCardContext, mountContextEditor, mountBoardSettingsNavigation, mountBoardSync, mountLaneAutomation, newBoardNavigation } from '../../../VibeRails/wwwroot/js/modules/board-settings.js';

test('failed publication reloads the saved sync choice', async () => {
    const listeners = new Map();
    const content = { innerHTML: '' };
    const element = {
        isConnected: true,
        querySelector: () => content,
        addEventListener: (name, listener) => listeners.set(name, listener),
        removeEventListener: name => listeners.delete(name)
    };
    const toasts = [];
    let reads = 0;
    BoardApi.attach({
        apiCall: (_path, method) => {
            if (method === 'GET') return Promise.resolve({ enabled: ++reads > 1, configured: true, lastError: reads > 1 ? 'Publication failed' : null });
            assert.equal(method, 'PUT');
            return Promise.reject(new Error('Publication failed'));
        }
    });
    const dispose = mountBoardSync({ showToast: (...args) => toasts.push(args) }, element, 'board-id');
    await new Promise(resolve => setImmediate(resolve));
    const target = { checked: true, disabled: false, matches: selector => selector === '[data-board-sync-enabled]' };

    await listeners.get('change')({ target });

    assert.equal(reads, 2);
    assert.match(content.innerHTML, /data-board-sync-enabled checked/);
    assert.match(content.innerHTML, /Publication failed/);
    assert.doesNotMatch(content.innerHTML, /This board stays local/);
    assert.equal(toasts[0][2], 'error');
    dispose();
});

test('closing settings suppresses a late board-status callback', async () => {
    let finish;
    let signal;
    const content = { innerHTML: 'loading' };
    const element = {
        isConnected: true,
        querySelector: () => content,
        addEventListener() {}, removeEventListener() {}
    };
    BoardApi.attach({ apiCall: (_path, _method, _body, options) => {
        signal = options.signal;
        return new Promise(resolve => { finish = resolve; });
    } });
    const dispose = mountBoardSync({}, element, 'closed-board', () => assert.fail('must not update a closed editor'));
    dispose();
    assert.equal(signal.aborted, true);
    finish({ isJiraBoard: true });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(content.innerHTML, 'loading');
});

for (const mount of [mountAppContext, mountBoardLevelContext, mountCardContext, mountLaneAutomation]) {
    test(`${mount.name} aborts on close and ignores a late response`, async () => {
        let finish;
        let signal;
        const content = { innerHTML: 'loading' };
        const element = {
            isConnected: true,
            querySelector: () => content,
            querySelectorAll: () => [],
            addEventListener() {}, removeEventListener() {}
        };
        const app = {
            apiCall: (_path, _method, _body, options) => {
                signal = options.signal;
                return new Promise(resolve => { finish = resolve; });
            },
            showToast: () => assert.fail('a disposed modal must not show errors')
        };
        BoardApi.attach(app);
        const dispose = mount(app, element, 'scoped-id');
        dispose();
        assert.equal(signal.aborted, true);
        finish({ revision: 1, context: { defaultMessage: 'late', typeOverrides: [] }, jobId: 2, jobs: [] });
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(content.innerHTML, 'loading');
    });
}

test('a dedicated Azure DevOps board hides the viberails.ai sync choice', async () => {
    const content = { innerHTML: 'loading' };
    const element = { isConnected: true, hidden: false, querySelector: () => content, addEventListener() {}, removeEventListener() {} };
    const statuses = [];
    BoardApi.attach({ apiCall: () => Promise.resolve({ isJiraBoard: false, remoteProvider: 'ado', enabled: false, configured: true }) });
    const dispose = mountBoardSync({}, element, 'ado-board', status => statuses.push(status.remoteProvider));
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(element.hidden, true);
    assert.equal(content.innerHTML, '');
    assert.deepEqual(statuses, ['ado']);
    dispose();
});

test('a copy of a board shared with you shows the read-only notice instead of the sync choice (VIBE-235)', async () => {
    const content = { innerHTML: 'loading' };
    const element = { isConnected: true, hidden: false, querySelector: () => content, addEventListener() {}, removeEventListener() {} };
    BoardApi.attach({ apiCall: () => Promise.resolve({ shared: true, enabled: true, configured: true, remoteProvider: null,
        remoteUrl: 'https://viberails.ai/Boards/11111111-1111-4111-8111-111111111111' }) });
    const dispose = mountBoardSync({}, element, 'shared-board');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(element.hidden, false);
    assert.match(content.innerHTML, /shared with you and is read-only/);
    assert.match(content.innerHTML, /Open on viberails\.ai/);
    assert.doesNotMatch(content.innerHTML, /data-board-sync-enabled|data-board-sync-now/);
    dispose();
});

// Just enough DOM for the settings navigation: tabs, panels in document order, and moves.
function settingsEditor() {
    const node = (props = {}) => {
        const element = { hidden: false, attributes: {}, classes: new Set(), listeners: {}, dataset: {}, ...props,
            classList: { toggle: (name, on) => (on ? element.classes.add(name) : element.classes.delete(name)),
                add: name => element.classes.add(name), remove: name => element.classes.delete(name), contains: name => element.classes.has(name) },
            setAttribute(name, value) { element.attributes[name] = String(value); },
            removeAttribute(name) {
                delete element.attributes[name];
                if (name === 'data-board-settings-panel') delete element.dataset.boardSettingsPanel;
            },
            addEventListener(type, listener) { element.listeners[type] = listener; },
            focus() { focused = element; },
            contains: other => other === element };
        return element;
    };
    let focused = null;
    const order = [];
    const ids = ['general', 'jira', 'ado', 'context', 'history'];
    const tabs = ids.map(id => node({ id: `board-settings-tab-${id}`, dataset: { boardSettingsTab: id } }));
    tabs[0].classes.add('active');
    const panels = ids.map(id => {
        const panel = node({ dataset: { boardSettingsPanel: id }, hidden: id !== 'general', parent: null });
        panel.before = other => { order.splice(order.indexOf(other), 1); order.splice(order.indexOf(panel), 0, other); other.parent = null; };
        panel.after = other => { order.splice(order.indexOf(other), 1); order.splice(order.indexOf(panel) + 1, 0, other); other.parent = null; };
        panel.append = child => { order.splice(order.indexOf(child), 1); order.push(child); child.parent = panel; };
        order.push(panel);
        return panel;
    });
    const editor = {
        isConnected: true,
        querySelectorAll: selector => selector === '[data-board-settings-tab]' ? tabs
            : order.filter(panel => panel.dataset.boardSettingsPanel !== undefined),
        querySelector: selector => order.find(panel => selector === `[data-board-settings-panel="${panel.dataset.boardSettingsPanel}"]`) || null
    };
    const visibleTabs = () => tabs.filter(tab => !tab.hidden).map(tab => tab.dataset.boardSettingsTab);
    const activeTab = () => tabs.find(tab => tab.classes.has('active')).dataset.boardSettingsTab;
    const panelOrder = () => order.filter(panel => !panel.parent).map(panel => panel.dataset.boardSettingsPanel);
    return { editor, tabs, panels, visibleTabs, activeTab, panelOrder, focused: () => focused };
}

test('settings navigation follows the board provider without replacing any panel', () => {
    const { editor, tabs, panels, visibleTabs, activeTab, panelOrder } = settingsEditor();
    const [general, jira, ado] = panels;
    const selections = [];
    const navigation = mountBoardSettingsNavigation(editor, (tab, tracker) => selections.push([tab, tracker]));
    assert.deepEqual(visibleTabs(), ['general', 'jira', 'ado', 'context', 'history']);
    tabs[2].listeners.click();
    assert.deepEqual(selections.at(-1), ['ado', 'ado']);
    assert.equal(ado.hidden, false);
    tabs[0].listeners.click();
    assert.deepEqual(selections.at(-1), ['general', null]);

    // An Azure DevOps board: its connection joins General and both tracker tabs go.
    navigation.followProvider('ado');
    assert.deepEqual(visibleTabs(), ['general', 'context', 'history']);
    assert.equal(ado.parent, general);
    assert.equal(ado.hidden, false);
    assert.equal(ado.attributes['aria-label'], 'Azure DevOps');
    assert.equal('role' in ado.attributes, false);
    assert.ok(ado.classes.has('board-settings-tracker-combined'));
    assert.equal(jira.hidden, true, 'the Jira panel stays out of sight');
    assert.equal(jira.parent, null);
    assert.deepEqual(selections.at(-1), ['general', 'ado']);
    const arrow = { key: 'ArrowRight', preventDefault() {} };
    tabs[0].listeners.keydown(arrow);
    assert.equal(activeTab(), 'context', 'keyboard navigation skips hidden tabs');
    tabs[3].listeners.keydown({ key: 'ArrowLeft', preventDefault() {} });
    assert.equal(activeTab(), 'general');
    const before = selections.length;
    navigation.followProvider('ado');
    assert.equal(selections.length, before, 'the same provider changes nothing');

    // Unlinked: the panel returns to its own tab, which is selected so the result stays visible.
    navigation.followProvider(null);
    assert.deepEqual(visibleTabs(), ['general', 'jira', 'ado', 'context', 'history']);
    assert.equal(ado.parent, null);
    assert.equal(ado.dataset.boardSettingsPanel, 'ado');
    assert.equal(ado.attributes.role, 'tabpanel');
    assert.equal(ado.attributes['aria-labelledby'], 'board-settings-tab-ado');
    assert.equal('aria-label' in ado.attributes, false);
    assert.ok(!ado.classes.has('board-settings-tracker-combined'));
    assert.deepEqual(panelOrder(), ['general', 'jira', 'ado', 'context', 'history']);
    assert.equal(activeTab(), 'ado');
    assert.deepEqual(selections.at(-1), ['ado', 'ado']);

    // A Jira board while the Azure DevOps tab is open: General takes over and shows Jira.
    navigation.followProvider('jira');
    assert.deepEqual(visibleTabs(), ['general', 'context', 'history']);
    assert.equal(jira.parent, general);
    assert.equal(ado.hidden, true);
    assert.deepEqual(selections.at(-1), ['general', 'jira']);
    navigation.followProvider('github');
    assert.equal(jira.parent, null, 'an unknown provider is a local board');
    assert.deepEqual(panelOrder(), ['general', 'jira', 'ado', 'context', 'history']);

    navigation.dispose();
    navigation.followProvider('ado');
    assert.equal(ado.parent, null, 'a closed dialog is never rearranged');
});

test('the New board dialog offers a local board or one connected to Jira or Azure DevOps, with the settings tab ids', () => {
    const tabs = html => [...html.matchAll(/data-board-settings-tab="([^"]+)">([^<]+)</g)].map(match => [match[1], match[2]]);
    assert.deepEqual(tabs(newBoardNavigation()), [['general', 'Local'], ['jira', 'Jira Cloud'], ['ado', 'Azure DevOps']]);
    assert.match(newBoardNavigation(), /aria-label="Board type" role="tablist"/);
    assert.match(newBoardNavigation(), /id="board-settings-tab-ado" aria-controls="board-settings-ado" aria-selected="false"/);
    assert.deepEqual(tabs(boardSettingsNavigation()), [['general', 'General'], ['jira', 'Jira Cloud'], ['ado', 'Azure DevOps'],
        ['context', 'Context'], ['history', 'History']]);
    assert.match(boardSettingsNavigation(), /aria-label="Board settings" role="tablist"/);
});

test('the Context workspace places Project context between App and the board picker', () => {
    const levels = html => [...html.matchAll(/data-context-unit="([^"]+)"/g)].map(match => match[1]);
    const scope = contextEditorSection({ project: true });
    assert.deepEqual(levels(scope), ['app', 'nudge', 'project']);
    assert.deepEqual(levels(contextEditorSection()), ['app', 'nudge']);
    assert.match(scope, /<summary>App context<\/summary><p[^>]*>Applies across all repos[^<]*Nudge/);
    assert.match(scope, /<summary>Project context<\/summary><p[^>]*>Sent to every LLM launched from this repo[^<]*Nudge off/);
    assert.ok(scope.indexOf('App context') < scope.indexOf('Project context'));
    assert.ok(scope.indexOf('Project context') < scope.indexOf('context-board-picker'));
    assert.match(contextEditorSection(), /Open a Git repository to edit Project context/);
    assert.doesNotMatch(boardSettingsNavigation(), /App context|Every Context|data-board-settings-tab="every-context"/);
});

test('Card context is collapsed above card types and all levels share the editor save', async () => {
    const content = { innerHTML: '' };
    const element = {
        isConnected: true,
        querySelector: () => content,
        querySelectorAll: () => [],
        addEventListener() {}, removeEventListener() {}
    };
    const app = { apiCall: () => Promise.resolve({ revision: 2, context: { defaultMessage: 'Card text', typeOverrides: [{ type: 'bug', mode: 'append', message: '<b>Bug</b>' }] } }), showToast() {} };
    BoardApi.attach(app);
    const dispose = mountCardContext(app, element, 'board-a');
    await new Promise(resolve => setImmediate(resolve));
    const html = content.innerHTML;
    assert.ok(html.indexOf('data-context-level="card"') < html.indexOf('data-context-level="type"'));
    assert.match(html, /Always sent/);
    assert.match(html, /<option value="append" selected>Card context \+ type-specific<\/option>/);
    assert.match(html, /&lt;b&gt;Bug&lt;\/b&gt;/);
    assert.match(html, /<details[^>]*data-context-level="card">/);
    assert.equal((html.match(/<details/g) || []).length, 7);
    assert.doesNotMatch(html, /<details[^>]*\bopen\b|data-settings-save/);
    assert.equal((contextEditorSection().match(/data-context-save/g) || []).length, 1);
    assert.match(contextEditorSection(), /data-context-level="app" open/);
    assert.doesNotMatch(contextEditorSection(), /data-context-level="board" open/);
    dispose();
});

test('changing boards disposes only the former board requests; leaving disposes every request', () => {
    const calls = [];
    const units = new Map(['app', 'nudge', 'project', 'board', 'card'].map(name => [name, { content: { innerHTML: 'loading' } }]));
    const unitElement = name => ({
        dataset: { contextUnit: name },
        isConnected: true,
        querySelector: () => units.get(name).content,
        querySelectorAll: () => [],
        addEventListener() {}, removeEventListener() {}
    });
    const button = { addEventListener() {}, removeEventListener() {} };
    const boardUnits = { innerHTML: '' };
    const root = {
        querySelector(selector) {
            if (selector === '[data-context-save]') return button;
            if (selector === '[data-context-board-units]') return boardUnits;
            return unitElement(/data-context-unit="([^"]+)"/.exec(selector)[1]);
        },
        addEventListener() {}, removeEventListener() {}
    };
    const app = { apiCall: (path, method, _body, options) => { calls.push({ path, method, signal: options.signal }); return new Promise(() => {}); } };
    BoardApi.attach(app);
    const editor = mountContextEditor(app, root);
    assert.deepEqual(calls.map(call => call.path), ['/api/v1/board/app-context', '/api/v1/settings', '/api/v1/board/project-context']);
    editor.setBoard('board-a');
    assert.deepEqual(calls.slice(3).map(call => call.path),
        ['/api/v1/board/boards/board-a/every-context', '/api/v1/board/boards/board-a/context']);
    editor.setBoard('board-b');
    assert.ok(calls.slice(3, 5).every(call => call.signal.aborted));
    assert.ok(calls.slice(0, 3).every(call => !call.signal.aborted));
    editor.dispose();
    assert.ok(calls.every(call => call.signal.aborted));
});

test('App context saves through its own route with its revision', async () => {
    const listeners = new Map();
    const content = { innerHTML: '' };
    const input = { value: 'For every launch' };
    const element = {
        isConnected: true,
        querySelector: selector => selector === '[data-board-settings-content]' ? content : input,
        querySelectorAll: () => [],
        addEventListener: (name, listener) => listeners.set(name, listener),
        removeEventListener: name => listeners.delete(name)
    };
    const calls = [];
    const toasts = [];
    const app = {
        apiCall: (path, method, body) => {
            calls.push({ path, method, body });
            return Promise.resolve(method === 'GET' ? { message: 'Old', revision: 5 } : { message: body.message, revision: 6 });
        },
        showToast: (...args) => toasts.push(args)
    };
    BoardApi.attach(app);
    const dispose = mountAppContext(app, element, null, { toastTitle: 'Settings' });
    await new Promise(resolve => setImmediate(resolve));
    assert.match(content.innerHTML, /data-context-message="app">Old<\/textarea>/);
    await listeners.get('click')({ target: { closest: selector => selector === '[data-settings-save]' ? { disabled: false } : null } });
    assert.deepEqual(calls[1], { path: '/api/v1/board/app-context', method: 'PUT', body: { message: 'For every launch', expectedRevision: 5 } });
    assert.deepEqual(toasts[0], ['Settings', 'App context saved for every launch.', 'success']);
    dispose();
});

test('Board context loads escaped text and saves its board message with its revision', async () => {
    const listeners = new Map();
    const content = { innerHTML: '' };
    const input = { value: 'Updated shared context' };
    const button = { disabled: false };
    const element = {
        isConnected: true,
        querySelector: selector => selector === '[data-board-settings-content]' ? content : input,
        querySelectorAll: () => [],
        addEventListener: (name, listener) => listeners.set(name, listener),
        removeEventListener: name => listeners.delete(name)
    };
    const calls = [];
    let finishSave;
    const toasts = [];
    const app = {
        apiCall: (path, method, body) => {
            calls.push({ path, method, body });
            return method === 'GET' ? Promise.resolve({ message: '</textarea><script>bad()</script>', revision: 3 })
                : new Promise(resolve => { finishSave = resolve; });
        },
        showToast: (...args) => toasts.push(args)
    };
    BoardApi.attach(app);
    const dispose = mountBoardLevelContext(app, element, 'board-a');
    await new Promise(resolve => setImmediate(resolve));
    assert.match(content.innerHTML, /&lt;\/textarea&gt;&lt;script&gt;/);
    assert.match(contextEditorSection(), /currently selected board/);
    const event = { target: { closest: selector => selector === '[data-settings-save]' ? button : null } };
    const saving = listeners.get('click')(event);
    await listeners.get('click')(event);
    assert.equal(button.disabled, true);
    assert.equal(calls.length, 2, 'duplicate saves are suppressed');
    assert.deepEqual(calls[1], { path: '/api/v1/board/boards/board-a/every-context', method: 'PUT', body: { message: input.value, expectedRevision: 3 } });
    finishSave({ message: input.value, revision: 4 });
    await saving;
    assert.equal(button.disabled, false);
    assert.equal(toasts[0][2], 'success');
    dispose();
});

// The viberails.ai section with a plan: its content host, both listeners and every prompt it opens.
async function syncFixture({ status, locked = false, write = async () => status }) {
    const listeners = new Map();
    const content = { innerHTML: '' };
    const element = { isConnected: true, hidden: false, querySelector: () => content,
        addEventListener: (name, listener) => listeners.set(name, listener), removeEventListener: name => listeners.delete(name) };
    const calls = [], modals = [], toasts = [];
    const app = {
        entitlements: { state: locked ? 'free' : 'unknown', features: { remote_boards: !locked } },
        appSettings: { apiKey: '••••abcd' },
        apiCall: (path, method) => { calls.push(method); return method === 'GET' ? Promise.resolve(status) : write(path, method); },
        showModal: (title, html) => { modals.push({ title, html }); },
        showToast: (...args) => toasts.push(args),
        refreshEntitlements: async () => app.entitlements
    };
    BoardApi.attach(app);
    const dispose = mountBoardSync(app, element, 'board-id');
    await new Promise(resolve => setImmediate(resolve));
    const toggle = checked => {
        const target = { checked, disabled: false, matches: selector => selector === '[data-board-sync-enabled]' };
        return listeners.get('change')({ target }).then(() => target);
    };
    const press = selector => listeners.get('click')({ target: { disabled: false, closest: candidate => candidate === selector ? {} : null } });
    return { content, calls, modals, toasts, toggle, press, dispose };
}

test('a paused sync says why and offers Upgrade only to the account that pays', async () => {
    const paused = pausedPayer => ({ enabled: true, configured: true, lastError: 'The hosted server paused this board.',
        pausedCode: 'pro_required', pausedPayer });
    const account = await syncFixture({ status: paused('account'), locked: true });
    assert.doesNotMatch(account.content.innerHTML, /role="alert"/, 'the paused line replaces the stored reason it came from');
    assert.match(account.content.innerHTML, /data-board-sync-paused>Sync is paused because remote Boards need Vibe Rails Pro\.[^<]*nothing was deleted\./);
    assert.match(account.content.innerHTML, /data-board-sync-upgrade>Upgrade</);
    assert.match(account.content.innerHTML, /data-pro-badge="remote_boards" title="Part of Vibe Rails Pro">Pro</);
    assert.deepEqual(account.modals, [], 'reading the status never opens the prompt');
    await account.press('[data-board-sync-upgrade]');
    assert.equal(account.modals.length, 1);
    assert.equal(account.modals[0].title, 'Remote Boards need Vibe Rails Pro');
    assert.deepEqual(account.calls, ['GET']);
    account.dispose();

    const member = await syncFixture({ status: paused('board_owner') });
    assert.match(member.content.innerHTML, /data-board-sync-paused>Sync is paused until this Board's owner has Vibe Rails Pro\. You do not need to upgrade/);
    assert.doesNotMatch(member.content.innerHTML, /data-board-sync-upgrade/);
    member.dispose();

    // An older backend sends neither field: the section reads as it always has.
    const older = await syncFixture({ status: { enabled: true, configured: true, lastError: 'Bad API key' } });
    assert.match(older.content.innerHTML, /Bad API key/);
    assert.doesNotMatch(older.content.innerHTML, /data-board-sync-paused|data-board-sync-upgrade/);
    assert.match(older.content.innerHTML, /data-pro-badge="remote_boards" title="Part of Vibe Rails Pro" hidden>Pro</);
    older.dispose();
});

test('a locked plan cannot turn sync on or sync now, and can always turn it off', async () => {
    const status = { enabled: true, configured: true, lastError: null };
    const f = await syncFixture({ status, locked: true, write: async () => ({ ...status, enabled: false }) });
    const on = await f.toggle(true);
    assert.equal(on.checked, false, 'the switch stays off');
    assert.equal(f.modals.length, 1);
    await f.press('[data-board-sync-now]');
    assert.equal(f.modals.length, 2);
    assert.deepEqual(f.calls, ['GET'], 'nothing was sent to the board');
    assert.deepEqual(f.toasts, []);

    await f.toggle(false);
    assert.deepEqual(f.calls, ['GET', 'PUT']);
    assert.equal(f.modals.length, 2);
    assert.deepEqual(f.toasts[0], ['Board', 'Board sync disabled.', 'success']);
    f.dispose();
});

test('the server\'s Pro denial opens the prompt in place of an error toast', async () => {
    const denial = payer => Object.assign(new Error('Vibe Rails Pro is required.'), { code: 'pro_required', feature: 'remote_boards', payer });
    const status = { enabled: false, configured: true, lastError: null };
    const f = await syncFixture({ status, write: (_path, method) => Promise.reject(denial(method === 'PUT' ? 'account' : 'board_owner')) });
    await f.toggle(true);
    assert.equal(f.modals[0].title, 'Remote Boards need Vibe Rails Pro');
    await f.press('[data-board-sync-now]');
    assert.equal(f.modals[1].title, "This Board's owner needs Vibe Rails Pro");
    assert.doesNotMatch(f.modals[1].html, /Upgrade to Pro|Manage billing|href=/);
    assert.deepEqual(f.toasts, []);
    assert.deepEqual(f.calls, ['GET', 'PUT', 'GET', 'POST']);
    f.dispose();
});
