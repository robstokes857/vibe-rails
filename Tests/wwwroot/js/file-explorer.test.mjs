import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/file-explorer.js');
const moduleSource = readFileSync(modulePath, 'utf8');
const stylePath = path.resolve('VibeRails/wwwroot/style.css');
const {
    FILE_EXPLORER_MODES,
    FILE_EXPLORER_SORT_KEYS,
    normalizeFileExplorerMode,
    getFileExplorerInitialPath,
    buildFileExplorerEntriesEndpoint,
    normalizeFileExplorerPayload,
    mergeFileExplorerEntries,
    filterAndSortFileExplorerEntries,
    sortFileExplorerEntries,
    normalizeFileExplorerSort,
    normalizeFileExplorerFilters,
    matchesFileExplorerFilter,
    getFileExplorerTypeLabel,
    findFileExplorerTypeAheadIndex,
    resolveFileExplorerFileNameInput,
    isAbsoluteFileExplorerPath,
    splitFileExplorerPath,
    canSelectFileExplorerKind,
    fileExplorerNameFromPath,
    formatFileExplorerSize,
    formatFileExplorerDate,
    createFileExplorerResult,
    createCanceledFileExplorerResult,
    getFileExplorerLastPathKey,
    readFileExplorerLastPath,
    writeFileExplorerLastPath,
    shouldFallBackToDefaultPath,
    resolveFileExplorerPrimaryAction,
    planFileExplorerMissingName,
    formatFileExplorerStatusText
} = await import(pathToFileURL(modulePath).href);

/** Minimal Storage stand-in; `failing` makes every call throw like a blocked localStorage. */
function fakeStorage(seed = {}, { failing = false } = {}) {
    const store = new Map(Object.entries(seed));
    const guard = () => {
        if (failing) throw new Error('storage disabled');
    };
    return {
        store,
        getItem: key => (guard(), store.has(key) ? store.get(key) : null),
        setItem: (key, value) => (guard(), void store.set(key, String(value))),
        removeItem: key => (guard(), void store.delete(key))
    };
}

test('picker modes normalize to file, directory, or the safe any fallback', () => {
    assert.equal(normalizeFileExplorerMode(' FILE '), FILE_EXPLORER_MODES.FILE);
    assert.equal(normalizeFileExplorerMode('directory'), FILE_EXPLORER_MODES.DIRECTORY);
    assert.equal(normalizeFileExplorerMode('any'), FILE_EXPLORER_MODES.ANY);
    assert.equal(normalizeFileExplorerMode('unknown'), FILE_EXPLORER_MODES.ANY);
});

test('initial path prefers an explicit value, then project root, then launch directory', () => {
    const app = {
        data: {
            configs: {
                rootPath: 'C:\\source\\project',
                launchDirectory: 'C:\\source'
            }
        }
    };

    assert.equal(getFileExplorerInitialPath(app, ' D:\\work '), 'D:\\work');
    assert.equal(getFileExplorerInitialPath(app), 'C:\\source\\project');
    assert.equal(
        getFileExplorerInitialPath({ data: { configs: { launchDirectory: '/srv/app' } } }),
        '/srv/app');
});

test('browse endpoint safely round-trips special, plus, percent, hash, and Unicode path characters', () => {
    const requestedPath = 'C:\\work\\space # + % & 雪';
    const endpoint = buildFileExplorerEntriesEndpoint(requestedPath, true, {
        search: ' report # + 雪 ',
        cursor: 'cursor+/value',
        pageSize: 500
    });
    const url = new URL(endpoint, 'http://localhost');

    assert.equal(url.pathname, '/api/v1/filesystem/entries');
    assert.equal(url.searchParams.get('path'), requestedPath);
    assert.equal(url.searchParams.get('includeHidden'), 'true');
    assert.equal(url.searchParams.get('search'), 'report # + 雪');
    assert.equal(url.searchParams.get('cursor'), 'cursor+/value');
    assert.equal(url.searchParams.get('pageSize'), '500');
    assert.ok(!endpoint.includes('#'));
});

