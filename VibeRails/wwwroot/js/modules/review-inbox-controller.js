// `review-inbox` view: "Reviews requested of you" (VB-50 slice D). Lists the review requests
// viberails.ai holds for the signed-in account's verified email, as the root backend last fetched
// them on the Board sync cadence; Check now asks viberails.ai at once. Reject posts a comment
// from here; Approve only opens the signing popup in its own console window, which shows the
// request, rebuilds the tree from this project's repository and signs it. Nothing on this page
// signs, and no password is ever typed into it.
import { escapeHtml } from './utils.js';
import { canAct, describeRefresh, emptyMessage, emptyState, requestRow, summarizeInbox } from './review-inbox-view.js';

const esc = escapeHtml;
const API = '/api/v1/review-inbox';
const VIEW = 'review-inbox';
/** The page re-reads the local cache once a minute while visible; the cache itself follows the backend's cadence. */
const POLL_MS = 60_000;
/** While a signing popup is open, poll faster so its outcome shows soon after the window closes. */
const SIGNING_POLL_MS = 5_000;

export class ReviewInboxController {
    constructor(app) {
        this.app = app;
        this.root = null;
        this.view = null;
        this.error = '';
        this.busy = '';
        this.generation = 0;
    }

    async loadView() {
        this.unload();
        const content = document.getElementById('app-content');
        if (!content) return;
        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('review-inbox-template');
        this.root = fragment.querySelector(`[data-view="${VIEW}"]`);
        content.appendChild(fragment);
        this.abort = new AbortController();
        this.bindEvents();
        this.render();
        const generation = ++this.generation;
        await this.load(generation);
        if (this.generation !== generation) return;
        this.schedulePoll();
        this.unsubscribeUiVisible = this.app.onUiVisible(() => void this.pollTick());
    }

    unload() {
        this.unsubscribeUiVisible?.();
        this.unsubscribeUiVisible = null;
        if (this.pollTimer) window.clearTimeout(this.pollTimer);
        this.pollTimer = null;
        this.abort?.abort();
        this.abort = null;
        this.pollPending = false;
        this.generation += 1;
        this.root = null;
    }

    schedulePoll() {
        if (this.pollTimer) window.clearTimeout(this.pollTimer);
        const signing = this.view?.requests?.some(item => item.signing?.outcome === 'signing');
        this.pollTimer = window.setTimeout(() => void this.pollTick(), signing ? SIGNING_POLL_MS : POLL_MS);
    }

    async pollTick() {
        if (this.app.currentView !== VIEW || !this.root) return;
        if (!this.app.isUiHidden() && !this.pollPending && !this.busy) {
            this.pollPending = true;
            try { await this.load(this.generation); }
            finally { this.pollPending = false; }
        }
        if (this.root) this.schedulePoll();
    }

    // ------------------------------------------------------------------ data

    request(path, method = 'GET', body = null) {
        return this.app.apiCall(API + path, method, body, { showLoading: false, signal: this.abort?.signal, preferErrorResponseMessage: true });
    }

    async load(generation) {
        try {
            const view = await this.request('');
            if (this.generation !== generation) return;
            this.view = view;
            this.error = '';
        } catch (error) {
            if (error?.name === 'AbortError' || this.generation !== generation) return;
            this.error = error?.message || 'Could not read the review requests.';
        }
        this.render();
    }

    async refreshNow() {
        if (this.busy) return;
        this.busy = 'refresh';
        this.render();
        try {
            this.view = await this.request('/refresh', 'POST', {});
            this.error = '';
        } catch (error) {
            if (error?.name !== 'AbortError') this.error = error?.message || 'Could not check viberails.ai.';
        } finally {
            this.busy = '';
            this.render();
        }
    }

    /** Opens the signing popup for one request. The popup, not this page, shows the request and signs. */
    async approve(reference) {
        if (this.busy) return;
        this.busy = reference;
        this.render();
        try {
            const result = await this.request(`/${encodeURIComponent(reference)}/approve`, 'POST', {});
            this.app.showToast('Review request', result?.message || 'The signing popup opened in its own window.', 'info');
            await this.load(this.generation);
        } catch (error) {
            if (error?.name !== 'AbortError') this.app.showError(error?.message || 'Could not open the signing popup.');
        } finally {
            this.busy = '';
            this.render();
            this.schedulePoll();
        }
    }

    async reject(reference, comment) {
        if (this.busy) return;
        this.busy = reference;
        this.render();
        try {
            const result = await this.request(`/${encodeURIComponent(reference)}/reject`, 'POST', { comment });
            this.app.showToast('Review request', result?.message || 'Your rejection was recorded.', 'success');
            await this.load(this.generation);
        } catch (error) {
            if (error?.name !== 'AbortError') this.app.showError(error?.message || 'Could not record the rejection.');
        } finally {
            this.busy = '';
            this.render();
        }
    }

    // -------------------------------------------------------------------- ui

