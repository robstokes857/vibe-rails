// Send to (VIBE-363): replay a session, ask for a handoff, and launch a terminal with the edited text.
// VIBE-370: the work belongs to the page, not to the dialog or the history sidebar that opened it, so
// the dialog can be minimized while it works and the Terminals view can be left. A dock button shows
// its progress; when the handoff is ready an alert offers Open. One handoff is written at a time.
//
// Only Cancel and the dialog's close button discard the work. Escape, or another dialog taking the
// shared modal container, minimizes it instead: a handoff takes minutes and a model request to write.

import { confirmDialog, escapeHtml } from './utils.js';
import { cardLabel } from './board-card-label.js';
import { renderSessionHandoffText } from './session-handoff-text.js';
import { dismissAppToast } from './toast-service.js';

/** The longest handoff a launch accepts; the handoff route returns the current value as maxChars. */
export const DEFAULT_HANDOFF_CHAR_LIMIT = 20000;
const formatCount = value => value.toLocaleString('en-US');
const DOCK_ID = 'script-run-dock';

// The handoff each page is writing or holding, by app.
const handoffs = new WeakMap();

/**
 * Opens Send to for one session. Asking again for the same session and target reopens the handoff
 * already under way; a different one asks before discarding it. Resolves when this handoff's work
 * has finished (or at once when nothing was started).
 *
 * `session` is `{ id, title, workingDirectory }`; `llm` is the parsed picker selection
 * (`{ cli, environmentName }`); `createTerminal(options)` returns an xterm.js Terminal.
 */
export async function openSessionHandoff(app, { session, llm, llmLabel, createTerminal }) {
    const current = handoffs.get(app);
    if (current?.isOpenable) {
        if (current.isFor(session.id, llm)) return current.show();
        const ready = current.state === 'ready';
        const replace = await confirmDialog({
            title: ready ? 'Discard the finished handoff?' : 'Stop the handoff in progress?',
            message: ready
                ? `The handoff from “${current.sessionName}” is ready and has not been launched. Starting another discards it.`
                : `Send to is still writing a handoff from “${current.sessionName}”. Only one is written at a time.`,
            confirmLabel: ready ? 'Discard and start' : 'Stop and start',
            cancelLabel: 'Keep it',
            danger: true
        });
        if (!replace) return undefined;
        if (handoffs.get(app) === current) current.end();
    }
    const handoff = new SessionHandoff(app, { session, llm, llmLabel, createTerminal });
    handoffs.set(app, handoff);
    handoff.show();
    return handoff.start();
}

class SessionHandoff {
    constructor(app, { session, llm, llmLabel, createTerminal }) {
        this.app = app;
        this.session = session;
        this.llm = llm;
        this.llmLabel = llmLabel;
        this.createTerminal = createTerminal;
        this.abort = new AbortController();
        // working → ready | failed; ended once launched, discarded or replaced.
        this.state = 'working';
        this.status = { label: 'Replaying the session', detail: 'Reading everything this session has recorded so far.', short: 'Replaying' };
        this.result = null;
        this.draft = '';
        this.error = '';
        this.charLimit = DEFAULT_HANDOFF_CHAR_LIMIT;
        this.dialog = null;
        this.discardOnClose = false;
        this.dockButton = null;
        this.alert = null;
        this.work = null;
    }

    /** Still worth reopening: being written, or written and not launched. */
    get isOpenable() { return this.state === 'working' || this.state === 'ready'; }

    get sessionName() { return this.session.title || this.session.id; }

    isFor(sessionId, llm) {
        return this.session.id === sessionId && this.llm.cli === llm.cli
            && (this.llm.environmentName || null) === (llm.environmentName || null);
    }

    start() {
        this.work ??= this._write();
        return this.work;
    }

    /** Opens (or reopens) the dialog for this handoff. */
    show() {
        if (this.state === 'ended') return this.work ?? Promise.resolve();
        // A reopen replaces this handoff's own dialog, if open: that close is not a minimize.
        this.dialog = null;
        const dialog = {};
        const opened = this.app.showModal(`Sending to ${this.llmLabel}`, `
            <div class="text-muted small mb-3">${escapeHtml(this.session.id)}</div>
            <div id="ch-resume-body"></div>
            <div class="d-flex justify-content-end align-items-center gap-2 mt-3" id="ch-resume-actions"></div>
        `, { onClose: () => this._dialogClosed(dialog) });
        dialog.body = document.getElementById('ch-resume-body');
        dialog.actions = document.getElementById('ch-resume-actions');
        if (opened === false || !dialog.body || !dialog.actions) {
            // Another dialog refused to close: keep working where the dock can reach it.
            if (this.isOpenable) this._showDock();
            return this.work ?? Promise.resolve();
        }
        // The header's close button discards; it runs before the dialog's own close handler.
        dialog.body.closest('.modal')?.addEventListener('click', event => {
            if (event.target.closest?.('[data-action="close-modal"]')) this.discardOnClose = true;
        }, true);
        dialog.actions.addEventListener('click', event => this._action(event.target.closest?.('[data-handoff-action]')?.dataset.handoffAction));
        this.dialog = dialog;
        this._removeDock();
        this._dismissAlert();
        this._render();
        return this.work ?? Promise.resolve();
    }

