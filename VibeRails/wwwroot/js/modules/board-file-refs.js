// ============================================
// Board @file references — composer typeahead
// ============================================
//
// Typing `@` in a card composer (description, comment, or the new-card description)
// opens a small list of repository files under the caret. Up/Down move, Enter/Tab
// insert, Esc closes, and the last row always opens the shared file explorer for
// anything the index does not list. What gets inserted is plain text — `@path`,
// or `@"path with spaces"` — the same syntax board-text.js renders and
// BoardPromptComposer.cs extracts. Nothing is stored beyond the text itself.
//
// This is composer chrome only: no terminal, no byte stream, no new storage.
//
// The pure helpers at the top are covered by Tests/wwwroot/js/board-file-refs.test.mjs
// and must stay free of DOM access so that file can import this module under node.

import { escapeHtml } from './utils.js';
import { BoardApi } from './board-api.js';

/** Matches the server cap; a longer token is just text the popup ignores. */
export const MAX_QUERY_LENGTH = 256;
const DEBOUNCE_MS = 200;
// Mirror of the renderer's bare-token rule in board-text.js: no whitespace, none of the
// characters an escaped entity starts with or a quote could open, no trailing sentence
// punctuation, and a slash or a dot somewhere so it looks like a path.
const BARE_PATH_RE = /^[^\s<>"'`&]*[^\s<>"'`&.,;:!?)\]}]$/;
const LOOKS_LIKE_A_PATH_RE = /[./]/;

/**
 * The text to insert for a path: `@path` when the renderer recognises it bare, else the
 * quoted form. Backslashes become slashes so a Windows pick reads the same everywhere.
 */
export function formatFileReference(path) {
    const normalized = String(path || '').replaceAll('\\', '/').trim();
    if (!normalized) return '';
    if (BARE_PATH_RE.test(normalized) && LOOKS_LIKE_A_PATH_RE.test(normalized)) return `@${normalized}`;
    // A double quote cannot be carried by either form; it is dropped rather than left
    // to end the reference early. (Impossible in a Windows file name anyway.)
    return `@"${normalized.replaceAll('"', '')}"`;
}

/**
 * The reference being typed at the caret, or null. Scans back on the caret's line to the
 * nearest `@` that starts the text or follows whitespace. An open `@"` may contain spaces;
 * a bare token may not, so once the author types a space the reference is finished.
 * @returns {{ start: number, query: string, quoted: boolean } | null}
 */
export function findFileReferenceToken(value, caret) {
    const text = String(value ?? '');
    const end = Math.min(Math.max(Number(caret) || 0, 0), text.length);
    if (end === 0) return null;
    const lineStart = text.lastIndexOf('\n', end - 1) + 1;
    const at = text.lastIndexOf('@', end - 1);
    if (at < lineStart) return null;
    if (at > 0 && !/\s/.test(text[at - 1])) return null; // rob@example.com
    const body = text.slice(at + 1, end);
    if (body.startsWith('"')) {
        if (body.indexOf('"', 1) !== -1) return null; // the quote closed: reference complete
        const query = body.slice(1);
        return query.length > MAX_QUERY_LENGTH ? null : { start: at, query, quoted: true };
    }
    if (/\s/.test(body)) return null;
    return body.length > MAX_QUERY_LENGTH ? null : { start: at, query: body, quoted: false };
}

/**
 * Repo-relative with forward slashes when the pick is inside the project root; otherwise the
 * path as picked, so an agent can still open a file that lives outside the repository.
 */
export function toProjectRelativePath(pickedPath, projectRoot) {
    const normalize = value => String(value || '').replaceAll('\\', '/').replace(/\/+$/, '');
    const picked = normalize(pickedPath);
    const root = normalize(projectRoot);
    if (!picked || !root) return picked;
    const windows = /^[a-z]:/i.test(root);
    const compare = value => (windows ? value.toLowerCase() : value);
    const prefix = `${root}/`;
    return compare(picked).startsWith(compare(prefix)) ? picked.slice(prefix.length) : picked;
}

/**
 * Splices a reference over the token range and returns the new value plus caret. A space
 * follows the reference unless the text already continues with whitespace, so the next
 * keystroke never glues itself onto the path.
 */
export function insertFileReference(value, start, end, reference) {
    const text = String(value ?? '');
    const before = text.slice(0, start);
    const after = text.slice(end);
    const trailing = /^\s/.test(after) ? '' : ' ';
    const next = `${before}${reference}${trailing}${after}`;
    const caret = before.length + reference.length + trailing.length;
    return { value: next, caret };
}

// ---------------------------------------------------------------- DOM binder

const MIRROR_PROPERTIES = [
    'boxSizing', 'width', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft',
    'borderTopWidth', 'borderRightWidth', 'borderBottomWidth', 'borderLeftWidth',
    'fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'letterSpacing', 'lineHeight',
    'textTransform', 'wordSpacing', 'textIndent', 'tabSize'
];

/**
 * Where the character at `index` sits inside the textarea's content box, measured with a
 * hidden mirror that wraps exactly like the textarea. Synchronous on purpose: reading layout
 * forces it, and a frame never arrives while a VS Code webview is occluded (see wwwroot/AGENTS.md).
 */
function caretOffset(textarea, index) {
    const style = getComputedStyle(textarea);
    const mirror = document.createElement('div');
    for (const property of MIRROR_PROPERTIES) mirror.style[property] = style[property];
    Object.assign(mirror.style, {
        position: 'absolute', top: '0', left: '-9999px', visibility: 'hidden',
        whiteSpace: 'pre-wrap', overflowWrap: 'break-word', overflow: 'hidden', height: 'auto'
    });
    mirror.textContent = textarea.value.slice(0, index);
    const marker = document.createElement('span');
    marker.textContent = '​';
    mirror.appendChild(marker);
    document.body.appendChild(mirror);
    const lineHeight = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.4 || 20;
    const result = { top: marker.offsetTop, left: marker.offsetLeft, lineHeight };
    mirror.remove();
    return result;
}

/**
 * Wires the `@` popup to one composer textarea. `host` is the positioned parent the popup is
 * appended to (the `.board-composer`); `app` supplies the project root, the file explorer and
 * toasts. Returns a dispose function; call it when the editor closes or is replaced.
 */
export function bindFileReferencePopup(input, { app, host = input?.parentElement } = {}) {
    if (!input || !host || typeof document === 'undefined') return () => {};
    const lifetime = new AbortController();
    const { signal } = lifetime;
    let popup = null;
    let token = null;
    let items = [];
    let activeIndex = 0;
    let truncated = false;
    let status = '';
    let timer = null;
    let fetchAbort = null;
    let generation = 0;
    let disposed = false;

    const projectRoot = () => app?.data?.configs?.rootPath || app?.data?.configs?.launchDirectory || '';

    function close() {
        generation++;
        clearTimeout(timer);
        timer = null;
        fetchAbort?.abort();
        fetchAbort = null;
        popup?.remove();
        popup = null;
        token = null;
        items = [];
        activeIndex = 0;
        truncated = false;
        status = '';
    }

    function position() {
        if (!popup || !token) return;
        const caret = caretOffset(input, token.start);
        const top = input.offsetTop + caret.top - input.scrollTop + caret.lineHeight;
        const maxLeft = Math.max(0, host.clientWidth - popup.offsetWidth - 4);
        popup.style.top = `${Math.max(0, Math.round(top))}px`;
        popup.style.left = `${Math.round(Math.min(Math.max(0, input.offsetLeft + caret.left), maxLeft))}px`;
    }

    function render() {
        if (!popup) return;
        const browseIndex = items.length;
        const rows = items.map((file, index) => `
            <div class="board-file-popup-row${index === activeIndex ? ' is-active' : ''}" role="option"
                aria-selected="${index === activeIndex}" data-board-file-row="${escapeHtml(file)}" title="${escapeHtml(file)}">
                <i class="fa-regular fa-file board-file-popup-icon" aria-hidden="true"></i>
                <span class="board-file-popup-path">${escapeHtml(file)}</span>
            </div>`);
        let note = '';
        if (status === 'loading' && items.length === 0) note = 'Finding files…';
        else if (status === 'error') note = 'Could not list files.';
        else if (status === '' && items.length === 0) note = 'No matching files.';
        else if (truncated) note = 'Showing 50 files. Keep typing to narrow.';
        popup.innerHTML = `${rows.join('')}
            ${note ? `<div class="board-file-popup-note" data-board-file-note>${escapeHtml(note)}</div>` : ''}
            <div class="board-file-popup-row board-file-popup-browse${activeIndex === browseIndex ? ' is-active' : ''}" role="option"
                aria-selected="${activeIndex === browseIndex}" data-board-file-row data-board-file-browse>
                <i class="fa-regular fa-folder-open board-file-popup-icon" aria-hidden="true"></i>
                <span>Browse for a file…</span>
            </div>`;
        popup.querySelector('.is-active')?.scrollIntoView?.({ block: 'nearest' });
        position();
    }

    function open() {
        if (popup) return;
        popup = document.createElement('div');
        popup.className = 'board-file-popup';
        popup.setAttribute('role', 'listbox');
        popup.setAttribute('aria-label', 'Repository files');
        popup.dataset.boardFilePopup = '';
        // mousedown rather than click, and prevented: the textarea keeps focus and its caret
        // through the pick, so the insert lands where the author was typing.
        popup.addEventListener('mousedown', event => {
            event.preventDefault();
            const row = event.target.closest('[data-board-file-row]');
            if (!row) return;
            if (row.dataset.boardFileBrowse !== undefined) void browse();
            else insert(row.dataset.boardFileRow);
        }, { signal });
        host.appendChild(popup);
    }

    async function load() {
        if (disposed || !token) return;
        const current = ++generation;
        fetchAbort?.abort();
        fetchAbort = new AbortController();
        status = 'loading';
        render();
        try {
            const result = await BoardApi.searchFilesAsync(token.query, { signal: fetchAbort.signal });
            if (disposed || current !== generation || !token) return;
            items = result.files;
            truncated = result.truncated;
            status = '';
            activeIndex = 0;
            render();
        } catch (error) {
            if (disposed || current !== generation || error?.name === 'AbortError') return;
            items = [];
            status = 'error';
            render();
        }
    }

    function refresh() {
        if (disposed) return;
        const next = findFileReferenceToken(input.value, input.selectionStart);
        if (!next) {
            if (popup) close();
            return;
        }
        const opening = !popup;
        token = next;
        open();
        if (opening) {
            items = [];
            activeIndex = 0;
            truncated = false;
        }
        render();
        clearTimeout(timer);
        timer = setTimeout(() => void load(), DEBOUNCE_MS);
    }

    function commit(start, end, reference) {
        const next = insertFileReference(input.value, start, end, reference);
        input.value = next.value;
        input.focus();
        input.setSelectionRange(next.caret, next.caret);
        // Bubbles so the composer's auto-grow and the editor's unsaved-edit tracking both see it.
        input.dispatchEvent(new Event('input', { bubbles: true }));
    }

    function insert(path) {
        if (!token) return;
        const reference = formatFileReference(path);
        const { start } = token;
        const end = input.selectionStart;
        close();
        if (reference) commit(start, end, reference);
    }

    async function browse() {
        if (!token) return;
        const range = { start: token.start, end: input.selectionStart };
        close();
        if (typeof app?.pickFileSystemEntry !== 'function') {
            app?.showToast?.('Board', 'The file picker is not available in this window.', 'warning');
            return;
        }
        let picked;
        try {
            picked = await app.pickFileSystemEntry({
                mode: 'file', title: 'Reference a file', initialPath: projectRoot(), triggerElement: input
            });
        } catch (error) {
            app?.showToast?.('Board', error?.message || 'Could not open the file picker.', 'error');
            return;
        }
        if (disposed || !input.isConnected) return;
        if (!picked || picked.canceled || !picked.path) {
            input.focus();
            return;
        }
        const reference = formatFileReference(toProjectRelativePath(picked.path, projectRoot()));
        if (!reference) return;
        // Re-anchor on the typed token when it is still there; otherwise insert at the caret.
        const value = input.value;
        const anchored = range.end <= value.length && value.slice(range.start, range.end).startsWith('@');
        commit(anchored ? range.start : input.selectionStart, anchored ? range.end : input.selectionEnd, reference);
    }

    input.addEventListener('input', refresh, { signal });
    // The caret moved by mouse: re-evaluate whether it still sits in a token.
    input.addEventListener('click', () => { if (popup) refresh(); }, { signal });
    input.addEventListener('blur', () => close(), { signal });
    input.addEventListener('keydown', event => {
        if (!popup) return;
        const total = items.length + 1; // + the Browse row
        switch (event.key) {
            case 'ArrowDown':
                activeIndex = (activeIndex + 1) % total;
                render();
                break;
            case 'ArrowUp':
                activeIndex = (activeIndex - 1 + total) % total;
                render();
                break;
            case 'Enter':
            case 'Tab':
                // Ctrl+Enter still posts the comment; Shift+Tab still moves focus.
                if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
                if (status === 'loading' && items.length === 0) {
                    // Nothing to choose yet: let the key do its normal job instead of guessing.
                    close();
                    return;
                }
                if (activeIndex === items.length) void browse();
                else insert(items[activeIndex]);
                break;
            case 'Escape':
                // preventDefault is what keeps app.js from also closing the card modal.
                close();
                break;
            default:
                return;
        }
        event.preventDefault();
        event.stopPropagation();
    }, { signal });

    return () => {
        disposed = true;
        close();
        lifetime.abort();
    };
}