    bindEvents() {
        this.root?.addEventListener('click', (event) => {
            const external = event.target.closest('a[data-ri-external]');
            if (external && this.root?.contains(external) && typeof window.__viberails_openExternal__ === 'function') {
                // The VS Code webview cannot follow target=_blank; the host opens the browser instead.
                event.preventDefault();
                window.__viberails_openExternal__(external.href);
                return;
            }
            const button = event.target.closest('[data-ri-action]');
            if (!button || !this.root?.contains(button)) return;
            const action = button.dataset.riAction;
            if (action === 'refresh') { void this.refreshNow(); return; }
            if (action === 'back') { this.app.navigate('pr-reviews'); return; }
            const row = button.closest('[data-ri-row]');
            const reference = row?.dataset.riRow;
            if (!reference) return;
            if (action === 'approve') { void this.approve(reference); return; }
            if (action === 'reject') {
                const form = row.querySelector('[data-ri-reject-form]');
                if (form) { form.hidden = false; form.querySelector('[data-ri-comment]')?.focus(); }
                return;
            }
            if (action === 'cancel-reject') {
                const form = row.querySelector('[data-ri-reject-form]');
                if (form) form.hidden = true;
                return;
            }
            if (action === 'send-reject') {
                const comment = row.querySelector('[data-ri-comment]')?.value?.trim() || '';
                if (!comment) { this.app.showError('Say why in a comment when you reject a review.'); return; }
                void this.reject(reference, comment);
            }
        });
    }

    render() {
        if (!this.root) return;
        const view = this.view;
        const status = this.root.querySelector('[data-ri-status]');
        if (status) {
            const who = view?.email ? ` for ${esc(view.email)}` : '';
            const line = view ? describeRefresh(view) : this.error ? 'The list could not be read.' : 'Loading…';
            status.innerHTML = `<span class="ri-muted">${esc(line)}${who}</span>`
                + `<button type="button" class="btn btn-sm btn-outline-secondary" data-ri-action="refresh" ${this.busy || !view?.hasApiKey ? 'disabled' : ''}>`
                + `${this.busy === 'refresh' ? 'Checking…' : 'Check now'}</button>`;
        }
        const problem = this.root.querySelector('[data-ri-problem]');
        if (problem) {
            const message = this.error || (view?.fetchedUtc && view?.lastError ? `The last automatic check failed: ${view.lastError}` : '');
            problem.hidden = !message;
            problem.innerHTML = message ? `<div class="ri-alert" data-tone="${this.error ? 'danger' : 'warning'}" role="status">${esc(message)}</div>` : '';
        }
        const popup = this.root.querySelector('[data-ri-popup-note]');
        if (popup) popup.hidden = !view || view.popupAvailable;
        const list = this.root.querySelector('[data-ri-list]');
        if (!list) return;
        const requests = Array.isArray(view?.requests) ? view.requests : [];
        if (requests.length === 0) {
            list.innerHTML = emptyState(this.error && !view ? this.error : emptyMessage(view));
            this.setCount(summarizeInbox(view));
            return;
        }
        const summary = summarizeInbox(view);
        this.setCount(summary);
        // A render replaces every row, and the poll renders while the person may be typing a rejection:
        // keep each open form and its text across the replacement.
        const drafts = new Map();
        for (const form of list.querySelectorAll('[data-ri-reject-form]:not([hidden])')) {
            const reference = form.closest('[data-ri-row]')?.dataset.riRow;
            if (reference) drafts.set(reference, form.querySelector('[data-ri-comment]')?.value ?? '');
        }
        const focused = document.activeElement?.matches?.('[data-ri-comment]') ? document.activeElement.closest('[data-ri-row]')?.dataset.riRow : null;
        list.innerHTML = requests.map(item => requestRow(item, view)).join('')
            + (summary.more ? `<p class="ri-muted mt-2">viberails.ai lists the newest 100; open it to see the rest.</p>` : '');
        for (const [reference, text] of drafts) {
            const row = list.querySelector(`[data-ri-row="${CSS.escape(reference)}"]`);
            const form = row?.querySelector('[data-ri-reject-form]');
            const comment = form?.querySelector('[data-ri-comment]');
            if (!form || !comment) continue;
            form.hidden = false;
            comment.value = text;
            if (reference === focused) {
                comment.focus();
                comment.setSelectionRange(text.length, text.length);
            }
        }
        if (this.busy) {
            for (const button of list.querySelectorAll('button')) button.disabled = true;
        } else {
            for (const row of list.querySelectorAll('[data-ri-row]')) {
                const item = requests.find(candidate => candidate.id === row.dataset.riRow);
                const approve = row.querySelector('[data-ri-action="approve"]');
                if (approve && item) approve.disabled = !(canAct(item, view) && view.popupAvailable);
            }
        }
    }

    setCount(summary) {
        const count = this.root?.querySelector('[data-ri-count]');
        if (count) count.textContent = summary.open ? `${summary.open} waiting` : summary.total ? `${summary.total}` : '';
    }
}