test('browse payload normalizes kinds and optional metadata without inventing a zero-byte size', () => {
    const payload = normalizeFileExplorerPayload({
        defaultPath: '/repo',
        currentPath: '/repo/src',
        currentName: 'src',
        parentPath: '/repo',
        breadcrumbs: [{ label: 'repo', path: '/repo' }, { label: 'src', path: '/repo/src' }],
        roots: [{ label: '/', path: '/' }],
        entries: [
            { name: 'nested', path: '/repo/src/nested', kind: 'folder', size: null },
            { name: 'index.js', path: '/repo/src/index.js', kind: 'file', size: 42 }
        ],
        truncated: true,
        nextCursor: 'next-page',
        totalCount: 12,
        search: 'index'
    });

    assert.equal(payload.entries[0].kind, FILE_EXPLORER_MODES.DIRECTORY);
    assert.equal(payload.entries[0].size, null);
    assert.equal(payload.entries[1].kind, FILE_EXPLORER_MODES.FILE);
    assert.equal(payload.entries[1].size, 42);
    assert.equal(payload.breadcrumbs.at(-1).path, '/repo/src');
    assert.equal(payload.roots[0].path, '/');
    assert.equal(payload.truncated, true);
    assert.equal(payload.nextCursor, 'next-page');
    assert.equal(payload.totalCount, 12);
    assert.equal(payload.search, 'index');
});

test('browse payload carries sidebar places, defaulting to none and dropping duplicates or junk', () => {
    assert.deepEqual(normalizeFileExplorerPayload({ currentPath: '/repo' }).places, []);
    assert.deepEqual(normalizeFileExplorerPayload({ currentPath: '/repo', places: 'nope' }).places, []);

    const payload = normalizeFileExplorerPayload({
        currentPath: 'C:\\repo',
        places: [
            { label: 'Home', path: 'C:\\Users\\rob', kind: 'home' },
            { label: 'Desktop', path: 'C:\\Users\\rob\\Desktop', kind: 'DESKTOP' },
            { label: 'Again', path: 'c:/users/rob/', kind: 'documents' },
            { label: 'Weird', path: 'C:\\Users\\rob\\Downloads', kind: 'something-else' },
            { label: 'Empty', path: '', kind: 'downloads' }
        ]
    });

    assert.deepEqual(payload.places, [
        { label: 'Home', path: 'C:\\Users\\rob', kind: 'home' },
        { label: 'Desktop', path: 'C:\\Users\\rob\\Desktop', kind: 'desktop' },
        { label: 'Weird', path: 'C:\\Users\\rob\\Downloads', kind: 'home' }
    ]);
});

test('page merging de-duplicates paths and keeps a stable directory-first display order', () => {
    const merged = mergeFileExplorerEntries(
        [
            { name: 'file10.txt', path: '/repo/file10.txt', kind: 'file' },
            { name: 'src', path: '/repo/src', kind: 'directory' }
        ],
        [
            { name: 'file10.txt', path: '/repo/file10.txt', kind: 'file' },
            { name: 'file2.txt', path: '/repo/file2.txt', kind: 'file' }
        ]
    );

    assert.deepEqual(merged.map(entry => entry.name), ['src', 'file2.txt', 'file10.txt']);
});

test('entry filtering is case-insensitive and keeps directories before naturally sorted files', () => {
    const entries = [
        { name: 'file10.txt', kind: 'file' },
        { name: 'Zoo', kind: 'directory' },
        { name: 'file2.txt', kind: 'file' },
        { name: 'alpha', kind: 'directory' }
    ];

    assert.deepEqual(
        filterAndSortFileExplorerEntries(entries).map(entry => entry.name),
        ['alpha', 'Zoo', 'file2.txt', 'file10.txt']);
    assert.deepEqual(
        filterAndSortFileExplorerEntries(entries, 'FILE').map(entry => entry.name),
        ['file2.txt', 'file10.txt']);
});

const sortable = [
    { name: 'b.txt', kind: 'file', size: 300, lastModifiedUtc: '2026-01-02T00:00:00Z' },
    { name: 'zeta', kind: 'directory', lastModifiedUtc: '2026-01-05T00:00:00Z' },
    { name: 'a.py', kind: 'file', size: 10, lastModifiedUtc: '2026-01-03T00:00:00Z' },
    { name: 'alpha', kind: 'directory', lastModifiedUtc: '2026-01-01T00:00:00Z' },
    { name: 'c.md', kind: 'file', size: null, lastModifiedUtc: null }
];

