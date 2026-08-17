import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/file-explorer.js');
const moduleSource = readFileSync(modulePath, 'utf8');
const {
    FILE_EXPLORER_MODES,
    normalizeFileExplorerMode,
    getFileExplorerInitialPath,
    buildFileExplorerEntriesEndpoint,
    normalizeFileExplorerPayload,
    mergeFileExplorerEntries,
    filterAndSortFileExplorerEntries,
    canSelectFileExplorerKind,
    fileExplorerNameFromPath,
    formatFileExplorerSize,
    createFileExplorerResult,
    createCanceledFileExplorerResult
} = await import(pathToFileURL(modulePath).href);

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

test('selection rules keep navigation available while enforcing the caller mode', () => {
    assert.equal(canSelectFileExplorerKind('file', 'file'), true);
    assert.equal(canSelectFileExplorerKind('file', 'directory'), false);
    assert.equal(canSelectFileExplorerKind('directory', 'folder'), true);
    assert.equal(canSelectFileExplorerKind('directory', 'file'), false);
    assert.equal(canSelectFileExplorerKind('any', 'file'), true);
    assert.equal(canSelectFileExplorerKind('any', 'directory'), true);
});

test('display helpers handle roots, unavailable sizes, and readable byte units', () => {
    assert.equal(fileExplorerNameFromPath('C:\\'), 'C:');
    assert.equal(fileExplorerNameFromPath('/'), '/');
    assert.equal(fileExplorerNameFromPath('/repo/src/'), 'src');
    assert.equal(formatFileExplorerSize(null), '—');
    assert.equal(formatFileExplorerSize(0), '0 B');
    assert.equal(formatFileExplorerSize(1536), '1.50 KB');
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

test('nested modal lifecycle owns Escape early and contains focus outside modal-container', () => {
    const earlyEscape = moduleSource.indexOf("window.addEventListener('keydown'");
    const openFunction = moduleSource.indexOf('export function openFileExplorer');

    assert.ok(earlyEscape >= 0 && earlyEscape < openFunction);
    assert.match(moduleSource, /activeExplorer\.cancel\(\)/);
    assert.match(moduleSource, /collectExplorerBackground\(host\)/);
    assert.match(moduleSource, /!layer\.contains\(document\.activeElement\)/);
});

test('navigation retries preserve history intent and search input matches the API limit', () => {
    assert.match(moduleSource, /state\.retryNavigation = \{/);
    assert.match(moduleSource, /historyMode: retry\?\.historyMode \|\| 'none'/);
    assert.match(moduleSource, /targetHistoryIndex: retry\?\.targetHistoryIndex \?\? null/);
    assert.match(moduleSource, /type="search"[^>]*maxlength="256"/);
});
