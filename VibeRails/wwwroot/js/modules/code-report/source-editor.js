import { ensureMonaco } from '../monaco-loader.js';
import { getMonacoLanguageForPath } from '../code-analyzer-dashboard.js';

/** Read-only source owned by one file modal. Late loads cannot recreate a closed editor. */
export class QualitySourceEditor {
    constructor(root, app, path, scope, onClose) {
        this.root = root;
        this.app = app;
        this.path = path;
        this.scope = scope;
        this.mode = scope ? 'file' : 'excerpt';
        this.status = root.querySelector('[data-source-status]');
        this.host = root.querySelector('[data-source-editor]');
        this.buttons = [...root.querySelectorAll('[data-source-mode]')];
        this.buttons.forEach(button => button.addEventListener('click', () => {
            this.mode = button.dataset.sourceMode;
            this.render();
        }));
        this.request = new AbortController();
        this.loading = Boolean(scope);
        this.render();
        this.ready = this.mount(onClose);
        if (scope) void this.load();
    }

    async mount(onClose) {
        const monaco = await ensureMonaco();
        if (this.closed) return;
        if (!monaco) {
            this.editorFailed = true;
            this.render();
            return;
        }
        this.host.replaceChildren();
        this.editor = monaco.editor.create(this.host, {
            value: '', language: getMonacoLanguageForPath(this.path), theme: 'viberails-dark',
            readOnly: true, domReadOnly: true, automaticLayout: true, minimap: { enabled: false },
            scrollBeyondLastLine: false, fontSize: 14, wordWrap: 'off', contextmenu: false,
            ariaLabel: `Source for ${this.path}`, padding: { top: 12 }, tabFocusMode: true
        });
        this.editor.addCommand(monaco.KeyCode.Escape, onClose,
            'editorTextFocus && !suggestWidgetVisible && !findWidgetVisible'
            + ' && !renameInputVisible && !inSnippetMode && !parameterHintsVisible && !editorHoverVisible');
        this.render();
    }

    async load() {
        try {
            const query = `path=${encodeURIComponent(this.path)}${this.scope === 'unpushed' ? '&scope=unpushed' : ''}`;
            const response = await this.app.apiCall(`/api/v1/code-analyzer/source?${query}`, 'GET', null,
                { showLoading: false, preferErrorResponseMessage: true, signal: this.request.signal });
            if (this.closed) return;
            if (response?.exists === false) this.notice = 'The source file is missing or unreadable.';
            else if (response?.isBinary) this.notice = 'Binary source cannot be displayed.';
            else if (response?.truncated) this.notice = 'The source file is too large to display.';
            else if (typeof response?.content === 'string') this.content = response.content;
            else this.notice = 'Source is unavailable.';
        } catch (error) {
            if (this.closed) return;
            this.notice = `Source could not be loaded: ${error.message || 'Try reopening the file.'}`;
        }
        if (this.closed) return;
        this.loading = false;
        if (this.content === undefined) this.mode = 'excerpt';
        this.render();
    }

    select(item) {
        this.item = item;
        this.render();
    }

    render() {
        if (this.closed) return;
        const full = this.mode === 'file' && typeof this.content === 'string';
        const text = full ? this.content : this.item?.snippet || '';
        const origin = full ? (this.scope === 'unpushed' ? 'Current HEAD' : 'Current working tree') : 'Captured excerpt';
        this.status.textContent = this.loading && this.mode === 'file' ? 'Loading source…'
            : `${this.notice ? `${this.notice} ` : ''}${origin} · Read-only${!full && !text ? ' · No excerpt saved for this measurement.' : ''}`;
        if (this.editorFailed) this.status.textContent += ' Editor unavailable; showing plain text.';
        for (const button of this.buttons) {
            button.setAttribute('aria-pressed', String(button.dataset.sourceMode === this.mode));
            if (button.dataset.sourceMode === 'file') button.disabled = !full && this.content === undefined;
        }
        const line = Math.max(1, Math.trunc(this.item?.line || 1));
        if (!this.editor) {
            const pre = this.host.ownerDocument.createElement('pre');
            pre.className = 'quality-source-fallback';
            pre.textContent = text || (this.loading ? 'Loading source…' : 'No source available.');
            this.host.replaceChildren(pre);
            return;
        }
        if (this.editor.getValue() !== text) this.editor.setValue(text);
        this.editor.updateOptions({ lineNumbers: full ? 'on' : number => String(number + line - 1) });
        const target = full ? Math.min(line, this.editor.getModel().getLineCount()) : 1;
        this.editor.setPosition({ lineNumber: target, column: 1 });
        this.editor.revealLineInCenter(target);
    }

    destroy() {
        if (this.closed) return;
        this.closed = true;
        this.request.abort();
        const model = this.editor?.getModel();
        this.editor?.dispose();
        model?.dispose();
        this.editor = null;
    }
}