test('sorting by name honours direction but folders always stay ahead of files', () => {
    assert.deepEqual(
        sortFileExplorerEntries(sortable, { key: 'name', direction: 'asc' }).map(entry => entry.name),
        ['alpha', 'zeta', 'a.py', 'b.txt', 'c.md']);
    assert.deepEqual(
        sortFileExplorerEntries(sortable, { key: 'name', direction: 'desc' }).map(entry => entry.name),
        ['zeta', 'alpha', 'c.md', 'b.txt', 'a.py']);
});

test('sorting by date, type, and size keeps folders first and breaks ties by name', () => {
    assert.deepEqual(
        sortFileExplorerEntries(sortable, { key: FILE_EXPLORER_SORT_KEYS.MODIFIED, direction: 'asc' })
            .map(entry => entry.name),
        ['alpha', 'zeta', 'c.md', 'b.txt', 'a.py']);
    assert.deepEqual(
        sortFileExplorerEntries(sortable, { key: FILE_EXPLORER_SORT_KEYS.MODIFIED, direction: 'desc' })
            .map(entry => entry.name),
        ['zeta', 'alpha', 'a.py', 'b.txt', 'c.md']);
    assert.deepEqual(
        sortFileExplorerEntries(sortable, { key: FILE_EXPLORER_SORT_KEYS.SIZE, direction: 'desc' })
            .map(entry => entry.name),
        ['alpha', 'zeta', 'b.txt', 'a.py', 'c.md']);
    assert.deepEqual(
        sortFileExplorerEntries(sortable, { key: FILE_EXPLORER_SORT_KEYS.TYPE, direction: 'asc' })
            .map(entry => entry.name),
        ['alpha', 'zeta', 'c.md', 'a.py', 'b.txt']);
    assert.deepEqual(normalizeFileExplorerSort({ key: 'bogus', direction: 'DESC' }), { key: 'name', direction: 'desc' });
    assert.deepEqual(normalizeFileExplorerSort(undefined), { key: 'name', direction: 'asc' });
});

test('type labels read like a details view: File folder, known type names, or EXT File', () => {
    assert.equal(getFileExplorerTypeLabel({ name: 'src', kind: 'directory' }), 'File folder');
    assert.equal(getFileExplorerTypeLabel({ name: 'app.py', kind: 'file' }), 'Python');
    assert.equal(getFileExplorerTypeLabel({ name: 'notes.xyz', kind: 'file' }), 'XYZ File');
    assert.equal(getFileExplorerTypeLabel({ name: 'LICENSE', kind: 'file' }), 'File');
});

test('file-type filters normalize extensions and match case-insensitively; folders always pass', () => {
    assert.deepEqual(normalizeFileExplorerFilters(undefined), []);
    assert.deepEqual(normalizeFileExplorerFilters('py'), []);
    // The select shows the pattern like a desktop dialog; the short label is kept for the
    // status line, and a caller that already wrote the pattern is not doubled up.
    assert.deepEqual(
        normalizeFileExplorerFilters([
            { label: 'Python files', extensions: ['py', '.PYW', 'py'] },
            { label: 'All files', extensions: [] },
            { extensions: ['md'] },
            {},
            { label: 'Text (*.txt)', extensions: ['txt'] },
            null
        ]),
        [
            { label: 'Python files (*.py, *.pyw)', shortLabel: 'Python files', extensions: ['py', 'pyw'] },
            { label: 'All files (*.*)', shortLabel: 'All files', extensions: [] },
            { label: '*.md', shortLabel: '*.md', extensions: ['md'] },
            { label: 'All files (*.*)', shortLabel: 'All files', extensions: [] },
            { label: 'Text (*.txt)', shortLabel: 'Text (*.txt)', extensions: ['txt'] }
        ]);

    const python = { label: 'Python files', extensions: ['py'] };
    assert.equal(matchesFileExplorerFilter({ name: 'app.PY', kind: 'file' }, python), true);
    assert.equal(matchesFileExplorerFilter({ name: 'app.py', kind: 'file', extension: '.py' }, python), true);
    assert.equal(matchesFileExplorerFilter({ name: 'readme.md', kind: 'file' }, python), false);
    assert.equal(matchesFileExplorerFilter({ name: 'py', kind: 'file' }, python), false);
    assert.equal(matchesFileExplorerFilter({ name: 'lib', kind: 'directory' }, python), true);
    assert.equal(matchesFileExplorerFilter({ name: 'readme.md', kind: 'file' }, { extensions: [] }), true);
    assert.equal(matchesFileExplorerFilter({ name: 'readme.md', kind: 'file' }, null), true);
    assert.deepEqual(
        filterAndSortFileExplorerEntries(
            [
                { name: 'readme.md', kind: 'file' },
                { name: 'tools', kind: 'directory' },
                { name: 'run.py', kind: 'file' }
            ],
            '',
            { filter: python }
        ).map(entry => entry.name),
        ['tools', 'run.py']);
});

