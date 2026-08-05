import {
    buildLlmSelectionValue,
    getLlmName,
    parseLlmSelection
} from './utils.js';
import { showReplayModal } from './session-viewer.js';

const LLM = Object.freeze({
    CODEX: 1,
    CLAUDE: 2,
    ANTIGRAVITY: 3,
    COPILOT: 4,
    OPENCODE: 6,
    GLM_52: 7
});

const LLM_BY_CLI = Object.freeze({
    codex: LLM.CODEX,
    claude: LLM.CLAUDE,
    antigravity: LLM.ANTIGRAVITY,
    copilot: LLM.COPILOT,
    opencode: LLM.OPENCODE,
    'glm-5.2': LLM.GLM_52
});

const CLI_BY_LLM = Object.freeze(Object.fromEntries(
    Object.entries(LLM_BY_CLI).map(([cli, llm]) => [llm, cli])
));

export function getJobLlmForCli(cli) {
    return LLM_BY_CLI[String(cli || '').trim().toLowerCase()] ?? null;
}

export function getJobCliForLlm(llm) {
    return CLI_BY_LLM[Number(llm)] || null;
}
const TRIGGER = Object.freeze({ SCHEDULE: 0, COMMIT: 2, MANUAL: 3 });
const SCHEDULE = Object.freeze({ INTERVAL: 0, DAILY: 1, WEEKLY: 2 });

const RUN_STATUS = Object.freeze({
    0: { label: 'Queued', tone: 'neutral' },
    1: { label: 'Running', tone: 'info' },
    2: { label: 'Succeeded', tone: 'success' },
    3: { label: 'Failed', tone: 'danger' },
    4: { label: 'Cancelled', tone: 'neutral' },
    5: { label: 'Timed out', tone: 'warning' },
    6: { label: 'Interrupted', tone: 'warning' }
});

// Named codes for the comparisons below, so "which statuses can be retried" reads as
// intent rather than as an integer boundary. Values mirror JobRunStatus in JobsDtos.cs.
const RUN_STATUS_CODE = Object.freeze({
    QUEUED: 0,
    RUNNING: 1,
    SUCCEEDED: 2,
    FAILED: 3,
    CANCELLED: 4,
    TIMED_OUT: 5,
    INTERRUPTED: 6
});

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

export class JobController {
    constructor(app) {
        this.app = app;
        this.root = null;
        this.jobs = [];
        // One entry per automation for the page-level table; the full run list is fetched per
        // automation when its history modal opens, so a busy job can't crowd out quieter ones.
        this.runSummaries = [];
        this.historyJobId = null;
        this.historyRuns = [];
        this.historySelection = new Set();
        this.historyPage = 1;
        this.historyPageSize = 50;
        this.historyTotalRuns = 0;
        this.historyRequestGeneration = 0;
        this.historyLoadInFlight = false;
        this._lastHistoryHtml = null;
        this.environments = [];
        this.pollTimer = null;
        this.logTimer = null;
        this.runModalGeneration = 0;
        this.editorModalCleanup = null;
        this.activeEditorJob = null;
        this.activeEditorSource = null;
        this.activeEditorPreferredTrigger = null;
        this._lastJobsListHtml = null;
        this._lastRunsHtml = null;
    }

    async loadView(data = {}) {
        this.unload();
        const content = document.getElementById('app-content');
        if (!content) return;
        content.innerHTML = this.renderPage();
        this.root = content.querySelector('[data-view="jobs"]');
        this.bindPageActions();
        // The list now holds the loading placeholder; a stale render cache from a previous
        // visit would make the first renderJobs() skip and leave the spinner forever.
        this._lastJobsListHtml = null;
        this._lastRunsHtml = null;
        await this.refreshAll();

        if (data?.newJob) {
            this.openEditor(null, Number(data.triggerKind));
        }
        this.pollTimer = window.setInterval(() => {
            if (this.app.currentView !== 'jobs') return;
            this.refreshRuns({ quiet: true });
            // Jobs too, not just run history: the scheduler advances nextRunUtc server-side,
            // so a "Next run" rendered once at load drifts into "Due now" and stays there.
            this.refreshJobs({ quiet: true });
        }, 5000);
    }

    unload() {
        this.runModalGeneration += 1;
        this.historyRequestGeneration += 1;
        this.disposeEditorModal();
        this.activeEditorJob = null;
        this.activeEditorSource = null;
        this.activeEditorPreferredTrigger = null;
        if (this.pollTimer) window.clearInterval(this.pollTimer);
        if (this.logTimer) window.clearInterval(this.logTimer);
        this.pollTimer = null;
        this.logTimer = null;
        this.root = null;
    }

    disposeEditorModal() {
        const cleanup = this.editorModalCleanup;
        this.editorModalCleanup = null;
        cleanup?.();
    }

    registerEditorModalCleanup(selection, pickerDisposer = null) {
        this.disposeEditorModal();

        let disposed = false;
        const keydownTarget = typeof window !== 'undefined' ? window : document;
        const handleEscape = event => {
            if (event.key !== 'Escape') return;
            const modalContainer = typeof document !== 'undefined' ? document.getElementById?.('modal-container') : null;
            if (modalContainer?.firstElementChild) return;
            this.closeEditor();
        };
        const cleanup = () => {
            if (disposed) return;
            disposed = true;
            keydownTarget?.removeEventListener?.('keydown', handleEscape, true);
            if (pickerDisposer) pickerDisposer();
            else if (selection?.tomselect && typeof selection.tomselect.destroy === 'function') selection.tomselect.destroy();
            if (this.editorModalCleanup === cleanup) {
                this.editorModalCleanup = null;
            }
        };

        keydownTarget?.addEventListener?.('keydown', handleEscape, true);
        this.editorModalCleanup = cleanup;
        return cleanup;
    }

    renderPage() {
        return `
            <div class="view jobs-view" data-view="jobs">
                <header class="jobs-page-header">
                    <div>
                        <h4 class="jobs-page-title text-uppercase fw-bold mb-0"><span class="text-gradient">Automation</span></h4>
                        <p>Run a saved Environment on a schedule, after every commit, or on demand. Each run is a recorded terminal you can replay here.</p>
                    </div>
                    <div class="jobs-page-actions">
                        <button class="btn btn-outline-secondary" type="button" data-job-action="import-recipe">
                            <i class="fa-solid fa-file-import me-1" aria-hidden="true"></i>Import recipe
                        </button>
                        <button class="btn btn-primary" type="button" data-job-action="new">
                            <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>New automation
                        </button>
                    </div>
                </header>

                <section class="jobs-runtime-strip" aria-label="Automation runtime">
                    <span class="jobs-runtime-icon" aria-hidden="true"><i class="fa-solid fa-bolt"></i></span>
                    <div class="jobs-runtime-copy">
                        <strong>Automations run only while VibeRails is open</strong>
                        <span>Queued work is shared, and a single active instance claims each run.</span>
                    </div>
                </section>

                <section class="jobs-section" aria-labelledby="jobs-list-title">
                    <div class="jobs-section-heading">
                        <div><h2 id="jobs-list-title">Automations</h2></div>
                        <span class="jobs-count" data-jobs-count>0 automations</span>
                    </div>
                    <div class="job-inline-editor" data-job-editor hidden></div>
                    <div class="jobs-grid" data-jobs-list>
                        <div class="jobs-empty"><span class="spinner-border spinner-border-sm"></span> Loading automations…</div>
                    </div>
                </section>

                <section class="jobs-section" aria-labelledby="job-runs-title">
                    <div class="jobs-section-heading">
                        <div><h2 id="job-runs-title">Run history</h2></div>
                        <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="refresh">
                            <i class="fa-solid fa-rotate-right me-1" aria-hidden="true"></i>Refresh
                        </button>
                    </div>
                    <div class="jobs-runs" data-job-runs></div>
                </section>
            </div>`;
    }

    bindPageActions() {
        this.root?.addEventListener('click', async event => {
            const actionElement = event.target.closest('[data-job-action]');
            if (!actionElement) return;
            const action = actionElement.dataset.jobAction;
            const jobId = Number(actionElement.dataset.jobId);
            const runId = actionElement.dataset.runId;

            if (action === 'new') return this.openEditor();
            if (action === 'refresh') return this.refreshAll();
            if (action === 'edit') return this.openEditor(this.jobs.find(job => job.id === jobId));
            if (action === 'run') return this.runNow(jobId, actionElement);
            if (action === 'toggle') return this.toggleJob(jobId, actionElement);
            if (action === 'delete') return this.deleteJob(jobId);
            if (action === 'export-recipe') return this.exportRecipe(jobId);
            if (action === 'import-recipe') return this.importRecipe();
            if (action === 'view-run' && runId) return this.openRun(runId);
            if (action === 'view-history' && Number.isFinite(jobId)) return this.openRunHistory(jobId);
            if (action === 'cancel-run' && runId) return this.cancelRun(runId, actionElement);
            if (action === 'retry-run' && runId) return this.retryRun(runId, actionElement);
        });
    }

