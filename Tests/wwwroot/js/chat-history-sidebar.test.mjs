import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/chat-history-sidebar.js');
const { ChatHistorySidebar } = await import(pathToFileURL(modulePath).href);

test('ChatHistorySidebar removes document listeners when destroyed', (t) => {
    const originalDocument = globalThis.document;
    const originalLocalStorage = globalThis.localStorage;
    t.after(() => {
        globalThis.document = originalDocument;
        globalThis.localStorage = originalLocalStorage;
    });

    const added = [];
    const removed = [];
    globalThis.document = {
        activeElement: null,
        addEventListener(type, handler) { added.push({ type, handler }); },
        removeEventListener(type, handler) { removed.push({ type, handler }); }
    };
    globalThis.localStorage = {
        getItem() { return '1'; },
        setItem() {}
    };

    const sidebarElement = {
        classList: { contains: () => false },
        querySelector: () => null,
        addEventListener() {}
    };
    const root = {
        querySelector(selector) {
            return selector === '#ch-sidebar' ? sidebarElement : null;
        }
    };
    const sidebar = new ChatHistorySidebar({});

    sidebar.mount(root);
    sidebar.destroy();

    assert.deepEqual(added.map(entry => entry.type), ['click', 'keydown']);
    assert.equal(removed.length, 2);
    for (const addedEntry of added) {
        const removedEntry = removed.find(entry => entry.type === addedEntry.type);
        assert.equal(removedEntry?.handler, addedEntry.handler, `${addedEntry.type} removes the exact mounted handler`);
    }
});

test('Terminal manager destruction tears down its history sidebar', () => {
    const terminalSource = readFileSync(
        path.resolve('VibeRails/wwwroot/js/modules/terminal-multitab.js'),
        'utf8');
    assert.match(terminalSource, /this\.historySidebar\?\.destroy\(\)/);
    assert.match(terminalSource, /this\.manager\.historySidebar = historySidebar/);
});

test('ChatHistorySidebar only emits allowlisted inline logo filters', () => {
    const sidebar = new ChatHistorySidebar({});
    const valid = sidebar._renderBrandLogo({
        logo: '/logo.svg',
        label: 'Safe',
        logoFilter: 'brightness(0) invert(1)'
    }, 'logo');
    const invalid = sidebar._renderBrandLogo({
        logo: '/logo.svg',
        label: 'Unsafe',
        logoFilter: 'none; background-image: url(javascript:alert(1))'
    }, 'logo');

    assert.match(valid, /style="filter: brightness\(0\) invert\(1\)"/);
    assert.doesNotMatch(invalid, /style=/);
    assert.doesNotMatch(invalid, /javascript:/);
});

