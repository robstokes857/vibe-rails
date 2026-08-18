import { ensureMonaco } from './monaco-loader.js';
import { confirmDialog, escapeHtml, isConfirmDialogOpen } from './utils.js';

const STATUS_TONE = Object.freeze({
    approved: { label: 'Signed', tone: 'success', icon: 'fa-circle-check' },
    modified: { label: 'Changed since signing', tone: 'warning', icon: 'fa-triangle-exclamation' },
    unapproved: { label: 'Not signed', tone: 'neutral', icon: 'fa-circle-minus' }
});

/**
 * Monaco editor for one Python script, used when there is no VS Code window to hand the
 * file to (plain browser, or "Edit here" from the row menu).
 *
 * Sizing follows the terminal editor modal: app.showModal's auto-height dialog collapses
 * Monaco to 0px, so this owns a sized overlay (.vb-python-editor-modal in style.css) and
 * appends its own layer rather than taking over #modal-container — the PIN prompt that
 * "Save & sign" opens afterwards lives in that same container.
 *
 * Saving never signs: a changed script drops to "Changed since signing" until the user
 * re-approves with their PIN (an identical save keeps its existing approval). That is the
 * whole point of the gate, so the header pill and footer make the state explicit.
 */
export class PythonScriptEditorModal {
    /**
     * @param {object} options
     * @param {object} options.app VibeRails application instance (toasts, errors).
     * @param {(name: string, content: string, expectedVersion: string) =>
     *        Promise<{status: string, version: string}>} options.onSave
     *        Persists the content and resolves the script's new status/version.
     * @param {(name: string) => Promise<void>} options.onSign Runs the PIN signing flow.
     */
    constructor({ app, onSave, onSign }) {
        this.app = app;
        this.onSave = onSave;
        this.onSign = onSign;
        this.layer = null;
        this.editor = null;
        this.name = '';
        this.baseline = '';
        this.version = '';
        this.status = 'unapproved';
        this.saving = false;
        this.closed = false;
        this._resizeObserver = null;
        this._generation = 0;
        this._onKeydown = this._onKeydown.bind(this);
    }

    get isOpen() {
        return Boolean(this.layer);
    }

    /**
     * @param {{name: string, content: string, status: string, version: string, path?: string}} script
     * @returns {Promise<{saved: boolean}>} resolves once the modal is gone.
     */
    open(script) {
        void this.close({ force: true });
        const host = document.getElementById('modal-container');
        if (!host) return Promise.resolve({ saved: false });

        const generation = ++this._generation;
        this.closed = false;
        this.saving = false;
        this.name = script.name;
        this.baseline = script.content ?? '';
        this.version = script.version || '';
        this.status = script.status || 'unapproved';
        this._saved = false;

        const layer = document.createElement('div');
        layer.className = 'vb-python-editor-layer';
        layer.innerHTML = `
            <div class="modal fade show d-block vb-python-editor-modal" tabindex="-1" role="dialog"
                 aria-modal="true" aria-labelledby="vb-python-editor-title">
                <div class="modal-dialog">
                    <div class="modal-content">
                        <div class="modal-header">
                            <div class="vb-python-editor-heading">
                                <h5 class="modal-title" id="vb-python-editor-title">
                                    <i class="fa-brands fa-python me-2" aria-hidden="true"></i>${escapeHtml(script.name)}
                                </h5>
                                <div class="vb-python-editor-subhead">
                                    <span class="python-script-status" data-py-editor-status></span>
                                    ${script.path ? `<code title="${escapeHtml(script.path)}">${escapeHtml(script.path)}</code>` : ''}
                                </div>
                            </div>
                            <button type="button" class="btn-close" data-py-editor-action="close" aria-label="Close"></button>
                        </div>
                        <div class="modal-body">
                            <div class="vb-python-editor-host" data-py-editor-host></div>
                        </div>
                        <div class="modal-footer">
                            <span class="vb-python-editor-hint text-muted small me-auto" data-py-editor-hint></span>
                            <button type="button" class="btn btn-secondary" data-py-editor-action="close">Cancel</button>
                            <button type="button" class="btn btn-outline-primary" data-py-editor-action="save">
                                <i class="fa-solid fa-floppy-disk me-1" aria-hidden="true"></i>Save
                            </button>
                            <button type="button" class="btn btn-primary" data-py-editor-action="save-sign">
                                <i class="fa-solid fa-signature me-1" aria-hidden="true"></i>Save &amp; sign
                            </button>
                        </div>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show"></div>`;

        host.appendChild(layer);
        this.layer = layer;
        layer.querySelectorAll('[data-py-editor-action]').forEach((button) => {
            button.addEventListener('click', () => {
                if (generation !== this._generation) return;
                const action = button.dataset.pyEditorAction;
                if (action === 'close') void this.close();
                if (action === 'save') void this._save({ close: true }, generation);
                if (action === 'save-sign') void this._save({ close: true, sign: true }, generation);
            });
        });

        // Capture phase: app.js's global Escape handler closes modals and navigates back,
        // which would strip this layer without disposing Monaco or asking about unsaved work.
        document.addEventListener('keydown', this._onKeydown, true);
        this._renderStatus();
        this._renderHint();

        this._closedPromise = new Promise((resolve) => {
            this._resolveClosed = resolve;
        });
        void this._mountEditor(generation, layer);
        return this._closedPromise;
    }

