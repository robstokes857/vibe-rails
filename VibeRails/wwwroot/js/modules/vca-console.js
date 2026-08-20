const DEFAULT_CONSOLE_MESSAGE = [
    'VCA hook console ready.',
    'Run a hook check to see the output shown during git commit.'
].join('\n');

export function normalizeVcaConsoleText(value) {
    return String(value ?? '')
        .replace(/\r\n?/g, '\n')
        .replace(/\0/g, '');
}

export function formatVcaDuration(durationMs) {
    const value = Number(durationMs);
    if (!Number.isFinite(value) || value < 0) return '';
    if (value < 1000) return `${Math.round(value)} ms`;
    return `${(value / 1000).toFixed(value < 10000 ? 1 : 0)} s`;
}

export function getVcaPreviewTone(response) {
    const status = String(response?.status ?? '').trim().toLowerCase();
    const exitCode = Number(response?.exitCode);

    if (response?.success === false || (Number.isFinite(exitCode) && exitCode !== 0) ||
        ['failed', 'failure', 'error', 'blocked'].includes(status)) {
        return 'danger';
    }

    if (['warning', 'warn', 'attention'].includes(status)) return 'warning';
    if (response?.success === true || exitCode === 0 || ['passed', 'pass', 'success', 'complete', 'completed'].includes(status)) {
        return 'success';
    }

    return 'neutral';
}

export function buildVcaPreviewViewModel(response = {}) {
    const tone = getVcaPreviewTone(response);
    const title = String(response?.title || '').trim() || 'Hook check';
    const status = String(response?.status || '').trim() ||
        (tone === 'success' ? 'Passed' : tone === 'danger' ? 'Failed' : 'Complete');
    const duration = formatVcaDuration(response?.durationMs);
    const exitCode = Number(response?.exitCode);
    const metaParts = [];

    if (duration) metaParts.push(`Finished in ${duration}`);
    if (Number.isFinite(exitCode)) metaParts.push(`Exit code ${exitCode}`);

    let output = normalizeVcaConsoleText(response?.output).trimEnd();
    if (!output) {
        output = `${title}\n${status}`;
    }

    return {
        title,
        status,
        tone,
        output,
        meta: metaParts.join(' · ') || status
    };
}

export async function copyVcaConsoleText(text, options = {}) {
    const value = normalizeVcaConsoleText(text);
    if (!value) return false;

    const clipboard = options.clipboard ?? globalThis.navigator?.clipboard;
    if (clipboard?.writeText) {
        try {
            await clipboard.writeText(value);
            return true;
        } catch {
            // Some embedded webviews expose Clipboard but reject the call. Fall through
            // to the selection-based copy path when a document is available.
        }
    }

    const documentRef = options.documentRef ?? globalThis.document;
    if (!documentRef?.body || typeof documentRef.execCommand !== 'function') return false;

    const textarea = documentRef.createElement('textarea');
    textarea.value = value;
    textarea.setAttribute('readonly', '');
    textarea.style.position = 'fixed';
    textarea.style.opacity = '0';
    textarea.style.pointerEvents = 'none';
    documentRef.body.appendChild(textarea);
    textarea.select();

    try {
        return documentRef.execCommand('copy') === true;
    } finally {
        textarea.remove();
    }
}

/**
 * Small, reusable plain-text console for hook output. It deliberately writes with
 * textContent: hook output can include filenames and rule messages we must never
 * interpret as markup.
 */
export class VcaConsole {
    constructor(root, options = {}) {
        this.root = root || null;
        this.output = this.root?.querySelector('[data-vca-console-output]') || null;
        this.state = this.root?.querySelector('[data-vca-console-state]') || null;
        this.meta = this.root?.querySelector('[data-vca-console-meta]') || null;
        this.spinner = this.root?.querySelector('[data-vca-console-spinner]') || null;
        this.clipboard = options.clipboard;
        this.documentRef = options.documentRef;
        this.defaultMessage = normalizeVcaConsoleText(options.defaultMessage || DEFAULT_CONSOLE_MESSAGE);
        this.runningMessage = String(options.runningMessage || 'Starting VCA hook check…');
        this.failureMessage = String(options.failureMessage || 'The hook check could not be completed.');
        this.failureMeta = String(options.failureMeta || 'Hook check failed');

        if (this.output && !this.output.textContent.trim()) {
            this.output.textContent = this.defaultMessage;
        }
    }