    /** Stops the work and removes everything this handoff shows. */
    end() {
        if (this.state === 'ended') return;
        this.state = 'ended';
        this.abort.abort();
        this._removeDock();
        this._dismissAlert();
        if (this.dialog) {
            this.dialog = null;
            this.app.closeModal();
        }
        if (handoffs.get(this.app) === this) handoffs.delete(this.app);
    }

    minimize() {
        if (!this.dialog) return;
        this.app.closeModal();
    }

    async _write() {
        const signal = this.abort.signal;
        try {
            const text = await renderSessionHandoffText(this.session.id, {
                request: (path, requestSignal) => this.app.apiCall(path, 'GET', null, { showLoading: false, signal: requestSignal }),
                createTerminal: this.createTerminal,
                signal,
                onProgress: ({ phase, done, total }) => {
                    if (this.state !== 'working' || !total) return;
                    const percent = Math.min(100, Math.floor(done / total * 100));
                    this._setStatus('Replaying the session', phase === 'loading'
                        ? `Reading the recording (${percent}%).`
                        : `Rendering the terminal output (${percent}%).`,
                    `${phase === 'loading' ? 'Reading' : 'Replaying'} ${percent}%`);
                }
            });
            if (this.state !== 'working') return;
            if (!text.trim()) throw new Error('This session has no terminal output to send.');

            this._setStatus('Writing the handoff',
                'GPT-6.1 Terra is reading the whole session. A long session can take several minutes.', 'Writing');
            // The backend answers a viberails.ai failure with JSON naming what went wrong; show it.
            const result = await this.app.apiCall(`/api/v1/chatHistory/${encodeURIComponent(this.session.id)}/handoff`, 'POST',
                { text }, { showLoading: false, signal, preferErrorResponseMessage: true });
            if (this.state !== 'working') return;

            this.result = { summary: result?.summary ?? '', boardCards: result?.boardCards || [] };
            this.draft = this.result.summary;
            this.charLimit = Number.isInteger(result?.maxChars) && result.maxChars > 0 ? result.maxChars : DEFAULT_HANDOFF_CHAR_LIMIT;
            this.state = 'ready';
            if (this.dialog) this._render();
            else this._announceReady();
        } catch (error) {
            if (this.state !== 'working' || error?.name === 'AbortError' || signal.aborted) return;
            this.state = 'failed';
            this.error = error?.message || 'Something went wrong.';
            if (this.dialog) {
                this._render();
            } else {
                this.app.showToast('Handoff failed', `Could not write the handoff from “${this.sessionName}”: ${this.error}`, 'error',
                    { requireDismiss: true });
                this.end();
            }
        }
    }

    // Updated in place, so focus stays on Cancel or Minimize while the status moves.
    _setStatus(label, detail, short) {
        this.status = { label, detail, short };
        this._updateDock();
        if (!this.dialog || this.state !== 'working') return;
        const body = this.dialog.body;
        const status = body.querySelector('#ch-resume-status');
        if (status) status.textContent = `${label}…`;
        body.querySelector('[role="status"]')?.setAttribute('aria-label', label);
        const statusDetail = body.querySelector('#ch-resume-status-detail');
        if (statusDetail) statusDetail.textContent = detail;
    }

    _dialogClosed(dialog) {
        if (this.dialog !== dialog) return;
        this.dialog = null;
        const discard = this.discardOnClose;
        this.discardOnClose = false;
        if (this.isOpenable && !discard) this._showDock();
        else this.end();
    }

    _action(action) {
        if (!action || !this.dialog) return;
        if (action === 'minimize') {
            this.minimize();
        } else if (action === 'cancel') {
            this.discardOnClose = true;
            this.app.closeModal();
        } else if (action === 'launch') {
            this._launch();
        }
    }

    _launch() {
        const summary = this.draft.trim();
        if (this.state !== 'ready' || !summary || summary.length > this.charLimit) return;
        const options = {
            cli: this.llm.cli,
            environmentName: this.llm.environmentName || null,
            workingDirectory: this.session.workingDirectory || null,
            resumeSummary: summary,
            resumeSessionId: this.session.id,
            title: this.session.title,
            forceNewTab: true
        };
        this.end();
        // Reopened from the dock, the dialog may sit over another view whose terminals are gone.
        const controller = this.app.terminalController;
        const manager = controller?.manager;
        if (manager && !manager.isDestroyed?.() && manager.container?.isConnected !== false) manager.startWithOptions(options);
        else void controller?.launchInFocus?.(options);
    }