test('type-ahead jumps to the first name starting with the buffer and cycles on a repeated key', () => {
    const entries = [
        { name: 'alpha' },
        { name: 'apple' },
        { name: 'Beta' },
        { name: 'bravo' },
        { name: 'gamma' }
    ];

    assert.equal(findFileExplorerTypeAheadIndex(entries, 'b'), 2);
    assert.equal(findFileExplorerTypeAheadIndex(entries, 'B', 2), 3);
    assert.equal(findFileExplorerTypeAheadIndex(entries, 'bb', 3), 2);
    assert.equal(findFileExplorerTypeAheadIndex(entries, 'ap'), 1);
    assert.equal(findFileExplorerTypeAheadIndex(entries, 'gam', 4), 4);
    assert.equal(findFileExplorerTypeAheadIndex(entries, 'zzz'), -1);
    assert.equal(findFileExplorerTypeAheadIndex(entries, ''), -1);
    assert.equal(findFileExplorerTypeAheadIndex([], 'a'), -1);
});

test('the file-name box resolves names in the folder, absolute paths, and unknown names', () => {
    const entries = [
        { name: 'App.py', path: 'C:\\repo\\App.py', kind: 'file' },
        { name: 'src', path: 'C:\\repo\\src', kind: 'directory' }
    ];

    assert.deepEqual(resolveFileExplorerFileNameInput('   ', entries, 'C:\\repo'), { kind: 'empty' });
    assert.deepEqual(resolveFileExplorerFileNameInput('App.py', entries, 'C:\\repo'), { kind: 'entry', entry: entries[0] });
    assert.deepEqual(resolveFileExplorerFileNameInput('app.PY', entries, 'C:\\repo'), { kind: 'entry', entry: entries[0] });
    assert.deepEqual(resolveFileExplorerFileNameInput('missing.py', entries, 'C:\\repo'), { kind: 'missing', name: 'missing.py' });
    assert.deepEqual(
        resolveFileExplorerFileNameInput('D:\\other\\tool.py', entries, 'C:\\repo'),
        { kind: 'path', directory: 'D:\\other', name: 'tool.py' });
    assert.deepEqual(
        resolveFileExplorerFileNameInput('c:/repo/app.py', entries, 'C:\\repo'),
        { kind: 'entry', entry: entries[0] });
    assert.deepEqual(
        resolveFileExplorerFileNameInput('/srv/data/', [], '/home/rob'),
        { kind: 'path', directory: '/srv/data', name: '' });
    assert.deepEqual(
        resolveFileExplorerFileNameInput('/etc/hosts', [], '/home/rob'),
        { kind: 'path', directory: '/etc', name: 'hosts' });
    assert.deepEqual(
        resolveFileExplorerFileNameInput('src/main.py', entries, 'C:\\repo'),
        { kind: 'path', directory: 'C:\\repo\\src', name: 'main.py' });
});

