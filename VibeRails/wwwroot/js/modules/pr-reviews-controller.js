// `pr-reviews` view: the lead-review tracker for the current project's GitHub pull requests.
// Two pages on one view: the list (no data) and one PR's detail ({ number }). The list polls the
// review queue and GitHub lifecycle once a minute while visible; the detail page never polls, so a
// half-written review is never re-rendered out from under the user.
import { confirmDialog, escapeHtml } from './utils.js';
import {
    LIFECYCLE_FILTERS, STATUS_ORDER, BLOCKING_VERDICT, statusMeta, summarizePullRequests, retiredBlockerNote,
    repositoryLink, sectionHeading, emptyState, trackedRow, openRow, nextUpCard, detailHeader, detailAlerts,
    pendingReviewForm, reviewCard, pullRequestEditForm
} from './pr-reviews-view.js';

const esc = escapeHtml;
const API = '/api/v1/pr-reviews';
const POLL_MS = 60_000;
const VIEW = 'pr-reviews';

export class PrReviewsController {
    constructor(app) {
        this.app = app;
        this.root = null;
        this.generation = 0;
        // Filters survive a trip to a PR's detail page and back.
        this.filters = { lifecycle: 'live', status: 'all', query: '' };
        this.state = this.emptyState(null);
    }

    emptyState(number) {
        return {
            number, list: null, listError: '', queue: null, queueError: '', queueLoading: false, queueLoadedAt: null,
            starting: null, detail: null, detailError: '', busy: '', editingReviewId: null, editingPr: false
        };
    }

    async loadView(data = {}) {
        this.unload();
        const content = document.getElementById('app-content');
        if (!content) return;
        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('pr-reviews-template');
        this.root = fragment.querySelector(`[data-view="${VIEW}"]`);
        content.appendChild(fragment);
        const number = Number.parseInt(data?.number, 10);
        this.state = this.emptyState(Number.isInteger(number) && number > 0 ? number : null);
        this.abort = new AbortController();
        this.bindEvents();
        this.showPage();
        const root = this.root;
        const generation = ++this.generation;
        if (this.state.number) {
            await this.loadDetail(generation);
            return;
        }
        this.renderList();
        await this.loadList(generation);
        if (this.root !== root) return;
        void this.refreshGitHub(generation);
        this.pollTimer = window.setInterval(() => void this.pollTick(), POLL_MS);
        this.unsubscribeUiVisible = this.app.onUiVisible(() => void this.pollTick());
    }

    unload() {
        this.unsubscribeUiVisible?.();
        this.unsubscribeUiVisible = null;
        if (this.pollTimer) window.clearInterval(this.pollTimer);
        this.pollTimer = null;
        this.abort?.abort();
        this.abort = null;
        this.pollPending = false;
        this.generation += 1;
        this.root = null;
    }

    /** One minute poll. Nothing while hidden, never a second round behind a slow first. */
    pollTick() {
        if (this.app.currentView !== VIEW || this.state.number || !this.root) return null;
        if (this.app.isUiHidden() || this.pollPending) return null;
        return this.refreshGitHub(this.generation);
    }

    // ------------------------------------------------------------------ data

    request(path, method = 'GET', body = null) {
        return this.app.apiCall(API + path, method, body, {
            showLoading: false, preferErrorResponseMessage: true, signal: this.abort?.signal
        });
    }

    isCurrent(generation) {
        return this.root !== null && generation === this.generation;
    }

    async loadList(generation) {
        try {
            const list = await this.request('');
            if (!this.isCurrent(generation)) return;
            this.state.list = list;
            this.state.listError = '';
        } catch (error) {
            if (error?.name === 'AbortError' || !this.isCurrent(generation)) return;
            this.state.listError = error?.message || 'Could not load PR reviews.';
        }
        this.renderList();
    }

