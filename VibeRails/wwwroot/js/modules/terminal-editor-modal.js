import { ensureMonaco } from './monaco-loader.js';

/**
 * "Open in text editor" — a Monaco-backed scratchpad for the terminal kebab menu.
 *
 * Composing long / multi-line input (e.g. an agent prompt) in a raw TUI is
 * painful. This opens a real editor in a modal; "Send to terminal" injects the
 * text into the active terminal session as a bracketed paste (paste-only — the
 * cursor is left after the text so the user presses Enter themselves).
 *
 * The shared modal owns cleanup even when navigation or another dialog replaces
 * this one. The terminal-editor class supplies Monaco's fixed flex dimensions.
 * Escape bubbles through Monaco first, so its Find/suggest widgets keep the key.
 * The session draft lives on the manager so it survives reopen, not a new session.
 */
export class TerminalEditorModal {
    constructor(manager) {
        this.manager = manager;
        this.app = manager.app;
        this.editor = null;
        this._resizeObserver = null;
        this._closed = false;
        this._skipDraftSave = false;
        this._modalState = null;
        this._dialog = null;
        this._terminalTab = null;
        this._onKeydown = this._onKeydown.bind(this);
        this._onCloseClick = this._onCloseClick.bind(this);
    }

    async show() {
        const container = document.getElementById('modal-container');
        if (!container) {
            return;
        }

        this._terminalTab = this.manager.getActiveTab();
        this.app.showModal('Open in text editor', `
            <div class="vb-terminal-editor-host" id="vb-terminal-editor-host"></div>
            <div class="modal-footer">
                <span class="vb-terminal-editor-hint text-muted small me-auto">Ctrl/⌘+Enter to send · Esc to close</span>
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="button" class="btn btn-primary" id="vb-terminal-editor-send-btn">Send to terminal</button>
            </div>
        `, { onClose: () => this._dispose() });
        this._modalState = this.app.modalState;
        this._dialog = this._modalState.dialog;
        this._dialog.classList.add('vb-terminal-editor-modal');
        this._dialog.querySelector('.modal-dialog').classList.remove('modal-dialog-scrollable');
        this._dialog.querySelector('.modal-content').append(this._dialog.querySelector('.modal-footer'));

        // Explicit close returns to this terminal; navigation/replacement only
        // runs cleanup and leaves the shared modal's normal focus ownership intact.
        this._dialog.addEventListener('click', this._onCloseClick, true);
        this._dialog.addEventListener('keydown', this._onKeydown);
        container.querySelector('#vb-terminal-editor-send-btn')
            ?.addEventListener('click', () => this._send());

        const monaco = await ensureMonaco();
        if (this._closed || this.app.modalState !== this._modalState) {
            return; // user closed during the async load
        }
        if (!monaco) {
            this.app.showError('Failed to load the text editor.');
            this._close();
            return;
        }

        const host = container.querySelector('#vb-terminal-editor-host');
        this.editor = monaco.editor.create(host, {
            value: this.manager.getEditorDraft(),
            language: 'plaintext',
            theme: 'viberails-dark',
            wordWrap: 'on',
            automaticLayout: true,
            minimap: { enabled: false },
            scrollBeyondLastLine: false,
            fontSize: 14,
            fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
            padding: { top: 8 },
        });

        // The host only has real dimensions after the modal paints; lay out then
        // focus. automaticLayout (above) + this ResizeObserver keep it sized as the
        // window/modal changes.
        requestAnimationFrame(() => {
            if (this._closed || !this.editor) {
                return;
            }
            this.editor.layout();
            this.editor.focus();
        });
        this._resizeObserver = new ResizeObserver(() => this.editor?.layout());
        this._resizeObserver.observe(host);

        // Ctrl/Cmd+Enter inside the editor sends.
        this.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => this._send());
        // Monaco consumes plain Escape even with no widget open. Give the
        // scratchpad a local close command only when editor widgets do not own it.
        this.editor.addCommand(monaco.KeyCode.Escape, () => this._close(),
            'editorTextFocus && !suggestWidgetVisible && !findWidgetVisible'
            + ' && !renameInputVisible && !inSnippetMode && !parameterHintsVisible && !editorHoverVisible');
    }

    _onKeydown(e) {
        if (e.key === 'Escape' && !e.defaultPrevented && !this._closed
            && this.app.modalState === this._modalState) {
            e.preventDefault();
            e.stopPropagation();
            this._close();
        }
    }

    _onCloseClick(e) {
        if (!e.target.closest?.('[data-action="close-modal"]')) return;
        e.preventDefault();
        e.stopPropagation();
        this._close();
    }

    _send() {
        if (!this.editor) {
            return;
        }
        const text = this.editor.getValue();
        if (text.length === 0) {
            this._close();
            return;
        }

        // The terminal WebSocket closes the session on any single message larger than
        // TerminalControlProtocol.MaxMessageBytes (256 KiB). Guard here (with headroom for
        // the bracketed-paste markers) so a big prompt fails with a clear message instead of
        // silently dropping the connection and losing the text.
        const maxSendBytes = 256 * 1024 - 1024;
        const byteLength = new TextEncoder().encode(text).length;
        if (byteLength > maxSendBytes) {
            this.app.showError(
                `This text is too large to send (${Math.round(byteLength / 1024)} KB; the limit is about 255 KB). ` +
                `Trim it or send it in smaller pieces.`);
            return; // keep the modal and draft so nothing is lost
        }

        const active = this.manager.getActiveTab();
        const ok = active?.instance?.injectText(text);
        if (!ok) {
            // Leave the modal and the draft intact so the text isn't lost.
            this.app.showError('No active terminal session to paste into.');
            return;
        }

        this.manager.setEditorDraft('');
        this._close({ skipDraftSave: true });
    }

    _close({ skipDraftSave = false } = {}) {
        if (this._closed || this.app.modalState !== this._modalState) return;
        this._skipDraftSave = skipDraftSave;
        const tab = this.manager.getActiveTab();
        const input = tab?.instance?.getHelperTextarea?.();
        if (tab === this._terminalTab && input?.isConnected && !this.manager.isDestroyed()) {
            // app.closeModal restores this only AFTER removing the background inert state.
            this._modalState.previousFocus = input;
        }
        this.app.closeModal();
    }

    _dispose() {
        if (this._closed) return;
        this._closed = true;

        if (this.editor && !this._skipDraftSave) {
            this.manager.setEditorDraft(this.editor.getValue());
        }

        this._dialog?.removeEventListener('keydown', this._onKeydown);
        this._dialog?.removeEventListener('click', this._onCloseClick, true);

        try { this._resizeObserver?.disconnect(); } catch (_) { /* no-op */ }
        this._resizeObserver = null;

        const model = this.editor?.getModel();
        try { this.editor?.dispose(); } catch (_) { /* no-op */ }
        try { model?.dispose(); } catch (_) { /* no-op */ }
        this.editor = null;
        this._dialog = null;
    }
}
