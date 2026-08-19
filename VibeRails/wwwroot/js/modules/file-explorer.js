import { getFileTypeVisual } from './file-type-icons.js';
import { isConfirmDialogOpen } from './utils.js';

export const FILE_EXPLORER_MODES = Object.freeze({
    FILE: 'file',
    DIRECTORY: 'directory',
    ANY: 'any'
});

export const FILE_EXPLORER_SORT_KEYS = Object.freeze({
    NAME: 'name',
    MODIFIED: 'modified',
    TYPE: 'type',
    SIZE: 'size'
});

const ENTRIES_ENDPOINT = '/api/v1/filesystem/entries';
const LAST_PATH_STORAGE_PREFIX = 'viberails.fileExplorer.lastPath:';
const FILE_EXPLORER_PAGE_SIZE = 500;
const SEARCH_DEBOUNCE_MS = 250;
const TYPE_AHEAD_RESET_MS = 700;
const DEFAULT_SORT = Object.freeze({ key: FILE_EXPLORER_SORT_KEYS.NAME, direction: 'asc' });
const CANCELED_RESULT = Object.freeze({
    canceled: true,
    path: null,
    kind: null,
    name: null
});
const PLACE_ICONS = Object.freeze({
    project: 'fa-solid fa-folder-tree',
    home: 'fa-solid fa-house',
    desktop: 'fa-solid fa-desktop',
    documents: 'fa-solid fa-file-lines',
    downloads: 'fa-solid fa-download',
    drive: 'fa-solid fa-hard-drive'
});

let activeExplorer = null;

// Register before view-specific modal listeners. Some existing forms listen for Escape on
// window/capture, which fires before document/capture; this early guard gives the top-most file
// explorer first refusal without requiring every caller to understand its nested lifecycle.
// The explorer may consume Escape itself (revert address editing, clear the search box) before
// it falls through to cancelling the dialog.
if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
    window.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || !activeExplorer || isConfirmDialogOpen()) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        if (activeExplorer.handleEscape?.(event)) return;
        activeExplorer.cancel();
    }, true);
}

export function normalizeFileExplorerMode(mode) {
    const normalized = String(mode || '').trim().toLowerCase();
    return Object.values(FILE_EXPLORER_MODES).includes(normalized)
        ? normalized
        : FILE_EXPLORER_MODES.ANY;
}

export function getFileExplorerInitialPath(app, initialPath) {
    if (typeof initialPath === 'string' && initialPath.trim()) {
        return initialPath.trim();
    }

    return stringValue(
        app?.data?.configs?.rootPath
        || app?.data?.configs?.launchDirectory
        || ''
    );
}

/** localStorage key under which the folder last accepted from a picker of this mode is kept. */
export function getFileExplorerLastPathKey(mode) {
    return `${LAST_PATH_STORAGE_PREFIX}${normalizeFileExplorerMode(mode)}`;
}

/**
 * The folder the previous dialog of this mode was accepted from, or '' when nothing was stored
 * or storage is unavailable (private mode, quota, disabled). Never throws.
 */
export function readFileExplorerLastPath(mode, storage = defaultStorage()) {
    try {
        return cleanString(storage?.getItem?.(getFileExplorerLastPathKey(mode)));
    } catch {
        return '';
    }
}

/** Remembers `path` for the next dialog of this mode; a blank path clears the memory. */
export function writeFileExplorerLastPath(mode, path, storage = defaultStorage()) {
    const key = getFileExplorerLastPathKey(mode);
    const value = cleanString(path);
    if (!storage) return false;
    try {
        if (value) storage.setItem(key, value);
        else storage.removeItem(key);
        return true;
    } catch {
        return false;
    }
}

/**
 * Whether a failed load should be replaced by a quiet retry at `fallbackPath` instead of an
 * error: only the very first load (nothing in history yet) of a path that is not the fallback
 * itself qualifies — a remembered folder that has since been deleted, unplugged, or belongs to
 * another machine should not greet the user with an error.
 */
export function shouldFallBackToDefaultPath({ historyIndex, requestedPath, fallbackPath }) {
    return Number(historyIndex) < 0
        && Boolean(cleanString(fallbackPath))
        && !samePath(requestedPath, fallbackPath);
}

export function buildFileExplorerEntriesEndpoint(path = '', includeHidden = false, options = {}) {
    const query = new URLSearchParams();
    const requestedPath = stringValue(path);
    if (requestedPath.trim()) query.set('path', requestedPath);
    query.set('includeHidden', includeHidden ? 'true' : 'false');
    const search = cleanString(options?.search);
    const cursor = cleanString(options?.cursor);
    const pageSize = Number(options?.pageSize);
    if (search) query.set('search', search);
    if (cursor) query.set('cursor', cursor);
    if (Number.isInteger(pageSize) && pageSize > 0) query.set('pageSize', String(pageSize));
    return `${ENTRIES_ENDPOINT}?${query.toString()}`;
}

export function normalizeFileExplorerKind(kind) {
    const normalized = String(kind || '').trim().toLowerCase();
    return normalized === 'directory' || normalized === 'folder' || normalized === 'root'
        ? FILE_EXPLORER_MODES.DIRECTORY
        : FILE_EXPLORER_MODES.FILE;
}

export function normalizeFileExplorerPayload(payload = {}, requestedPath = '') {
    const currentPath = stringValue(payload?.currentPath) || stringValue(requestedPath);
    const entries = Array.isArray(payload?.entries)
        ? payload.entries
            .map(normalizeEntry)
            .filter(entry => entry.path && entry.name)
        : [];

    return {
        defaultPath: stringValue(payload?.defaultPath) || currentPath,
        currentPath,
        currentName: stringValue(payload?.currentName) || fileExplorerNameFromPath(currentPath),
        parentPath: stringValue(payload?.parentPath) || null,
        breadcrumbs: normalizeLocations(payload?.breadcrumbs),
        roots: normalizeLocations(payload?.roots),
        places: normalizePlaces(payload?.places),
        entries,
        truncated: Boolean(payload?.truncated),
        nextCursor: cleanString(payload?.nextCursor) || null,
        totalCount: normalizeTotalCount(payload?.totalCount),
        search: cleanString(payload?.search)
    };
}

export function mergeFileExplorerEntries(existing = [], incoming = []) {
    const entries = [];
    const seen = new Set();
    for (const entry of [...existing, ...incoming]) {
        const key = fileExplorerPathKey(entry?.path);
        if (!key || seen.has(key)) continue;
        seen.add(key);
        entries.push(entry);
    }
    return filterAndSortFileExplorerEntries(entries);
}

/**
 * Applies the local name filter, the optional file-type filter, and the column sort.
 *
 * @param {Array<object>} entries
 * @param {string} [searchText] Case-insensitive substring match on the entry name.
 * @param {{sort?: {key: string, direction: string}, filter?: {extensions?: string[]}|null}} [options]
 */
export function filterAndSortFileExplorerEntries(entries = [], searchText = '', options = {}) {
    const query = String(searchText || '').trim().toLocaleLowerCase();
    const filter = options?.filter ?? null;
    const filtered = (Array.isArray(entries) ? entries : []).filter(entry => {
        if (query && !String(entry?.name || '').toLocaleLowerCase().includes(query)) return false;
        return matchesFileExplorerFilter(entry, filter);
    });
    return sortFileExplorerEntries(filtered, options?.sort);
}

/**
 * Sorts entries the way a details view does: folders always come before files, then the active
 * column decides the order inside each group, and the name breaks ties.
 */
export function sortFileExplorerEntries(entries = [], sort = DEFAULT_SORT) {
    const { key, direction } = normalizeFileExplorerSort(sort);
    const sign = direction === 'desc' ? -1 : 1;
    return (Array.isArray(entries) ? entries : []).slice().sort((left, right) => {
        const leftDirectory = normalizeFileExplorerKind(left?.kind) === FILE_EXPLORER_MODES.DIRECTORY;
        const rightDirectory = normalizeFileExplorerKind(right?.kind) === FILE_EXPLORER_MODES.DIRECTORY;
        if (leftDirectory !== rightDirectory) return leftDirectory ? -1 : 1;

        const byName = compareEntryNames(left, right);
        if (key === FILE_EXPLORER_SORT_KEYS.NAME) return sign * byName;

        let comparison = 0;
        if (key === FILE_EXPLORER_SORT_KEYS.MODIFIED) {
            comparison = compareNumbers(entryTimestamp(left), entryTimestamp(right));
        } else if (key === FILE_EXPLORER_SORT_KEYS.TYPE) {
            comparison = compareText(getFileExplorerTypeLabel(left), getFileExplorerTypeLabel(right));
        } else if (key === FILE_EXPLORER_SORT_KEYS.SIZE) {
            comparison = compareNumbers(entrySize(left), entrySize(right));
        }
        return comparison !== 0 ? sign * comparison : byName;
    });
}

export function normalizeFileExplorerSort(sort) {
    const key = Object.values(FILE_EXPLORER_SORT_KEYS).includes(sort?.key)
        ? sort.key
        : DEFAULT_SORT.key;
    const direction = String(sort?.direction || '').toLowerCase() === 'desc' ? 'desc' : 'asc';
    return { key, direction };
}

/**
 * Normalizes the caller's `filters` option. Extensions are lower-cased and stored without a dot;
 * an empty extension list means "all files". Returns [] when the option is absent or malformed.
 *
 * `label` is what the "Files of type" select shows and, like a desktop dialog, carries the
 * pattern: "Python files (*.py)", "All files (*.*)". `shortLabel` is the caller's wording alone
 * for places that already say what is being counted, such as the status line.
 */
export function normalizeFileExplorerFilters(filters) {
    if (!Array.isArray(filters)) return [];
    return filters
        .filter(filter => filter && typeof filter === 'object')
        .map(filter => {
            const extensions = Array.isArray(filter.extensions)
                ? filter.extensions
                    .map(normalizeExtension)
                    .filter(Boolean)
                : [];
            const unique = Array.from(new Set(extensions));
            const pattern = unique.length ? unique.map(extension => `*.${extension}`).join(', ') : '*.*';
            const shortLabel = cleanString(filter.label) || (unique.length ? pattern : 'All files');
            // A caller that already spelled out the pattern is left alone rather than doubled up.
            const label = /\(\*\.[^)]*\)$/.test(shortLabel) || shortLabel === pattern
                ? shortLabel
                : `${shortLabel} (${pattern})`;
            return { label, shortLabel, extensions: unique };
        });
}