    /**
     * The open-PR queue and the tracked PRs' lifecycle both come from GitHub, so they refresh
     * together; the local list is re-read afterwards because an agent may have written a review.
     */
    async refreshGitHub(generation) {
        if (!this.state.list?.repository || this.pollPending) return;
        this.pollPending = true;
        this.state.queueLoading = true;
        this.renderOpen();
        try {
            const tracked = this.state.list.pullRequests?.length || 0;
            const [queue, sync] = await Promise.allSettled([
                this.request('/queue'),
                tracked ? this.request('/github/sync', 'POST') : Promise.resolve(null)
            ]);
            if (!this.isCurrent(generation)) return;
            if (queue.status === 'fulfilled') {
                this.state.queue = queue.value;
                this.state.queueError = '';
                this.state.queueLoadedAt = new Date().toISOString();
            } else if (queue.reason?.name !== 'AbortError') {
                this.state.queueError = queue.reason?.message || 'Could not reach GitHub.';
            }
            if (sync.status === 'rejected' && sync.reason?.name !== 'AbortError') {
                console.warn('[PrReviews] GitHub lifecycle sync failed:', sync.reason?.message);
            }
            await this.loadList(generation);
        } finally {
            if (this.isCurrent(generation)) {
                this.state.queueLoading = false;
                this.pollPending = false;
                this.renderOpen();
            }
        }
    }

    async loadDetail(generation) {
        try {
            const detail = await this.request(`/${this.state.number}`);
            if (!this.isCurrent(generation)) return;
            this.state.detail = detail;
            this.state.detailError = '';
        } catch (error) {
            if (error?.name === 'AbortError' || !this.isCurrent(generation)) return;
            this.state.detailError = error?.message || 'Could not load this PR.';
        }
        this.renderDetail();
    }

    /** Every detail mutation has the same shape: call, toast, re-read, report. */
    async mutate(work, notice, busy = 'saving') {
        if (this.state.busy) return false;
        const generation = this.generation;
        this.state.busy = busy;
        this.syncBusyButtons();
        try {
            await work();
            if (!this.isCurrent(generation)) return false;
            if (notice) this.app.showToast('PR Reviews', notice, 'success');
            this.state.busy = '';
            await this.loadDetail(generation);
            return true;
        } catch (error) {
            if (error?.name !== 'AbortError' && this.isCurrent(generation)) {
                this.app.showToast('PR Reviews', error?.message || 'The change was not saved.', 'error');
            }
            return false;
        } finally {
            if (this.isCurrent(generation)) {
                this.state.busy = '';
                this.syncBusyButtons();
            }
        }
    }

    async startFromQueue(number) {
        if (this.state.starting !== null) return;
        const entries = [...(this.state.queue?.queue || []), ...(this.state.queue?.skipped || [])];
        const entry = entries.find(item => item.number === number);
        if (!entry) return;
        const generation = this.generation;
        this.state.starting = number;
        this.renderOpen();
        try {
            await this.request(`/${number}/reviews/start`, 'POST', {
                title: entry.title, author: entry.author, openedAt: entry.openedUtc, headSha: entry.headSha
            });
            if (this.isCurrent(generation)) this.app.navigate(VIEW, { number });
        } catch (error) {
            if (error?.name !== 'AbortError' && this.isCurrent(generation)) {
                this.app.showToast('PR Reviews', error?.message || `Could not start a review for #${number}.`, 'error');
            }
        } finally {
            if (this.isCurrent(generation)) {
                this.state.starting = null;
                this.renderOpen();
            }
        }
    }

    // ------------------------------------------------------------------ rendering

    q(selector) {
        return this.root?.querySelector(selector) || null;
    }

    showPage() {
        const detail = Boolean(this.state.number);
        const list = this.q('[data-pr-list-page]');
        const page = this.q('[data-pr-detail-page]');
        if (list) list.hidden = detail;
        if (page) page.hidden = !detail;
    }