test('Resume modal passes its title to the shared modal escaper exactly once', () => {
    const source = readFileSync(modulePath, 'utf8');
    assert.match(source, /showModal\(`Sending to \$\{llmDisplayLabel\}`/);
    assert.doesNotMatch(source, /showModal\(`Sending to \$\{escapeHtml\(llmDisplayLabel\)\}`/);
});

function historySidebar(apiCall = async () => ({ items: [] })) {
    const sidebar = new ChatHistorySidebar({
        data: { environments: [], configs: { rootPath: 'C:/project' } },
        getCliBrand: cli => ({ label: cli || 'CLI' }),
        getProjectNameFromPath: dir => dir.split(/[\\/]/).at(-1),
        apiCall, showError() {}
    });
    sidebar.body = { isConnected: true, clientHeight: 0 };
    sidebar._renderItems = () => {};
    return sidebar;
}

function session(id, overrides = {}) {
    return { id, cli: 'codex', workingDirectory: 'C:\\project', inputText: 'First prompt',
        startedUTC: '2026-09-28T10:00:00Z', endedUTC: '2026-09-28T11:00:00Z', exitCode: 0, ...overrides };
}

const cards = [{ id: 'card-a', key: 'VB-M66G2-64', title: 'Better filters' },
    { id: 'card-b', key: 'VB-XYZ12-65', title: 'Additional card' }];

test('Board labels replace automatic names and preserve explicit renames', () => {
    const sidebar = historySidebar();
    assert.equal(sidebar._getDisplayName(session('a', { boardCards: cards })), 'VB-M66G2-64 · Better filters');
    assert.equal(sidebar._getDisplayName(session('a', { boardCards: cards, sessionDisplayName: 'First prompt' })), 'VB-M66G2-64 · Better filters');
    assert.equal(sidebar._getDisplayName(session('a', { boardCards: cards, sessionDisplayName: 'My investigation' })), 'My investigation');
    assert.equal(sidebar._getDisplayName(session('a')), 'First prompt');
});

test('filters intersect provider, exact environment, attached card, folder and outcome', () => {
    const sidebar = historySidebar();
    sidebar.allItems = [
        session('match', { boardCards: cards, environmentName: 'Review', exitCode: 2 }),
        session('other-provider', { cli: 'claude', boardCards: cards, environmentName: 'Review', exitCode: 2 }),
        session('base', { boardCards: cards, exitCode: 2 }),
        session('no-card', { environmentName: 'Review', exitCode: 2 }),
        session('other-dir', { boardCards: cards, environmentName: 'Review', workingDirectory: 'C:/other', exitCode: 2 }),
        session('success', { boardCards: cards, environmentName: 'Review' }),
        session('live', { boardCards: cards, environmentName: 'Review', endedUTC: null, exitCode: 2 })
    ];
    sidebar.llmFilters.add('codex');
    sidebar.environmentFilter = sidebar._environmentKey('codex', 'review');
    sidebar.boardFilter = 'linked';
    sidebar.cardFilter = 'VB-65 additional';
    sidebar.currentDirOnly = true;
    sidebar.statusFilter = 'failed';
    assert.deepEqual(sidebar._getFilteredItems().map(x => x.id), ['match']);
    sidebar.statusFilter = 'live';
    assert.deepEqual(sidebar._getFilteredItems().map(x => x.id), ['live']);
    sidebar._clearFilters();
    assert.equal(sidebar._hasActiveFilters(), false);
    assert.equal(sidebar._getFilteredItems().length, 7);
});

test('search finds card shorthand and metadata even after a chat is renamed', () => {
    const sidebar = historySidebar();
    sidebar.allItems = [session('a', { boardCards: cards, sessionDisplayName: 'Renamed', environmentName: 'Review' })];
    sidebar.filterText = 'VB-64 review project';
    assert.equal(sidebar._getFilteredItems().length, 1);
    sidebar.filterText = 'VB-65';
    assert.equal(sidebar._getFilteredItems().length, 1);
    sidebar.filterText = 'missing';
    assert.equal(sidebar._getFilteredItems().length, 0);
});

test('provider-only and card-only filters scan older pages without a text query', async () => {
    for (const configure of [s => s.llmFilters.add('codex'), s => { s.cardFilter = 'VB-64'; }]) {
        const calls = [];
        const sidebar = historySidebar(async url => {
            const page = Number(new URL(url, 'http://test').searchParams.get('page'));
            calls.push(page);
            return { items: page === 1 ? [session('a', { cli: 'claude' }), session('b', { cli: 'claude' })]
                : [session('older-match', { boardCards: cards })] };
        });
        sidebar.pageSize = 2;
        configure(sidebar);
        await sidebar._loadRemainingPagesForFilters();
        assert.deepEqual(calls, [1, 2]);
        assert.deepEqual(sidebar._getFilteredItems().map(x => x.id), ['older-match']);
        assert.equal(sidebar.hasMore, false);
    }
});

test('clearing filters during a request stops the older-page scan', async () => {
    let complete;
    let calls = 0;
    const sidebar = historySidebar(() => { calls++; return new Promise(resolve => { complete = resolve; }); });
    sidebar.pageSize = 1;
    sidebar.boardFilter = 'linked';
    const loading = sidebar._loadRemainingPagesForFilters();
    sidebar._clearFilters();
    complete({ items: [session('a')] });
    await loading;
    assert.equal(calls, 1);
    assert.equal(sidebar.hasMore, true);
    assert.equal(sidebar.isLoadingForSearch, false);
});

test('late pages cannot overwrite a refreshed or remounted sidebar', async () => {
    const pending = [];
    const sidebar = historySidebar((url, method, data, options) => new Promise(resolve => pending.push({ resolve, signal: options.signal })));
    const oldLoad = sidebar._loadNextPage();
    const freshLoad = sidebar._load();
    assert.equal(pending[0].signal.aborted, true);
    pending[1].resolve({ items: [session('fresh')] });
    await freshLoad;
    pending[0].resolve({ items: [session('stale')] });
    await oldLoad;
    assert.deepEqual(sidebar.allItems.map(x => x.id), ['fresh']);
    assert.equal(sidebar.currentPage, 1);
    assert.equal(sidebar.isLoadingPage, false);
});

test('failed older pages keep matches and report incomplete results', async () => {
    const sidebar = historySidebar(async () => { throw new Error('offline'); });
    sidebar.allItems = [session('kept')];
    sidebar.currentPage = 1;
    sidebar.statusFilter = 'ended';
    await sidebar._loadRemainingPagesForFilters();
    assert.deepEqual(sidebar._getFilteredItems().map(x => x.id), ['kept']);
    assert.equal(sidebar.loadFailed, true);
    assert.match(sidebar._renderFooter(), /incomplete.*data-history-retry/);
    assert.equal(sidebar.currentPage, 1);
});

test('base, custom and unlinked filters handle legacy rows with no metadata', () => {
    const sidebar = historySidebar();
    sidebar.allItems = [session('base'), session('custom', { environmentName: 'old deleted env', boardCards: cards })];
    sidebar.environmentFilter = 'custom';
    assert.deepEqual(sidebar._getFilteredItems().map(x => x.id), ['custom']);
    sidebar.environmentFilter = 'base';
    sidebar.boardFilter = 'unlinked';
    assert.deepEqual(sidebar._getFilteredItems().map(x => x.id), ['base']);
});

test('overlapping direct lookups keep loading feedback until both finish', async () => {
    const pending = [];
    const sidebar = historySidebar(() => new Promise(resolve => pending.push(resolve)));
    sidebar.filterText = 'session';
    const first = sidebar._fetchSessionByIdIntoList('first');
    const second = sidebar._fetchSessionByIdIntoList('second');
    assert.match(sidebar._renderFooter(), /Searching older sessions/);
    pending[1](session('second'));
    await second;
    assert.equal(sidebar._pendingLookups, 1);
    pending[0](session('first'));
    await first;
    assert.equal(sidebar._pendingLookups, 0);
    assert.equal(sidebar._renderFooter(), '');
});

test('a late soft refresh cannot restore stale Board labels after a full refresh', async () => {
    const pending = [];
    const sidebar = historySidebar(() => new Promise(resolve => pending.push(resolve)));
    const background = sidebar._runSoftRefresh();
    const fresh = sidebar._load();
    pending[1]({ items: [session('a', { boardCards: [] })] });
    await fresh;
    pending[0]({ items: [session('a', { boardCards: cards })] });
    await background;
    assert.deepEqual(sidebar.allItems[0].boardCards, []);
    assert.equal(sidebar._softRefreshInFlight, false);
});
