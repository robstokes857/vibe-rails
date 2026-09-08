// ============================================
// Shared Monaco diff modal
// ============================================
//
// One diff viewer, two callers: the sandbox "View Diff" action and a Board card's
// commits. Takes files that are already fetched — it owns no endpoint.
//
// WHY THIS IS A NESTED LAYER AND NOT A `showModal` / innerHTML MODAL
// ------------------------------------------------------------------
// The original implementation (in sandbox-controller.js) built itself by replacing
// #modal-container.innerHTML wholesale. That works only while nothing else is open,
// which was true for its one caller. Opened from inside the Board's card editor —
// which IS an app.showModal dialog — it would have destroyed that dialog's DOM out
// from under app.modalState, and its close path would then have resolved the stale
// state, closing both dialogs and returning focus past the card entirely.
//
// So this appends its own layer instead, exactly like environment-steps.js's
// openStepsEditor: inert + aria-hide the existing children, trap focus inside, own
// Escape, restore on close. app.js's hasActiveNestedModalLayer() knows about the
// layer class and stands its own Tab-trap down for us.
//
// TWO BEHAVIOURS THAT MUST NOT REGRESS
//   1. Monaco diff editors do NOT dispose externally-set models along with the
//      editor. Both TextModels hold the full before/after text of the last file
//      shown, so they are disposed explicitly, and always BEFORE the container
//      goes away.
//   2. Monaco owns Escape for its own widgets (Find, etc). An Escape raised from
//      inside .monaco-editor must be left alone rather than closing the viewer.

import { ensureMonaco } from './monaco-loader.js';
import { escapeHtml, isConfirmDialogOpen } from './utils.js';

const LAYER_CLASS = 'vb-diff-modal-layer';

const FOCUSABLE = 'a[href], button:not([disabled]), textarea:not([disabled]), '
    + 'input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

function trapFocus(event, layer) {
    const focusable = Array.from(layer.querySelectorAll(FOCUSABLE))
        .filter(element => element.offsetParent !== null || element === document.activeElement);
    if (focusable.length === 0) return;

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
    }
}

function fileStatus(file) {
    if (!file.originalContent) return { code: 'A', cls: 'added' };
    if (!file.modifiedContent) return { code: 'D', cls: 'deleted' };
    return { code: 'M', cls: 'modified' };
}

function renderFileList(files) {
    return files.map((file, index) => {
        const status = fileStatus(file);
        const name = String(file.fileName || '').split('/').pop();
        const dir = String(file.fileName || '').includes('/')
            ? String(file.fileName).slice(0, String(file.fileName).lastIndexOf('/') + 1)
            : '';
        return `
            <button type="button" class="vb-diff-file-item${index === 0 ? ' active' : ''}"
                data-file-index="${index}" title="${escapeHtml(file.fileName || '')}">
                <span class="file-status ${status.cls}">${status.code}</span>
                <span class="vb-diff-file-name"><span class="vb-diff-file-dir">${escapeHtml(dir)}</span>${escapeHtml(name)}</span>
            </button>`;
    }).join('');
}

/**
 * Opens the diff viewer as a nested modal layer.
 *
 * @param {object} options
 * @param {string} options.title  heading text
 * @param {Array<{fileName: string, language?: string, originalContent?: string, modifiedContent?: string}>} options.files
 *        Same shape /api/v1/sandboxes/{id}/diff returns and Monaco consumes directly.
 * @param {() => void} [options.onClose]
 * @returns {{ close: () => void, ready: Promise<boolean> }}
 */