    renderList() {
        const { list, listError } = this.state;
        const repo = this.q('[data-pr-repository]');
        if (repo) {
            repo.innerHTML = list?.repository
                ? `Lead-review log for ${repositoryLink(list.repositoryUrl, list.repository)}`
                : 'Lead-review log for this project\'s GitHub pull requests';
        }
        const problem = this.q('[data-pr-problem]');
        const available = Boolean(list?.repository);
        if (problem) {
            const message = listError || (list && !available ? list.problem : '');
            problem.hidden = !message;
            problem.innerHTML = message ? `<div class="pr-alert" data-tone="${listError ? 'danger' : 'info'}" role="status">${esc(message)}</div>` : '';
        }
        for (const section of this.root?.querySelectorAll('[data-pr-needs-repo]') || []) section.hidden = !available;
        if (!available) return;

        const summary = summarizePullRequests(list.pullRequests, this.filters);
        const pending = this.q('[data-pr-pending-section]');
        if (pending) {
            pending.hidden = summary.pending.length === 0;
            pending.innerHTML = summary.pending.length
                ? sectionHeading('Recovery queue: finish or re-run these', 'In review', summary.pending.length, 'warning')
                    + `<div class="pr-list">${summary.pending.map(pr => trackedRow(pr, 'warning')).join('')}</div>`
                : '';
        }
        const blocked = this.q('[data-pr-blocked-section]');
        if (blocked) {
            const note = retiredBlockerNote(summary.retiredBlockers);
            blocked.hidden = summary.blocked.length === 0 && !note;
            blocked.innerHTML = summary.blocked.length || note
                ? sectionHeading('Reviewed: must not merge', 'Blocked', summary.blocked.length, 'danger')
                    + (summary.blocked.length
                        ? `<div class="pr-list">${summary.blocked.map(pr => trackedRow(pr, 'danger')).join('')}</div>`
                        : emptyState('No open PR is currently blocked.'))
                    + (note ? `<p class="pr-muted mt-2">${esc(note)}</p>` : '')
                : '';
        }

        const count = this.q('[data-pr-all-count]');
        if (count) {
            const total = list.pullRequests.length;
            count.textContent = summary.visible.length === total ? String(total) : `${summary.visible.length} / ${total}`;
        }
        const lifecycle = this.q('[data-pr-lifecycle-filters]');
        if (lifecycle) {
            lifecycle.innerHTML = LIFECYCLE_FILTERS.map(([value, label]) =>
                `<button type="button" class="btn btn-sm btn-outline-secondary" data-pr-lifecycle="${esc(value)}"
                    aria-pressed="${value === this.filters.lifecycle}">${esc(label)} ${esc(summary.lifecycleCounts[value] ?? 0)}</button>`).join('');
        }
        const status = this.q('[data-pr-status-filters]');
        if (status) {
            status.innerHTML = [['all', 'All'], ...STATUS_ORDER.map(key => [key, statusMeta(key).label])].map(([value, label]) =>
                `<button type="button" class="btn btn-sm btn-outline-secondary" data-pr-status="${esc(value)}"
                    aria-pressed="${value === this.filters.status}">${esc(label)} ${esc(summary.statusCounts[value] ?? 0)}</button>`).join('');
        }
        const body = this.q('[data-pr-all-body]');
        if (body) {
            body.innerHTML = summary.visible.length
                ? `<div class="pr-list">${summary.visible.map(pr => trackedRow(pr)).join('')}</div>`
                : emptyState(list.pullRequests.length
                    ? 'No tracked PR matches these filters.'
                    : 'No PRs yet. Start a review from the open PRs above, or let an agent start one with start_pr_review.');
        }
    }

    renderOpen() {
        const { queue, queueError, queueLoading, queueLoadedAt, starting } = this.state;
        const updated = this.q('[data-pr-updated]');
        if (updated) {
            updated.textContent = queueLoading ? 'Refreshing…'
                : queueLoadedAt ? `Updated ${new Date(queueLoadedAt).toLocaleTimeString()}` : '';
        }
        const refresh = this.q('[data-pr-refresh]');
        if (refresh) refresh.disabled = queueLoading;
        const count = this.q('[data-pr-open-count]');
        if (count) count.textContent = String(queue?.counts?.open ?? 0);
        const body = this.q('[data-pr-open-body]');
        if (!body) return;
        if (queueError) {
            body.innerHTML = `<div class="pr-alert" data-tone="danger" role="status">${esc(queueError)}</div>`;
            return;
        }
        if (!queue) {
            body.innerHTML = emptyState(queueLoading ? 'Loading open pull requests from GitHub…' : 'Open pull requests have not loaded yet.');
            return;
        }
        const entries = [...queue.queue, ...queue.skipped].sort((a, b) => b.number - a.number);
        body.innerHTML = nextUpCard(queue, starting !== null)
            + (entries.length
                ? `<div class="pr-list">${entries.map(entry => openRow(entry, starting !== null)).join('')}</div>`
                : emptyState('No open pull requests were returned by GitHub.'));
    }