test('absolute path detection and splitting handle drive roots and the Unix root', () => {
    assert.equal(isAbsoluteFileExplorerPath('C:\\repo'), true);
    assert.equal(isAbsoluteFileExplorerPath('/repo'), true);
    assert.equal(isAbsoluteFileExplorerPath('repo/file.txt'), false);
    assert.deepEqual(splitFileExplorerPath('C:\\'), { directory: 'C:\\', name: '' });
    assert.deepEqual(splitFileExplorerPath('C:\\file.txt'), { directory: 'C:\\', name: 'file.txt' });
    assert.deepEqual(splitFileExplorerPath('/'), { directory: '/', name: '' });
    assert.deepEqual(splitFileExplorerPath('/file.txt'), { directory: '/', name: 'file.txt' });
    assert.deepEqual(splitFileExplorerPath('/a/b/c'), { directory: '/a/b', name: 'c' });
});

test('selection rules keep navigation available while enforcing the caller mode', () => {
    assert.equal(canSelectFileExplorerKind('file', 'file'), true);
    assert.equal(canSelectFileExplorerKind('file', 'directory'), false);
    assert.equal(canSelectFileExplorerKind('directory', 'folder'), true);
    assert.equal(canSelectFileExplorerKind('directory', 'file'), false);
    assert.equal(canSelectFileExplorerKind('any', 'file'), true);
    assert.equal(canSelectFileExplorerKind('any', 'directory'), true);
});

test('display helpers handle roots, unavailable sizes, readable byte units, and compact dates', () => {
    assert.equal(fileExplorerNameFromPath('C:\\'), 'C:');
    assert.equal(fileExplorerNameFromPath('/'), '/');
    assert.equal(fileExplorerNameFromPath('/repo/src/'), 'src');
    assert.equal(formatFileExplorerSize(null), '—');
    assert.equal(formatFileExplorerSize(0), '0 B');
    assert.equal(formatFileExplorerSize(1536), '1.50 KB');
    assert.equal(formatFileExplorerDate(null), '—');
    assert.equal(formatFileExplorerDate('not a date'), '—');
    const formatted = formatFileExplorerDate('2026-08-19T15:04:00Z');
    assert.match(formatted, /2026/);
    assert.ok(!/Aug/.test(formatted), 'the date column uses the compact numeric form');
    assert.ok(!formatted.includes(','), `date and time are joined by a space, not the locale comma: ${formatted}`);
    assert.match(formatted, /\d\s\d{1,2}:\d{2}/, 'the time still follows the date');
});

test('selected and canceled outcomes have a stable caller-facing shape', () => {
    assert.deepEqual(createFileExplorerResult({
        name: 'src',
        path: '/repo/src',
        kind: 'directory'
    }), {
        canceled: false,
        path: '/repo/src',
        kind: 'directory',
        name: 'src'
    });

    assert.deepEqual(createCanceledFileExplorerResult(), {
        canceled: true,
        path: null,
        kind: null,
        name: null
    });
});

test('the last accepted folder is remembered per picker mode and read back defensively', () => {
    assert.equal(getFileExplorerLastPathKey('file'), 'viberails.fileExplorer.lastPath:file');
    assert.equal(getFileExplorerLastPathKey('DIRECTORY'), 'viberails.fileExplorer.lastPath:directory');
    assert.equal(getFileExplorerLastPathKey('nonsense'), 'viberails.fileExplorer.lastPath:any');

    const storage = fakeStorage({ 'viberails.fileExplorer.lastPath:file': '  C:\\repo\\src  ' });
    assert.equal(readFileExplorerLastPath('file', storage), 'C:\\repo\\src');
    assert.equal(readFileExplorerLastPath('directory', storage), '', 'modes do not share a memory');
    assert.equal(writeFileExplorerLastPath('directory', 'D:\\data', storage), true);
    assert.equal(storage.store.get('viberails.fileExplorer.lastPath:directory'), 'D:\\data');
    assert.equal(writeFileExplorerLastPath('directory', '   ', storage), true, 'a blank path clears the memory');
    assert.equal(storage.store.has('viberails.fileExplorer.lastPath:directory'), false);

    // Blocked or absent storage must never throw into the dialog.
    const blocked = fakeStorage({}, { failing: true });
    assert.equal(readFileExplorerLastPath('file', blocked), '');
    assert.equal(writeFileExplorerLastPath('file', 'C:\\x', blocked), false);
    assert.equal(readFileExplorerLastPath('file', null), '');
    assert.equal(writeFileExplorerLastPath('file', 'C:\\x', null), false);
});