    setBusy(isBusy, message = 'Running hook check…') {
        if (this.root) {
            this.root.setAttribute('aria-busy', String(Boolean(isBusy)));
            this.root.classList.toggle('is-running', Boolean(isBusy));
        }
        if (this.state) this.state.textContent = isBusy ? 'Running' : 'Ready';
        if (this.spinner) this.spinner.hidden = !isBusy;
        if (isBusy && this.meta) this.meta.textContent = message;
    }

    begin(command = 'vb --vca-hook pre-commit') {
        this.setTone('neutral');
        this.write(`$ ${command}\n\n${this.runningMessage}`);
        this.setBusy(true);
    }

    complete(response) {
        const viewModel = buildVcaPreviewViewModel(response);
        this.write(viewModel.output);
        this.setBusy(false);
        this.setTone(viewModel.tone);
        if (this.state) this.state.textContent = viewModel.status;
        if (this.meta) this.meta.textContent = viewModel.meta;
        return viewModel;
    }

    fail(error) {
        const message = normalizeVcaConsoleText(error?.message || error || this.failureMessage);
        this.write(`[error] ${message}`);
        this.setBusy(false);
        this.setTone('danger');
        if (this.state) this.state.textContent = 'Error';
        if (this.meta) this.meta.textContent = this.failureMeta;
    }

    finishStream({ tone = 'neutral', state = 'Complete', meta = 'Preflight finished' } = {}) {
        this.setBusy(false);
        this.setTone(tone);
        if (this.state) this.state.textContent = state;
        if (this.meta) this.meta.textContent = meta;
    }

    write(value, { append = false } = {}) {
        if (!this.output) return;
        const text = normalizeVcaConsoleText(value);
        if (append && this.output.firstChild) {
            // Append a text node instead of reassigning textContent: rebuilding the
            // string re-reads and re-writes the ENTIRE console for every streamed
            // line — O(n²) over a long preflight run. _tailChar tracks the last
            // written character so the newline separator logic stays identical
            // without re-reading the accumulated text.
            if (this._tailChar === undefined) {
                this._tailChar = this.output.textContent.slice(-1);
            }
            const appended = `${this._tailChar === '\n' ? '' : '\n'}${text}`;
            this.output.appendChild(document.createTextNode(appended));
            if (appended.length > 0) this._tailChar = appended.slice(-1);
        } else {
            this.output.textContent = text;
            this._tailChar = text.slice(-1);
        }
        this.output.scrollTop = this.output.scrollHeight;
    }

    writeLine(value) {
        this.write(value, { append: true });
    }

    report(value, { tone = 'neutral', state = '', meta = '' } = {}) {
        this.writeLine(value);
        this.setTone(tone);
        if (state && this.state) this.state.textContent = state;
        if (meta && this.meta) this.meta.textContent = meta;
    }

    clear() {
        if (this.root?.getAttribute?.('aria-busy') === 'true') return false;
        this.write(this.defaultMessage);
        this.setTone('neutral');
        if (this.state) this.state.textContent = 'Ready';
        if (this.meta) this.meta.textContent = 'Console cleared';
        return true;
    }

    async copy() {
        return copyVcaConsoleText(this.output?.textContent || '', {
            clipboard: this.clipboard,
            documentRef: this.documentRef
        });
    }

    setTone(tone) {
        if (!this.root) return;
        this.root.dataset.tone = ['success', 'warning', 'danger'].includes(tone) ? tone : 'neutral';
    }
}