    renderDetail() {
        const body = this.q('[data-pr-detail-body]');
        if (!body) return;
        const { detail, detailError, editingPr, editingReviewId, busy } = this.state;
        if (detailError) {
            body.innerHTML = `<div class="pr-alert" data-tone="danger" role="status">${esc(detailError)}</div>`;
            return;
        }
        if (!detail) {
            body.innerHTML = emptyState('Loading…');
            return;
        }
        const pr = detail.pullRequest;
        const repo = this.q('[data-pr-repository]');
        if (repo) repo.innerHTML = `Lead-review log for ${repositoryLink(`https://github.com/${detail.repository}`, detail.repository)}`;
        const pending = detail.reviews.find(review => review.status === 'in_progress');
        const completed = detail.reviews.filter(review => review.status === 'completed');
        const reviewArea = pending
            ? pendingReviewForm(pr, pending, busy)
            : `<section class="pr-card pr-start-card">
                <div><span class="pr-eyebrow">No pending review</span><h2 class="pr-section-title">Ready for another pass?</h2></div>
                <button type="button" class="btn btn-primary" data-pr-start-review${busy ? ' disabled' : ''}>Start review</button>
            </section>`;
        body.innerHTML = (editingPr ? pullRequestEditForm(pr) : detailHeader(pr, detail.url))
            + detailAlerts(pr)
            + reviewArea
            + `<section class="pr-section">${sectionHeading('Newest first', 'Review history', completed.length)}
                ${completed.length
                    ? completed.map((review, index) => reviewCard(review, { isLatest: index === 0, editing: review.id === editingReviewId })).join('')
                    : emptyState('No completed reviews yet.')}
            </section>`;
    }

    syncBusyButtons() {
        const busy = Boolean(this.state.busy);
        for (const button of this.root?.querySelectorAll('[data-pr-detail-body] button') || []) {
            if (button.matches('[data-pr-back], [data-pr-expand]')) continue;
            button.disabled = busy;
        }
    }

    // ------------------------------------------------------------------ events

    bindEvents() {
        const root = this.root;
        if (!root) return;
        root.addEventListener('click', event => void this.onClick(event));
        root.addEventListener('submit', event => void this.onSubmit(event));
        root.addEventListener('change', event => this.onChange(event));
        root.addEventListener('input', event => {
            if (!event.target.matches('[data-pr-query]')) return;
            this.filters.query = event.target.value;
            this.renderList();
        });
        const query = root.querySelector('[data-pr-query]');
        if (query) query.value = this.filters.query;
    }

    async onClick(event) {
        const target = event.target.closest('a, button');
        if (!target || !this.root?.contains(target)) return;

        if (target.matches('a[data-pr-external]')) {
            // The VS Code webview cannot follow target=_blank; the host opens the browser instead.
            if (typeof window.__viberails_openExternal__ === 'function') {
                event.preventDefault();
                window.__viberails_openExternal__(target.href);
            }
            return;
        }
        if (target.matches('[data-pr-open]')) {
            this.app.navigate(VIEW, { number: Number(target.dataset.prOpen) });
        } else if (target.matches('[data-pr-start]')) {
            await this.startFromQueue(Number(target.dataset.prStart));
        } else if (target.matches('[data-pr-refresh]')) {
            await this.refreshGitHub(this.generation);
        } else if (target.matches('[data-pr-lifecycle]')) {
            this.filters.lifecycle = target.dataset.prLifecycle;
            this.renderList();
        } else if (target.matches('[data-pr-status]')) {
            this.filters.status = target.dataset.prStatus;
            this.renderList();
        } else if (target.matches('[data-pr-back]')) {
            this.app.navigate(VIEW, {});
        } else if (target.matches('[data-pr-expand]')) {
            const feedback = target.previousElementSibling;
            const collapsed = feedback?.classList.toggle('is-collapsed');
            target.textContent = collapsed ? 'Show full review' : 'Show less';
        } else if (target.matches('[data-pr-start-review]')) {
            await this.mutate(() => this.request(`/${this.state.number}/reviews/start`, 'POST'),
                `Review started for PR #${this.state.number}.`);
        } else if (target.matches('[data-pr-save-draft]')) {
            const form = target.closest('form');
            await this.mutate(() => this.request(`/${this.state.number}/reviews/draft`, 'POST', this.readPendingForm(form)),
                'Draft saved.', 'draft');
        } else if (target.matches('[data-pr-toggle-blocker]')) {
            const blocking = target.dataset.blocking !== 'true';
            await this.mutate(() => this.request(`/reviews/${target.dataset.prToggleBlocker}`, 'PUT', { blocking }),
                blocking ? 'Review marked as a blocker.' : 'Blocker cleared.');
        } else if (target.matches('[data-pr-edit-review]')) {
            this.state.editingReviewId = Number(target.dataset.prEditReview);
            this.renderDetail();
        } else if (target.matches('[data-pr-cancel-edit]')) {
            this.state.editingReviewId = null;
            this.renderDetail();
        } else if (target.matches('[data-pr-delete-review]')) {
            const confirmed = await confirmDialog({
                title: 'Delete this review?', danger: true, confirmLabel: 'Delete review',
                message: 'This permanently removes this review from the PR history. GitHub is not touched.'
            });
            if (confirmed) await this.mutate(() => this.request(`/reviews/${target.dataset.prDeleteReview}`, 'DELETE'), 'Review deleted.');
        } else if (target.matches('[data-pr-edit-pr]')) {
            this.state.editingPr = true;
            this.renderDetail();
        } else if (target.matches('[data-pr-cancel-pr-edit]')) {
            this.state.editingPr = false;
            this.renderDetail();
        } else if (target.matches('[data-pr-delete-pr]')) {
            await this.deletePullRequest();
        }
    }