    async refreshAll({ quiet = false } = {}) {
        try {
            const projectPath = this.currentProjectPath();
            const jobsRequest = projectPath
                ? this.app.apiCall(`/api/v1/jobs?projectPath=${encodeURIComponent(projectPath)}`, 'GET', null, { showLoading: !quiet })
                : Promise.resolve({ jobs: [] });
            const [jobsResponse, runsResponse, environmentsResponse] = await Promise.all([
                jobsRequest,
                this.app.apiCall(`/api/v1/jobs/runs/summary?projectPath=${encodeURIComponent(projectPath)}`, 'GET', null, { showLoading: false }),
                this.app.apiCall('/api/v1/environments', 'GET', null, { showLoading: false })
            ]);
            this.jobs = jobsResponse?.jobs || [];
            this.runSummaries = runsResponse?.summaries || [];
            const environmentRecords = environmentsResponse?.environments || [];
            this.environments = this.app.environmentController?.setEnvironments
                ? this.app.environmentController.setEnvironments(environmentRecords)
                : environmentRecords;
            this.app.data.environments = this.environments;
            this.renderJobs();
            this.renderRuns();
        } catch (error) {
            if (!quiet) this.app.showError(error?.message || 'Could not load Automation.');
        }
    }

    async refreshRuns({ quiet = false } = {}) {
        try {
            const projectPath = this.currentProjectPath();
            const response = await this.app.apiCall(
                `/api/v1/jobs/runs/summary?projectPath=${encodeURIComponent(projectPath)}`,
                'GET', null, { showLoading: false });
            this.runSummaries = response?.summaries || [];
            this.renderRuns();
            // The modal loads its rows from a different endpoint, so without this they keep saying
            // "Queued"/"Running" after the run has finished — Stop stays visible and remove/run-again
            // stay disabled until it is reopened. renderHistory skips an identical re-render, so a
            // poll that changes nothing does not touch the DOM.
            if (this.historyJobId !== null && document.querySelector('[data-job-history]')) {
                await this.loadHistoryRuns({ quiet: true });
            }
            if (!quiet) this.app.showToast('Automation', 'Run history refreshed.', 'info');
        } catch (error) {
            if (!quiet) this.app.showError(error?.message || 'Could not refresh automation runs.');
        }
    }

    async refreshJobs({ quiet = false } = {}) {
        try {
            const projectPath = this.currentProjectPath();
            if (projectPath) {
                const response = await this.app.apiCall(
                    `/api/v1/jobs?projectPath=${encodeURIComponent(projectPath)}`,
                    'GET', null, { showLoading: false });
                this.jobs = response?.jobs || [];
            }
        } catch (error) {
            if (!quiet) this.app.showError(error?.message || 'Could not refresh automations.');
        }
        // Render even when the fetch failed or was skipped: the countdown labels
        // ("in 12 min") age out of the cached data on their own.
        this.renderJobs();
    }

    async environmentChanged({ selectedEnvironmentId = null } = {}) {
        this.environments = this.app.data.environments || [];
        this.renderJobs();
        this.refreshEditorEnvironmentPicker(selectedEnvironmentId);
    }

    renderJobs() {
        const target = this.root?.querySelector('[data-jobs-list]');
        const count = this.root?.querySelector('[data-jobs-count]');
        if (count) count.textContent = `${this.jobs.length} ${this.jobs.length === 1 ? 'automation' : 'automations'}`;
        if (!target) return;
        const html = this.renderJobsListHtml();
        // The 5s poll calls this constantly. Only touch the DOM when the markup actually
        // changed (data or a countdown label), so hover/focus and a busy "Run now" button
        // aren't wiped by an identical re-render.
        if (html === this._lastJobsListHtml) return;
        this._lastJobsListHtml = html;
        target.innerHTML = html;
    }

    renderJobsListHtml() {
        if (this.jobs.length === 0) {
            return `
                <div class="jobs-empty jobs-empty-large">
                    <i class="fa-regular fa-clock" aria-hidden="true"></i>
                    <strong>No automations yet</strong>
                    <span>Choose an Environment and run it on a timer, after commits, or on demand.</span>
                    <button class="btn btn-primary" type="button" data-job-action="new">Create your first automation</button>
                </div>`;
        }

        return this.jobs.map(job => {
            const triggers = (job.triggers || []).map(trigger => `<span class="jobs-trigger-chip">${this.escape(this.formatTrigger(trigger))}</span>`).join('');
            const environment = this.findEnvironment(job.environmentId);
            const cli = environment?.cli || getJobCliForLlm(job.llm) || 'unknown';
            const llm = getLlmName(Number(getJobLlmForCli(cli) ?? job.llm));
            const environmentName = environment?.name
                || (job.environmentName ? `${job.environmentName} (missing)` : 'Missing environment');
            const nextRun = this.nextRunSummary(job);
            const nextRunTitle = nextRun.utc
                ? ` title="${this.escape(new Date(nextRun.utc).toLocaleString())}"`
                : '';
            const jobId = this.escape(job.id);
            const safeName = this.escape(job.name);
            // The Environment owns the initial message, but it is the automation's actual logic,
            // so it belongs on the card rather than one click away in the editor.
            const prompt = (job.prompt || '').trim();
            return `
                <article class="job-card" data-enabled="${job.enabled === true}">
                    <div class="job-card-main">
                        <div class="job-card-topline">
                            <div class="job-card-title">
                                <h3>${safeName}</h3>
                                <span class="job-provider" data-provider="${this.escape(cli)}">${this.escape(llm)}</span>
                            </div>
                            <span class="job-state" data-tone="${job.enabled ? 'success' : 'neutral'}">${job.enabled ? 'Enabled' : 'Disabled'}</span>
                        </div>
                        <div class="job-card-facts">
                            <div class="job-card-fact" ${environment ? '' : 'data-tone="warning"'}>
                                <span><i class="fa-solid fa-layer-group" aria-hidden="true"></i>Environment</span>
                                <strong>${this.escape(environmentName)}</strong>
                            </div>
                            <div class="job-card-fact job-card-trigger">
                                <span><i class="fa-regular fa-clock" aria-hidden="true"></i>Trigger</span>
                                <div class="job-triggers">${triggers || '<span class="jobs-trigger-chip">On demand</span>'}</div>
                            </div>
                            <div class="job-card-fact">
                                <span><i class="fa-solid fa-forward" aria-hidden="true"></i>Next run</span>
                                <strong${nextRunTitle}>${this.escape(nextRun.label)}</strong>
                            </div>
                        </div>
                        ${prompt ? `
                        <div class="job-card-prompt">
                            <span><i class="fa-regular fa-message" aria-hidden="true"></i>Initial message</span>
                            <p title="${this.escape(this.truncate(prompt, 400))}">${this.escape(prompt)}</p>
                        </div>` : ''}
                    </div>
                    <div class="job-card-actions">
                        <button class="btn btn-sm btn-primary" type="button" data-job-action="run" data-job-id="${jobId}"><i class="fa-solid fa-play me-1"></i>Run now</button>
                        <button class="btn btn-sm btn-outline-secondary job-icon-action" type="button" data-job-action="edit" data-job-id="${jobId}" title="Edit automation" aria-label="Edit ${safeName}"><i class="fa-solid fa-pen" aria-hidden="true"></i></button>
                        <button class="btn btn-sm btn-outline-secondary job-icon-action" type="button" data-job-action="toggle" data-job-id="${jobId}" title="${job.enabled ? 'Disable' : 'Enable'} automation" aria-label="${job.enabled ? 'Disable' : 'Enable'} ${safeName}"><i class="fa-solid ${job.enabled ? 'fa-pause' : 'fa-play'}" aria-hidden="true"></i></button>
                        <button class="btn btn-sm btn-outline-secondary job-icon-action" type="button" data-job-action="export-recipe" data-job-id="${jobId}" title="Export as recipe" aria-label="Export ${safeName} as recipe"><i class="fa-solid fa-file-export" aria-hidden="true"></i></button>
                        <button class="btn btn-sm btn-outline-danger job-icon-action" type="button" data-job-action="delete" data-job-id="${jobId}" title="Delete automation" aria-label="Delete ${safeName}"><i class="fa-solid fa-trash" aria-hidden="true"></i></button>
                    </div>
                </article>`;
        }).join('');
    }

    nextRunSummary(job) {
        if (job.enabled !== true) return { label: 'Paused', utc: null };

        const triggers = job.triggers || [];
        const scheduled = triggers.filter(trigger => Number(trigger.kind) === TRIGGER.SCHEDULE);
        const next = scheduled
            .map(trigger => trigger.nextRunUtc)
            .filter(value => value && Number.isFinite(new Date(value).getTime()))
            .sort((left, right) => new Date(left).getTime() - new Date(right).getTime())[0];

        if (next) return { label: this.formatFutureTime(next), utc: next };
        if (scheduled.length > 0) return { label: 'Waiting for schedule', utc: null };
        if (triggers.some(trigger => Number(trigger.kind) === TRIGGER.COMMIT)) {
            return { label: 'After next commit', utc: null };
        }
        return { label: 'On demand', utc: null };
    }

    renderRuns() {
        const target = this.root?.querySelector('[data-job-runs]');
        if (!target) return;
        const html = this.renderRunsHtml();
        // Same reason renderJobs caches: the 5s poll calls this constantly, and rewriting
        // innerHTML on every tick tore out hover, focus, and any just-clicked Cancel/Retry
        // button in the row underneath the pointer. Only touch the DOM on a real change —
        // a live run's ticking Duration still differs each tick, so it keeps updating.
        if (html === this._lastRunsHtml) return;
        this._lastRunsHtml = html;
        target.innerHTML = html;
    }

