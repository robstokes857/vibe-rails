import { getFileTypeVisual } from './file-type-icons.js';
import { isConfirmDialogOpen } from './utils.js';

export const FILE_EXPLORER_MODES = Object.freeze({
    FILE: 'file',
    DIRECTORY: 'directory',
    ANY: 'any'
});

const ENTRIES_ENDPOINT = '/api/v1/filesystem/entries';
const FILE_EXPLORER_PAGE_SIZE = 500;
const SEARCH_DEBOUNCE_MS = 250;
const CANCELED_RESULT = Object.freeze({
    canceled: true,
    path: null,
    kind: null,
    name: null
});

let activeExplorer = null;

// Register before view-specific modal listeners. Some existing forms listen for Escape on
// window/capture, which fires before document/capture; this early guard gives the top-most file
// explorer first refusal without requiring every caller to understand its nested lifecycle.
if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
    window.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || !activeExplorer || isConfirmDialogOpen()) return;
        event.preventDefault();
        event.stopImmediatePropagation();
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

export function filterAndSortFileExplorerEntries(entries = [], searchText = '') {
    const query = String(searchText || '').trim().toLocaleLowerCase();
    const filtered = (Array.isArray(entries) ? entries : []).filter(entry => {
        if (!query) return true;
        return String(entry?.name || '').toLocaleLowerCase().includes(query);
    });

    return filtered.slice().sort((left, right) => {
        const leftKind = normalizeFileExplorerKind(left?.kind);
        const rightKind = normalizeFileExplorerKind(right?.kind);
        if (leftKind !== rightKind) {
            return leftKind === FILE_EXPLORER_MODES.DIRECTORY ? -1 : 1;
        }
        return String(left?.name || '').localeCompare(String(right?.name || ''), undefined, {
            numeric: true,
            sensitivity: 'base'
        });
    });
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
 * Opens the server-backed filesystem explorer.
 *
 * @param {object} app VibeRails application instance.
 * @param {object} options
 * @param {'file'|'directory'|'any'} [options.mode='any'] Selectable entry type.
 * @param {string} [options.initialPath] Initial directory; defaults to project root/launch directory.
 * @param {string} [options.title] Dialog title.
 * @param {boolean} [options.includeHidden=false] Initial hidden-file visibility.
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
    const initialPath = getFileExplorerInitialPath(app, options.initialPath);
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
        includeHidden: Boolean(options.includeHidden),
        data: normalizeFileExplorerPayload({}, initialPath),
        selected: null,
        history: [],
        historyIndex: -1,
        searchText: '',
        searchTimer: null,
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

    setStaticCopy(elements, mode, options.title);
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
            if (layer.isConnected) layer.remove();
            dispose({ restoreFocus });
            resolve(result);
        };

        const cancel = ({ restoreFocus = true } = {}) => settle(
            createCanceledFileExplorerResult(),
            { restoreFocus }
        );
        activeExplorer = { layer, cancel };

        const selectEntry = entry => {
            if (state.loading
                || !entry
                || entry.isSymbolicLink
                || !canSelectFileExplorerKind(mode, entry.kind)) return;
            settle(createFileExplorerResult(entry));
        };

        const selectCurrentDirectory = () => {
            if (state.loading
                || !state.locationReady
                || !canSelectFileExplorerKind(mode, FILE_EXPLORER_MODES.DIRECTORY)) return;
            selectEntry({
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

        const loadPath = async (
            path,
            {
                historyMode = 'push',
                targetHistoryIndex = null,
                append = false,
                cursor = null
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
            const restoreNavigationFocus = [elements.list, elements.breadcrumbs, elements.roots]
                .some(container => container.contains(activeElement));
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
                state.selected = null;
                state.loading = true;
                state.locationReady = false;
                renderLoading(elements);
                if (restoreNavigationFocus) {
                    elements.search.focus({ preventScroll: true });
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
                if (!append) state.selected = null;
                state.loading = false;
                state.loadingMore = false;
                state.locationReady = true;
                if (!append) {
                    state.retryNavigation = null;
                    applyHistory(normalized.currentPath, historyMode, targetHistoryIndex);
                }
                renderExplorer(elements, state, {
                    onSelect: entry => {
                        state.selected = entry;
                        updateEntrySelection(elements, state);
                        updateSelectionFooter(elements, state);
                    },
                    onOpen: entry => {
                        if (entry.isSymbolicLink) {
                            state.selected = entry;
                            updateEntrySelection(elements, state);
                            updateSelectionFooter(elements, state);
                            return;
                        }
                        if (entry.kind === FILE_EXPLORER_MODES.DIRECTORY) {
                            void loadPath(entry.path, { historyMode: 'push' });
                        } else {
                            selectEntry(entry);
                        }
                    },
                    onNavigate: nextPath => void loadPath(nextPath, { historyMode: 'push' })
                });
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
                        ) || elements.search;
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
                    state.loading = false;
                    renderError(elements, message);
                }
                if (!append && restoreNavigationFocus) {
                    requestAnimationFrame(() => {
                        if (state.settled || requestId !== state.requestId || !layer.isConnected) return;
                        const focusTarget = elements.list.querySelector('[data-file-explorer-action="retry"]')
                            || elements.address;
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

        const handlers = {
            onSelect: entry => {
                state.selected = entry;
                updateEntrySelection(elements, state);
                updateSelectionFooter(elements, state);
            },
            onOpen: entry => {
                if (entry.isSymbolicLink) {
                    state.selected = entry;
                    updateEntrySelection(elements, state);
                    updateSelectionFooter(elements, state);
                    return;
                }
                if (entry.kind === FILE_EXPLORER_MODES.DIRECTORY) {
                    void loadPath(entry.path, { historyMode: 'push' });
                } else {
                    selectEntry(entry);
                }
            }
        };

        bindStaticActions(elements, {
            cancel,
            select: () => selectEntry(state.selected),
            selectCurrentDirectory,
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
                if (state.data.parentPath) void loadPath(state.data.parentPath, { historyMode: 'push' });
            },
            home: () => {
                if (state.data.defaultPath) void loadPath(state.data.defaultPath, { historyMode: 'push' });
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
            address: path => void loadPath(path, { historyMode: 'push' }),
            search: value => {
                state.searchText = value;
                if (state.selected && !getVisibleEntries(state)
                    .some(entry => samePath(entry.path, state.selected.path))) {
                    state.selected = null;
                }
                renderEntries(elements, state, handlers);
                renderNotice(elements, state);
                updateSelectionFooter(elements, state);
                updateStatus(elements, state);
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
            }
        });

        state.keydownHandler = event => {
            if (isConfirmDialogOpen()) return;
            if (state.settled) return;
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopImmediatePropagation();
                cancel();
                return;
            }
            if (event.key === 'Tab') trapFocus(event, layer);
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
        requestAnimationFrame(() => elements.search.focus());
        void loadPath(initialPath, { historyMode: 'replace' });
    });
}

function cleanString(value) {
    return typeof value === 'string' ? value.trim() : '';
}

function stringValue(value) {
    return typeof value === 'string' ? value : '';
}

function normalizeTotalCount(value) {
    if (value == null || value === '') return null;
    const count = Number(value);
    return Number.isSafeInteger(count) && count >= 0 ? count : null;
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

function getVisibleEntries(state) {
    const activeSearch = cleanString(state.searchText);
    const serverSearch = cleanString(state.data.search);
    return filterAndSortFileExplorerEntries(
        state.data.entries,
        activeSearch === serverSearch ? '' : activeSearch
    );
}

function createExplorerLayer() {
    const layer = document.createElement('div');
    layer.className = 'vb-file-explorer-layer';
    layer.innerHTML = `
        <div class="modal fade show d-block vb-file-explorer-modal" tabindex="-1"
             role="dialog" aria-modal="true" aria-labelledby="vb-file-explorer-title"
             data-file-explorer-modal>
            <div class="modal-dialog modal-xl modal-dialog-centered">
                <div class="modal-content">
                    <header class="modal-header vb-file-explorer-header">
                        <div class="vb-file-explorer-heading">
                            <span class="vb-file-explorer-heading-icon" aria-hidden="true">
                                <i class="fa-solid fa-folder-tree"></i>
                            </span>
                            <div>
                                <h5 class="modal-title" id="vb-file-explorer-title" data-file-explorer-title></h5>
                                <p data-file-explorer-subtitle></p>
                            </div>
                        </div>
                        <button type="button" class="btn-close" data-file-explorer-action="cancel"
                                aria-label="Close file explorer"></button>
                    </header>

                    <div class="vb-file-explorer-toolbar" aria-label="File explorer navigation">
                        <div class="vb-file-explorer-nav-buttons" role="group" aria-label="Navigation history">
                            <button type="button" class="btn btn-sm btn-outline-secondary" data-file-explorer-action="back"
                                    title="Back" aria-label="Back">
                                <i class="fa-solid fa-arrow-left" aria-hidden="true"></i>
                            </button>
                            <button type="button" class="btn btn-sm btn-outline-secondary" data-file-explorer-action="forward"
                                    title="Forward" aria-label="Forward">
                                <i class="fa-solid fa-arrow-right" aria-hidden="true"></i>
                            </button>
                            <button type="button" class="btn btn-sm btn-outline-secondary" data-file-explorer-action="up"
                                    title="Parent folder" aria-label="Parent folder">
                                <i class="fa-solid fa-arrow-up" aria-hidden="true"></i>
                            </button>
                            <button type="button" class="btn btn-sm btn-outline-secondary" data-file-explorer-action="home"
                                    title="Project root" aria-label="Project root">
                                <i class="fa-solid fa-house" aria-hidden="true"></i>
                            </button>
                            <button type="button" class="btn btn-sm btn-outline-secondary" data-file-explorer-action="refresh"
                                    title="Refresh" aria-label="Refresh">
                                <i class="fa-solid fa-rotate-right" aria-hidden="true"></i>
                            </button>
                        </div>
                        <form class="vb-file-explorer-address-form" data-file-explorer-address-form>
                            <i class="fa-solid fa-location-dot" aria-hidden="true"></i>
                            <label class="visually-hidden" for="vb-file-explorer-address">Location</label>
                            <input id="vb-file-explorer-address" type="text" autocomplete="off" spellcheck="false"
                                   data-file-explorer-path aria-label="Filesystem location">
                            <button type="submit" class="btn btn-sm btn-primary" aria-label="Open typed location">Go</button>
                        </form>
                    </div>

                    <nav class="vb-file-explorer-breadcrumbs" aria-label="Current path"
                         data-file-explorer-breadcrumb></nav>

                    <div class="modal-body vb-file-explorer-body">
                        <aside class="vb-file-explorer-rail" aria-label="Quick locations">
                            <div class="vb-file-explorer-rail-title">Locations</div>
                            <div class="vb-file-explorer-roots" data-file-explorer-roots></div>
                            <label class="vb-file-explorer-hidden-toggle">
                                <input type="checkbox" class="form-check-input" data-file-explorer-hidden>
                                <span>Show hidden items</span>
                            </label>
                        </aside>

                        <section class="vb-file-explorer-workspace" aria-label="Directory contents">
                            <div class="vb-file-explorer-workspace-head">
                                <div class="vb-file-explorer-current">
                                    <span class="vb-file-explorer-current-icon" aria-hidden="true">
                                        <i class="fa-regular fa-folder-open"></i>
                                    </span>
                                    <div>
                                        <strong data-file-explorer-current-name>Location</strong>
                                        <small data-file-explorer-current-path></small>
                                    </div>
                                </div>
                                <label class="vb-file-explorer-search">
                                    <i class="fa-solid fa-magnifying-glass" aria-hidden="true"></i>
                                    <span class="visually-hidden">Filter this folder</span>
                                    <input type="search" placeholder="Filter this folder" autocomplete="off" maxlength="256"
                                           data-file-explorer-search>
                                </label>
                            </div>

                            <div class="vb-file-explorer-columns" aria-hidden="true">
                                <span>Name</span><span>Type</span><span>Size</span><span>Modified</span>
                            </div>
                            <div class="vb-file-explorer-list" role="listbox" aria-label="Files and folders"
                                 data-file-explorer-list></div>
                            <div class="vb-file-explorer-notice d-none" aria-live="polite"
                                 data-file-explorer-notice></div>
                        </section>
                    </div>

                    <footer class="modal-footer vb-file-explorer-footer">
                        <div class="vb-file-explorer-status" aria-live="polite" data-file-explorer-status>
                            Choose an item
                        </div>
                        <div class="vb-file-explorer-actions">
                            <button type="button" class="btn btn-outline-primary d-none"
                                    data-file-explorer-action="select-current">
                                <i class="fa-regular fa-folder-open" aria-hidden="true"></i>
                                <span>Select This Folder</span>
                            </button>
                            <button type="button" class="btn btn-secondary" data-file-explorer-action="cancel">Cancel</button>
                            <button type="button" class="btn btn-primary" data-file-explorer-action="select" disabled>
                                <i class="fa-solid fa-check" aria-hidden="true"></i>
                                <span data-file-explorer-select-label>Select</span>
                            </button>
                        </div>
                    </footer>
                </div>
            </div>
        </div>
        <div class="modal-backdrop fade show vb-file-explorer-backdrop"
             data-file-explorer-action="cancel"></div>`;
    return layer;
}

function collectElements(layer) {
    const byAction = action => layer.querySelector(`[data-file-explorer-action="${action}"]`);
    return {
        layer,
        modal: layer.querySelector('[data-file-explorer-modal]'),
        title: layer.querySelector('[data-file-explorer-title]'),
        subtitle: layer.querySelector('[data-file-explorer-subtitle]'),
        back: byAction('back'),
        forward: byAction('forward'),
        up: byAction('up'),
        home: byAction('home'),
        refresh: byAction('refresh'),
        select: byAction('select'),
        selectCurrent: byAction('select-current'),
        selectLabel: layer.querySelector('[data-file-explorer-select-label]'),
        addressForm: layer.querySelector('[data-file-explorer-address-form]'),
        address: layer.querySelector('[data-file-explorer-path]'),
        breadcrumbs: layer.querySelector('[data-file-explorer-breadcrumb]'),
        roots: layer.querySelector('[data-file-explorer-roots]'),
        hiddenToggle: layer.querySelector('[data-file-explorer-hidden]'),
        search: layer.querySelector('[data-file-explorer-search]'),
        currentName: layer.querySelector('[data-file-explorer-current-name]'),
        currentPath: layer.querySelector('[data-file-explorer-current-path]'),
        list: layer.querySelector('[data-file-explorer-list]'),
        notice: layer.querySelector('[data-file-explorer-notice]'),
        status: layer.querySelector('[data-file-explorer-status]'),
        retry: null
    };
}

function setStaticCopy(elements, mode, title) {
    const titles = {
        [FILE_EXPLORER_MODES.FILE]: 'Choose a File',
        [FILE_EXPLORER_MODES.DIRECTORY]: 'Choose a Folder',
        [FILE_EXPLORER_MODES.ANY]: 'Choose a File or Folder'
    };
    const subtitles = {
        [FILE_EXPLORER_MODES.FILE]: 'Browse the local machine and select a file.',
        [FILE_EXPLORER_MODES.DIRECTORY]: 'Browse the local machine and select a folder.',
        [FILE_EXPLORER_MODES.ANY]: 'Browse the local machine and select a file or folder.'
    };
    elements.title.textContent = cleanString(title) || titles[mode];
    elements.subtitle.textContent = subtitles[mode];
    elements.selectCurrent.classList.toggle(
        'd-none',
        !canSelectFileExplorerKind(mode, FILE_EXPLORER_MODES.DIRECTORY)
    );
}

function bindStaticActions(elements, handlers) {
    elements.layer.querySelectorAll('[data-file-explorer-action="cancel"]')
        .forEach(button => button.addEventListener('click', () => handlers.cancel()));
    elements.modal.addEventListener('click', event => {
        if (event.target === elements.modal) handlers.cancel();
    });
    elements.select.addEventListener('click', handlers.select);
    elements.selectCurrent.addEventListener('click', handlers.selectCurrentDirectory);
    elements.back.addEventListener('click', handlers.back);
    elements.forward.addEventListener('click', handlers.forward);
    elements.up.addEventListener('click', handlers.up);
    elements.home.addEventListener('click', handlers.home);
    elements.refresh.addEventListener('click', handlers.refresh);
    elements.addressForm.addEventListener('submit', event => {
        event.preventDefault();
        const path = elements.address.value;
        if (path.trim()) handlers.address(path.trim());
    });
    elements.search.addEventListener('input', event => handlers.search(event.target.value));
    elements.hiddenToggle.addEventListener('change', event => handlers.hidden(event.target.checked));
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

function renderExplorer(elements, state, handlers) {
    elements.address.value = state.data.currentPath;
    elements.currentName.textContent = state.data.currentName || 'Location';
    elements.currentPath.textContent = state.data.currentPath;
    elements.currentPath.title = state.data.currentPath;
    elements.search.value = state.searchText;
    renderBreadcrumbs(elements, state, handlers.onNavigate);
    renderRoots(elements, state, handlers.onNavigate);
    renderEntries(elements, state, handlers);
    renderNotice(elements, state);
    updateToolbar(elements, state);
    updateSelectionFooter(elements, state);
    updateStatus(elements, state);
}

function renderLoading(elements) {
    elements.list.replaceChildren();
    elements.list.setAttribute('aria-busy', 'true');
    const loading = document.createElement('div');
    loading.className = 'vb-file-explorer-state';
    loading.setAttribute('role', 'status');

    const spinner = document.createElement('span');
    spinner.className = 'spinner-border spinner-border-sm';
    spinner.setAttribute('aria-hidden', 'true');
    const label = document.createElement('strong');
    label.textContent = 'Opening location…';
    const hint = document.createElement('small');
    hint.textContent = 'Reading files from the local VibeRails server';
    loading.append(spinner, label, hint);
    elements.list.appendChild(loading);
    elements.notice.classList.add('d-none');
    elements.status.textContent = 'Loading…';
}

function renderError(elements, message) {
    elements.list.replaceChildren();
    elements.list.setAttribute('aria-busy', 'false');
    const error = document.createElement('div');
    error.className = 'vb-file-explorer-state vb-file-explorer-state-error';
    error.setAttribute('role', 'alert');

    const icon = document.createElement('i');
    icon.className = 'fa-solid fa-triangle-exclamation';
    icon.setAttribute('aria-hidden', 'true');
    const title = document.createElement('strong');
    title.textContent = 'Could not open this location';
    const detail = document.createElement('small');
    detail.textContent = String(message || 'The filesystem request failed.');
    const retry = document.createElement('button');
    retry.type = 'button';
    retry.className = 'btn btn-sm btn-outline-primary';
    retry.dataset.fileExplorerAction = 'retry';
    retry.textContent = 'Try Again';
    error.append(icon, title, detail, retry);
    elements.list.appendChild(error);
    elements.status.textContent = 'Location unavailable';
}

function renderBreadcrumbs(elements, state, onNavigate) {
    elements.breadcrumbs.replaceChildren();
    const breadcrumbs = state.data.breadcrumbs.length
        ? state.data.breadcrumbs
        : [{ label: state.data.currentName || state.data.currentPath, path: state.data.currentPath }];

    breadcrumbs.forEach((crumb, index) => {
        if (index > 0) {
            const separator = document.createElement('i');
            separator.className = 'fa-solid fa-chevron-right';
            separator.setAttribute('aria-hidden', 'true');
            elements.breadcrumbs.appendChild(separator);
        }

        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'vb-file-explorer-crumb';
        button.dataset.path = crumb.path;
        button.textContent = crumb.label;
        button.title = crumb.path;
        if (index === breadcrumbs.length - 1) button.setAttribute('aria-current', 'page');
        button.addEventListener('click', () => onNavigate(crumb.path));
        elements.breadcrumbs.appendChild(button);
    });
}

function renderRoots(elements, state, onNavigate) {
    elements.roots.replaceChildren();
    const locations = [];
    if (state.data.defaultPath) {
        locations.push({ label: 'Project', path: state.data.defaultPath, icon: 'fa-solid fa-code-branch' });
    }
    state.data.roots.forEach((root, index) => {
        if (locations.some(location => samePath(location.path, root.path))) return;
        locations.push({
            ...root,
            icon: index === 0 ? 'fa-solid fa-hard-drive' : 'fa-regular fa-folder'
        });
    });

    locations.forEach(location => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'vb-file-explorer-root';
        button.classList.toggle('is-current', samePath(location.path, state.data.currentPath));
        button.dataset.path = location.path;
        button.title = location.path;

        const icon = document.createElement('i');
        icon.className = location.icon;
        icon.setAttribute('aria-hidden', 'true');
        const label = document.createElement('span');
        label.textContent = location.label;
        button.append(icon, label);
        button.addEventListener('click', () => onNavigate(location.path));
        elements.roots.appendChild(button);
    });

    if (locations.length === 0) {
        const empty = document.createElement('small');
        empty.className = 'text-muted';
        empty.textContent = 'No quick locations';
        elements.roots.appendChild(empty);
    }
}

function renderEntries(elements, state, handlers) {
    elements.list.replaceChildren();
    elements.list.setAttribute('aria-busy', 'false');
    const entries = getVisibleEntries(state);
    if (entries.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'vb-file-explorer-state';
        const icon = document.createElement('i');
        icon.className = state.searchText ? 'fa-solid fa-filter-circle-xmark' : 'fa-regular fa-folder-open';
        icon.setAttribute('aria-hidden', 'true');
        const title = document.createElement('strong');
        title.textContent = state.searchText ? 'No matching items' : 'This folder is empty';
        const hint = document.createElement('small');
        hint.textContent = state.searchText
            ? 'Try a different filter.'
            : state.includeHidden
                ? 'There are no items to show.'
                : 'Turn on hidden items if you expected something here.';
        empty.append(icon, title, hint);
        elements.list.appendChild(empty);
        updateStatus(elements, state);
        return;
    }

    const hasVisibleSelection = entries.some(entry => samePath(state.selected?.path, entry.path));
    entries.forEach((entry, index) => {
        const row = document.createElement('div');
        row.className = 'vb-file-explorer-entry';
        const selected = samePath(state.selected?.path, entry.path);
        row.tabIndex = selected || (!hasVisibleSelection && index === 0) ? 0 : -1;
        row.setAttribute('role', 'option');
        row.setAttribute('aria-selected', selected ? 'true' : 'false');
        row.dataset.fileExplorerEntry = '';
        row.dataset.kind = entry.kind;
        row.dataset.path = entry.path;
        row.classList.toggle('is-selected', selected);
        row.classList.toggle('is-hidden', entry.isHidden);
        row.classList.toggle('is-link-blocked', entry.isSymbolicLink);
        if (entry.isSymbolicLink) row.setAttribute('aria-disabled', 'true');
        row.title = entry.isSymbolicLink
            ? `${entry.path} — linked items cannot be opened or selected`
            : entry.path;

        const visual = createEntryVisual(entry);
        const copy = document.createElement('div');
        copy.className = 'vb-file-explorer-entry-copy';
        const name = document.createElement('strong');
        name.textContent = entry.name;
        const path = document.createElement('small');
        path.textContent = entry.path;
        copy.append(name, path);

        const type = document.createElement('span');
        type.className = 'vb-file-explorer-entry-type';
        type.textContent = entry.kind === FILE_EXPLORER_MODES.DIRECTORY ? 'Folder' : visual.typeName;
        if (entry.isSymbolicLink) {
            const link = document.createElement('i');
            link.className = 'fa-solid fa-link vb-file-explorer-link-badge';
            link.title = 'Symbolic link';
            link.setAttribute('aria-label', 'Symbolic link');
            type.prepend(link);
        }

        const size = document.createElement('span');
        size.className = 'vb-file-explorer-entry-size';
        size.textContent = entry.kind === FILE_EXPLORER_MODES.DIRECTORY
            ? '—'
            : formatFileExplorerSize(entry.size);

        const modified = document.createElement('time');
        modified.className = 'vb-file-explorer-entry-modified';
        modified.textContent = formatModifiedTime(entry.lastModifiedUtc);
        if (entry.lastModifiedUtc) modified.dateTime = entry.lastModifiedUtc;

        const chevron = document.createElement('i');
        chevron.className = entry.isSymbolicLink
            ? 'fa-solid fa-shield-halved vb-file-explorer-entry-open'
            : entry.kind === FILE_EXPLORER_MODES.DIRECTORY
                ? 'fa-solid fa-chevron-right vb-file-explorer-entry-open'
                : 'fa-solid fa-check vb-file-explorer-entry-check';
        chevron.setAttribute('aria-hidden', 'true');

        row.append(visual.element, copy, type, size, modified, chevron);
        row.addEventListener('click', () => {
            handlers.onSelect(entry);
            row.focus({ preventScroll: true });
        });
        row.addEventListener('dblclick', () => handlers.onOpen(entry));
        row.addEventListener('keydown', event => {
            if (event.key === 'Enter') {
                event.preventDefault();
                handlers.onOpen(entry);
                return;
            }
            if (event.key === ' ') {
                event.preventDefault();
                handlers.onSelect(entry);
                return;
            }
            if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
            event.preventDefault();
            const rows = Array.from(elements.list.querySelectorAll('[data-file-explorer-entry]'));
            const index = rows.indexOf(row);
            const next = event.key === 'ArrowDown' ? rows[index + 1] : rows[index - 1];
            if (next) {
                row.tabIndex = -1;
                next.tabIndex = 0;
                next.focus();
            }
        });
        elements.list.appendChild(row);
    });
    updateStatus(elements, state);
}

function updateEntrySelection(elements, state) {
    elements.list.querySelectorAll('[data-file-explorer-entry]').forEach(row => {
        const selected = samePath(row.dataset.path, state.selected?.path);
        row.classList.toggle('is-selected', selected);
        row.setAttribute('aria-selected', selected ? 'true' : 'false');
        row.tabIndex = selected ? 0 : -1;
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
        return { element: wrapper, typeName: 'Folder' };
    }

    const visual = getFileTypeVisual(entry.name);
    const image = document.createElement('img');
    image.alt = '';
    image.loading = 'lazy';
    image.src = typeof window.__viberails_asset_url__ === 'function'
        ? window.__viberails_asset_url__(visual.iconPath)
        : visual.iconPath;
    wrapper.appendChild(image);
    return { element: wrapper, typeName: visual.name };
}

function formatModifiedTime(value) {
    if (!value) return '—';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '—';
    return new Intl.DateTimeFormat(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: 'numeric',
        minute: '2-digit'
    }).format(date);
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
        text.textContent = 'Searching the entire folder…';
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
        button.className = 'btn btn-sm btn-outline-warning';
        button.dataset.fileExplorerAction = 'load-more';
        button.disabled = state.loadingMore;
        button.innerHTML = state.loadingMore
            ? '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Loading…'
            : state.loadMoreError
                ? '<i class="fa-solid fa-rotate-right" aria-hidden="true"></i> Retry'
                : '<i class="fa-solid fa-plus" aria-hidden="true"></i> Load More';
        elements.notice.appendChild(button);
    }
}

function updateToolbar(elements, state) {
    elements.back.disabled = state.historyIndex <= 0;
    elements.forward.disabled = state.historyIndex < 0 || state.historyIndex >= state.history.length - 1;
    elements.up.disabled = !state.data.parentPath;
    elements.home.disabled = !state.data.defaultPath || samePath(state.data.defaultPath, state.data.currentPath);
    elements.address.value = state.locationReady
        ? state.data.currentPath
        : state.requestedPath || state.data.currentPath || '';
}

function updateSelectionFooter(elements, state) {
    const selected = state.selected;
    const selectable = Boolean(
        selected
        && !selected.isSymbolicLink
        && canSelectFileExplorerKind(state.mode, selected.kind)
    );
    elements.select.disabled = state.loading || !selectable;

    const defaultLabel = state.mode === FILE_EXPLORER_MODES.FILE
        ? 'Select File'
        : state.mode === FILE_EXPLORER_MODES.DIRECTORY
            ? 'Select Folder'
            : 'Select Item';
    elements.selectLabel.textContent = selectable
        ? `Select ${selected.kind === FILE_EXPLORER_MODES.DIRECTORY ? 'Folder' : 'File'}`
        : defaultLabel;

    elements.selectCurrent.disabled = state.loading || !state.locationReady || !state.data.currentPath;
    if (selected) {
        if (selected.isSymbolicLink) {
            elements.status.textContent = `${selected.name} is a linked item and cannot be opened or selected`;
            return;
        }
        const allowed = canSelectFileExplorerKind(state.mode, selected.kind);
        elements.status.textContent = allowed
            ? `${selected.name} selected`
            : `${selected.name} can be opened, but not selected in this mode`;
    }
}

function updateStatus(elements, state) {
    if (state.selected) {
        updateSelectionFooter(elements, state);
        return;
    }
    const visible = getVisibleEntries(state).length;
    const loaded = state.data.entries.length;
    const total = state.data.totalCount;
    const searchPending = cleanString(state.searchText) !== cleanString(state.data.search);
    if (searchPending) {
        elements.status.textContent = `${visible} matching loaded items · searching…`;
        return;
    }
    const noun = state.data.search ? 'matches' : visible === 1 ? 'item' : 'items';
    elements.status.textContent = total != null
        ? `${loaded.toLocaleString()} of ${total.toLocaleString()} ${noun}`
        : state.data.truncated
            ? `${loaded.toLocaleString()}+ ${noun}`
            : `${visible.toLocaleString()} ${noun}`;
}

function trapFocus(event, layer) {
    const focusable = Array.from(layer.querySelectorAll(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
    )).filter(element => !element.closest('[inert]') && (element.offsetParent !== null || element === document.activeElement));
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