/**
 * Decides what the primary button (or Enter) does once the file-name box has had its say.
 * Folder pickers list files muted for orientation; a muted highlight must not turn "Select
 * Folder" into a dead button, so it falls through to the folder being viewed, as if nothing
 * were highlighted.
 *
 * @returns {'navigate'|'accept-entry'|'accept-current'|'none'}
 */
export function resolveFileExplorerPrimaryAction(mode, selected) {
    const normalizedMode = normalizeFileExplorerMode(mode);
    if (selected && !selected.isSymbolicLink) {
        const kind = normalizeFileExplorerKind(selected.kind);
        if (kind === FILE_EXPLORER_MODES.DIRECTORY) {
            return normalizedMode === FILE_EXPLORER_MODES.FILE ? 'navigate' : 'accept-entry';
        }
        if (canSelectFileExplorerKind(normalizedMode, kind)) return 'accept-entry';
    }
    return canSelectFileExplorerKind(normalizedMode, FILE_EXPLORER_MODES.DIRECTORY)
        ? 'accept-current'
        : 'none';
}

/**
 * What to do about a typed name that is not among the loaded entries. "Not here" is only
 * proven when the whole folder is loaded; behind a further page, or while a server search
 * narrows the list, the honest move is to ask the server for that name first.
 *
 * @param {string} name The typed name.
 * @param {{nextCursor?: string|null, search?: string}} data The current folder payload.
 * @param {string} mode Picker mode (chooses the file/folder wording).
 * @returns {{action:'search'} | {action:'hint', message: string}}
 */
export function planFileExplorerMissingName(name, data = {}, mode = FILE_EXPLORER_MODES.ANY) {
    const wanted = cleanString(name);
    const activeSearch = cleanString(data?.search);
    const searchedForName = Boolean(activeSearch)
        && activeSearch.toLocaleLowerCase() === wanted.toLocaleLowerCase();
    if (!searchedForName && (data?.nextCursor || activeSearch)) return { action: 'search' };
    if (data?.nextCursor) {
        // Even the search for this exact name has more pages: say what was actually checked.
        return { action: 'hint', message: 'Not found in the loaded items — load more or search' };
    }
    return {
        action: 'hint',
        message: normalizeFileExplorerMode(mode) === FILE_EXPLORER_MODES.DIRECTORY
            ? 'No such folder here'
            : 'No such file in this folder'
    };
}

/**
 * Directories always pass; files pass when the filter has no extensions or lists theirs.
 * Matching is case-insensitive and tolerant of a leading dot in either place.
 */
export function matchesFileExplorerFilter(entry, filter) {
    if (!filter || typeof filter !== 'object') return true;
    const extensions = Array.isArray(filter.extensions)
        ? filter.extensions.map(normalizeExtension).filter(Boolean)
        : [];
    if (extensions.length === 0) return true;
    if (normalizeFileExplorerKind(entry?.kind) === FILE_EXPLORER_MODES.DIRECTORY) return true;
    const extension = fileExplorerExtension(entry);
    return Boolean(extension) && extensions.includes(extension);
}

/**
 * The "Type" column: "File folder" for directories, else the known type name from
 * file-type-icons or "<EXT> File" for anything it does not recognise.
 */
export function getFileExplorerTypeLabel(entry) {
    if (normalizeFileExplorerKind(entry?.kind) === FILE_EXPLORER_MODES.DIRECTORY) return 'File folder';
    const visual = getFileTypeVisual(stringValue(entry?.name));
    if (visual.name !== 'File') return visual.name;
    const extension = fileExplorerExtension(entry);
    return extension ? `${extension.toUpperCase()} File` : 'File';
}

/**
 * Type-ahead like a desktop list: the accumulated buffer jumps to the first name that starts with
 * it; pressing the same key repeatedly cycles through the names starting with that letter.
 *
 * @returns {number} Index of the entry to focus, or -1 when nothing matches.
 */
export function findFileExplorerTypeAheadIndex(entries = [], buffer = '', currentIndex = -1) {
    const list = Array.isArray(entries) ? entries : [];
    const query = String(buffer || '').toLocaleLowerCase();
    if (!query || list.length === 0) return -1;

    const names = list.map(entry => String(entry?.name || '').toLocaleLowerCase());
    const repeated = query.length === 1 || query.split('').every(character => character === query[0]);
    if (repeated) {
        const start = Number.isInteger(currentIndex) ? currentIndex : -1;
        for (let step = 1; step <= names.length; step += 1) {
            const index = (start + step + names.length) % names.length;
            if (names[index].startsWith(query[0])) return index;
        }
        return -1;
    }
    return names.findIndex(name => name.startsWith(query));
}

/**
 * Interprets what the user typed into the "File name" box against the current folder.
 *
 * @returns {{kind:'empty'}
 *   | {kind:'entry', entry: object}
 *   | {kind:'path', directory: string, name: string}
 *   | {kind:'missing', name: string}}
 *   `entry` is a name found in the current folder (exact match first, then a unique
 *   case-insensitive match); `path` means "go to `directory`, then select `name` if present";
 *   `missing` is a plain name that is not in the folder.
 */
export function resolveFileExplorerFileNameInput(text, entries = [], currentPath = '') {
    const value = stringValue(text).trim();
    if (!value) return { kind: 'empty' };

    const list = Array.isArray(entries) ? entries : [];
    if (isAbsoluteFileExplorerPath(value)) {
        const { directory, name } = splitFileExplorerPath(value);
        if (!name) return { kind: 'path', directory, name: '' };
        if (samePath(directory, currentPath)) {
            const entry = findEntryByName(list, name);
            return entry ? { kind: 'entry', entry } : { kind: 'missing', name };
        }
        return { kind: 'path', directory, name };
    }

    if (/[\\/]/.test(value)) {
        // A relative sub-path such as src/app.py: walk from the current folder.
        const separator = isWindowsFileSystemPath(currentPath) ? '\\' : '/';
        const { directory, name } = splitFileExplorerPath(
            `${stringValue(currentPath).replace(/[\\/]+$/, '')}${separator}${value.replace(/^[\\/]+/, '')}`
        );
        return { kind: 'path', directory, name };
    }

    const entry = findEntryByName(list, value);
    return entry ? { kind: 'entry', entry } : { kind: 'missing', name: value };
}

export function isAbsoluteFileExplorerPath(path) {
    const value = stringValue(path).trim();
    return isWindowsFileSystemPath(value) || value.startsWith('/');
}

/**
 * Splits an absolute path into its parent directory and final segment. A path that ends with a
 * separator, or that is a root, yields an empty `name`.
 */
export function splitFileExplorerPath(path) {
    const value = stringValue(path).trim();
    const windowsPath = isWindowsFileSystemPath(value);
    if (!value) return { directory: '', name: '' };
    if (value === '/' || /^[a-z]:[\\/]?$/i.test(value)) {
        return { directory: windowsPath ? `${value.slice(0, 2)}\\` : value, name: '' };
    }
    if (/[\\/]$/.test(value)) {
        return { directory: normalizeDirectoryPath(value.replace(/[\\/]+$/, ''), windowsPath), name: '' };
    }
    const index = Math.max(value.lastIndexOf('/'), windowsPath ? value.lastIndexOf('\\') : -1);
    if (index < 0) return { directory: '', name: value };
    return {
        directory: normalizeDirectoryPath(value.slice(0, index), windowsPath),
        name: value.slice(index + 1)
    };
}

export function canSelectFileExplorerKind(mode, kind) {
    const normalizedMode = normalizeFileExplorerMode(mode);
    const normalizedKind = normalizeFileExplorerKind(kind);
    return normalizedMode === FILE_EXPLORER_MODES.ANY || normalizedMode === normalizedKind;
}