    async _mountEditor(generation, layer) {
        const monaco = await ensureMonaco();
        if (generation !== this._generation || this.closed || this.layer !== layer) return;
        if (!monaco) {
            this.app.showError('Could not load the code editor.');
            void this.close({ force: true });
            return;
        }

        const host = layer.querySelector('[data-py-editor-host]');
        const editor = monaco.editor.create(host, {
            value: this.baseline,
            language: 'python',
            theme: 'viberails-dark',
            automaticLayout: true,
            minimap: { enabled: false },
            scrollBeyondLastLine: false,
            tabSize: 4,
            insertSpaces: true,
            renderWhitespace: 'selection',
            fontSize: 13,
            fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
            padding: { top: 8 }
        });
        this.editor = editor;
        editor.onDidChangeModelContent(() => {
            if (generation === this._generation) this._renderHint();
        });
        // Ctrl/Cmd+S saves in place — the buttons behave like a dialog and close, the
        // keyboard behaves like an editor and keeps you in the file.
        editor.addCommand(
            monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS,
            () => void this._save({ close: false }, generation));

        requestAnimationFrame(() => {
            if (generation !== this._generation || this.closed || this.editor !== editor) return;
            editor.layout();
            editor.focus();
        });
        this._resizeObserver = new ResizeObserver(() => {
            if (generation === this._generation && this.editor === editor) editor.layout();
        });
        this._resizeObserver.observe(host);
    }

    _currentText() {
        return this.editor ? this.editor.getValue() : this.baseline;
    }

    get isDirty() {
        return this._currentText() !== this.baseline;
    }

    async _save({ close = false, sign = false } = {}, generation = this._generation) {
        const editor = this.editor;
        if (generation !== this._generation || this.saving || !editor) return;
        const name = this.name;
        const expectedVersion = this.version;
        const content = editor.getValue();
        this.saving = true;
        this._renderHint('Saving…');
        let result;
        try {
            result = await this.onSave(name, content, expectedVersion);
        } catch (error) {
            if (generation !== this._generation) return;
            this.saving = false;
            this._renderHint();
            this.app.showError(error?.message || `Could not save ${name}.`);
            return;
        }

        // Navigation or a newer open invalidates this operation. The server may have saved
        // the captured script, but this completion must never mutate or close the newer modal.
        if (generation !== this._generation || this.editor !== editor) return;
        this.status = result.status;
        this.version = result.version;
        this.baseline = content;
        this._saved = true;
        this.saving = false;
        this._renderStatus();
        this._renderHint(close ? '' : 'Saved.');

        if (close) {
            await this.close({ force: true });
            if (sign) await this.onSign(name);
        }
    }

    _renderStatus() {
        const meta = STATUS_TONE[this.status] || STATUS_TONE.unapproved;
        const element = this.layer?.querySelector('[data-py-editor-status]');
        if (!element) return;
        element.dataset.tone = meta.tone;
        element.innerHTML = `<i class="fa-solid ${meta.icon}" aria-hidden="true"></i>${meta.label}`;
    }

    _renderHint(message = '') {
        const element = this.layer?.querySelector('[data-py-editor-hint]');
        if (!element) return;
        if (message) {
            element.textContent = message;
            return;
        }
        element.textContent = this.isDirty
            ? 'Unsaved changes · Ctrl/⌘+S to save · Esc to close'
            : this.status === 'approved'
                ? 'Signed. Saving an edit clears the signature until you re-sign.'
                : 'Saving does not sign the script — sign it before it can run.';
    }

    _onKeydown(event) {
        if (event.key !== 'Escape' || isConfirmDialogOpen()) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        void this.close();
    }

    /**
     * @param {{force?: boolean}} options force skips the unsaved-changes prompt (used when
     *        the close follows a successful save, or a failed mount).
     */
    async close({ force = false } = {}) {
        if (!this.layer || this.closed) return;
        const generation = this._generation;
        if (!force && this.isDirty) {
            const discard = await confirmDialog({
                title: `Discard changes to ${this.name}?`,
                message: 'The edits in this editor have not been saved to the script file.',
                confirmLabel: 'Discard changes',
                danger: true
            });
            if (!discard || this.closed || generation !== this._generation) return;
        }

        this._generation += 1;
        this.closed = true;
        document.removeEventListener('keydown', this._onKeydown, true);
        try { this._resizeObserver?.disconnect(); } catch { /* detached */ }
        this._resizeObserver = null;
        try { this.editor?.dispose(); } catch { /* already gone */ }
        this.editor = null;
        this.layer.remove();
        this.layer = null;
        this._resolveClosed?.({ saved: Boolean(this._saved) });
        this._resolveClosed = null;
    }
}