    renderRunsHtml() {
        if (this.runSummaries.length === 0) {
            // The summary endpoint returns nothing at all without a project path, so "no runs yet"
            // would be a guess here — say which of the two it actually is.
            return this.currentProjectPath()
                ? '<div class="jobs-empty">No automation runs yet.</div>'
                : '<div class="jobs-empty">Open a project to see its automation runs.</div>';
        }
        return `
            <div class="table-responsive"><table class="table jobs-runs-table align-middle">
                <thead><tr><th>Automation</th><th>Last run</th><th>When</th><th>Runs</th><th><span class="visually-hidden">Actions</span></th></tr></thead>
                <tbody>${this.runSummaries.map(summary => this.renderSummaryRow(summary)).join('')}</tbody>
            </table></div>`;
    }

    // One row per automation. Listing every run put a job on a 15-minute timer at the top of the
    // table forever and buried everything else, so the individual runs moved into a modal that is
    // opened per automation and fetched on demand.
    renderSummaryRow(summary) {
        const statusCode = Number(summary.lastStatus);
        const status = RUN_STATUS[statusCode] || { label: 'Unknown', tone: 'neutral' };
        const jobId = this.escape(summary.jobId);
        const safeName = this.escape(summary.jobName);
        const detail = this.runDetail({ errorMessage: summary.lastErrorMessage, exitCode: summary.lastExitCode });
        const total = Number(summary.totalRuns) || 0;
        const activeRuns = Number(summary.activeRuns) || 0;
        const openTitle = `View this automation's ${total} ${total === 1 ? 'run' : 'runs'}`;
        return `<tr>
            <td><button class="job-run-link" type="button" data-job-action="view-history" data-job-id="${jobId}" title="${openTitle}">${safeName}</button><small>${this.escape(getLlmName(Number(summary.lastLlm)))}${summary.lastEnvironmentName ? ` · ${this.escape(summary.lastEnvironmentName)}` : ''}</small></td>
            <td><span class="job-run-status" data-tone="${status.tone}">${status.label}</span>${detail ? `<small class="job-run-detail" title="${this.escape(detail)}">${this.escape(detail)}</small>` : ''}</td>
            <td title="${this.escape(summary.lastQueuedUtc)}">${this.escape(this.relativeTime(summary.lastQueuedUtc))}</td>
            <td><span class="jobs-run-count">${total}</span>${activeRuns > 0 ? `<small class="job-run-detail">${activeRuns} active</small>` : ''}</td>
            <td class="text-end jobs-run-actions">
                <button class="btn btn-sm btn-outline-secondary job-icon-action" type="button" data-job-action="view-history" data-job-id="${jobId}" title="${openTitle}" aria-label="${openTitle}"><i class="fa-solid fa-clock-rotate-left" aria-hidden="true"></i></button>
            </td>
        </tr>`;
    }

    // ----- Run history modal: every run of one automation, with watch / run again / delete -----

    async openRunHistory(jobId) {
        const summary = this.runSummaries.find(entry => Number(entry.jobId) === jobId);
        const name = this.jobs.find(job => job.id === jobId)?.name || summary?.jobName || 'Automation';
        const modalGeneration = ++this.runModalGeneration;
        this.historyJobId = jobId;
        this.historyRuns = [];
        this.historySelection = new Set();
        this.historyPage = 1;
        this.historyTotalRuns = 0;
        // The modal body below is fresh markup, so the previous modal's cached html must not
        // suppress the first real render.
        this._lastHistoryHtml = null;
        this.app.showModal(`Run history — ${name}`, `
            <div class="job-history" data-job-history data-history-generation="${modalGeneration}">
                <div class="jobs-empty"><span class="spinner-border spinner-border-sm"></span> Loading runs…</div>
            </div>`);
        // Bound once against the container. Every re-render replaces only its innerHTML, so the
        // delegated listeners survive and never stack up.
        this.bindHistoryActions();
        await this.loadHistoryRuns({ jobId, page: 1, modalGeneration });
    }

    /// `quiet` is for the five-second poll: a transient fetch failure must leave the rows the user
    /// is looking at alone rather than replacing the whole modal body with an error, and it defers
    /// to any load already running. Without that, a poll starting between a "next page" click and
    /// its response would request the still-current page, win the generation race, and silently
    /// undo the user's navigation.
    async loadHistoryRuns({
        jobId = this.historyJobId,
        page = this.historyPage,
        modalGeneration = this.runModalGeneration,
        quiet = false
    } = {}) {
        if (jobId === null) return;
        if (quiet && this.historyLoadInFlight) return;
        const requestGeneration = ++this.historyRequestGeneration;
        const rootSelector = `[data-job-history][data-history-generation="${modalGeneration}"]`;
        this.historyLoadInFlight = true;
        try {
            const response = await this.app.apiCall(
                `/api/v1/jobs/runs?jobId=${encodeURIComponent(jobId)}&page=${encodeURIComponent(page)}&pageSize=${this.historyPageSize}`,
                'GET', null, { showLoading: false });
            const root = document.querySelector(rootSelector);
            if (!root
                || modalGeneration !== this.runModalGeneration
                || requestGeneration !== this.historyRequestGeneration
                || jobId !== this.historyJobId) return;

            this.historyRuns = response?.runs || [];
            // Pagination metadata is optional on the wire — `Number(null)` is 0, which would read as
            // a valid "page 0 of 0 runs", so absent values fall back rather than being parsed.
            const asCount = (value, fallback) => {
                if (value === null || value === undefined) return fallback;
                const parsed = Number(value);
                return Number.isInteger(parsed) && parsed >= 0 ? parsed : fallback;
            };
            this.historyPage = Math.max(1, asCount(response?.page, Math.max(1, Number(page) || 1)));
            this.historyPageSize = asCount(response?.pageSize, 0) > 0
                ? asCount(response?.pageSize, this.historyPageSize)
                : this.historyPageSize;
            this.historyTotalRuns = asCount(response?.totalRuns, this.historyRuns.length);
            // A run deleted underneath us must not stay selected and re-appear in the next delete.
            const present = new Set(this.historyRuns.map(run => run.id));
            this.historySelection = new Set([...this.historySelection].filter(id => present.has(id)));
            this.renderHistory(root);
        } catch (error) {
            if (quiet) return;
            const root = document.querySelector(rootSelector);
            if (root
                && modalGeneration === this.runModalGeneration
                && requestGeneration === this.historyRequestGeneration
                && jobId === this.historyJobId) {
                this._lastHistoryHtml = null;
                root.innerHTML = `<div class="jobs-empty">${this.escape(error?.message || 'Could not load this automation’s runs.')}</div>`;
            }
        } finally {
            this.historyLoadInFlight = false;
        }
    }

    // Queued and running rows cannot be removed, so they are also not selectable — otherwise
    // "select all" would arm an operation that the server refuses.
    deletableRunIds() {
        return this.historyRuns
            .filter(run => Number(run.status) !== RUN_STATUS_CODE.QUEUED
                && Number(run.status) !== RUN_STATUS_CODE.RUNNING)
            .map(run => run.id);
    }

    bindHistoryActions() {
        const root = document.querySelector('[data-job-history]');
        if (!root) return;

        root.addEventListener('click', async event => {
            const element = event.target.closest('[data-history-action]');
            if (!element) return;
            const action = element.dataset.historyAction;
            const runId = element.dataset.runId;

            if (action === 'watch' && runId) {
                this.runModalGeneration += 1;
                this.historyRequestGeneration += 1;
                this.app.closeModal();
                return this.openRun(runId);
            }
            if (action === 'page') {
                const nextPage = Number(element.dataset.historyPage);
                if (!Number.isInteger(nextPage) || nextPage < 1 || nextPage === this.historyPage) return;
                this.historySelection = new Set();
                return this.loadHistoryRuns({ page: nextPage });
            }
            if (action === 'run-again' && runId) return this.runAgainFromHistory(runId, element);
            if (action === 'stop' && runId) return this.stopFromHistory(runId, element);
            if (action === 'delete' && runId) return this.deleteRuns([runId]);
            if (action === 'delete-selected') return this.deleteRuns([...this.historySelection]);
        });

        root.addEventListener('change', event => {
            const all = event.target.closest('[data-run-select-all]');
            if (all) {
                this.historySelection = new Set(all.checked ? this.deletableRunIds() : []);
                root.querySelectorAll('[data-run-select]').forEach(box => {
                    if (!box.disabled) box.checked = all.checked;
                });
                this.updateHistorySelectionUi(root);
                return;
            }

            const box = event.target.closest('[data-run-select]');
            if (!box) return;
            if (box.checked) this.historySelection.add(box.dataset.runSelect);
            else this.historySelection.delete(box.dataset.runSelect);
            this.updateHistorySelectionUi(root);
        });
    }

    // Updates the bulk bar in place rather than re-rendering: a re-render on every tick of a
    // checkbox would drop focus and make keyboard selection unusable.
    updateHistorySelectionUi(root = document.querySelector('[data-job-history]')) {
        if (!root) return;
        const selected = this.historySelection.size;
        const deletable = this.deletableRunIds().length;

        const bar = root.querySelector('[data-history-bulk]');
        if (bar) bar.hidden = selected === 0;
        const count = root.querySelector('[data-history-bulk-count]');
        if (count) count.textContent = String(selected);

        const all = root.querySelector('[data-run-select-all]');
        if (all) {
            all.checked = deletable > 0 && selected === deletable;
            all.indeterminate = selected > 0 && selected < deletable;
        }
    }