export function fileExplorerNameFromPath(path) {
    const value = stringValue(path);
    if (!value) return '';

    const windowsPath = isWindowsFileSystemPath(value);
    const withoutTrailingSeparators = windowsPath
        ? value.replace(/[\\/]+$/, '')
        : value.replace(/\/+$/, '');
    if (!withoutTrailingSeparators) return value;
    const segments = withoutTrailingSeparators
        .split(windowsPath ? /[\\/]/ : /\//)
        .filter(Boolean);
    return segments.at(-1) || withoutTrailingSeparators;
}

export function formatFileExplorerSize(size) {
    if (size == null || size === '') return '—';
    const bytes = Number(size);
    if (!Number.isFinite(bytes) || bytes < 0) return '—';
    if (bytes < 1024) return `${bytes} B`;

    const units = ['KB', 'MB', 'GB', 'TB'];
    let value = bytes / 1024;
    let unit = units[0];
    for (let index = 1; index < units.length && value >= 1024; index += 1) {
        value /= 1024;
        unit = units[index];
    }
    const digits = value >= 100 ? 0 : value >= 10 ? 1 : 2;
    return `${value.toFixed(digits)} ${unit}`;
}

/** Compact locale date for the "Date modified" column, e.g. 8/19/2026 3:04 PM. */
export function formatFileExplorerDate(value) {
    if (!value) return '—';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '—';
    const formatter = new Intl.DateTimeFormat(undefined, {
        year: 'numeric',
        month: 'numeric',
        day: 'numeric',
        hour: 'numeric',
        minute: '2-digit'
    });
    // The details view of a desktop dialog separates date and time with a space, not the
    // locale's ", " — only the literal joiners are touched so digits and AM/PM stay as-is.
    return formatter.formatToParts(date)
        .map(part => part.type === 'literal' ? part.value.replace(/,\s*/g, ' ') : part.value)
        .join('');
}

export function createFileExplorerResult(entry) {
    const path = stringValue(entry?.path);
    if (!path) return { ...CANCELED_RESULT };

    return {
        canceled: false,
        path,
        kind: normalizeFileExplorerKind(entry?.kind),
        name: stringValue(entry?.name) || fileExplorerNameFromPath(path) || null
    };
}

export function createCanceledFileExplorerResult() {
    return { ...CANCELED_RESULT };
}

/**
 * Opens the server-backed filesystem explorer as a desktop-style Open / Select Folder dialog.
 *
 * @param {object} app VibeRails application instance.
 * @param {object} options
 * @param {'file'|'directory'|'any'} [options.mode='any'] Selectable entry type.
 * @param {string} [options.initialPath] Initial directory; defaults to project root/launch directory.
 * @param {string} [options.title] Dialog title.
 * @param {boolean} [options.includeHidden=false] Initial hidden-file visibility.
 * @param {Array<{label: string, extensions: string[]}>} [options.filters] "Files of type" choices
 *   (extensions without dots, case-insensitive; an empty list means all files). Ignored in
 *   directory mode. When omitted every file is shown, as before.
 * @param {HTMLElement} [options.triggerElement] Focus target restored after dismissal.
 * @returns {Promise<{canceled:boolean,path:string|null,kind:string|null,name:string|null}>}
 */
export function openFileExplorer(app, options = {}) {
    activeExplorer?.cancel?.({ restoreFocus: false });

    const host = typeof document !== 'undefined'
        ? document.getElementById('modal-container')
        : null;
    if (!host || typeof app?.apiCall !== 'function') {
        return Promise.resolve(createCanceledFileExplorerResult());
    }

    const mode = normalizeFileExplorerMode(options.mode);
    const projectPath = getFileExplorerInitialPath(app);
    // Without an explicit start, reopen where the last picker of this mode was accepted, like a
    // desktop dialog does; the project root is the quiet fallback should that folder be gone.
    const rememberedPath = cleanString(options.initialPath) ? '' : readFileExplorerLastPath(mode);
    const initialPath = getFileExplorerInitialPath(app, cleanString(options.initialPath) || rememberedPath);
    const fallbackPath = rememberedPath && !samePath(rememberedPath, projectPath) ? projectPath : '';
    const filters = mode === FILE_EXPLORER_MODES.DIRECTORY
        ? []
        : normalizeFileExplorerFilters(options.filters);
    const triggerElement = options.triggerElement instanceof HTMLElement
        ? options.triggerElement
        : document.activeElement instanceof HTMLElement
            ? document.activeElement
            : null;

    const layer = createExplorerLayer();
    const underlying = collectExplorerBackground(host).map(element => ({
        element,
        inert: Boolean(element.inert),
        ariaHidden: element.getAttribute('aria-hidden')
    }));
    underlying.forEach(({ element }) => {
        element.inert = true;
        element.setAttribute('aria-hidden', 'true');
    });
    host.appendChild(layer);

    const elements = collectElements(layer);
    const state = {
        mode,
        filters,
        filterIndex: 0,
        includeHidden: Boolean(options.includeHidden),
        // Seeded with the project root so the Places sidebar has something to show even when
        // the very first load fails and no server payload ever arrives.
        data: normalizeFileExplorerPayload({ defaultPath: projectPath }, initialPath),
        fallbackPath,
        selected: null,
        history: [],
        historyIndex: -1,
        searchText: '',
        searchTimer: null,
        sort: { ...DEFAULT_SORT },
        typeAhead: { buffer: '', timer: null },
        addressEditing: false,
        fileNameMirror: '',
        pendingSelectName: null,
        requestId: 0,
        requestController: null,
        retryNavigation: null,
        requestedPath: initialPath,
        loading: false,
        loadingMore: false,
        loadMoreError: '',
        locationReady: false,
        settled: false,
        disposed: false,
        observer: null,
        keydownHandler: null
    };

    setStaticCopy(elements, state, options.title);
    renderFilterSelect(elements, state);
    updateSortHeaders(elements, state);
    elements.hiddenToggle.checked = state.includeHidden;

    return new Promise(resolve => {
        const restoreUnderlying = () => {
            underlying.forEach(({ element, inert, ariaHidden }) => {
                if (!element.isConnected) return;
                element.inert = inert;
                if (ariaHidden == null) element.removeAttribute('aria-hidden');
                else element.setAttribute('aria-hidden', ariaHidden);
            });
        };

        const dispose = ({ restoreFocus = true } = {}) => {
            if (state.disposed) return;
            state.disposed = true;
            state.requestController?.abort();
            state.requestController = null;
            if (state.searchTimer) clearTimeout(state.searchTimer);
            state.searchTimer = null;
            if (state.typeAhead.timer) clearTimeout(state.typeAhead.timer);
            state.typeAhead.timer = null;
            state.observer?.disconnect();
            if (state.keydownHandler) {
                document.removeEventListener('keydown', state.keydownHandler, true);
            }
            restoreUnderlying();
            if (activeExplorer?.layer === layer) activeExplorer = null;

            if (restoreFocus && triggerElement?.isConnected) {
                triggerElement.focus?.({ preventScroll: true });
            }
        };

        const settle = (result, { restoreFocus = true } = {}) => {
            if (state.settled) return;
            state.settled = true;
            if (!result?.canceled && state.locationReady) {
                writeFileExplorerLastPath(mode, state.data.currentPath);
            }
            if (layer.isConnected) layer.remove();
            dispose({ restoreFocus });
            resolve(result);
        };

        const cancel = ({ restoreFocus = true } = {}) => settle(
            createCanceledFileExplorerResult(),
            { restoreFocus }
        );

        const handleEscape = () => {
            const active = document.activeElement;
            if (state.addressEditing && active === elements.address) {
                exitAddressEdit(elements, state, { focusCrumbs: true });
                return true;
            }
            if (active === elements.search && elements.search.value) {
                handlers.search('');
                elements.search.value = '';
                return true;
            }
            return false;
        };
        activeExplorer = { layer, cancel, handleEscape };

        const acceptEntry = entry => {
            if (state.loading
                || !entry
                || entry.isSymbolicLink
                || !canSelectFileExplorerKind(mode, entry.kind)) return;
            settle(createFileExplorerResult(entry));
        };

        const acceptCurrentDirectory = () => {
            if (state.loading
                || !state.locationReady
                || !canSelectFileExplorerKind(mode, FILE_EXPLORER_MODES.DIRECTORY)) return;
            acceptEntry({
                path: state.data.currentPath,
                name: state.data.currentName || fileExplorerNameFromPath(state.data.currentPath),
                kind: FILE_EXPLORER_MODES.DIRECTORY
            });
        };

        const applyHistory = (path, historyMode, targetHistoryIndex = null) => {
            if (historyMode === 'push') {
                const previous = state.history[state.historyIndex];
                if (samePath(previous, path)) return;
                state.history = state.history.slice(0, state.historyIndex + 1);
                state.history.push(path);
                state.historyIndex = state.history.length - 1;
                return;
            }
            if (historyMode === 'replace') {
                if (state.historyIndex < 0) {
                    state.history = [path];
                    state.historyIndex = 0;
                } else {
                    state.history[state.historyIndex] = path;
                }
                return;
            }
            if (Number.isInteger(targetHistoryIndex)) {
                state.historyIndex = targetHistoryIndex;
            }
        };

        const refreshSelectionUi = () => {
            updateEntrySelection(elements, state);
            updateFileNameField(elements, state);
            updateSelectionFooter(elements, state);
            updateStatus(elements, state);
        };

        const loadPath = async (
            path,
            {
                historyMode = 'push',
                targetHistoryIndex = null,
                append = false,
                cursor = null,
                selectName = null
            } = {}
        ) => {
            if (state.settled) return;

            const requestedPath = stringValue(path);
            const requestSearch = cleanString(state.searchText);
            if (append && (!cursor || requestSearch !== cleanString(state.data.search))) {
                append = false;
                cursor = null;
            }
            if (state.searchTimer) clearTimeout(state.searchTimer);
            state.searchTimer = null;
            const activeElement = document.activeElement;
            const restoreLoadMoreFocus = append && Boolean(
                activeElement?.closest?.('[data-file-explorer-action="load-more"]')
            );
            const restoreNavigationFocus = [
                elements.view,
                elements.addressBar,
                elements.places
            ].some(container => container.contains(activeElement));
            state.requestedPath = requestedPath;
            const requestId = ++state.requestId;
            state.requestController?.abort();
            const controller = new AbortController();
            state.requestController = controller;
            state.loadMoreError = '';
            if (append) {
                state.loadingMore = true;
                renderNotice(elements, state);
            } else {
                state.retryNavigation = {
                    path: requestedPath,
                    historyMode,
                    targetHistoryIndex
                };
                state.pendingSelectName = cleanString(selectName) || null;
                state.selected = null;
                state.loading = true;
                state.locationReady = false;
                if (state.addressEditing) exitAddressEdit(elements, state);
                setFileNameHint(elements, '');
                renderLoading(elements);
                if (restoreNavigationFocus) {
                    // Keep focus inside the dialog while the rows it was on are torn down; the
                    // view itself is the parking spot so no text field swallows type-ahead keys.
                    elements.view.focus({ preventScroll: true });
                }
            }
            updateToolbar(elements, state);
            updateSelectionFooter(elements, state);

            try {
                const payload = await app.apiCall(
                    buildFileExplorerEntriesEndpoint(requestedPath, state.includeHidden, {
                        search: requestSearch,
                        cursor,
                        pageSize: FILE_EXPLORER_PAGE_SIZE
                    }),
                    'GET',
                    null,
                    {
                        showLoading: false,
                        signal: controller.signal,
                        preferErrorResponseMessage: true
                    }
                );
                if (state.settled || controller.signal.aborted || requestId !== state.requestId) return;

                const normalized = normalizeFileExplorerPayload(payload, requestedPath);
                state.data = append
                    ? {
                        ...normalized,
                        entries: mergeFileExplorerEntries(state.data.entries, normalized.entries)
                    }
                    : normalized;
                state.requestedPath = normalized.currentPath;
                state.loading = false;
                state.loadingMore = false;
                state.locationReady = true;
                let unresolvedName = null;
                if (!append) {
                    state.selected = null;
                    state.retryNavigation = null;
                    applyHistory(normalized.currentPath, historyMode, targetHistoryIndex);
                    if (elements.fileName.value === state.fileNameMirror) setFileNameValue(elements, state, '');
                    unresolvedName = resolvePendingSelection(elements, state);
                }
                renderExplorer(elements, state, handlers);
                if (unresolvedName) {
                    // The wanted name may sit beyond this page or outside the active search:
                    // ask the server for it before telling the user it does not exist.
                    searchForName(unresolvedName);
                    return;
                }
                if (restoreLoadMoreFocus) {
                    requestAnimationFrame(() => {
                        if (state.settled || requestId !== state.requestId || !layer.isConnected) return;
                        const nextButton = elements.notice.querySelector(
                            '[data-file-explorer-action="load-more"]'
                        );
                        if (nextButton) {
                            nextButton.focus({ preventScroll: true });
                        } else {
                            elements.status.tabIndex = -1;
                            elements.status.focus({ preventScroll: true });
                        }
                    });
                }
                if (restoreNavigationFocus) {
                    requestAnimationFrame(() => {
                        if (state.settled || requestId !== state.requestId || !layer.isConnected) return;
                        const focusTarget = elements.list.querySelector(
                            '[data-file-explorer-entry][tabindex="0"]'
                        ) || elements.view;
                        focusTarget.focus?.({ preventScroll: true });
                    });
                }
            } catch (error) {
                if (state.settled || controller.signal.aborted || requestId !== state.requestId) return;
                const message = error?.message || 'This location could not be opened.';
                if (append) {
                    state.loadingMore = false;
                    state.loadMoreError = message;
                    renderNotice(elements, state);
                    updateStatus(elements, state);
                    if (restoreLoadMoreFocus) {
                        requestAnimationFrame(() => elements.notice.querySelector(
                            '[data-file-explorer-action="load-more"]'
                        )?.focus({ preventScroll: true }));
                    }
                } else {
                    if (shouldFallBackToDefaultPath({
                        historyIndex: state.historyIndex,
                        requestedPath,
                        fallbackPath: state.fallbackPath
                    })) {
                        // The remembered folder is gone (deleted, unplugged, another machine):
                        // start where a fresh dialog would instead of opening on an error.
                        const fallback = state.fallbackPath;
                        state.fallbackPath = '';
                        void loadPath(fallback, { historyMode: 'replace' });
                        return;
                    }
                    state.loading = false;
                    renderError(elements, state, message, handlers.onNavigate);
                }
                if (!append && restoreNavigationFocus) {
                    requestAnimationFrame(() => {
                        if (state.settled || requestId !== state.requestId || !layer.isConnected) return;
                        const focusTarget = elements.state.querySelector('[data-file-explorer-action="retry"]')
                            || elements.view;
                        focusTarget.focus?.({ preventScroll: true });
                    });
                }
            } finally {
                if (state.requestController === controller) state.requestController = null;
                if (!state.settled && requestId === state.requestId) {
                    state.loadingMore = false;
                    updateToolbar(elements, state);
                    updateSelectionFooter(elements, state);
                }
            }
        };

        const navigate = (path, extra = {}) => void loadPath(path, { historyMode: 'push', ...extra });

        const handlers = {
            onSelect: entry => {
                state.selected = entry || null;
                if (entry) setFileNameHint(elements, '');
                refreshSelectionUi();
            },
            onOpen: entry => {
                if (!entry) return;
                if (entry.isSymbolicLink) {
                    handlers.onSelect(entry);
                    return;
                }
                if (entry.kind === FILE_EXPLORER_MODES.DIRECTORY) {
                    navigate(entry.path);
                } else if (canSelectFileExplorerKind(mode, entry.kind)) {
                    acceptEntry(entry);
                } else {
                    handlers.onSelect(entry);
                }
            },
            onNavigate: nextPath => navigate(nextPath),
            onSort: key => {
                const current = normalizeFileExplorerSort(state.sort);
                state.sort = current.key === key
                    ? { key, direction: current.direction === 'asc' ? 'desc' : 'asc' }
                    : { key, direction: 'asc' };
                updateSortHeaders(elements, state);
                renderEntries(elements, state, handlers);
            },
            onFilter: index => {
                state.filterIndex = Number.isInteger(index) && state.filters[index] ? index : 0;
                dropHiddenSelection(state);
                renderEntries(elements, state, handlers);
                renderNotice(elements, state);
                refreshSelectionUi();
            },
            // The primary button and Enter in the file-name box share one path: resolve typed
            // text first (a real dialog opens what you typed), then fall back to the highlighted
            // row, then — for folder pickers — to the folder being viewed.
            primary: () => {
                if (state.loading || !state.locationReady) return;
                if (commitFileNameInput()) return;
                const selected = state.selected;
                switch (resolveFileExplorerPrimaryAction(mode, selected)) {
                    case 'navigate':
                        navigate(selected.path);
                        return;
                    case 'accept-entry':
                        acceptEntry(selected);
                        return;
                    case 'accept-current':
                        acceptCurrentDirectory();
                        return;
                    default:
                        return;
                }
            },
            cancel,
            back: () => {
                if (state.historyIndex <= 0) return;
                const nextIndex = state.historyIndex - 1;
                void loadPath(state.history[nextIndex], {
                    historyMode: 'none',
                    targetHistoryIndex: nextIndex
                });
            },
            forward: () => {
                if (state.historyIndex >= state.history.length - 1) return;
                const nextIndex = state.historyIndex + 1;
                void loadPath(state.history[nextIndex], {
                    historyMode: 'none',
                    targetHistoryIndex: nextIndex
                });
            },
            up: () => {
                if (state.data.parentPath) navigate(state.data.parentPath);
            },
            refresh: () => void loadPath(state.data.currentPath || state.requestedPath, { historyMode: 'none' }),
            retry: () => {
                const retry = state.retryNavigation;
                void loadPath(
                    retry?.path || state.requestedPath || initialPath,
                    {
                        historyMode: retry?.historyMode || 'none',
                        targetHistoryIndex: retry?.targetHistoryIndex ?? null
                    }
                );
            },
            loadMore: () => void loadPath(
                state.data.currentPath || state.requestedPath,
                {
                    historyMode: 'none',
                    append: true,
                    cursor: state.data.nextCursor
                }
            ),
            address: path => navigate(path),
            search: value => {
                state.searchText = value;
                dropHiddenSelection(state);
                renderEntries(elements, state, handlers);
                renderNotice(elements, state);
                refreshSelectionUi();
                if (state.searchTimer) clearTimeout(state.searchTimer);
                state.searchTimer = setTimeout(() => {
                    state.searchTimer = null;
                    void loadPath(
                        state.data.currentPath || state.requestedPath,
                        { historyMode: 'none' }
                    );
                }, SEARCH_DEBOUNCE_MS);
            },
            hidden: checked => {
                state.includeHidden = checked;
                void loadPath(state.data.currentPath || state.requestedPath, { historyMode: 'none' });
            },
            fileNameInput: () => {
                setFileNameHint(elements, '');
                updateSelectionFooter(elements, state);
            }
        };

        /**
         * Applies the text in the file-name box. Returns true when the text was acted on (or
         * rejected with a hint) so the caller must not also run the highlighted-row action.
         */
        function commitFileNameInput() {
            const text = elements.fileName.value.trim();
            if (!text || text === state.fileNameMirror.trim()) return false;
            const resolution = resolveFileExplorerFileNameInput(text, state.data.entries, state.data.currentPath);
            if (resolution.kind === 'empty') return false;
            if (resolution.kind === 'path') {
                navigate(resolution.directory, { selectName: resolution.name });
                return true;
            }
            if (resolution.kind === 'missing') {
                const plan = planFileExplorerMissingName(resolution.name, state.data, mode);
                if (plan.action === 'search') searchForName(resolution.name);
                else setFileNameHint(elements, plan.message);
                return true;
            }
            const entry = resolution.entry;
            if (entry.isSymbolicLink) {
                handlers.onSelect(entry);
                return true;
            }
            if (entry.kind === FILE_EXPLORER_MODES.DIRECTORY) {
                if (mode === FILE_EXPLORER_MODES.FILE) navigate(entry.path);
                else acceptEntry(entry);
                return true;
            }
            if (canSelectFileExplorerKind(mode, entry.kind)) {
                acceptEntry(entry);
            } else {
                setFileNameHint(elements, `${entry.name} is a file, not a folder`);
            }
            return true;
        }

        /**
         * Asks the server for a name the loaded page does not contain: the search box takes the
         * name (so the narrowed list is explained), and the exact match — if it exists — is
         * highlighted when the results arrive.
         */
        function searchForName(name) {
            state.searchText = name;
            elements.search.value = name;
            void loadPath(state.data.currentPath || state.requestedPath, {
                historyMode: 'none',
                selectName: name
            });
        }

        bindStaticActions(elements, state, handlers);
        bindListInteractions(elements, state, handlers);

        state.keydownHandler = event => {
            if (isConfirmDialogOpen()) return;
            if (state.settled) return;
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopImmediatePropagation();
                if (!handleEscape(event)) cancel();
                return;
            }
            if (event.key === 'Tab') {
                trapFocus(event, layer);
                return;
            }
            handleDialogShortcut(event, elements, state, handlers);
        };
        document.addEventListener('keydown', state.keydownHandler, true);

        state.observer = new MutationObserver(() => {
            if (!layer.isConnected && !state.settled) {
                settle(createCanceledFileExplorerResult(), { restoreFocus: false });
            }
        });
        state.observer.observe(host, { childList: true });

        renderLoading(elements);
        updateSelectionFooter(elements, state);
        requestAnimationFrame(() => elements.fileName.focus());
        void loadPath(initialPath, { historyMode: 'replace' });
    });
}