test('a failed first load of a remembered folder falls back to the project root, later failures do not', () => {
    const fallbackPath = 'C:\\source\\project';
    assert.equal(shouldFallBackToDefaultPath({ historyIndex: -1, requestedPath: 'E:\\gone', fallbackPath }), true);
    assert.equal(shouldFallBackToDefaultPath({ historyIndex: 0, requestedPath: 'E:\\gone', fallbackPath }), false,
        'once a folder has loaded, errors are shown as errors');
    assert.equal(shouldFallBackToDefaultPath({ historyIndex: -1, requestedPath: 'c:/source/project/', fallbackPath }), false,
        'the fallback itself failing is a real error');
    assert.equal(shouldFallBackToDefaultPath({ historyIndex: -1, requestedPath: 'E:\\gone', fallbackPath: '' }), false,
        'callers that pass their own initialPath get no silent fallback');
});

test('the primary button acts on the highlighted row, but a muted file in a folder picker still selects the folder', () => {
    const file = { name: 'app.py', kind: 'file', path: '/repo/app.py' };
    const folder = { name: 'src', kind: 'directory', path: '/repo/src' };
    const link = { name: 'ln', kind: 'file', path: '/repo/ln', isSymbolicLink: true };

    assert.equal(resolveFileExplorerPrimaryAction('directory', file), 'accept-current');
    assert.equal(resolveFileExplorerPrimaryAction('directory', folder), 'accept-entry');
    assert.equal(resolveFileExplorerPrimaryAction('directory', null), 'accept-current');
    assert.equal(resolveFileExplorerPrimaryAction('directory', link), 'accept-current');
    assert.equal(resolveFileExplorerPrimaryAction('file', file), 'accept-entry');
    assert.equal(resolveFileExplorerPrimaryAction('file', folder), 'navigate');
    assert.equal(resolveFileExplorerPrimaryAction('file', null), 'none');
    assert.equal(resolveFileExplorerPrimaryAction('file', link), 'none');
    assert.equal(resolveFileExplorerPrimaryAction('any', file), 'accept-entry');
    assert.equal(resolveFileExplorerPrimaryAction('any', folder), 'accept-entry');
    assert.equal(resolveFileExplorerPrimaryAction('any', null), 'accept-current');
});

test('a typed name that is not loaded is searched for before it is called missing', () => {
    // Whole folder loaded: the answer is known.
    assert.deepEqual(planFileExplorerMissingName('report.py', { nextCursor: null, search: '' }, 'file'),
        { action: 'hint', message: 'No such file in this folder' });
    assert.deepEqual(planFileExplorerMissingName('build', {}, 'directory'),
        { action: 'hint', message: 'No such folder here' });
    // More pages, or a narrower server search: ask the server for the name first.
    assert.deepEqual(planFileExplorerMissingName('report.py', { nextCursor: 'page-2' }, 'file'), { action: 'search' });
    assert.deepEqual(planFileExplorerMissingName('report.py', { search: 'rep' }, 'file'), { action: 'search' });
    // The server already searched for exactly this name (case-insensitively): now it is missing…
    assert.deepEqual(planFileExplorerMissingName('Report.py', { search: 'report.py' }, 'file'),
        { action: 'hint', message: 'No such file in this folder' });
    // …unless even those matches have further pages, in which case say what was checked.
    assert.deepEqual(planFileExplorerMissingName('report.py', { search: 'report.py', nextCursor: 'page-2' }, 'file'),
        { action: 'hint', message: 'Not found in the loaded items — load more or search' });
});