    _render() {
        const { body, actions } = this.dialog;
        if (this.state === 'working') {
            body.innerHTML = `
                <div class="d-flex flex-column gap-2 py-2 text-muted">
                    <div class="d-flex align-items-center gap-2">
                        <span class="vb-spinner vb-spinner-sm flex-shrink-0" role="status" aria-label="${escapeHtml(this.status.label)}"></span>
                        <span id="ch-resume-status">${escapeHtml(this.status.label)}&hellip;</span>
                    </div>
                    <div class="small" id="ch-resume-status-detail" style="padding-left:1.75rem;">${escapeHtml(this.status.detail)}</div>
                </div>`;
            actions.innerHTML = `
                <button type="button" class="btn btn-secondary" data-handoff-action="cancel">Cancel</button>
                <button type="button" class="btn btn-outline-secondary" data-handoff-action="minimize"
                    title="Keep writing the handoff while you use VibeRails">
                    <i class="fa-solid fa-minus me-1" aria-hidden="true"></i>Minimize
                </button>`;
            return;
        }
        if (this.state === 'failed') {
            body.innerHTML = `<div class="text-danger small">Could not write the handoff: ${escapeHtml(this.error)}</div>`;
            actions.innerHTML = '<button type="button" class="btn btn-secondary" data-handoff-action="cancel">Close</button>';
            return;
        }
        body.innerHTML = `
            <div class="mb-2">
                <label class="form-label fw-semibold small text-muted text-uppercase mb-1" style="letter-spacing:.05em;" for="ch-resume-summary">Handoff</label>
                <textarea class="form-control" id="ch-resume-summary" rows="14"
                    style="font-size:0.85rem;resize:vertical;">${escapeHtml(this.draft)}</textarea>
                <div class="d-flex justify-content-end mt-1">
                    <small id="ch-resume-char-count"></small>
                </div>
            </div>
            ${renderCards(this.result.boardCards)}`;
        actions.innerHTML = `
            <button type="button" class="btn btn-secondary" data-handoff-action="cancel">Cancel</button>
            <button type="button" class="btn btn-primary" id="ch-resume-launch-btn" data-handoff-action="launch">
                <i class="fa-solid fa-terminal me-1" aria-hidden="true"></i>Launch Terminal
            </button>`;
        // Navigating closes the dialog; the handoff waits in the dock until it is launched.
        body.querySelectorAll('[data-resume-card]').forEach(link => {
            link.addEventListener('click', () => {
                if (this.dialog) this.app.navigate('board', { openCardId: link.dataset.resumeCard });
            });
        });
        const textarea = body.querySelector('#ch-resume-summary');
        const count = body.querySelector('#ch-resume-char-count');
        const launch = actions.querySelector('#ch-resume-launch-btn');
        const update = () => {
            this.draft = textarea.value;
            const length = this.draft.length;
            const overLimit = length > this.charLimit;
            count.textContent = `${formatCount(length)} / ${formatCount(this.charLimit)}`;
            count.classList.toggle('text-danger', overLimit);
            count.classList.toggle('text-muted', !overLimit);
            launch.disabled = length === 0 || overLimit;
        };
        textarea.addEventListener('input', update);
        update();
    }

    _announceReady() {
        this._updateDock();
        this._dismissAlert();
        this.alert = this.app.showToast('Handoff ready',
            `The handoff from “${this.sessionName}” for ${this.llmLabel} is ready to review.`, 'success', {
                requireDismiss: true,
                action: { label: 'Open', onClick: () => { this.alert = null; this.show(); } }
            }) ?? null;
    }

    _dismissAlert() {
        const alert = this.alert;
        this.alert = null;
        if (alert) dismissAppToast(alert);
    }

    // The minimized handoff shares the dock the script and installer windows use.
    _showDock() {
        if (!this.dockButton) {
            let dock = document.getElementById(DOCK_ID);
            if (!dock) {
                dock = document.createElement('div');
                dock.id = DOCK_ID;
                dock.setAttribute('aria-label', 'Minimized windows');
                document.body.appendChild(dock);
            }
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'vb-run-dock-button vb-handoff-dock-button';
            button.addEventListener('click', () => this.show());
            dock.appendChild(button);
            this.dockButton = button;
        }
        this._updateDock();
    }

    _updateDock() {
        const button = this.dockButton;
        if (!button) return;
        const status = this.state === 'ready' ? 'Ready' : this.status.short;
        button.textContent = `Send to ${this.llmLabel} · ${status}`;
        button.dataset.tone = this.state === 'ready' ? 'success' : 'waiting';
        button.title = this.state === 'ready'
            ? `Open the handoff from “${this.sessionName}”`
            : `Show the handoff from “${this.sessionName}” while it is written`;
    }

    _removeDock() {
        this.dockButton?.remove();
        this.dockButton = null;
    }
}

function renderCards(cards) {
    if (!cards.length) return '';
    return `<div class="mt-3">
        <div class="form-label fw-semibold small text-muted text-uppercase">Associated Board cards</div>
        <ul class="list-unstyled mb-2">${cards.map(card => `<li class="mb-1">
            <button type="button" class="btn btn-link p-0 text-start" data-resume-card="${escapeHtml(card.id)}">${escapeHtml(cardLabel(card))}</button>
            <div class="small text-muted">${escapeHtml(card.key)}</div>
        </li>`).join('')}</ul>
        <p class="small text-muted mb-0">These card references and guidance for reviewing them through VibeRails MCP will be added after the summary when the terminal launches.</p>
    </div>`;
}