function cleanString(value) {
    return typeof value === 'string' ? value.trim() : '';
}

function stringValue(value) {
    return typeof value === 'string' ? value : '';
}

/** window.localStorage when reachable; touching it can throw in sandboxed frames. */
function defaultStorage() {
    try {
        return globalThis.localStorage ?? null;
    } catch {
        return null;
    }
}

function normalizeTotalCount(value) {
    if (value == null || value === '') return null;
    const count = Number(value);
    return Number.isSafeInteger(count) && count >= 0 ? count : null;
}

function normalizeExtension(value) {
    return String(value ?? '').trim().replace(/^\.+/, '').toLocaleLowerCase();
}

function fileExplorerExtension(entry) {
    const declared = normalizeExtension(entry?.extension);
    if (declared) return declared;
    const name = stringValue(entry?.name);
    const index = name.lastIndexOf('.');
    return index > 0 && index < name.length - 1 ? name.slice(index + 1).toLocaleLowerCase() : '';
}

function entryTimestamp(entry) {
    const value = entry?.lastModifiedUtc;
    if (!value) return Number.NEGATIVE_INFINITY;
    const time = new Date(value).getTime();
    return Number.isNaN(time) ? Number.NEGATIVE_INFINITY : time;
}

function entrySize(entry) {
    const size = Number(entry?.size);
    return entry?.size == null || !Number.isFinite(size) ? -1 : size;
}

function compareNumbers(left, right) {
    return left < right ? -1 : left > right ? 1 : 0;
}

function compareText(left, right) {
    return String(left || '').localeCompare(String(right || ''), undefined, {
        numeric: true,
        sensitivity: 'base'
    });
}

function compareEntryNames(left, right) {
    return compareText(left?.name, right?.name);
}

function findEntryByName(entries, name) {
    const wanted = stringValue(name);
    if (!wanted) return null;
    const exact = entries.find(entry => stringValue(entry?.name) === wanted);
    if (exact) return exact;
    const folded = wanted.toLocaleLowerCase();
    const loose = entries.filter(entry => stringValue(entry?.name).toLocaleLowerCase() === folded);
    return loose.length === 1 ? loose[0] : null;
}

