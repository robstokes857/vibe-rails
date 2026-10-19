import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { mountBoardContext, mountBoardSettingsNavigation, mountBoardSync, mountLaneAutomation } from '../../../VibeRails/wwwroot/js/modules/board-settings.js';

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

for (const mount of [mountBoardContext, mountLaneAutomation]) {
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
