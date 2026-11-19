import test from 'node:test';
import assert from 'node:assert/strict';
import { TerminalController, TerminalManager } from '../../../VibeRails/wwwroot/js/modules/terminal-multitab.js';
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

const boardCard = { id: 'story', key: 'VB-ABCDE-17', displayId: 'VIBE-17', title: 'Investigate the queue' };
const boardLabel = 'VIBE-17 · Investigate the queue';

test('generated titles replace both remembered and legacy Board launch labels and keep refreshing', () => {
    const { manager, add } = titleManager();
    const remembered = add('remembered', { customLabel: true, labelSource: 'launch',
        taskKey: 'board-card:story', label: boardLabel, boardCard });
    const legacy = add('legacy', { customLabel: true,
        taskKey: 'board-card:story:chat', label: boardLabel, boardCard });
    manager.syncSessionTitles([titled('remembered'), titled('legacy')]);
    for (const state of [remembered, legacy]) {
        assert.equal(state.label, 'Diagnose backup failures');
        assert.equal(state.title, state.label);
        assert.equal(state.customLabel, false, 'a generated name remains automatic');
    }
    manager.syncSessionTitles([titled('remembered', 'Fix queue acknowledgements'),
        titled('legacy', 'Fix queue acknowledgements')]);
    assert.equal(remembered.label, 'Fix queue acknowledgements');
    assert.equal(legacy.label, remembered.label);
});

test('Board title updates preserve manual names, renames in progress and unrelated launch labels', () => {
    const { manager, add, rendered } = titleManager();
    const manual = add('manual', { customLabel: true, taskKey: 'board-card:story', boardCard });
    manager.renameTab('manual', boardLabel);
    assert.equal(manual.labelSource, 'user');
    add('legacy-manual', { customLabel: true, taskKey: 'board-card:story', label: 'My queue notes', boardCard });
    add('editing', { customLabel: true, labelSource: 'launch', taskKey: 'board-card:story', renaming: true });
    add('script', { customLabel: true, labelSource: 'launch', taskKey: 'python-script:script' });
    add('manager', { customLabel: true, labelSource: 'launch', taskKey: 'board-manager:board' });
    add('other-card', { customLabel: true, taskKey: 'board-card:different', label: boardLabel, boardCard });
    rendered.length = 0;
    manager.syncSessionTitles([...manager.tabs.keys()].map(id => titled(id)));
    assert.deepEqual(rendered, []);
    assert.equal(manual.label, boardLabel, 'even a manual name identical to the card stays protected');
});

test('launch-label provenance survives storage and reopening cannot erase a manual rename', t => {
    const storage = new Map();
    const previousWindow = globalThis.window;
    globalThis.window = { sessionStorage: {
        setItem: (key, value) => storage.set(key, value), getItem: key => storage.get(key) ?? null
    } };
    t.after(() => { globalThis.window = previousWindow; });
    const { manager, add } = titleManager();
    manager.saveTabMeta = TerminalManager.prototype.saveTabMeta;
    const controller = new TerminalController({});
    const metadata = { label: boardLabel, title: boardLabel, taskKey: 'board-card:story' };
    controller.rememberTabLaunch('remembered', metadata);
    const saved = manager.getTabMetaFromStorage('remembered');
    assert.equal(saved.labelSource, 'launch');
    const state = add('remembered', { ...saved, boardCard });
    manager.renameTab(state.id, boardLabel);
    assert.equal(manager.getTabMetaFromStorage(state.id).labelSource, 'user');

    // No live manager: the restored metadata alone must protect the user's name.
    controller.rememberTabLaunch(state.id, { ...metadata, label: 'A renamed card' });
    const restored = manager.getTabMetaFromStorage(state.id);
    assert.equal(restored.label, boardLabel);
    assert.equal(restored.labelSource, 'user');
    Object.assign(state, restored);
    manager.syncSessionTitles([titled(state.id)]);
    assert.equal(state.label, boardLabel);
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