function normalizeDirectoryPath(directory, windowsPath) {
    if (!directory) return windowsPath ? '' : '/';
    if (windowsPath && /^[a-z]:$/i.test(directory)) return `${directory}\\`;
    return directory;
}

function isWindowsFileSystemPath(path) {
    const value = stringValue(path);
    return /^[a-z]:([\\/]|$)/i.test(value) || /^\\\\/.test(value);
}

function normalizeEntry(entry = {}) {
    return {
        name: stringValue(entry?.name),
        path: stringValue(entry?.path),
        kind: normalizeFileExplorerKind(entry?.kind),
        isHidden: Boolean(entry?.isHidden),
        isSymbolicLink: Boolean(entry?.isSymbolicLink),
        size: entry?.size != null && Number.isFinite(Number(entry.size)) ? Number(entry.size) : null,
        lastModifiedUtc: cleanString(entry?.lastModifiedUtc) || null,
        extension: cleanString(entry?.extension) || null
    };
}

function normalizeLocations(locations) {
    if (!Array.isArray(locations)) return [];
    return locations.map(location => {
        if (typeof location === 'string') {
            const path = stringValue(location);
            return { label: fileExplorerNameFromPath(path) || path, path };
        }
        const path = stringValue(location?.path);
        return {
            label: stringValue(location?.label) || stringValue(location?.name) || fileExplorerNameFromPath(path) || path,
            path
        };
    }).filter(location => location.path);
}

function normalizePlaces(places) {
    if (!Array.isArray(places)) return [];
    const seen = new Set();
    return places.map(place => {
        const path = stringValue(place?.path);
        const kind = cleanString(place?.kind).toLowerCase();
        return {
            label: stringValue(place?.label) || fileExplorerNameFromPath(path) || path,
            path,
            kind: Object.hasOwn(PLACE_ICONS, kind) && kind !== 'project' && kind !== 'drive' ? kind : 'home'
        };
    }).filter(place => {
        const key = fileExplorerPathKey(place.path);
        if (!key || seen.has(key)) return false;
        seen.add(key);
        return true;
    });
}

function samePath(left, right) {
    const normalize = value => {
        const path = stringValue(value);
        if (path === '/') return path;
        if (isWindowsFileSystemPath(path)) {
            if (/^[a-z]:[\\/]?$/i.test(path)) return path.slice(0, 2);
            return path.replace(/[\\/]+$/, '');
        }
        return path.replace(/\/+$/, '');
    };
    const leftPath = normalize(left);
    const rightPath = normalize(right);
    if (!leftPath || !rightPath) return false;

    if (isWindowsFileSystemPath(leftPath) && isWindowsFileSystemPath(rightPath)) {
        return leftPath.replaceAll('/', '\\').toLocaleLowerCase()
            === rightPath.replaceAll('/', '\\').toLocaleLowerCase();
    }
    return leftPath === rightPath;
}

function fileExplorerPathKey(path) {
    const value = stringValue(path);
    if (!value) return '';
    const withoutTrailingSeparators = value === '/'
        ? value
        : value.replace(/[\\/]+$/, '');
    return isWindowsFileSystemPath(withoutTrailingSeparators)
        ? withoutTrailingSeparators.replaceAll('/', '\\').toLocaleLowerCase()
        : withoutTrailingSeparators;
}

function collectExplorerBackground(host) {
    const elements = new Set(Array.from(host.children));
    let current = host;
    while (current?.parentElement) {
        const parent = current.parentElement;
        Array.from(parent.children).forEach(element => {
            if (element === current
                || !(element instanceof HTMLElement)
                || ['SCRIPT', 'STYLE', 'LINK', 'TEMPLATE'].includes(element.tagName)) return;
            elements.add(element);
        });
        if (parent === document.body) break;
        current = parent;
    }
    return Array.from(elements);
}

function activeFilter(state) {
    return state.filters[state.filterIndex] || null;
}

function getVisibleEntries(state) {
    const activeSearch = cleanString(state.searchText);
    const serverSearch = cleanString(state.data.search);
    return filterAndSortFileExplorerEntries(
        state.data.entries,
        activeSearch === serverSearch ? '' : activeSearch,
        { sort: state.sort, filter: activeFilter(state) }
    );
}

function dropHiddenSelection(state) {
    if (state.selected && !getVisibleEntries(state)
        .some(entry => samePath(entry.path, state.selected.path))) {
        state.selected = null;
    }
}

/**
 * Highlights the name a navigation asked for once its folder has loaded. Returns the name when
 * it is not in the loaded page and the caller should ask the server for it (see
 * planFileExplorerMissingName); otherwise the hint is shown here and null is returned.
 */
function resolvePendingSelection(elements, state) {
    const name = state.pendingSelectName;
    state.pendingSelectName = null;
    if (!name) return null;
    const entry = findEntryByName(state.data.entries, name);
    if (entry) {
        state.selected = entry;
        return null;
    }
    if (state.mode !== FILE_EXPLORER_MODES.DIRECTORY) elements.fileName.value = name;
    const plan = planFileExplorerMissingName(name, state.data, state.mode);
    if (plan.action === 'search') return name;
    setFileNameHint(elements, plan.message);
    return null;
}

function createExplorerLayer() {
    const layer = document.createElement('div');
    layer.className = 'vb-file-explorer-layer';
    layer.innerHTML = `
        <div class="modal fade show d-block vb-file-explorer-modal" tabindex="-1"
             role="dialog" aria-modal="true" aria-labelledby="vb-file-explorer-title"
             data-file-explorer-modal>
            <div class="modal-dialog modal-dialog-centered">
                <div class="modal-content vb-file-explorer-dialog">
                    <header class="vb-file-explorer-titlebar">
                        <h5 class="modal-title" id="vb-file-explorer-title" data-file-explorer-title></h5>
                        <button type="button" class="btn-close" data-file-explorer-action="cancel"
                                aria-label="Close"></button>
                    </header>

                    <div class="vb-file-explorer-toolbar" role="toolbar" aria-label="Navigation">
                        <div class="vb-file-explorer-nav" role="group" aria-label="History">
                            <button type="button" class="vb-file-explorer-tool" data-file-explorer-action="back"
                                    title="Back (Alt+Left)" aria-label="Back">
                                <i class="fa-solid fa-arrow-left" aria-hidden="true"></i>
                            </button>
                            <button type="button" class="vb-file-explorer-tool" data-file-explorer-action="forward"
                                    title="Forward (Alt+Right)" aria-label="Forward">
                                <i class="fa-solid fa-arrow-right" aria-hidden="true"></i>
                            </button>
                            <button type="button" class="vb-file-explorer-tool" data-file-explorer-action="up"
                                    title="Up (Alt+Up)" aria-label="Up one level">
                                <i class="fa-solid fa-arrow-up" aria-hidden="true"></i>
                            </button>
                        </div>
                        <div class="vb-file-explorer-address-bar" data-file-explorer-address-bar
                             title="Click to type a path (Ctrl+L)">
                            <i class="fa-regular fa-folder vb-file-explorer-address-icon" aria-hidden="true"></i>
                            <nav class="vb-file-explorer-crumbs" aria-label="Current path"
                                 data-file-explorer-breadcrumb></nav>
                            <input type="text" class="vb-file-explorer-address-input" autocomplete="off"
                                   spellcheck="false" aria-label="Location" hidden data-file-explorer-path>
                        </div>
                        <button type="button" class="vb-file-explorer-tool" data-file-explorer-action="refresh"
                                title="Refresh (F5)" aria-label="Refresh">
                            <i class="fa-solid fa-rotate-right" aria-hidden="true"></i>
                        </button>
                        <label class="vb-file-explorer-search">
                            <i class="fa-solid fa-magnifying-glass" aria-hidden="true"></i>
                            <span class="visually-hidden">Search this folder</span>
                            <input type="search" placeholder="Search" autocomplete="off" maxlength="256"
                                   data-file-explorer-search>
                        </label>
                    </div>

                    <div class="vb-file-explorer-body">
                        <aside class="vb-file-explorer-sidebar" aria-label="Places" data-file-explorer-places></aside>

                        <section class="vb-file-explorer-main" aria-label="Folder contents">
                            <div class="vb-file-explorer-view" tabindex="-1" data-file-explorer-view>
                                <div class="vb-file-explorer-grid" role="grid" aria-label="Files and folders"
                                     data-file-explorer-grid>
                                    <div class="vb-file-explorer-columns" role="rowgroup">
                                        <div class="vb-file-explorer-columns-row" role="row">
                                            <div class="vb-file-explorer-column is-name" role="columnheader"
                                                 aria-sort="ascending" data-file-explorer-column="name">
                                                <button type="button" data-file-explorer-sort="name" title="Sort by name">
                                                    <span>Name</span>
                                                    <i class="vb-file-explorer-sort-icon fa-solid fa-chevron-up" aria-hidden="true"></i>
                                                </button>
                                            </div>
                                            <div class="vb-file-explorer-column is-modified" role="columnheader"
                                                 aria-sort="none" data-file-explorer-column="modified">
                                                <button type="button" data-file-explorer-sort="modified" title="Sort by date modified">
                                                    <span>Date modified</span>
                                                    <i class="vb-file-explorer-sort-icon fa-solid fa-chevron-up" aria-hidden="true"></i>
                                                </button>
                                            </div>
                                            <div class="vb-file-explorer-column is-type" role="columnheader"
                                                 aria-sort="none" data-file-explorer-column="type">
                                                <button type="button" data-file-explorer-sort="type" title="Sort by type">
                                                    <span>Type</span>
                                                    <i class="vb-file-explorer-sort-icon fa-solid fa-chevron-up" aria-hidden="true"></i>
                                                </button>
                                            </div>
                                            <div class="vb-file-explorer-column is-size" role="columnheader"
                                                 aria-sort="none" data-file-explorer-column="size">
                                                <button type="button" data-file-explorer-sort="size" title="Sort by size">
                                                    <span>Size</span>
                                                    <i class="vb-file-explorer-sort-icon fa-solid fa-chevron-up" aria-hidden="true"></i>
                                                </button>
                                            </div>
                                        </div>
                                    </div>
                                    <div class="vb-file-explorer-list" role="rowgroup" data-file-explorer-list></div>
                                </div>
                                <div class="vb-file-explorer-state d-none" data-file-explorer-state></div>
                            </div>
                            <div class="vb-file-explorer-notice d-none" aria-live="polite"
                                 data-file-explorer-notice></div>
                        </section>
                    </div>

                    <div class="vb-file-explorer-namerow">
                        <label class="vb-file-explorer-namerow-label" for="vb-file-explorer-filename"
                               data-file-explorer-filename-label>File name:</label>
                        <div class="vb-file-explorer-filename">
                            <input id="vb-file-explorer-filename" type="text" class="vb-file-explorer-filename-input"
                                   autocomplete="off" spellcheck="false" data-file-explorer-filename>
                            <small class="vb-file-explorer-filename-hint d-none" role="status"
                                   data-file-explorer-filename-hint></small>
                        </div>
                        <select class="form-select form-select-sm vb-file-explorer-filter d-none"
                                aria-label="Files of type" data-file-explorer-filter></select>
                    </div>

                    <footer class="vb-file-explorer-footer">
                        <div class="vb-file-explorer-footer-info">
                            <span class="vb-file-explorer-status" aria-live="polite" data-file-explorer-status></span>
                            <label class="vb-file-explorer-hidden-toggle">
                                <input type="checkbox" class="form-check-input" data-file-explorer-hidden>
                                <span>Show hidden items</span>
                            </label>
                        </div>
                        <div class="vb-file-explorer-actions">
                            <button type="button" class="btn btn-primary btn-sm" data-file-explorer-action="select" disabled>
                                <span data-file-explorer-select-label>Open</span>
                            </button>
                            <button type="button" class="btn btn-secondary btn-sm" data-file-explorer-action="cancel">Cancel</button>
                        </div>
                    </footer>
                </div>
            </div>
        </div>
        <div class="modal-backdrop fade show vb-file-explorer-backdrop"></div>`;
    return layer;
}

