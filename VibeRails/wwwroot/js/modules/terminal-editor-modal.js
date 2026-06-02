import { ensureMonaco } from './monaco-loader.js';

/**
 * "Open in text editor" — a Monaco-backed scratchpad for the terminal kebab menu.
 *
 * Composing long / multi-line input (e.g. an agent prompt) in a raw TUI is
 * painful. This opens a real editor in a modal; "Send to terminal" injects the
 * text into the active terminal session as a bracketed paste (paste-only — the
 * cursor is left after the text so the user presses Enter themselves).
 *
 * Why a custom overlay instead of app.showModal: Monaco needs an explicitly
 * sized flex container — app.showModal's auto-height dialog collapses the editor
 * to 0px. We mirror the sandbox diff viewer's sized-overlay approach (see
 * .vb-terminal-editor-modal in style.css).
 *
 * Lifecycle: app.closeModal() only clears #modal-container — it runs no teardown.
 * So _close() owns all cleanup (dispose the editor, drop the ResizeObserver,
 * remove the keydown listener) and must run on every close path. The session
 * draft lives on the manager so it survives reopen but not a fresh session.
 */
export class TerminalEditorModal {
    constructor(manager) {
        this.manager = manager;
        this.app = manager.app;
        this.editor = null;
        this._resizeObserver = null;
        this._closed = false;
        this._onKeydown = this._onKeydown.bind(this);
    }

    async show() {
        const container = document.getElementById('modal-container');
        if (!container) {
            return;
        }

        container.innerHTML = `
            <div class="modal fade show d-block vb-terminal-editor-modal" tabindex="-1">
                <div class="modal-dialog">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">Open in text editor</h5>
                            <button type="button" class="btn-close" data-action="close-modal" aria-label="Close"></button>
                        </div>
                        <div class="modal-body">
                            <div class="vb-terminal-editor-host" id="vb-terminal-editor-host"></div>
                        </div>
                        <div class="modal-footer">
                            <span class="vb-terminal-editor-hint text-muted small me-auto">Ctrl/⌘+Enter to send · Esc to close</span>
                            <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                            <button type="button" class="btn btn-primary" id="vb-terminal-editor-send-btn">Send to terminal</button>
                        </div>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show"></div>
        `;

        // CSP-safe wiring (no inline handlers). Arrow wrappers so the click Event
        // is not passed through as _close's options argument.
        container.querySelectorAll('[data-action="close-modal"]')
            .forEach(btn => btn.addEventListener('click', () => this._close()));
        container.querySelector('#vb-terminal-editor-send-btn')
            ?.addEventListener('click', () => this._send());

        // Capture-phase Esc: the app-wide handler (app.js setupKeyboardShortcuts)
        // calls closeModal()+goBack() on Escape in the bubble phase, which would
        // wipe the modal without disposing Monaco or saving the draft. Handling it
        // in the capture phase and stopping propagation lets _close() run instead.
        document.addEventListener('keydown', this._onKeydown, true);

        const monaco = await ensureMonaco();
        if (this._closed) {
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
    }

    _onKeydown(e) {
        if (e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            this._close();
        }
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
        active.instance.focusInput?.();
    }

    _close({ skipDraftSave = false } = {}) {
        if (this._closed) {
            return;
        }
        this._closed = true;

        if (this.editor && !skipDraftSave) {
            this.manager.setEditorDraft(this.editor.getValue());
        }

        document.removeEventListener('keydown', this._onKeydown, true);

        try { this._resizeObserver?.disconnect(); } catch (_) { /* no-op */ }
        this._resizeObserver = null;

        try { this.editor?.dispose(); } catch (_) { /* no-op */ }
        this.editor = null;

        this.app.closeModal();
    }
}