    async runAgainFromHistory(runId, button) {
        await this.retryRun(runId, button);
        await this.loadHistoryRuns();
        await this.refreshRuns({ quiet: true });
    }

    async stopFromHistory(runId, button) {
        await this.cancelRun(runId, button);
        await this.loadHistoryRuns();
        await this.refreshRuns({ quiet: true });
    }

    async deleteRuns(runIds) {
        const ids = [...new Set(runIds.filter(Boolean))];
        if (ids.length === 0) return;
        const modalGeneration = this.runModalGeneration;
        const jobId = this.historyJobId;
        const subject = ids.length === 1 ? 'this run' : `these ${ids.length} runs`;
        if (!window.confirm(`Remove ${subject} from this automation’s history? Any recorded terminal and logs will be kept in Chat History.`)) return;

        try {
            const response = await this.app.apiCall('/api/v1/jobs/runs/delete', 'POST', { runIds: ids });
            this.app.showToast('Automation', response?.message || 'Runs removed from Automation history.', 'info');
            if (modalGeneration !== this.runModalGeneration || jobId !== this.historyJobId) return;
            this.historySelection = new Set();
            await this.loadHistoryRuns();
            await this.refreshRuns({ quiet: true });
        } catch (error) {
            this.app.showError(error?.message || 'Could not remove the selected runs.');
        }
    }

    renderHistory(root = document.querySelector('[data-job-history]')) {
        if (!root) return;
        const html = this.renderHistoryHtml();
        // Same guard as the page-level tables: the five-second poll re-renders this modal, and
        // rewriting identical markup would drop the user's focus and reset the scroll position
        // mid-interaction. Selection state still refreshes, since it lives outside the markup.
        if (html !== this._lastHistoryHtml) {
            this._lastHistoryHtml = html;
            root.innerHTML = html;
        }

        if (this.historyTotalRuns > 0) this.updateHistorySelectionUi(root);
    }

    renderHistoryHtml() {
        if (this.historyTotalRuns === 0) {
            return '<div class="jobs-empty">This automation has no recorded runs.</div>';
        }

        const deletable = this.deletableRunIds().length;
        const totalPages = Math.max(1, Math.ceil(this.historyTotalRuns / this.historyPageSize));
        const firstVisible = ((this.historyPage - 1) * this.historyPageSize) + 1;
        const lastVisible = Math.min(this.historyTotalRuns, firstVisible + this.historyRuns.length - 1);
        return `
            <div class="job-history-bulk" data-history-bulk hidden>
                <span><strong data-history-bulk-count>0</strong> selected</span>
                <button class="btn btn-sm btn-outline-danger" type="button" data-history-action="delete-selected">
                    <i class="fa-solid fa-trash me-1" aria-hidden="true"></i>Remove selected
                </button>
            </div>
            <div class="table-responsive"><table class="table jobs-runs-table align-middle">
                <thead><tr>
                    <th class="job-history-check"><input type="checkbox" class="form-check-input" data-run-select-all aria-label="Select every run that can be removed"${deletable === 0 ? ' disabled' : ''}></th>
                    <th>Status</th><th>Trigger</th><th>Queued</th><th>Duration</th>
                    <th><span class="visually-hidden">Actions</span></th>
                </tr></thead>
                <tbody>${this.historyRuns.map(run => this.renderHistoryRow(run)).join('')}</tbody>
            </table></div>
            <nav class="job-history-pagination" aria-label="Run history pages">
                <span>Showing <strong>${firstVisible}–${lastVisible}</strong> of <strong>${this.historyTotalRuns}</strong> runs</span>
                <div>
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-history-action="page" data-history-page="${this.historyPage - 1}"${this.historyPage <= 1 ? ' disabled' : ''} aria-label="Previous run-history page"><i class="fa-solid fa-chevron-left" aria-hidden="true"></i></button>
                    <span>Page ${this.historyPage} of ${totalPages}</span>
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-history-action="page" data-history-page="${this.historyPage + 1}"${this.historyPage >= totalPages ? ' disabled' : ''} aria-label="Next run-history page"><i class="fa-solid fa-chevron-right" aria-hidden="true"></i></button>
                </div>
            </nav>`;
    }

    renderHistoryRow(run) {
        const statusCode = Number(run.status);
        const status = RUN_STATUS[statusCode] || { label: 'Unknown', tone: 'neutral' };
        const active = statusCode === RUN_STATUS_CODE.QUEUED || statusCode === RUN_STATUS_CODE.RUNNING;
        // Succeeded is terminal but it is not a failure, and offering a re-run on it invited
        // repeating work that had already landed. Running again starts at Failed.
        const retryable = statusCode >= RUN_STATUS_CODE.FAILED;
        const runId = this.escape(run.id);
        const detail = this.runDetail(run);
        const trigger = { 0: 'Schedule', 2: 'After commit', 3: 'Manual' }[Number(run.triggerKind)] || 'Manual';
        const watchTitle = active ? 'Watch this run live' : 'Watch this recording';
        const selected = this.historySelection.has(run.id);
        return `<tr>
            <td class="job-history-check"><input type="checkbox" class="form-check-input" data-run-select="${runId}"${selected ? ' checked' : ''}${active ? ' disabled title="A run that is still going cannot be removed"' : ''} aria-label="Select this run"></td>
            <td><span class="job-run-status" data-tone="${status.tone}">${status.label}</span>${detail ? `<small class="job-run-detail" title="${this.escape(detail)}">${this.escape(detail)}</small>` : ''}</td>
            <td>${trigger}</td>
            <td title="${this.escape(run.queuedUtc)}">${this.escape(this.relativeTime(run.queuedUtc))}</td>
            <td${this.durationTitle(run)}>${this.escape(this.runDuration(run))}</td>
            <td class="text-end jobs-run-actions">
                ${active ? `<button class="btn btn-sm btn-outline-danger job-icon-action" type="button" data-history-action="stop" data-run-id="${runId}" title="Stop this run" aria-label="Stop this run"><i class="fa-solid fa-stop" aria-hidden="true"></i></button>` : ''}
                ${retryable ? `<button class="btn btn-sm btn-outline-secondary job-icon-action" type="button" data-history-action="run-again" data-run-id="${runId}" title="Run this automation again" aria-label="Run this automation again"><i class="fa-solid fa-play" aria-hidden="true"></i></button>` : ''}
                ${active ? '' : `<button class="btn btn-sm btn-outline-danger job-icon-action" type="button" data-history-action="delete" data-run-id="${runId}" title="Remove this run from Automation history" aria-label="Remove this run from Automation history"><i class="fa-solid fa-trash" aria-hidden="true"></i></button>`}
                <button class="btn btn-sm btn-outline-secondary job-icon-action job-run-watch" type="button" data-history-action="watch" data-run-id="${runId}" title="${watchTitle}" aria-label="${watchTitle}"><i class="fa-solid fa-eye-slash" data-watch-idle aria-hidden="true"></i><i class="fa-solid fa-eye" data-watch-live aria-hidden="true"></i></button>
            </td>
        </tr>`;
    }

    // Why a run ended, drawn from fields the row previously dropped on the floor.
    // Without this a Failed or Interrupted row was a coloured word and nothing else,
    // even though the store had already recorded the reason.
    runDetail(run) {
        const parts = [];
        if (run.errorMessage) parts.push(String(run.errorMessage));
        const exitCode = Number(run.exitCode);
        // Exit 0 next to a success says nothing. A non-zero code is the only detail we
        // have when the CLI failed without an ErrorMessage of its own.
        if (run.exitCode !== null && run.exitCode !== undefined && Number.isFinite(exitCode) && exitCode !== 0) {
            parts.push(`exit ${exitCode}`);
        }
        return parts.join(' · ');
    }

    // A reaped run's EndedUTC is when the reaper noticed the owning process was gone,
    // not when it actually died, so the elapsed time overstates real runtime by up to a
    // scheduler tick. Mark those approximate rather than showing a precision we do not have.
    runDuration(run) {
        const duration = this.formatDuration(run.startedUtc, run.endedUtc);
        if (duration === '—') return duration;
        return Number(run.status) === RUN_STATUS_CODE.INTERRUPTED ? `~${duration}` : duration;
    }

    durationTitle(run) {
        if (Number(run.status) !== RUN_STATUS_CODE.INTERRUPTED || !run.startedUtc) return '';
        return ' title="Approximate — measured to when VibeRails noticed the terminal had closed, not to when it closed."';
    }