function collectElements(layer) {
    const byAction = action => layer.querySelector(`[data-file-explorer-action="${action}"]`);
    return {
        layer,
        title: layer.querySelector('[data-file-explorer-title]'),
        back: byAction('back'),
        forward: byAction('forward'),
        up: byAction('up'),
        refresh: byAction('refresh'),
        select: byAction('select'),
        selectLabel: layer.querySelector('[data-file-explorer-select-label]'),
        addressBar: layer.querySelector('[data-file-explorer-address-bar]'),
        address: layer.querySelector('[data-file-explorer-path]'),
        breadcrumbs: layer.querySelector('[data-file-explorer-breadcrumb]'),
        places: layer.querySelector('[data-file-explorer-places]'),
        hiddenToggle: layer.querySelector('[data-file-explorer-hidden]'),
        search: layer.querySelector('[data-file-explorer-search]'),
        view: layer.querySelector('[data-file-explorer-view]'),
        grid: layer.querySelector('[data-file-explorer-grid]'),
        columns: Array.from(layer.querySelectorAll('[data-file-explorer-column]')),
        list: layer.querySelector('[data-file-explorer-list]'),
        state: layer.querySelector('[data-file-explorer-state]'),
        notice: layer.querySelector('[data-file-explorer-notice]'),
        fileNameLabel: layer.querySelector('[data-file-explorer-filename-label]'),
        fileName: layer.querySelector('[data-file-explorer-filename]'),
        fileNameHint: layer.querySelector('[data-file-explorer-filename-hint]'),
        filter: layer.querySelector('[data-file-explorer-filter]'),
        status: layer.querySelector('[data-file-explorer-status]')
    };
}

function setStaticCopy(elements, state, title) {
    const titles = {
        [FILE_EXPLORER_MODES.FILE]: 'Open',
        [FILE_EXPLORER_MODES.DIRECTORY]: 'Select Folder',
        [FILE_EXPLORER_MODES.ANY]: 'Open'
    };
    elements.title.textContent = cleanString(title) || titles[state.mode];
    elements.fileNameLabel.textContent = state.mode === FILE_EXPLORER_MODES.DIRECTORY
        ? 'Folder:'
        : 'File name:';
    elements.select.dataset.mode = state.mode;
}

function renderFilterSelect(elements, state) {
    const select = elements.filter;
    select.replaceChildren();
    // The "Files of type" control only exists when the caller supplied filters (and never for
    // folder pickers); without it the dialog lists everything, exactly as before.
    const visible = state.mode !== FILE_EXPLORER_MODES.DIRECTORY && state.filters.length > 0;
    select.classList.toggle('d-none', !visible);
    if (!visible) return;
    state.filters.forEach((filter, index) => {
        const option = document.createElement('option');
        option.value = String(index);
        option.textContent = filter.label;
        option.selected = index === state.filterIndex;
        select.appendChild(option);
    });
}

function bindStaticActions(elements, state, handlers) {
    // Only Escape, Cancel, and the X dismiss the dialog. Clicking outside deliberately does
    // nothing: desktop file dialogs stay put, and a text-selection drag that ends on the
    // backdrop must not throw the user's navigation away.
    elements.layer.querySelectorAll('[data-file-explorer-action="cancel"]')
        .forEach(button => button.addEventListener('click', () => handlers.cancel()));
    elements.select.addEventListener('click', handlers.primary);
    elements.back.addEventListener('click', handlers.back);
    elements.forward.addEventListener('click', handlers.forward);
    elements.up.addEventListener('click', handlers.up);
    elements.refresh.addEventListener('click', handlers.refresh);

    // Address bar: breadcrumbs by default; the empty part of the bar (or Ctrl+L / F4) turns it
    // into a text field. Enter navigates, Escape and blur revert.
    elements.addressBar.addEventListener('click', event => {
        if (event.target.closest('.vb-file-explorer-crumb')) return;
        if (state.addressEditing) return;
        enterAddressEdit(elements, state);
    });
    elements.address.addEventListener('keydown', event => {
        if (event.key !== 'Enter') return;
        event.preventDefault();
        const path = elements.address.value.trim();
        exitAddressEdit(elements, state, { focusCrumbs: !path });
        if (path) {
            elements.view.focus({ preventScroll: true });
            handlers.address(path);
        }
    });
    elements.address.addEventListener('blur', () => {
        if (state.addressEditing) exitAddressEdit(elements, state);
    });

    elements.search.addEventListener('input', event => handlers.search(event.target.value));
    elements.hiddenToggle.addEventListener('change', event => handlers.hidden(event.target.checked));
    elements.filter.addEventListener('change', event => handlers.onFilter(Number(event.target.value)));
    elements.fileName.addEventListener('input', () => handlers.fileNameInput());
    elements.fileName.addEventListener('keydown', event => {
        if (event.key !== 'Enter') return;
        event.preventDefault();
        handlers.primary();
    });
    elements.grid.querySelectorAll('[data-file-explorer-sort]').forEach(button => {
        button.addEventListener('click', () => handlers.onSort(button.dataset.fileExplorerSort));
    });
    elements.layer.addEventListener('click', event => {
        const retry = event.target.closest('[data-file-explorer-action="retry"]');
        if (retry) {
            handlers.retry();
            return;
        }
        const loadMore = event.target.closest('[data-file-explorer-action="load-more"]');
        if (loadMore) handlers.loadMore();
    });
}

function bindListInteractions(elements, state, handlers) {
    // Clicking the blank area under the rows clears the highlight, as it does on the desktop.
    elements.view.addEventListener('click', event => {
        if (event.target === elements.view
            || event.target === elements.list
            || event.target === elements.grid
            || event.target === elements.state) {
            if (state.selected) handlers.onSelect(null);
        }
    });

    elements.list.addEventListener('click', event => {
        const row = event.target.closest('[data-file-explorer-entry]');
        if (!row) return;
        const entry = findEntryForRow(state, row);
        if (!entry) return;
        handlers.onSelect(entry);
        row.focus({ preventScroll: true });
    });
    elements.list.addEventListener('dblclick', event => {
        const row = event.target.closest('[data-file-explorer-entry]');
        const entry = row ? findEntryForRow(state, row) : null;
        if (entry) handlers.onOpen(entry);
    });
    elements.list.addEventListener('keydown', event => {
        const row = event.target.closest('[data-file-explorer-entry]');
        if (!row) return;
        const rows = Array.from(elements.list.querySelectorAll('[data-file-explorer-entry]'));
        const index = rows.indexOf(row);
        const entry = findEntryForRow(state, row);
        const moveTo = nextIndex => {
            const next = rows[Math.max(0, Math.min(rows.length - 1, nextIndex))];
            if (!next || next === row) return;
            const nextEntry = findEntryForRow(state, next);
            if (nextEntry) handlers.onSelect(nextEntry);
            next.focus({ preventScroll: false });
        };

        switch (event.key) {
            case 'Enter':
                event.preventDefault();
                if (entry) handlers.onOpen(entry);
                return;
            case ' ':
                event.preventDefault();
                if (entry) handlers.onSelect(entry);
                return;
            case 'ArrowDown':
                event.preventDefault();
                moveTo(index + 1);
                return;
            case 'ArrowUp':
                event.preventDefault();
                moveTo(index - 1);
                return;
            case 'Home':
                event.preventDefault();
                moveTo(0);
                return;
            case 'End':
                event.preventDefault();
                moveTo(rows.length - 1);
                return;
            case 'PageDown':
                event.preventDefault();
                moveTo(index + pageStep(elements, row));
                return;
            case 'PageUp':
                event.preventDefault();
                moveTo(index - pageStep(elements, row));
                return;
            default:
                break;
        }

        if (event.key.length !== 1 || event.ctrlKey || event.metaKey || event.altKey) return;
        event.preventDefault();
        const typeAhead = state.typeAhead;
        if (typeAhead.timer) clearTimeout(typeAhead.timer);
        typeAhead.buffer += event.key;
        typeAhead.timer = setTimeout(() => {
            typeAhead.buffer = '';
            typeAhead.timer = null;
        }, TYPE_AHEAD_RESET_MS);
        const entries = getVisibleEntries(state);
        const target = findFileExplorerTypeAheadIndex(entries, typeAhead.buffer, index);
        if (target >= 0) moveTo(target);
    });
}

function pageStep(elements, row) {
    const rowHeight = row?.offsetHeight || 28;
    const viewHeight = elements.view?.clientHeight || 0;
    return Math.max(1, Math.floor(viewHeight / rowHeight) - 1);
}

function findEntryForRow(state, row) {
    const path = row?.dataset?.path;
    return state.data.entries.find(entry => samePath(entry.path, path)) || null;
}