    async onSubmit(event) {
        const form = event.target;
        event.preventDefault();
        const number = this.state.number;
        if (form.matches('[data-pr-pending-form]')) {
            const body = this.readPendingForm(form);
            await this.mutate(() => this.request(`/${number}/reviews/complete`, 'POST', body),
                body.blocking ? `PR #${number} recorded as a blocker.` : `Review completed for PR #${number}.`, 'complete');
        } else if (form.matches('[data-pr-review-edit]')) {
            const id = form.dataset.prReviewEdit;
            const body = {
                feedback: form.elements.feedback.value,
                verdict: form.elements.verdict.value,
                blocking: form.elements.blocking.checked
            };
            // Leave edit mode inside the save, so the re-read paints the saved card, not the form.
            await this.mutate(async () => {
                await this.request(`/reviews/${id}`, 'PUT', body);
                this.state.editingReviewId = null;
            }, 'Review updated.');
        } else if (form.matches('[data-pr-pr-edit]')) {
            const body = {
                title: form.elements.title.value,
                author: form.elements.author.value,
                summary: form.elements.summary.value
            };
            await this.mutate(async () => {
                await this.request(`/${number}`, 'PUT', body);
                this.state.editingPr = false;
            }, `PR #${number} updated.`);
        }
    }

    /** Requesting changes IS the blocker verdict by default; keep them in step on a verdict change. */
    onChange(event) {
        const form = event.target.closest('[data-pr-pending-form]');
        if (!form) return;
        const blocking = form.querySelector('[data-pr-blocking]');
        if (event.target.matches('[data-pr-verdict]') && blocking) {
            blocking.checked = event.target.value === BLOCKING_VERDICT;
        }
        if (event.target.matches('[data-pr-verdict], [data-pr-blocking]')) {
            const on = Boolean(blocking?.checked);
            const note = form.querySelector('[data-pr-blocker-note]');
            if (note) note.hidden = !on;
            const complete = form.querySelector('[data-pr-complete]');
            if (complete) {
                complete.classList.toggle('btn-danger', on);
                complete.classList.toggle('btn-primary', !on);
                complete.textContent = on ? 'Complete as blocker' : 'Complete review';
            }
        }
    }

    readPendingForm(form) {
        return {
            summary: form.elements.summary.value,
            feedback: form.elements.feedback.value,
            verdict: form.elements.verdict.value,
            blocking: form.elements.blocking.checked
        };
    }

    async deletePullRequest() {
        const number = this.state.number;
        const confirmed = await confirmDialog({
            title: `Delete PR #${number}?`, danger: true, confirmLabel: 'Delete PR',
            message: 'This permanently removes the PR and all of its review history from the tracker. GitHub is not touched.'
        });
        if (!confirmed) return;
        const generation = this.generation;
        try {
            await this.request(`/${number}`, 'DELETE');
            if (!this.isCurrent(generation)) return;
            this.app.showToast('PR Reviews', `PR #${number} deleted.`, 'success');
            this.app.navigate(VIEW, {});
        } catch (error) {
            if (error?.name !== 'AbortError' && this.isCurrent(generation)) {
                this.app.showToast('PR Reviews', error?.message || `Could not delete PR #${number}.`, 'error');
            }
        }
    }
}