    openEditor(job = null, preferredTrigger = null, editorState = null) {
        this.disposeEditorModal();
        const editor = this.root?.querySelector('[data-job-editor]');
        if (!editor) return;

        const isEdit = Boolean(job);
        const source = editorState || job || {};
        this.activeEditorJob = job;
        this.activeEditorSource = source;
        this.activeEditorPreferredTrigger = preferredTrigger;

        const triggers = source.triggers || [];
        const scheduled = triggers.find(trigger => Number(trigger.kind) === TRIGGER.SCHEDULE);
        const hasCommit = triggers.some(trigger => Number(trigger.kind) === TRIGGER.COMMIT) || preferredTrigger === TRIGGER.COMMIT;
        const timezone = scheduled?.timeZoneId || Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
        const scheduleKind = Number(scheduled?.scheduleKind ?? SCHEDULE.INTERVAL);
        const environment = this.findEnvironment(source.environmentId);
        const selectedCli = environment?.cli || getJobCliForLlm(source.llm) || 'codex';
        const selectedValue = environment
            ? buildLlmSelectionValue(selectedCli, environment.id)
            : source.environmentId
                ? buildLlmSelectionValue(selectedCli, source.environmentId)
                : '';
        const projectPath = this.currentProjectPath();

        editor.hidden = false;
        editor.innerHTML = `
            <form data-job-form class="job-inline-form">
                <div class="job-inline-form-header">
                    <div><span class="jobs-eyebrow">${isEdit ? 'Edit automation' : 'New automation'}</span><h3>${isEdit ? this.escape(source.name) : 'Create an automation'}</h3><p>The Environment owns the CLI, model, arguments, and initial message. This automation controls when it runs and its timeout.</p></div>
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="cancel-editor" aria-label="Close automation editor"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>
                </div>
                <div class="row g-3">
                    <div class="col-lg-5"><label class="form-label" for="job-name">Automation name</label><input class="form-control" id="job-name" maxlength="100" required value="${this.escape(source.name || '')}" placeholder="Security review after commit"></div>
                    <div class="col-lg-7">
                        <label class="form-label" for="job-llm-selection">Environment</label>
                        <div class="job-environment-picker-row">
                            <div class="job-environment-picker"><select class="form-select" id="job-llm-selection" required></select></div>
                            <button class="btn btn-outline-primary text-nowrap" type="button" data-job-action="add-environment-from-editor"><i class="fa-solid fa-plus me-1" aria-hidden="true"></i>Add Environment</button>
                            <button class="btn btn-outline-secondary text-nowrap" type="button" data-job-action="edit-selected-environment" hidden disabled>Edit Environment</button>
                        </div>
                        <small class="form-text text-muted">The saved model, arguments, CLI settings, and initial message stay synchronized with this Environment.</small>
                    </div>
                    <div class="col-12"><div class="job-environment-preview" data-job-environment-preview aria-live="polite"></div></div>
                </div>

                <fieldset class="job-trigger-fieldset mt-4"><legend>When should it run?</legend>
                    <label class="job-trigger-option"><input class="form-check-input" type="checkbox" id="job-trigger-schedule" ${scheduled ? 'checked' : ''}><span><strong>On a timer</strong><small>Run at an interval or at a local daily or weekly time.</small></span></label>
                    <div class="job-schedule-editor" data-schedule-editor ${scheduled ? '' : 'hidden'}>
                        <div class="row g-2">
                            <div class="col-md-4"><label class="form-label" for="job-schedule-kind">Schedule</label><select class="form-select" id="job-schedule-kind"><option value="0" ${scheduleKind === 0 ? 'selected' : ''}>Every interval</option><option value="1" ${scheduleKind === 1 ? 'selected' : ''}>Daily</option><option value="2" ${scheduleKind === 2 ? 'selected' : ''}>Weekly</option></select></div>
                            <div class="col-md-4" data-interval-field><label class="form-label" for="job-interval">Every</label><div class="input-group"><input class="form-control" type="number" id="job-interval" min="5" max="43200" value="${scheduled?.intervalMinutes || 60}"><span class="input-group-text">min</span></div></div>
                            <div class="col-md-4" data-clock-field><label class="form-label" for="job-local-time">Local time</label><input class="form-control" type="time" id="job-local-time" value="${this.escape(scheduled?.localTime || '09:00')}"></div>
                            <div class="col-12" data-weekdays-field><label class="form-label">Weekdays</label><div class="job-weekdays">${WEEKDAYS.map((day, index) => `<label><input class="form-check-input" type="checkbox" data-weekday="${index}" ${(Number(scheduled?.daysOfWeekMask || 0) & (1 << index)) !== 0 ? 'checked' : ''}><span>${day}</span></label>`).join('')}</div></div>
                            <div class="col-12" data-timezone-field><label class="form-label" for="job-timezone">Time zone</label><input class="form-control" id="job-timezone" value="${this.escape(timezone)}"></div>
                        </div>
                    </div>
                    <label class="job-trigger-option"><input class="form-check-input" type="checkbox" id="job-trigger-commit" ${hasCommit ? 'checked' : ''}><span><strong>After every successful commit</strong><small>Queued by the native post-commit hook and picked up by an active VibeRails instance.</small></span></label>
                    <div class="job-manual-note"><i class="fa-solid fa-play" aria-hidden="true"></i>Run now is always available, even when no automatic trigger is selected.</div>
                </fieldset>

                <details class="job-advanced-settings mt-3">
                    <summary>Advanced automation settings</summary>
                    <div class="row g-3 mt-0">
                        <div class="col-md-5">
                            <div class="form-check"><input class="form-check-input" type="checkbox" id="job-timeout-enabled" ${source.timeoutMinutes ? 'checked' : ''}><label class="form-check-label" for="job-timeout-enabled">Stop the run after a time limit</label></div>
                            <div class="input-group mt-2" data-timeout-field ${source.timeoutMinutes ? '' : 'hidden'}><input class="form-control" type="number" id="job-timeout" min="1" max="720" value="${source.timeoutMinutes || 60}"><span class="input-group-text">minutes</span></div>
                            <small class="form-text text-muted">Off by default — the run stays open until the CLI finishes or you close its terminal window.</small>
                        </div>
                        <div class="col-md-7 d-flex flex-column justify-content-center gap-2">
                            <div class="form-check form-switch"><input class="form-check-input" type="checkbox" id="job-enabled" ${source.enabled !== false ? 'checked' : ''}><label class="form-check-label" for="job-enabled">Enabled — allow this automation to run on its triggers</label></div>
                            <div class="form-check"><input class="form-check-input" type="checkbox" id="job-launch-minimized" ${source.launchMinimized === true ? 'checked' : ''}><label class="form-check-label" for="job-launch-minimized">Launch minimized</label><small class="form-text text-muted d-block">Applied when the operating system supports minimized terminal launches.</small></div>
                        </div>
                    </div>
                </details>

                <div class="job-repository-context"><i class="fa-solid fa-code-branch" aria-hidden="true"></i><span>Runs in the current VibeRails repository</span><code>${this.escape(projectPath || 'No Git repository detected')}</code></div>
                <div class="job-inline-form-actions"><button class="btn btn-outline-secondary" type="button" data-job-action="cancel-editor">Cancel</button><button class="btn btn-primary" type="submit">${isEdit ? 'Save automation' : 'Create automation'}</button></div>
            </form>`;

        const form = editor.querySelector('[data-job-form]');
        const selection = form?.querySelector('#job-llm-selection');
        this.refreshEditorEnvironmentPicker(null, selectedValue);

        const updateScheduleFields = () => {
            const enabled = form.querySelector('#job-trigger-schedule').checked;
            const kind = Number(form.querySelector('#job-schedule-kind').value);
            form.querySelector('[data-schedule-editor]').hidden = !enabled;
            form.querySelector('[data-interval-field]').hidden = !enabled || kind !== SCHEDULE.INTERVAL;
            form.querySelector('[data-clock-field]').hidden = !enabled || kind === SCHEDULE.INTERVAL;
            form.querySelector('[data-timezone-field]').hidden = !enabled || kind === SCHEDULE.INTERVAL;
            form.querySelector('[data-weekdays-field]').hidden = !enabled || kind !== SCHEDULE.WEEKLY;
            form.querySelector('#job-interval').disabled = !enabled || kind !== SCHEDULE.INTERVAL;
            form.querySelector('#job-local-time').disabled = !enabled || kind === SCHEDULE.INTERVAL;
            form.querySelector('#job-timezone').disabled = !enabled || kind === SCHEDULE.INTERVAL;
        };
        const updateTimeoutField = () => {
            const enabled = form.querySelector('#job-timeout-enabled')?.checked === true;
            const field = form.querySelector('[data-timeout-field]');
            if (field) field.hidden = !enabled;
        };
        form?.querySelector('#job-timeout-enabled')?.addEventListener('change', updateTimeoutField);
        updateTimeoutField();
        selection?.addEventListener('change', () => this.updateEditorEnvironmentPreview());
        form?.querySelector('[data-job-action="add-environment-from-editor"]')?.addEventListener('click', () => this.createEnvironmentFromEditor());
        form?.querySelector('[data-job-action="edit-selected-environment"]')?.addEventListener('click', () => this.editSelectedEnvironment());
        form?.querySelectorAll('[data-job-action="cancel-editor"]')?.forEach(button => button.addEventListener('click', () => this.closeEditor()));
        form?.querySelector('#job-trigger-schedule')?.addEventListener('change', updateScheduleFields);
        form?.querySelector('#job-schedule-kind')?.addEventListener('change', updateScheduleFields);
        form?.addEventListener('submit', event => this.saveJob(event, job));
        updateScheduleFields();
        this.updateEditorEnvironmentPreview();

        editor.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
        form?.querySelector('#job-name')?.focus?.();
    }