function handleDialogShortcut(event, elements, state, handlers) {
    const target = event.target;
    if (!(target instanceof Node) || (!elements.layer.contains(target) && target !== document.body)) return;
    const inTextField = target instanceof HTMLElement
        && (target.matches('input, textarea, select') || target.isContentEditable);

    // Handled shortcuts stop here (capture phase) so the focused row's own key handling — e.g.
    // ArrowUp moving the highlight — does not also run for Alt+ArrowUp.
    const consume = action => {
        event.preventDefault();
        event.stopPropagation();
        action();
    };
    if ((event.key === 'l' || event.key === 'L') && (event.ctrlKey || event.metaKey) && !event.altKey) {
        consume(() => enterAddressEdit(elements, state));
        return;
    }
    if (event.key === 'F4' && !event.ctrlKey && !event.altKey && !event.metaKey) {
        consume(() => enterAddressEdit(elements, state));
        return;
    }
    if (event.key === 'F5' && !event.ctrlKey && !event.altKey && !event.metaKey) {
        consume(handlers.refresh);
        return;
    }
    // Text fields keep their own editing shortcuts (Option+Arrow word jumps, Backspace).
    if (inTextField) return;
    if (event.altKey && !event.ctrlKey && !event.metaKey) {
        if (event.key === 'ArrowLeft') consume(handlers.back);
        else if (event.key === 'ArrowRight') consume(handlers.forward);
        else if (event.key === 'ArrowUp') consume(handlers.up);
        return;
    }
    if (event.key === 'Backspace' && !event.ctrlKey && !event.metaKey) {
        // Backspace is Back, as in Explorer; before any history exists it steps Up instead so
        // the key is never dead. Alt+Up remains the explicit Up.
        consume(state.historyIndex > 0 ? handlers.back : handlers.up);
    }
}

function enterAddressEdit(elements, state) {
    if (state.addressEditing) {
        elements.address.focus({ preventScroll: true });
        elements.address.select();
        return;
    }
    state.addressEditing = true;
    elements.breadcrumbs.hidden = true;
    elements.address.hidden = false;
    elements.address.value = state.locationReady
        ? state.data.currentPath
        : state.requestedPath || state.data.currentPath || '';
    elements.address.focus({ preventScroll: true });
    elements.address.select();
}

function exitAddressEdit(elements, state, { focusCrumbs = false } = {}) {
    if (!state.addressEditing) return;
    state.addressEditing = false;
    elements.address.hidden = true;
    elements.breadcrumbs.hidden = false;
    if (focusCrumbs) {
        const crumbs = elements.breadcrumbs.querySelectorAll('.vb-file-explorer-crumb');
        (crumbs[crumbs.length - 1] || elements.view)?.focus({ preventScroll: true });
    }
}

function renderExplorer(elements, state, handlers) {
    elements.search.value = state.searchText;
    elements.search.placeholder = state.data.currentName
        ? `Search ${state.data.currentName}`
        : 'Search';
    renderBreadcrumbs(
        elements,
        state.data.breadcrumbs.length
            ? state.data.breadcrumbs
            : [{ label: state.data.currentName || state.data.currentPath, path: state.data.currentPath }],
        handlers.onNavigate
    );
    renderPlaces(elements, state, handlers.onNavigate);
    renderEntries(elements, state, handlers);
    renderNotice(elements, state);
    updateToolbar(elements, state);
    updateFileNameField(elements, state);
    updateSelectionFooter(elements, state);
    updateStatus(elements, state);
}

function showState(elements, node) {
    elements.state.replaceChildren(node);
    elements.state.classList.remove('d-none');
}

function hideState(elements) {
    elements.state.replaceChildren();
    elements.state.classList.add('d-none');
}

function renderLoading(elements) {
    elements.list.replaceChildren();
    elements.grid.setAttribute('aria-busy', 'true');
    const loading = document.createElement('div');
    loading.className = 'vb-file-explorer-message';
    loading.setAttribute('role', 'status');
    const spinner = document.createElement('span');
    spinner.className = 'spinner-border spinner-border-sm';
    spinner.setAttribute('aria-hidden', 'true');
    const label = document.createElement('span');
    label.textContent = 'Loading…';
    loading.append(spinner, label);
    showState(elements, loading);
    elements.notice.classList.add('d-none');
    elements.status.textContent = 'Loading…';
}

function renderError(elements, state, message, onNavigate) {
    elements.list.replaceChildren();
    // Keep the requested location visible so the address bar can be clicked and corrected, and
    // keep the Places sidebar (at least the Project entry) so there is always a way out.
    renderBreadcrumbs(elements, [{ label: state.requestedPath, path: state.requestedPath }], onNavigate);
    renderPlaces(elements, state, onNavigate);
    elements.grid.setAttribute('aria-busy', 'false');
    const error = document.createElement('div');
    error.className = 'vb-file-explorer-message is-error';
    error.setAttribute('role', 'alert');
    const icon = document.createElement('i');
    icon.className = 'fa-solid fa-triangle-exclamation';
    icon.setAttribute('aria-hidden', 'true');
    const detail = document.createElement('span');
    detail.textContent = String(message || 'This location could not be opened.');
    const retry = document.createElement('button');
    retry.type = 'button';
    retry.className = 'btn btn-sm btn-outline-secondary';
    retry.dataset.fileExplorerAction = 'retry';
    retry.textContent = 'Try again';
    error.append(icon, detail, retry);
    showState(elements, error);
    elements.status.textContent = 'Location unavailable';
}

function renderBreadcrumbs(elements, breadcrumbs, onNavigate) {
    elements.breadcrumbs.replaceChildren();
    breadcrumbs.filter(crumb => crumb?.path).forEach((crumb, index) => {
        if (index > 0) {
            const separator = document.createElement('span');
            separator.className = 'vb-file-explorer-crumb-separator';
            separator.setAttribute('aria-hidden', 'true');
            separator.textContent = '›';
            elements.breadcrumbs.appendChild(separator);
        }

        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'vb-file-explorer-crumb';
        button.dataset.path = crumb.path;
        button.textContent = crumb.label;
        button.title = crumb.path;
        if (index === breadcrumbs.length - 1) button.setAttribute('aria-current', 'location');
        button.addEventListener('click', event => {
            event.stopPropagation();
            onNavigate(crumb.path);
        });
        elements.breadcrumbs.appendChild(button);
    });
    // Keep the newest segment in view when the path is longer than the bar.
    elements.breadcrumbs.scrollLeft = elements.breadcrumbs.scrollWidth;
}

function renderPlaces(elements, state, onNavigate) {
    elements.places.replaceChildren();
    const quickAccess = [];
    if (state.data.defaultPath) {
        quickAccess.push({ label: 'Project', path: state.data.defaultPath, icon: PLACE_ICONS.project });
    }
    state.data.places.forEach(place => {
        if (quickAccess.some(location => samePath(location.path, place.path))) return;
        quickAccess.push({ ...place, icon: PLACE_ICONS[place.kind] || PLACE_ICONS.home });
    });
    const drives = state.data.roots.map(root => ({ ...root, icon: PLACE_ICONS.drive }));
    const windowsHost = [state.data.currentPath, state.data.defaultPath, drives[0]?.path]
        .some(path => isWindowsFileSystemPath(path));

    const groups = [
        { title: 'Quick access', id: 'vb-file-explorer-places-quick', locations: quickAccess },
        { title: windowsHost ? 'This PC' : 'Drives', id: 'vb-file-explorer-places-drives', locations: drives }
    ].filter(group => group.locations.length > 0);

    groups.forEach(group => {
        const section = document.createElement('div');
        section.className = 'vb-file-explorer-place-group';
        const heading = document.createElement('div');
        heading.className = 'vb-file-explorer-place-heading';
        heading.id = group.id;
        heading.textContent = group.title;
        const list = document.createElement('ul');
        list.className = 'vb-file-explorer-place-list';
        list.setAttribute('aria-labelledby', group.id);
        group.locations.forEach(location => {
            const item = document.createElement('li');
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'vb-file-explorer-place';
            const current = samePath(location.path, state.data.currentPath);
            button.classList.toggle('is-current', current);
            if (current) button.setAttribute('aria-current', 'location');
            button.dataset.path = location.path;
            button.title = location.path;
            const icon = document.createElement('i');
            icon.className = location.icon;
            icon.setAttribute('aria-hidden', 'true');
            const label = document.createElement('span');
            label.textContent = location.label;
            button.append(icon, label);
            button.addEventListener('click', () => onNavigate(location.path));
            item.appendChild(button);
            list.appendChild(item);
        });
        section.append(heading, list);
        elements.places.appendChild(section);
    });

    if (groups.length === 0) {
        const empty = document.createElement('small');
        empty.className = 'vb-file-explorer-place-empty';
        empty.textContent = 'No places';
        elements.places.appendChild(empty);
    }
}

function renderEntries(elements, state, handlers) {
    elements.list.replaceChildren();
    elements.grid.setAttribute('aria-busy', 'false');
    const entries = getVisibleEntries(state);
    if (entries.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'vb-file-explorer-message';
        const filtered = Boolean(state.searchText) || Boolean(activeFilter(state)?.extensions?.length);
        empty.textContent = state.data.entries.length === 0
            ? (state.searchText ? 'No items match your search.' : 'This folder is empty.')
            : filtered
                ? 'No items match the current filter.'
                : 'This folder is empty.';
        showState(elements, empty);
        updateStatus(elements, state);
        return;
    }
    hideState(elements);

    const hasVisibleSelection = entries.some(entry => samePath(state.selected?.path, entry.path));
    const fragment = document.createDocumentFragment();
    entries.forEach((entry, index) => {
        const row = document.createElement('div');
        row.className = 'vb-file-explorer-entry';
        const selected = samePath(state.selected?.path, entry.path);
        row.tabIndex = selected || (!hasVisibleSelection && index === 0) ? 0 : -1;
        row.setAttribute('role', 'row');
        row.setAttribute('aria-selected', selected ? 'true' : 'false');
        row.dataset.fileExplorerEntry = '';
        row.dataset.kind = entry.kind;
        row.dataset.path = entry.path;
        row.classList.toggle('is-selected', selected);
        row.classList.toggle('is-hidden', entry.isHidden);
        row.classList.toggle('is-link-blocked', entry.isSymbolicLink);
        // Folder pickers still list files for orientation, but mute them: they cannot be chosen.
        row.classList.toggle('is-muted', state.mode === FILE_EXPLORER_MODES.DIRECTORY
            && entry.kind === FILE_EXPLORER_MODES.FILE);
        if (entry.isSymbolicLink) row.setAttribute('aria-disabled', 'true');
        row.title = entry.isSymbolicLink
            ? `${entry.path} — linked items cannot be opened or selected`
            : entry.path;

        const nameCell = document.createElement('div');
        nameCell.className = 'vb-file-explorer-cell is-name';
        nameCell.setAttribute('role', 'gridcell');
        const name = document.createElement('span');
        name.className = 'vb-file-explorer-entry-name';
        name.textContent = entry.name;
        nameCell.append(createEntryVisual(entry), name);
        if (entry.isSymbolicLink) {
            const link = document.createElement('i');
            link.className = 'fa-solid fa-link vb-file-explorer-link-badge';
            link.title = 'Symbolic link';
            link.setAttribute('aria-label', 'Symbolic link');
            nameCell.appendChild(link);
        }

        const modified = document.createElement('div');
        modified.className = 'vb-file-explorer-cell is-modified';
        modified.setAttribute('role', 'gridcell');
        const time = document.createElement('time');
        time.textContent = formatFileExplorerDate(entry.lastModifiedUtc);
        if (entry.lastModifiedUtc) time.dateTime = entry.lastModifiedUtc;
        modified.appendChild(time);

        const type = document.createElement('div');
        type.className = 'vb-file-explorer-cell is-type';
        type.setAttribute('role', 'gridcell');
        type.textContent = getFileExplorerTypeLabel(entry);

        const size = document.createElement('div');
        size.className = 'vb-file-explorer-cell is-size';
        size.setAttribute('role', 'gridcell');
        size.textContent = entry.kind === FILE_EXPLORER_MODES.DIRECTORY
            ? ''
            : formatFileExplorerSize(entry.size);

        row.append(nameCell, modified, type, size);
        fragment.appendChild(row);
    });
    elements.list.appendChild(fragment);
    updateStatus(elements, state);
}