test('the status line names the type filter that hides part of the folder', () => {
    assert.equal(formatFileExplorerStatusText({ visible: 23, loaded: 35, filterLabel: 'Python files' }),
        '23 of 35 items (Python files)');
    assert.equal(formatFileExplorerStatusText({ visible: 23, loaded: 35 }), '23 of 35 items shown');
    assert.equal(formatFileExplorerStatusText({ visible: 3, loaded: 5, total: 900, filterLabel: 'Python files', search: 'x' }),
        '3 of 900 matches (Python files)');
    assert.equal(formatFileExplorerStatusText({ visible: 500, loaded: 500, total: 1200 }), '500 of 1,200 items');
    assert.equal(formatFileExplorerStatusText({ visible: 500, loaded: 500, truncated: true }), '500+ items');
    assert.equal(formatFileExplorerStatusText({ visible: 1, loaded: 1 }), '1 item');
    assert.equal(formatFileExplorerStatusText({ visible: 4, loaded: 4, search: 'a' }), '4 matches');
    assert.equal(formatFileExplorerStatusText({ visible: 4, loaded: 9, searchPending: true }), '4 items · searching…');
});

test('the dialog dismisses on Escape, Cancel, and X only — never on a backdrop click', () => {
    // A text-selection drag that ends outside the dialog used to cancel it; desktop file
    // dialogs stay put, so no click handler may sit on the modal shell or the backdrop.
    assert.ok(!/elements\.modal\.addEventListener\('click'/.test(moduleSource));
    assert.ok(!/vb-file-explorer-backdrop"\s*data-file-explorer-action="cancel"/.test(moduleSource));
    assert.match(moduleSource, /class="btn-close" data-file-explorer-action="cancel"/);
    assert.match(moduleSource, /data-file-explorer-action="cancel">Cancel<\/button>/);
});

test('the file-explorer stylesheet block is dense, flat, and every colour has a fallback', () => {
    const css = readFileSync(stylePath, 'utf8');
    const start = css.indexOf('Server-backed File Explorer');
    const end = css.indexOf('Nav Automation launcher', start);
    assert.ok(start > 0 && end > start, 'expected the file explorer block to sit before the nav launcher block');
    const block = css.slice(start, end);

    for (const match of block.matchAll(/var\((--color-[a-z-]+)([^)]*)\)/g)) {
        assert.ok(match[2].includes(','), `${match[1]} lacks a fallback: ${match[0]}`);
    }
    assert.ok(!block.includes('radial-gradient'), 'flat surfaces only');
    assert.match(block, /--vb-fx-row-height: 28px;/);

    // Bootstrap's .modal-dialog-centered min-height would beat a height on .modal-dialog and
    // stretch the dialog to the viewport, so the 640px cap must sit on the content box.
    const rule = selector => {
        const index = block.indexOf(`${selector} {`);
        assert.ok(index >= 0, `missing rule ${selector}`);
        return block.slice(index, block.indexOf('}', index));
    };
    const shell = rule('.vb-file-explorer-modal .modal-dialog');
    assert.match(shell, /width: min\(980px, calc\(100vw - 2rem\)\);/);
    assert.ok(!/\bheight:/.test(shell), '.modal-dialog must not set a height');
    const dialog = rule('.vb-file-explorer-modal .vb-file-explorer-dialog');
    assert.match(dialog, /height: min\(640px, calc\(100dvh - 2rem\)\);/);
    assert.match(dialog, /min-height: 0;/);
    const narrow = block.slice(block.indexOf('@media (max-width: 860px)'));
    assert.match(narrow, /\.vb-file-explorer-modal \.vb-file-explorer-dialog \{ height: calc\(100dvh - 1rem\); \}/);
    assert.ok(!/\.modal-dialog \{[^}]*height:/.test(narrow), 'the narrow layout also sizes the content box');

    for (const dropped of ['workspace-head', 'vb-file-explorer-breadcrumbs', 'select-current', 'heading-icon', 'address-form']) {
        assert.ok(!block.includes(dropped), `${dropped} styles should have been removed with their markup`);
    }
    // Every class the stylesheet targets is still produced by the module.
    const classes = new Set(Array.from(block.matchAll(/\.(vb-file-explorer-[a-z-]+)/g), match => match[1]));
    for (const className of classes) {
        assert.ok(moduleSource.includes(className), `${className} is styled but no longer rendered`);
    }
});