    refreshEditorEnvironmentPicker(selectedEnvironmentId = null, selectedValue = null) {
        const selection = this.root?.querySelector('[data-job-editor] #job-llm-selection');
        if (!selection) return;

        const currentValue = selectedValue
            || (selectedEnvironmentId == null ? (selection.tomselect?.getValue?.() || selection.value) : null);
        const preferredEnvironment = this.findEnvironment(selectedEnvironmentId);
        const valueToRestore = preferredEnvironment
            ? buildLlmSelectionValue(preferredEnvironment.cli, preferredEnvironment.id)
            : currentValue || '';

        this.disposeEditorModal();
        const parsedValue = parseLlmSelection(valueToRestore, this.environments);
        const missingEnvironment = valueToRestore.startsWith('env:')
            && !this.findEnvironment(parsedValue.envId);
        const pickerDisposer = this.app.llmPickerController.mount(selection, {
            context: 'automation',
            placeholder: this.environments.length > 0 ? 'Select an Environment...' : 'Add an Environment to continue...',
            selectedValue: valueToRestore,
            searchPlaceholder: 'Search Environments...',
            selectedFallback: missingEnvironment ? {
                value: valueToRestore,
                label: `${this.activeEditorJob?.environmentName || 'Deleted Environment'} (missing)`,
                cli: parsedValue.cli || '',
                environmentId: parsedValue.envId,
                environmentName: this.activeEditorJob?.environmentName || null,
                kind: 'environment',
                group: 'Custom Environments'
            } : null
        });
        this.registerEditorModalCleanup(selection, pickerDisposer);
        this.updateEditorEnvironmentPreview();
    }

    updateEditorEnvironmentPreview() {
        const form = this.root?.querySelector('[data-job-editor] [data-job-form]');
        if (!form) return;
        const selection = form.querySelector('#job-llm-selection');
        const preview = form.querySelector('[data-job-environment-preview]');
        const editButton = form.querySelector('[data-job-action="edit-selected-environment"]');
        const parsed = parseLlmSelection(selection?.tomselect?.getValue?.() || selection?.value, this.environments);
        const environment = parsed.kind === 'environment' ? this.findEnvironment(parsed.envId) : null;

        if (editButton) {
            editButton.hidden = !environment;
            editButton.disabled = !environment;
        }
        if (!preview) return;

        if (environment) {
            const prompt = (environment.customPrompt || '').trim();
            preview.dataset.tone = prompt ? 'ready' : 'warning';
            preview.innerHTML = prompt
                ? `<div><span><i class="fa-solid fa-layer-group" aria-hidden="true"></i><strong>${this.escape(environment.name)}</strong> owns this initial message</span><button class="btn btn-link btn-sm p-0" type="button" data-job-action="edit-preview-environment">Edit Environment</button></div><pre></pre>`
                : `<div><span><i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i><strong>${this.escape(environment.name)}</strong> needs an Initial Message before it can run as an Automation.</span><button class="btn btn-link btn-sm p-0" type="button" data-job-action="edit-preview-environment">Add initial message</button></div>`;
            const promptTarget = preview.querySelector('pre');
            if (promptTarget) promptTarget.textContent = prompt;
            preview.querySelector('[data-job-action="edit-preview-environment"]')?.addEventListener('click', () => this.editSelectedEnvironment());
            return;
        }

        preview.dataset.tone = 'empty';
        preview.innerHTML = '<span>Select an Environment, or add one without leaving this editor.</span>';
    }

    async createEnvironmentFromEditor() {
        const controller = this.app.environmentController;
        if (!controller) return;
        const knownIds = new Set(this.environments.map(environment => Number(environment.id)));
        const selection = this.root?.querySelector('[data-job-editor] #job-llm-selection');
        const current = parseLlmSelection(selection?.tomselect?.getValue?.() || selection?.value, this.environments);
        controller.createEnvironment({
            onChanged: async () => {
                const latest = this.app.data.environments || [];
                const created = latest.find(environment => !knownIds.has(Number(environment.id)));
                await this.environmentChanged({ selectedEnvironmentId: created?.id ?? current.envId });
            }
        });
    }

    async editSelectedEnvironment() {
        const controller = this.app.environmentController;
        const selection = this.root?.querySelector('[data-job-editor] #job-llm-selection');
        const parsed = parseLlmSelection(selection?.tomselect?.getValue?.() || selection?.value, this.environments);
        const environment = this.findEnvironment(parsed.envId);
        if (!controller || !environment) return;
        await controller.editEnvironment(environment.name, {
            onChanged: () => this.environmentChanged({ selectedEnvironmentId: environment.id })
        });
    }

    closeEditor() {
        this.disposeEditorModal();
        this.activeEditorJob = null;
        this.activeEditorSource = null;
        this.activeEditorPreferredTrigger = null;
        const editor = this.root?.querySelector('[data-job-editor]');
        if (!editor) return;
        editor.innerHTML = '';
        editor.hidden = true;
    }

    captureEditorState(form, { validate = false } = {}) {
        const parsedSelection = parseLlmSelection(
            form.querySelector('#job-llm-selection')?.tomselect?.getValue?.() || form.querySelector('#job-llm-selection')?.value,
            this.environments
        );
        const selectedEnvironment = parsedSelection.kind === 'environment'
            ? this.findEnvironment(parsedSelection.envId)
            : null;

        if (parsedSelection.kind === 'environment' && !selectedEnvironment) {
            if (validate) this.app.showError('The selected Environment no longer exists. Choose another Environment.');
            return null;
        }
        if (!selectedEnvironment) {
            if (validate) this.app.showError('Choose an Environment.');
            return null;
        }

        const llm = getJobLlmForCli(selectedEnvironment.cli);
        const prompt = (selectedEnvironment.customPrompt || '').trim();
        if (llm === null) {
            if (validate) this.app.showError('The selected Environment uses an unsupported CLI.');
            return null;
        }
        if (!prompt) {
            if (validate) this.app.showError('Edit this Environment and add an Initial Message before creating the automation.');
            return null;
        }

        const triggers = [];
        if (form.querySelector('#job-trigger-schedule').checked) {
            const scheduleKind = Number(form.querySelector('#job-schedule-kind').value);
            let mask = 0;
            form.querySelectorAll('[data-weekday]:checked').forEach(input => { mask |= 1 << Number(input.dataset.weekday); });
            if (validate && scheduleKind === SCHEDULE.WEEKLY && mask === 0) {
                this.app.showError('Choose at least one weekday.');
                return null;
            }
            triggers.push({
                kind: TRIGGER.SCHEDULE,
                scheduleKind,
                intervalMinutes: scheduleKind === SCHEDULE.INTERVAL ? Number(form.querySelector('#job-interval').value) : null,
                localTime: scheduleKind === SCHEDULE.INTERVAL ? null : form.querySelector('#job-local-time').value,
                daysOfWeekMask: scheduleKind === SCHEDULE.WEEKLY ? mask : 0,
                timeZoneId: scheduleKind === SCHEDULE.INTERVAL ? null : form.querySelector('#job-timezone').value.trim()
            });
        }
        if (form.querySelector('#job-trigger-commit').checked) triggers.push({ kind: TRIGGER.COMMIT });

        const projectPath = this.currentProjectPath();
        if (validate && !projectPath) {
            this.app.showError('Open VibeRails in a Git repository before creating an automation.');
            return null;
        }

        return {
            name: form.querySelector('#job-name').value.trim(),
            projectPath,
            llm,
            environmentId: Number(selectedEnvironment.id),
            prompt,
            // null = no time limit, which is the default. Only send a number when the user opted in.
            timeoutMinutes: form.querySelector('#job-timeout-enabled')?.checked
                ? Number(form.querySelector('#job-timeout').value)
                : null,
            enabled: form.querySelector('#job-enabled').checked,
            triggers,
            launchMinimized: form.querySelector('#job-launch-minimized')?.checked === true
        };
    }

    async saveJob(event, existingJob) {
        event.preventDefault();
        const form = event.currentTarget;
        const submit = form.querySelector('[type="submit"]');
        const payload = this.captureEditorState(form, { validate: true });
        if (!payload) return;

        submit.disabled = true;
        submit.textContent = existingJob ? 'Saving…' : 'Creating…';
        try {
            await this.app.apiCall(existingJob ? `/api/v1/jobs/${existingJob.id}` : '/api/v1/jobs', existingJob ? 'PUT' : 'POST', payload);
            this.closeEditor();
            this.app.showToast('Automation', existingJob ? 'Automation updated.' : 'Automation created.', 'success');
            await this.refreshAll({ quiet: true });
        } catch (error) {
            this.app.showError(error?.message || 'Could not save the automation.');
            submit.disabled = false;
            submit.textContent = existingJob ? 'Save automation' : 'Create automation';
        }
    }

    async runNow(jobId, button) {
        return this.withBusy(button, 'Queueing…', async () => {
            const response = await this.app.apiCall(`/api/v1/jobs/${jobId}/run`, 'POST');
            this.app.showToast('Automation queued', response?.message || 'The automation will start shortly.', 'success');
            await this.refreshRuns({ quiet: true });
        });
    }