export function openDiffModal({ title, files = [], onClose = null } = {}) {
    const host = typeof document !== 'undefined' ? document.getElementById('modal-container') : null;
    if (!host) return { close: () => { }, ready: Promise.resolve(false) };

    const layer = document.createElement('div');
    layer.className = LAYER_CLASS;
    layer.innerHTML = `
        <div class="modal fade show d-block vb-diff-modal" tabindex="-1" role="dialog"
             aria-modal="true" aria-label="${escapeHtml(title || 'Changes')}">
            <div class="modal-dialog">
                <div class="modal-content">
                    <div class="modal-header">
                        <h5 class="modal-title">${escapeHtml(title || 'Changes')}</h5>
                        <button type="button" class="btn-close" data-vb-diff-close aria-label="Close the diff"></button>
                    </div>
                    <div class="modal-body">
                        ${files.length === 0
                            ? '<div class="vb-diff-empty">No changes to show.</div>'
                            : `<div class="vb-diff-sidebar">
                                    <div class="vb-diff-sidebar-head">Changed files (${files.length})</div>
                                    ${renderFileList(files)}
                               </div>
                               <div class="vb-diff-main">
                                    <div class="vb-diff-toolbar">
                                        <button type="button" class="diff-btn active" data-vb-diff-mode="side">Side by side</button>
                                        <button type="button" class="diff-btn" data-vb-diff-mode="inline">Inline</button>
                                        <div class="diff-stat" data-vb-diff-stats>
                                            <span class="added">+0</span>&nbsp;<span class="removed">-0</span>
                                        </div>
                                    </div>
                                    <div class="vb-diff-editor-container" data-vb-diff-editor>
                                        <div class="vb-diff-empty">Loading the diff viewer…</div>
                                    </div>
                                    <div class="vb-diff-statusbar">
                                        <div class="status-left"><span data-vb-diff-count>0 changes</span></div>
                                        <div class="status-right">
                                            <span>UTF-8</span>
                                            <span data-vb-diff-language>${escapeHtml(files[0]?.language || 'plaintext')}</span>
                                        </div>
                                    </div>
                               </div>`}
                    </div>
                </div>
            </div>
        </div>
        <div class="modal-backdrop fade show vb-diff-modal-backdrop"></div>`;

    // Everything already in the container becomes inert for as long as we are up,
    // and gets its previous state back on close.
    const underlying = Array.from(host.children).map(element => ({
        element,
        inert: Boolean(element.inert),
        ariaHidden: element.getAttribute('aria-hidden')
    }));
    underlying.forEach(({ element }) => {
        element.inert = true;
        element.setAttribute('aria-hidden', 'true');
    });
    const previousFocus = document.activeElement;
    host.appendChild(layer);

    const state = { editor: null, monaco: null, disposed: false, keydownHandler: null, observer: null };

    function disposeEditor() {
        const editor = state.editor;
        if (!editor) return;
        state.editor = null;
        let model = null;
        try { model = editor.getModel(); } catch { /* already gone */ }
        try { editor.dispose(); } catch { /* already gone */ }
        try { model?.original?.dispose(); } catch { /* already gone */ }
        try { model?.modified?.dispose(); } catch { /* already gone */ }
    }

    function close({ restoreFocus = true } = {}) {
        if (state.disposed) return;
        state.disposed = true;
        // Editor first, while its container is still attached.
        disposeEditor();
        if (state.keydownHandler) document.removeEventListener('keydown', state.keydownHandler, true);
        state.observer?.disconnect();
        layer.remove();
        underlying.forEach(({ element, inert, ariaHidden }) => {
            if (!element.isConnected) return;
            element.inert = inert;
            if (ariaHidden == null) element.removeAttribute('aria-hidden');
            else element.setAttribute('aria-hidden', ariaHidden);
        });
        if (restoreFocus && previousFocus?.isConnected && typeof previousFocus.focus === 'function') {
            try { previousFocus.focus({ preventScroll: true }); } catch { /* detached */ }
        }
        onClose?.();
    }

    state.keydownHandler = event => {
        if (state.disposed) return;
        if (isConfirmDialogOpen()) return;
        if (event.key === 'Escape') {
            if (event.defaultPrevented) return;
            // Monaco owns Escape for its own widgets (Find, suggestions). Leave those alone.
            if (event.target?.closest?.('.monaco-editor')) return;
            event.preventDefault();
            event.stopImmediatePropagation();
            close();
            return;
        }
        if (event.key === 'Tab') trapFocus(event, layer);
    };
    document.addEventListener('keydown', state.keydownHandler, true);

    // If someone blanks #modal-container underneath us (app.closeModal), tear the
    // editor down rather than leaking it behind a detached container.
    state.observer = new MutationObserver(() => {
        if (!layer.isConnected && !state.disposed) {
            state.disposed = true;
            disposeEditor();
            if (state.keydownHandler) document.removeEventListener('keydown', state.keydownHandler, true);
            state.observer?.disconnect();
            onClose?.();
        }
    });
    state.observer.observe(host, { childList: true });

    layer.querySelectorAll('[data-vb-diff-close]').forEach(button =>
        button.addEventListener('click', () => close()));

    const ready = mountEditor();

    async function mountEditor() {
        if (files.length === 0) return false;
        const monaco = await ensureMonaco();
        // The user may have closed the viewer while Monaco was loading.
        if (state.disposed || !layer.isConnected) return false;

        const container = layer.querySelector('[data-vb-diff-editor]');
        if (!monaco || !container) {
            if (container) container.innerHTML = '<div class="vb-diff-empty">The diff viewer failed to load.</div>';
            return false;
        }

        state.monaco = monaco;
        container.innerHTML = '';
        const editor = monaco.editor.createDiffEditor(container, {
            theme: 'viberails-dark',
            automaticLayout: true,
            fontSize: 13,
            fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
            renderSideBySide: true,
            enableSplitViewResizing: true,
            renderIndicators: true,
            smoothScrolling: true,
            padding: { top: 8 },
            minimap: { enabled: false },
            scrollBeyondLastLine: false,
            originalEditable: false,
            readOnly: true
        });
        state.editor = editor;

        loadFile(files[0]);
        editor.onDidUpdateDiff(() => updateStats());

        layer.querySelectorAll('.vb-diff-file-item').forEach(item => {
            item.addEventListener('click', () => {
                const index = Number(item.dataset.fileIndex);
                layer.querySelectorAll('.vb-diff-file-item').forEach(other => other.classList.remove('active'));
                item.classList.add('active');
                loadFile(files[index]);
                const language = layer.querySelector('[data-vb-diff-language]');
                if (language) language.textContent = files[index]?.language || 'plaintext';
            });
        });

        layer.querySelectorAll('[data-vb-diff-mode]').forEach(button => {
            button.addEventListener('click', () => {
                const sideBySide = button.dataset.vbDiffMode === 'side';
                state.editor?.updateOptions({ renderSideBySide: sideBySide });
                layer.querySelectorAll('[data-vb-diff-mode]').forEach(other =>
                    other.classList.toggle('active', other === button));
            });
        });

        return true;
    }

    function loadFile(file) {
        if (!state.editor || !state.monaco || !file) return;
        const previous = state.editor.getModel();
        const original = state.monaco.editor.createModel(file.originalContent || '', file.language || 'plaintext');
        const modified = state.monaco.editor.createModel(file.modifiedContent || '', file.language || 'plaintext');
        state.editor.setModel({ original, modified });
        // Dispose the models we just swapped out, not the ones now in use.
        try { previous?.original?.dispose(); } catch { /* already gone */ }
        try { previous?.modified?.dispose(); } catch { /* already gone */ }
    }

    function updateStats() {
        const changes = state.editor?.getLineChanges();
        if (!changes) return;
        let added = 0;
        let removed = 0;
        // Monaco signals "nothing on this side" with an end line of 0 — that is an
        // empty range, not a range of length one. The inherited version added the
        // range first and then subtracted 1 unconditionally, so a pure insertion
        // pushed the removed count negative and rendered as "--3".
        changes.forEach(change => {
            if (change.modifiedEndLineNumber !== 0) {
                added += change.modifiedEndLineNumber - change.modifiedStartLineNumber + 1;
            }
            if (change.originalEndLineNumber !== 0) {
                removed += change.originalEndLineNumber - change.originalStartLineNumber + 1;
            }
        });

        const stats = layer.querySelector('[data-vb-diff-stats]');
        if (stats) {
            stats.querySelector('.added').textContent = `+${added}`;
            stats.querySelector('.removed').textContent = `-${removed}`;
        }
        const count = layer.querySelector('[data-vb-diff-count]');
        if (count) count.textContent = `${changes.length} change${changes.length === 1 ? '' : 's'}`;
    }

    requestAnimationFrame(() => layer.querySelector('.vb-diff-modal')?.focus());

    return { close, ready };
}
