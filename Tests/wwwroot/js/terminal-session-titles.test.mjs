import test from 'node:test';
import assert from 'node:assert/strict';
import { TerminalManager } from '../../../VibeRails/wwwroot/js/modules/terminal-multitab.js';
import { ChatHistorySidebar } from '../../../VibeRails/wwwroot/js/modules/chat-history-sidebar.js';

function titleManager() {
    const manager = Object.create(TerminalManager.prototype);
    const saved = [];
    const rendered = [];
    Object.assign(manager, {
        tabs: new Map(), activeTabId: 'selected',
        saveTabTitle: (...args) => saved.push(args),
        saveTabMeta() {}, applyTabAccent() {},
        renderTabButton: tab => rendered.push(tab.state.id)
    });
    const add = (id, overrides = {}) => {
        const tab = { state: { id, sessionId: id, hasActiveSession: true,
            label: 'Codex', title: 'Codex Terminal', ...overrides } };
        manager.tabs.set(id, tab);
        return tab.state;
    };
    return { manager, add, saved, rendered };
}

const titled = (id, name = 'Diagnose backup failures') => ({
    id, sessionDisplayName: name, sessionTitleGenerated: true
});

test('generated titles rename matching live tabs without focus changes or a custom-label flag', () => {
    const { manager, add, saved, rendered } = titleManager();
    const selected = add('selected');
    const background = add('background');
    const unrelated = add('unrelated');
    manager.syncSessionTitles([titled('selected'), titled('background', 'Check acknowledgements')]);
    assert.equal(selected.label, 'Diagnose backup failures');
    assert.equal(selected.title, selected.label);
    assert.equal(selected.customLabel, undefined);
    assert.equal(background.label, 'Check acknowledgements');
    assert.equal(unrelated.label, 'Codex');
    assert.equal(manager.activeTabId, 'selected');
    assert.deepEqual(saved, [['selected', selected.label], ['background', background.label]]);
    assert.deepEqual(rendered, ['selected', 'background']);

    manager.syncSessionTitles([titled('selected'), titled('background', background.label)]);
    assert.equal(rendered.length, 2, 'unchanged titles do not rerender the tabs');
});

test('history responses preserve explicit names, active renames and stopped or replaced sessions', async () => {
    const { manager, add, rendered } = titleManager();
    add('custom', { customLabel: true, label: 'My investigation' });
    add('editing', { renaming: true });
    add('stopped', { hasActiveSession: false });
    const replaced = add('reused');
    add('prompt');
    add('blank');
    let finish;
    const sidebar = new ChatHistorySidebar({ apiCall: () => new Promise(resolve => { finish = resolve; }) });
    sidebar.body = { isConnected: true };
    sidebar._renderItems = () => {};
    sidebar.onItemsUpdated = items => manager.syncSessionTitles(items);
    const refresh = sidebar._runSoftRefresh();
    replaced.sessionId = 'new-session';
    finish({ items: [titled('custom'), titled('editing'), titled('stopped'), titled('reused'),
        { ...titled('prompt'), sessionTitleGenerated: false }, titled('blank', '   ')] });
    await refresh;
    assert.deepEqual(rendered, []);
    assert.equal(replaced.label, 'Codex');

    manager._destroyed = true;
    manager.syncSessionTitles([titled('new-session')]);
    assert.deepEqual(rendered, [], 'a disposed manager ignores history updates');
});