function updateEntrySelection(elements, state) {
    const rows = Array.from(elements.list.querySelectorAll('[data-file-explorer-entry]'));
    const hasSelection = rows.some(row => samePath(row.dataset.path, state.selected?.path));
    rows.forEach((row, index) => {
        const selected = samePath(row.dataset.path, state.selected?.path);
        row.classList.toggle('is-selected', selected);
        row.setAttribute('aria-selected', selected ? 'true' : 'false');
        row.tabIndex = selected || (!hasSelection && index === 0) ? 0 : -1;
    });
}

function updateSortHeaders(elements, state) {
    const { key, direction } = normalizeFileExplorerSort(state.sort);
    elements.columns.forEach(column => {
        const active = column.dataset.fileExplorerColumn === key;
        column.classList.toggle('is-sorted', active);
        column.setAttribute('aria-sort', active
            ? (direction === 'desc' ? 'descending' : 'ascending')
            : 'none');
        const icon = column.querySelector('.vb-file-explorer-sort-icon');
        if (icon) {
            icon.classList.toggle('fa-chevron-up', direction !== 'desc');
            icon.classList.toggle('fa-chevron-down', direction === 'desc');
        }
    });
}

function createEntryVisual(entry) {
    const wrapper = document.createElement('span');
    wrapper.className = 'vb-file-explorer-entry-icon';
    if (entry.kind === FILE_EXPLORER_MODES.DIRECTORY) {
        const folder = document.createElement('i');
        folder.className = 'fa-solid fa-folder';
        folder.setAttribute('aria-hidden', 'true');
        wrapper.classList.add('is-folder');
        wrapper.appendChild(folder);
        return wrapper;
    }

    const visual = getFileTypeVisual(entry.name);
    const image = document.createElement('img');
    image.alt = '';
    image.loading = 'lazy';
    image.src = typeof window.__viberails_asset_url__ === 'function'
        ? window.__viberails_asset_url__(visual.iconPath)
        : visual.iconPath;
    wrapper.appendChild(image);
    return wrapper;
}

function renderNotice(elements, state) {
    elements.notice.replaceChildren();
    elements.notice.classList.remove('is-error');
    const searchPending = cleanString(state.searchText) !== cleanString(state.data.search);
    const visible = searchPending
        || state.data.truncated
        || Boolean(state.loadMoreError);
    elements.notice.classList.toggle('d-none', !visible);
    if (!visible) return;

    const icon = document.createElement('i');
    icon.className = state.loadMoreError
        ? 'fa-solid fa-triangle-exclamation'
        : searchPending
            ? 'fa-solid fa-spinner fa-spin'
            : 'fa-solid fa-circle-info';
    icon.setAttribute('aria-hidden', 'true');
    const text = document.createElement('span');
    if (state.loadMoreError) {
        elements.notice.classList.add('is-error');
        text.textContent = `Could not load more items: ${state.loadMoreError}`;
    } else if (searchPending) {
        text.textContent = 'Searching the whole folder…';
    } else if (state.data.nextCursor) {
        const loaded = state.data.entries.length.toLocaleString();
        const total = state.data.totalCount?.toLocaleString();
        text.textContent = total
            ? `${loaded} of ${total} ${state.data.search ? 'matches' : 'items'} loaded.`
            : `More ${state.data.search ? 'matches are' : 'items are'} available.`;
    } else {
        text.textContent = 'Only part of this folder was returned by the server.';
    }
    elements.notice.append(icon, text);

    if ((state.data.nextCursor || state.loadMoreError) && !searchPending) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'btn btn-sm btn-outline-secondary';
        button.dataset.fileExplorerAction = 'load-more';
        button.disabled = state.loadingMore;
        button.innerHTML = state.loadingMore
            ? '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Loading…'
            : state.loadMoreError
                ? '<i class="fa-solid fa-rotate-right" aria-hidden="true"></i> Retry'
                : 'Load more';
        elements.notice.appendChild(button);
    }
}

function updateToolbar(elements, state) {
    elements.back.disabled = state.historyIndex <= 0;
    elements.forward.disabled = state.historyIndex < 0 || state.historyIndex >= state.history.length - 1;
    elements.up.disabled = !state.data.parentPath;
    if (!state.addressEditing) {
        elements.address.value = state.locationReady
            ? state.data.currentPath
            : state.requestedPath || state.data.currentPath || '';
    }
}

function setFileNameValue(elements, state, value) {
    state.fileNameMirror = value;
    elements.fileName.value = value;
}

function setFileNameHint(elements, message) {
    elements.fileNameHint.textContent = message || '';
    elements.fileNameHint.classList.toggle('d-none', !message);
    elements.fileName.classList.toggle('is-invalid', Boolean(message));
    if (message) elements.fileName.setAttribute('aria-describedby', 'vb-file-explorer-filename-hint');
    else elements.fileName.removeAttribute('aria-describedby');
    elements.fileNameHint.id = 'vb-file-explorer-filename-hint';
}

/**
 * The file-name box mirrors the highlighted item: a file's name in Open dialogs, a folder's full
 * path (or the folder being viewed) in Select Folder dialogs. Text the user typed is left alone
 * until they highlight something else.
 */
function updateFileNameField(elements, state) {
    const selected = state.selected;
    if (state.mode === FILE_EXPLORER_MODES.DIRECTORY) {
        const value = selected && !selected.isSymbolicLink && selected.kind === FILE_EXPLORER_MODES.DIRECTORY
            ? selected.path
            : state.locationReady ? state.data.currentPath : (state.requestedPath || '');
        setFileNameValue(elements, state, value);
        return;
    }
    if (selected && !selected.isSymbolicLink && selected.kind === FILE_EXPLORER_MODES.FILE) {
        setFileNameValue(elements, state, selected.name);
    }
}

function updateSelectionFooter(elements, state) {
    const selected = state.selected;
    const selectedUsable = Boolean(selected && !selected.isSymbolicLink);
    const typed = elements.fileName.value.trim();
    const typedNew = Boolean(typed) && typed !== state.fileNameMirror.trim();
    const ready = !state.loading && state.locationReady && Boolean(state.data.currentPath);

    let label = 'Open';
    let enabled = false;
    if (state.mode === FILE_EXPLORER_MODES.FILE) {
        enabled = ready && (selectedUsable || typedNew);
    } else if (state.mode === FILE_EXPLORER_MODES.DIRECTORY) {
        label = 'Select Folder';
        enabled = ready;
    } else {
        label = selectedUsable && selected.kind === FILE_EXPLORER_MODES.FILE ? 'Open' : 'Select Folder';
        enabled = ready;
    }
    elements.selectLabel.textContent = label;
    elements.select.disabled = !enabled;
}

function updateStatus(elements, state) {
    const selected = state.selected;
    if (selected) {
        if (selected.isSymbolicLink) {
            elements.status.textContent = `${selected.name} is a link and cannot be opened or selected`;
            return;
        }
        elements.status.textContent = selected.kind === FILE_EXPLORER_MODES.DIRECTORY
            ? '1 folder selected'
            : '1 file selected';
        return;
    }

    const filter = activeFilter(state);
    elements.status.textContent = formatFileExplorerStatusText({
        visible: getVisibleEntries(state).length,
        loaded: state.data.entries.length,
        total: state.data.totalCount,
        truncated: state.data.truncated,
        search: state.data.search,
        searchPending: cleanString(state.searchText) !== cleanString(state.data.search),
        filterLabel: filter?.extensions?.length ? filter.shortLabel || filter.label : ''
    });
}

/**
 * The footer count line. Counts are of the loaded page; a type filter that hides part of it is
 * named ("23 of 35 items (Python files)") so the smaller number is explained.
 */
export function formatFileExplorerStatusText({
    visible = 0,
    loaded = 0,
    total = null,
    truncated = false,
    search = '',
    searchPending = false,
    filterLabel = ''
} = {}) {
    const noun = search ? 'matches' : 'items';
    if (searchPending) return `${visible.toLocaleString()} ${noun} · searching…`;
    if (visible !== loaded) {
        // A client-side type filter (or a name filter) is hiding part of the loaded page.
        const denominator = total != null && total > loaded ? total : loaded;
        const suffix = cleanString(filterLabel) ? ` (${cleanString(filterLabel)})` : ' shown';
        return `${visible.toLocaleString()} of ${denominator.toLocaleString()} ${noun}${suffix}`;
    }
    if (total != null && total > loaded) return `${loaded.toLocaleString()} of ${total.toLocaleString()} ${noun}`;
    if (truncated && total == null) return `${loaded.toLocaleString()}+ ${noun}`;
    return `${visible.toLocaleString()} ${visible === 1 && !search ? 'item' : noun}`;
}

function trapFocus(event, layer) {
    const focusable = Array.from(layer.querySelectorAll(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
    )).filter(element => !element.closest('[inert]')
        && !element.hidden
        && (element.offsetParent !== null || element === document.activeElement));
    if (focusable.length === 0) return;

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (!layer.contains(document.activeElement)
        || !focusable.includes(document.activeElement)) {
        event.preventDefault();
        (event.shiftKey ? last : first).focus();
        return;
    }
    if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
    }
}