    async toggleJob(jobId, button) {
        const job = this.jobs.find(item => item.id === jobId);
        if (!job) return;
        const environment = this.findEnvironment(job.environmentId);
        if (!environment) {
            this.app.showError('This automation needs a valid Environment before it can be enabled or disabled.');
            return;
        }
        const llm = getJobLlmForCli(environment.cli);
        if (llm === null) {
            this.app.showError('This automation uses an Environment with an unsupported CLI.');
            return;
        }
        const payload = {
            name: job.name, projectPath: this.currentProjectPath(), llm,
            environmentId: Number(environment.id), prompt: environment.customPrompt || '',
            timeoutMinutes: job.timeoutMinutes,
            enabled: !job.enabled,
            triggers: (job.triggers || []).map(({ kind, scheduleKind, intervalMinutes, localTime, daysOfWeekMask, timeZoneId }) => ({ kind, scheduleKind, intervalMinutes, localTime, daysOfWeekMask, timeZoneId }))
        };
        return this.withBusy(button, job.enabled ? 'Disabling…' : 'Enabling…', async () => {
            await this.app.apiCall(`/api/v1/jobs/${jobId}`, 'PUT', payload);
            await this.refreshAll({ quiet: true });
        });
    }

    async deleteJob(jobId) {
        const job = this.jobs.find(item => item.id === jobId);
        if (!job || !window.confirm(`Delete the automation “${job.name}”? Its run history will be kept.`)) return;
        try {
            await this.app.apiCall(`/api/v1/jobs/${jobId}`, 'DELETE');
            this.app.showToast('Automation', 'Automation deleted.', 'info');
            await this.refreshAll({ quiet: true });
        } catch (error) { this.app.showError(error?.message || 'Could not delete the automation.'); }
    }

    async cancelRun(runId, button) {
        return this.withBusy(button, 'Cancelling…', async () => {
            await this.app.apiCall(`/api/v1/jobs/runs/${encodeURIComponent(runId)}/cancel`, 'POST');
            await this.refreshRuns({ quiet: true });
        });
    }

    async retryRun(runId, button) {
        return this.withBusy(button, 'Queueing…', async () => {
            await this.app.apiCall(`/api/v1/jobs/runs/${encodeURIComponent(runId)}/retry`, 'POST');
            await this.refreshRuns({ quiet: true });
        });
    }

    async openRun(runId) {
        let run;
        try {
            run = await this.app.apiCall(`/api/v1/jobs/runs/${encodeURIComponent(runId)}`, 'GET', null, { showLoading: false });
        } catch (error) {
            this.app.showError(error?.message || 'Could not load this run.');
            return;
        }

        // A finished (or running) run IS a recorded terminal session — replay it with the exact same
        // xterm player Chat History uses. Automated sessions are hidden from the history list but stay
        // fetchable by id, so replay just works.
        if (run?.sessionId) {
            try {
                await showReplayModal(run.sessionId);
                return;
            } catch (error) {
                this.app.showError(error?.message || 'Could not replay this run.');
                return;
            }
        }

        // No session yet (run still queued, or it failed before launching a terminal).
        const status = RUN_STATUS[Number(run?.status)] || { label: 'Unknown', tone: 'neutral' };
        const active = Number(run?.status) <= 1;
        this.app.showModal('Automation run', `
            <div class="job-run-detail">
                <div class="job-run-detail-summary">
                    <div><strong>${this.escape(run?.jobName || 'Automation run')}</strong><span class="job-run-status" data-tone="${status.tone}">${status.label}</span></div>
                    <small>${this.escape(run?.projectPath || '')}</small>
                    ${run?.errorMessage ? `<p class="text-danger mb-0 mt-2">${this.escape(run.errorMessage)}</p>` : ''}
                    <p class="text-muted mb-0 mt-2">${active ? 'This run is starting — its recorded terminal will be ready to watch here shortly.' : 'This run has no recorded terminal to watch.'}</p>
                </div>
                <div class="d-flex justify-content-end gap-2 mt-3">
                    ${active ? '<button class="btn btn-outline-danger" type="button" data-run-cancel>Stop run</button>' : '<button class="btn btn-outline-secondary" type="button" data-run-retry>Run again</button>'}
                    <button class="btn btn-primary" type="button" data-action="close-modal">Close</button>
                </div>
            </div>`);
        const detail = document.querySelector('.job-run-detail');
        detail?.querySelector('[data-run-cancel]')?.addEventListener('click', async event => {
            await this.cancelRun(runId, event.currentTarget);
            this.app.closeModal();
            await this.refreshRuns({ quiet: true });
        });
        detail?.querySelector('[data-run-retry]')?.addEventListener('click', event => this.retryRun(runId, event.currentTarget));
    }

    formatTrigger(trigger) {
        const kind = Number(trigger.kind);
        if (kind === TRIGGER.COMMIT) return 'After successful commit';
        if (kind !== TRIGGER.SCHEDULE) return 'Manual';
        const scheduleKind = Number(trigger.scheduleKind);
        if (scheduleKind === SCHEDULE.INTERVAL) return `Every ${trigger.intervalMinutes} min`;
        if (scheduleKind === SCHEDULE.DAILY) return `Daily ${trigger.localTime}`;
        const days = WEEKDAYS.filter((_, index) => (Number(trigger.daysOfWeekMask) & (1 << index)) !== 0).join(', ');
        return `${days} ${trigger.localTime}`;
    }

    findEnvironment(environmentId) {
        if (environmentId == null || environmentId === '') return null;
        return this.environments.find(environment => Number(environment.id) === Number(environmentId)) || null;
    }

    currentProjectPath() {
        if (this.app.data.isInGit === false) return '';
        return String(this.app.data.configs?.rootPath || '').trim();
    }

    relativeTime(value) {
        try { return this.app.formatRelativeTime(value); } catch { return new Date(value).toLocaleString(); }
    }

    formatFutureTime(value, nowMilliseconds = Date.now()) {
        const scheduled = new Date(value);
        const scheduledMilliseconds = scheduled.getTime();
        if (!Number.isFinite(scheduledMilliseconds)) return 'Waiting for schedule';

        const remainingMilliseconds = scheduledMilliseconds - nowMilliseconds;
        if (remainingMilliseconds <= 0) return 'Due now';

        const minutes = Math.ceil(remainingMilliseconds / 60_000);
        if (minutes < 60) return `in ${minutes} min`;

        const now = new Date(nowMilliseconds);
        // Hour countdowns only while the run is still today, floored: ceil showed 1h05m
        // as "in 2 hr", overstating by nearly an hour. Once it crosses midnight an
        // absolute time ("Tomorrow 9:30 AM") reads better than a large hour count.
        if (scheduled.getFullYear() === now.getFullYear()
            && scheduled.getMonth() === now.getMonth()
            && scheduled.getDate() === now.getDate()) {
            return `in ${Math.max(1, Math.floor(remainingMilliseconds / 3_600_000))} hr`;
        }

        const tomorrow = new Date(now);
        tomorrow.setDate(now.getDate() + 1);
        if (scheduled.getFullYear() === tomorrow.getFullYear()
            && scheduled.getMonth() === tomorrow.getMonth()
            && scheduled.getDate() === tomorrow.getDate()) {
            return `Tomorrow ${scheduled.toLocaleTimeString(undefined, {
                hour: 'numeric',
                minute: '2-digit'
            })}`;
        }

        const options = {
            month: 'short',
            day: 'numeric',
            hour: 'numeric',
            minute: '2-digit'
        };
        if (scheduled.getFullYear() !== now.getFullYear()) options.year = 'numeric';
        return scheduled.toLocaleString(undefined, options);
    }

    formatDuration(start, end) {
        if (!start) return '—';
        const milliseconds = Math.max(0, new Date(end || Date.now()).getTime() - new Date(start).getTime());
        const seconds = Math.floor(milliseconds / 1000);
        if (seconds < 60) return `${seconds}s`;
        const minutes = Math.floor(seconds / 60);
        return `${minutes}m ${seconds % 60}s`;
    }

    async withBusy(button, label, operation) {
        const original = button?.innerHTML;
        const originalTitle = button?.getAttribute?.('title');
        if (button) {
            button.disabled = true;
            // Icon actions are a fixed 32px square, so a text label ("Queueing…") overflows
            // the box. Spin in place instead and put the wording in the tooltip, which is
            // already where an icon-only button explains itself.
            if (button.classList?.contains?.('job-icon-action')) {
                button.innerHTML = '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>';
                button.setAttribute?.('title', label);
            } else {
                button.textContent = label;
            }
        }
        try { await operation(); }
        catch (error) { this.app.showError(error?.message || 'The Automation action failed.'); }
        finally {
            if (button?.isConnected) {
                button.disabled = false;
                button.innerHTML = original;
                if (originalTitle !== null && originalTitle !== undefined) {
                    button.setAttribute?.('title', originalTitle);
                }
            }
        }
    }

    // ----- Recipes: export/import an Environment + Automation as a shareable recipe file -----
    // The human-readable Markdown renders on GitHub; a machine block (JSON inside an HTML comment)
    // is the source of truth for import. Only the env DEFINITION travels — never its config dir,
    // which holds the exporter's credentials; import rebuilds the dir locally.

    exportRecipe(jobId) {
        const job = this.jobs.find(item => item.id === jobId);
        if (!job) return;
        const environment = this.findEnvironment(job.environmentId);
        if (!environment) {
            this.app.showError('This automation needs a valid Environment before it can be exported.');
            return;
        }
        const cli = environment.cli;
        const customArgs = environment.customArgs || '';
        const prompt = (environment.customPrompt || '').trim();
        const model = this.extractArg(customArgs, ['--model', '-m']) || '';
        const effort = this.extractArg(customArgs, ['--effort']) || this.extractConfig(customArgs, 'model_reasoning_effort') || '';
        const triggers = (job.triggers || []).map(({ kind, scheduleKind, intervalMinutes, localTime, daysOfWeekMask, timeZoneId }) =>
            ({ kind, scheduleKind, intervalMinutes, localTime, daysOfWeekMask, timeZoneId }));

        const recipe = {
            recipeVersion: 'V1',
            name: job.name,
            llm: getLlmName(Number(getJobLlmForCli(cli) ?? job.llm)),
            cli,
            model,
            effort,
            customArgs,
            prompt,
            timeoutMinutes: job.timeoutMinutes,
            triggers
        };

        const whenText = triggers.length
            ? triggers.map(t => this.formatTrigger(t)).join(', ')
            : 'On demand only';
        const markdown = `# VibeRails Recipe — ${job.name}

- **Recipe version:** V1
- **LLM:** ${recipe.llm}${model ? `\n- **Model:** ${model}` : ''}${effort ? `\n- **Effort:** ${effort}` : ''}
- **Runs:** ${whenText}
- **Timeout:** ${job.timeoutMinutes ? `${job.timeoutMinutes} min` : 'none'}

## Initial message

${prompt ? prompt.split('\n').map(line => `> ${line}`).join('\n') : '> _(none)_'}

Import this file from the **Automation** screen in VibeRails to add the Environment and this automation.

<!-- viberails-recipe
${JSON.stringify(recipe, null, 2)}
viberails-recipe -->
`;

        const slug = (job.name || 'recipe').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'recipe';
        const blob = new Blob([markdown], { type: 'text/markdown' });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = `${slug}.recipe.md`;
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        URL.revokeObjectURL(url);
        this.app.showToast('Recipe exported', `Saved ${slug}.recipe.md — commit it to share this automation.`, 'success');
    }

    importRecipe() {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.md,.recipe.md,text/markdown';
        input.addEventListener('change', async () => {
            const file = input.files?.[0];
            if (!file) return;
            try {
                const text = await file.text();
                const recipe = this.parseRecipe(text);
                if (!recipe) {
                    this.app.showError('That file is not a VibeRails recipe.');
                    return;
                }
                this.confirmImportRecipe(recipe);
            } catch (error) {
                this.app.showError(error?.message || 'Could not read the recipe file.');
            }
        });
        input.click();
    }

    parseRecipe(text) {
        const match = /<!--\s*viberails-recipe\s*([\s\S]*?)\s*viberails-recipe\s*-->/.exec(text || '');
        if (!match) return null;
        try {
            const recipe = JSON.parse(match[1]);
            return recipe && recipe.cli && recipe.name ? recipe : null;
        } catch {
            return null;
        }
    }

    confirmImportRecipe(recipe) {
        const cli = String(recipe.cli || '').toLowerCase();
        const existingEnv = (this.environments || []).find(env => (env.name || '').toLowerCase() === String(recipe.name).toLowerCase() && (env.cli || '').toLowerCase() === cli);
        const whenText = (recipe.triggers || []).length
            ? (recipe.triggers || []).map(t => this.formatTrigger(t)).join(', ')
            : 'On demand only';
        // Preserve the exact strings applyRecipe will persist; trimming here could conceal leading
        // or trailing content while claiming that the user is reviewing the imported value.
        const customArgs = String(recipe.customArgs || '');
        const prompt = String(recipe.prompt || '');
        const hasReviewableContent = customArgs.trim().length > 0 || prompt.trim().length > 0;
        this.app.showModal('Import recipe', `
            <div class="job-recipe-import">
                <p>Import <strong>${this.escape(recipe.name)}</strong> (${this.escape(recipe.llm || cli)}) into this repository?</p>
                <ul class="text-muted small">
                    <li>Environment: <strong>${this.escape(recipe.name)}</strong>${recipe.model ? ` · ${this.escape(recipe.model)}` : ''}${existingEnv ? ' <em>(already exists — will be reused)</em>' : ''}</li>
                    <li>Runs: ${this.escape(whenText)} · ${recipe.timeoutMinutes ? `${this.escape(String(recipe.timeoutMinutes))} min limit` : 'no time limit'}</li>
                </ul>
                <div class="alert alert-warning small" role="alert">
                    <strong>Review the Environment content before importing.</strong>
                    Custom arguments can change approval or sandbox permissions, and the initial message becomes instructions to the agent.
                    ${existingEnv
                        ? 'The matching Environment already exists, so the recipe content below will not overwrite its settings.'
                        : 'The new Environment will import these fields exactly as shown.'}
                </div>
                <div class="job-recipe-review">
                    <div>
                        <div class="form-label mb-1">Custom arguments</div>
                        <pre class="job-recipe-review-value" data-recipe-custom-args>${this.escape(customArgs || '(none)')}</pre>
                    </div>
                    <div>
                        <div class="form-label mb-1">Initial message</div>
                        <pre class="job-recipe-review-value" data-recipe-prompt>${this.escape(prompt || '(none)')}</pre>
                    </div>
                </div>
                <label class="form-check"><input class="form-check-input" type="checkbox" id="recipe-add-env" ${existingEnv ? '' : 'checked'} ${existingEnv ? 'disabled' : ''}><span class="form-check-label">Add the Environment</span></label>
                <label class="form-check"><input class="form-check-input" type="checkbox" id="recipe-add-job" checked><span class="form-check-label">Add the automation (created disabled)</span></label>
                ${hasReviewableContent
                    ? '<label class="form-check mt-2"><input class="form-check-input" type="checkbox" id="recipe-reviewed"><span class="form-check-label">I have read the custom arguments and initial message above</span></label>'
                    : ''}
                <div class="d-flex justify-content-end gap-2 mt-3">
                    <button class="btn btn-outline-secondary" type="button" data-action="close-modal">Cancel</button>
                    <button class="btn btn-primary" type="button" id="recipe-import-confirm" ${hasReviewableContent ? 'disabled' : ''}>Import</button>
                </div>
            </div>`);
        // A recipe is an untrusted file from a repository: its custom arguments can turn off
        // approval prompts and its initial message is instructions the agent will follow. Showing
        // them is not the same as having read them, so when there is anything to read the Import
        // button stays disabled until the user says they did. Recipes carrying neither field have
        // nothing to review and are not made to click through a meaningless attestation.
        const reviewed = document.getElementById('recipe-reviewed');
        const confirmButton = document.getElementById('recipe-import-confirm');
        reviewed?.addEventListener('change', () => {
            if (confirmButton) confirmButton.disabled = !reviewed.checked;
        });
        confirmButton?.addEventListener('click', async event => {
            if (hasReviewableContent && !reviewed?.checked) return;
            const addEnv = document.getElementById('recipe-add-env')?.checked && !existingEnv;
            const addJob = document.getElementById('recipe-add-job')?.checked;
            await this.applyRecipe(recipe, { addEnv, addJob, existingEnv, button: event.currentTarget });
        });
    }

    async applyRecipe(recipe, { addEnv, addJob, existingEnv, button }) {
        return this.withBusy(button, 'Importing…', async () => {
            const cli = String(recipe.cli || '').toLowerCase();
            if (addEnv) {
                await this.app.apiCall('/api/v1/environments', 'POST', {
                    name: recipe.name,
                    cli,
                    customArgs: recipe.customArgs || '',
                    customPrompt: recipe.prompt || ''
                });
            }
            await this.refreshAll({ quiet: true });

            if (addJob) {
                const environment = (this.environments || []).find(env => (env.name || '').toLowerCase() === String(recipe.name).toLowerCase() && (env.cli || '').toLowerCase() === cli)
                    || existingEnv;
                if (!environment) throw new Error('The recipe Environment could not be created.');
                const llm = getJobLlmForCli(cli);
                await this.app.apiCall('/api/v1/jobs', 'POST', {
                    name: recipe.name,
                    projectPath: this.currentProjectPath(),
                    llm,
                    environmentId: Number(environment.id),
                    prompt: recipe.prompt || environment.customPrompt || '',
                    timeoutMinutes: Number(recipe.timeoutMinutes) || null,
                    enabled: false,
                    triggers: (recipe.triggers || []).filter(t => Number(t.kind) === TRIGGER.SCHEDULE || Number(t.kind) === TRIGGER.COMMIT)
                });
            }
            this.app.closeModal();
            this.app.showToast('Recipe imported', 'The Environment and automation were added.', 'success');
            await this.refreshAll({ quiet: true });
        });
    }

    extractArg(args, flags) {
        const tokens = String(args || '').split(/\s+/);
        for (let i = 0; i < tokens.length; i++) {
            for (const flag of flags) {
                if (tokens[i] === flag && tokens[i + 1]) return tokens[i + 1];
                if (tokens[i].startsWith(`${flag}=`)) return tokens[i].slice(flag.length + 1);
            }
        }
        return '';
    }

    extractConfig(args, key) {
        const match = new RegExp(`${key}=([^\\s"']+)`).exec(String(args || ''));
        return match ? match[1] : '';
    }

    escape(value) { return this.app.escapeHtml(String(value ?? '')); }

    // A prompt can be thousands of characters. Rendering all of it into a `title` produces a
    // tooltip that covers the window and is unreadable, so the hover preview is capped.
    truncate(value, max) {
        const text = String(value ?? '');
        return text.length <= max ? text : `${text.slice(0, max - 1).trimEnd()}…`;
    }
}
