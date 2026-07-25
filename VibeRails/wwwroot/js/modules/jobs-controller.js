import {
    buildLlmSelectionValue,
    enhanceLlmSelectWithTomSelect,
    getLlmName,
    parseLlmSelection,
    populateLlmSelectionSelect
} from './utils.js';
import { showReplayModal } from './session-viewer.js';

const LLM = Object.freeze({
    CODEX: 1,
    CLAUDE: 2,
    ANTIGRAVITY: 3,
    COPILOT: 4,
    OPENCODE: 6,
    GLM_52: 7,
    KIMI_K3: 8
});

const LLM_BY_CLI = Object.freeze({
    codex: LLM.CODEX,
    claude: LLM.CLAUDE,
    antigravity: LLM.ANTIGRAVITY,
    copilot: LLM.COPILOT,
    opencode: LLM.OPENCODE,
    'glm-5.2': LLM.GLM_52,
    'kimi-k3': LLM.KIMI_K3
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

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

export class JobController {
    constructor(app) {
        this.app = app;
        this.root = null;
        this.jobs = [];
        this.runs = [];
        this.environments = [];
        this.workerStatus = null;
        this.pollTimer = null;
        this.logTimer = null;
        this.runModalGeneration = 0;
        this.editorModalCleanup = null;
        this.activeEditorJob = null;
        this.activeEditorSource = null;
        this.activeEditorPreferredTrigger = null;
    }

    async loadView(data = {}) {
        this.unload();
        const content = document.getElementById('app-content');
        if (!content) return;
        content.innerHTML = this.renderPage();
        this.root = content.querySelector('[data-view="jobs"]');
        this.bindPageActions();
        await this.refreshAll();

        if (data?.newJob) {
            this.openEditor(null, Number(data.triggerKind));
        }
        this.pollTimer = window.setInterval(() => {
            if (this.app.currentView === 'jobs') this.refreshRuns({ quiet: true });
        }, 5000);
    }

    unload() {
        this.runModalGeneration += 1;
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

    registerEditorModalCleanup(selection) {
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
            if (selection?.tomselect && typeof selection.tomselect.destroy === 'function') {
                selection.tomselect.destroy();
            }
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
                        <h4 class="jobs-page-title text-uppercase fw-bold mb-0"><span class="text-gradient">Jobs</span></h4>
                        <p>Run a Custom Env on a schedule, after every commit, or on demand. Each run is a real recorded terminal you can replay right here.</p>
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

                <section class="jobs-section" aria-labelledby="jobs-scheduler-title">
                    <div class="jobs-section-heading">
                        <div><span class="jobs-eyebrow">Background scheduling</span><h2 id="jobs-scheduler-title">Run jobs while VibeRails is closed</h2></div>
                        <div data-jobs-scheduler-status></div>
                    </div>
                    <p class="jobs-section-intro">Scheduled jobs normally only fire while this dashboard is open. Register a background task and they fire whenever you're logged in — each one still opens a terminal window you can watch.</p>
                </section>

                <section class="jobs-section" aria-labelledby="job-environments-title" data-job-environments>
                    <div class="jobs-section-heading">
                        <div><span class="jobs-eyebrow">Shared configuration</span><h2 id="job-environments-title">Environment / Workers</h2></div>
                        <button class="btn btn-sm btn-outline-primary" type="button" data-job-action="new-environment">
                            <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>Add worker
                        </button>
                    </div>
                    <p class="jobs-section-intro">The same workers from the Workers screen — their CLI, model, arguments, and initial message stay in sync everywhere. A Job just decides when one runs.</p>
                    <div data-job-environments-table>
                        <div class="jobs-empty"><span class="spinner-border spinner-border-sm"></span> Loading workers…</div>
                    </div>
                </section>

                <section class="jobs-section" aria-labelledby="jobs-list-title">
                    <div class="jobs-section-heading">
                        <div><span class="jobs-eyebrow">When workers run</span><h2 id="jobs-list-title">Automation rules</h2></div>
                        <span class="jobs-count" data-jobs-count>0 rules</span>
                    </div>
                    <div class="job-inline-editor" data-job-editor hidden></div>
                    <div class="jobs-grid" data-jobs-list>
                        <div class="jobs-empty"><span class="spinner-border spinner-border-sm"></span> Loading automation rules…</div>
                    </div>
                </section>

                <section class="jobs-section" aria-labelledby="job-runs-title">
                    <div class="jobs-section-heading">
                        <div><span class="jobs-eyebrow">Recorded history</span><h2 id="job-runs-title">Recent runs</h2></div>
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
            if (action === 'new-environment') {
                return this.app.environmentController?.createEnvironment({
                    onChanged: () => this.environmentChanged()
                });
            }
            if (action === 'refresh') return this.refreshAll();
            if (action === 'install-scheduler') return this.setSchedulerInstalled(true);
            if (action === 'uninstall-scheduler') return this.setSchedulerInstalled(false);
            if (action === 'edit') return this.openEditor(this.jobs.find(job => job.id === jobId));
            if (action === 'run') return this.runNow(jobId, actionElement);
            if (action === 'toggle') return this.toggleJob(jobId, actionElement);
            if (action === 'delete') return this.deleteJob(jobId);
            if (action === 'export-recipe') return this.exportRecipe(jobId);
            if (action === 'import-recipe') return this.importRecipe();
            if (action === 'view-run' && runId) return this.openRun(runId);
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
                this.app.apiCall('/api/v1/jobs/runs?limit=100', 'GET', null, { showLoading: false }),
                this.app.apiCall('/api/v1/environments', 'GET', null, { showLoading: false })
            ]);
            this.jobs = jobsResponse?.jobs || [];
            this.runs = runsResponse?.runs || [];
            const environmentRecords = environmentsResponse?.environments || [];
            this.environments = this.app.environmentController?.setEnvironments
                ? this.app.environmentController.setEnvironments(environmentRecords)
                : environmentRecords;
            this.app.data.environments = this.environments;
            this.renderJobs();
            this.renderRuns();
            this.renderEnvironments();
            void this.refreshSchedulerStatus();
        } catch (error) {
            if (!quiet) this.app.showError(error?.message || 'Could not load Jobs.');
        }
    }

    async refreshSchedulerStatus() {
        const target = this.root?.querySelector('[data-jobs-scheduler-status]');
        if (!target) return;
        try {
            const status = await this.app.apiCall('/api/v1/jobs/scheduler', 'GET', null, { showLoading: false });
            this.renderSchedulerStatus(status);
        } catch (error) {
            target.innerHTML = `<span class="jobs-count">Unavailable</span>`;
        }
    }

    renderSchedulerStatus(status) {
        const target = this.root?.querySelector('[data-jobs-scheduler-status]');
        if (!target) return;
        if (!status?.supported) {
            target.innerHTML = `<span class="jobs-count">Not supported on this OS</span>`;
            return;
        }
        const installed = status.installed === true;
        target.innerHTML = `
            <div class="d-flex align-items-center gap-2">
                <span class="job-state" data-tone="${installed ? 'success' : 'neutral'}">${installed ? 'Registered' : 'Not registered'}</span>
                <button class="btn btn-sm ${installed ? 'btn-outline-secondary' : 'btn-outline-primary'}" type="button" data-job-action="${installed ? 'uninstall-scheduler' : 'install-scheduler'}">
                    ${installed ? 'Remove background task' : `Register with ${this.escape(status.platform || 'the OS')}`}
                </button>
            </div>`;
    }

    async setSchedulerInstalled(install) {
        try {
            const status = await this.app.apiCall('/api/v1/jobs/scheduler', install ? 'POST' : 'DELETE');
            this.renderSchedulerStatus(status);
            this.app.showToast(
                'Jobs',
                install
                    ? 'Background task registered. Scheduled jobs will now run whenever you are logged in.'
                    : 'Background task removed. Scheduled jobs only run while this dashboard is open.',
                'success');
        } catch (error) {
            this.app.showError(error?.message || 'Could not update the background task.');
        }
    }

    async refreshRuns({ quiet = false } = {}) {
        try {
            const response = await this.app.apiCall('/api/v1/jobs/runs?limit=100', 'GET', null, { showLoading: false });
            this.runs = response?.runs || [];
            this.renderRuns();
            if (!quiet) this.app.showToast('Jobs', 'Run history refreshed.', 'info');
        } catch (error) {
            if (!quiet) this.app.showError(error?.message || 'Could not refresh job runs.');
        }
    }

    renderEnvironments() {
        const target = this.root?.querySelector('[data-job-environments-table]');
        const controller = this.app.environmentController;
        if (!target || !controller) return;

        target.innerHTML = controller.renderEnvironmentsTable();
        controller.bindEnvironmentTableActions(target, {
            onChanged: () => this.environmentChanged()
        });
    }

    async environmentChanged({ selectedEnvironmentId = null } = {}) {
        this.environments = this.app.data.environments || [];
        this.renderEnvironments();
        this.renderJobs();
        this.refreshEditorWorkerPicker(selectedEnvironmentId);
    }

    renderJobs() {
        const target = this.root?.querySelector('[data-jobs-list]');
        const count = this.root?.querySelector('[data-jobs-count]');
        if (count) count.textContent = `${this.jobs.length} ${this.jobs.length === 1 ? 'rule' : 'rules'}`;
        if (!target) return;
        if (this.jobs.length === 0) {
            target.innerHTML = `
                <div class="jobs-empty jobs-empty-large">
                    <i class="fa-regular fa-clock" aria-hidden="true"></i>
                    <strong>No automation rules yet</strong>
                    <span>Choose a worker and run it on a timer, around commits, or manually.</span>
                    <button class="btn btn-primary" type="button" data-job-action="new">Create your first automation</button>
                </div>`;
            return;
        }

        target.innerHTML = this.jobs.map(job => {
            const triggers = (job.triggers || []).map(trigger => `<span class="jobs-trigger-chip">${this.escape(this.formatTrigger(trigger))}</span>`).join('');
            const llm = getLlmName(Number(job.llm));
            const cli = getJobCliForLlm(job.llm) || 'unknown';
            const environment = this.findEnvironment(job.environmentId);
            const workerName = environment?.name || job.environmentName || `${llm} default (legacy)`;
            const workerPrompt = environment?.customPrompt || job.prompt || '';
            const isLegacy = !job.environmentId;
            const jobId = this.escape(job.id);
            return `
                <article class="job-card" data-enabled="${job.enabled === true}">
                    <div class="job-card-topline">
                        <div class="job-card-title">
                            <span class="job-provider" data-provider="${cli}">${this.escape(llm)}</span>
                            <h3>${this.escape(job.name)}</h3>
                        </div>
                        <span class="job-state" data-tone="${job.enabled ? 'success' : 'neutral'}">${job.enabled ? 'Enabled' : 'Disabled'}</span>
                    </div>
                    <div class="job-worker-summary"><i class="fa-solid fa-robot" aria-hidden="true"></i><span><small>Environment / Worker</small><strong>${this.escape(workerName)}</strong></span></div>
                    ${workerPrompt ? `<details class="job-prompt-details"><summary>Worker initial message</summary><p class="job-prompt">${this.escape(workerPrompt)}</p></details>` : '<p class="job-prompt job-prompt-missing">This worker has no initial message.</p>'}
                    <div class="job-when"><span>When</span><div class="job-triggers">${triggers || '<span class="jobs-trigger-chip">Manual only</span>'}</div></div>
                    <div class="job-meta"><span><i class="fa-regular fa-hourglass-half"></i>${job.timeoutMinutes ? `${job.timeoutMinutes} min limit` : 'No time limit'}</span><span><i class="fa-solid fa-terminal"></i>Opens a terminal window</span></div>
                    <div class="job-card-actions">
                        <button class="btn btn-sm btn-primary" type="button" data-job-action="run" data-job-id="${jobId}"><i class="fa-solid fa-play me-1"></i>Run now</button>
                        <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="edit" data-job-id="${jobId}">Edit</button>
                        <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="toggle" data-job-id="${jobId}">${job.enabled ? 'Disable' : 'Enable'}</button>
                        <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="export-recipe" data-job-id="${jobId}" title="Export as recipe" aria-label="Export ${this.escape(job.name)} as recipe"><i class="fa-solid fa-file-export"></i></button>
                        <button class="btn btn-sm btn-outline-danger ms-auto" type="button" data-job-action="delete" data-job-id="${jobId}" aria-label="Delete ${this.escape(job.name)}"><i class="fa-solid fa-trash"></i></button>
                    </div>
                </article>`;
        }).join('');
    }

    renderRuns() {
        const target = this.root?.querySelector('[data-job-runs]');
        if (!target) return;
        if (this.runs.length === 0) {
            target.innerHTML = '<div class="jobs-empty">No job runs yet.</div>';
            return;
        }
        target.innerHTML = `
            <div class="table-responsive"><table class="table jobs-runs-table align-middle">
                <thead><tr><th>Job</th><th>Trigger</th><th>Status</th><th>Queued</th><th>Duration</th><th><span class="visually-hidden">Actions</span></th></tr></thead>
                <tbody>${this.runs.map(run => this.renderRunRow(run)).join('')}</tbody>
            </table></div>`;
    }

    renderRunRow(run) {
        const status = RUN_STATUS[Number(run.status)] || { label: 'Unknown', tone: 'neutral' };
        const trigger = { 0: 'Schedule', 2: 'After commit', 3: 'Manual' }[Number(run.triggerKind)] || 'Manual';
        const active = Number(run.status) === 0 || Number(run.status) === 1;
        const retryable = Number(run.status) >= 2;
        return `<tr>
            <td><button class="job-run-link" type="button" data-job-action="view-run" data-run-id="${this.escape(run.id)}">${this.escape(run.jobName)}</button><small>${this.escape(getLlmName(Number(run.llm)))}${run.environmentName ? ` · ${this.escape(run.environmentName)}` : ''}</small></td>
            <td>${trigger}</td>
            <td><span class="job-run-status" data-tone="${status.tone}">${status.label}</span></td>
            <td title="${this.escape(run.queuedUtc)}">${this.escape(this.relativeTime(run.queuedUtc))}</td>
            <td>${this.escape(this.formatDuration(run.startedUtc, run.endedUtc))}</td>
            <td class="text-end jobs-run-actions">
                ${active ? `<button class="btn btn-sm btn-outline-danger" type="button" data-job-action="cancel-run" data-run-id="${this.escape(run.id)}">Cancel</button>` : ''}
                ${retryable ? `<button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="retry-run" data-run-id="${this.escape(run.id)}">Retry</button>` : ''}
            </td>
        </tr>`;
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
            : isEdit && !source.environmentId
                ? buildLlmSelectionValue(selectedCli)
                : '';
        const projectPath = this.currentProjectPath();
        const legacyNotice = isEdit && !source.environmentId
            ? `<div class="job-legacy-notice" role="status"><i class="fa-solid fa-circle-info" aria-hidden="true"></i><span>This older rule uses the ${this.escape(getLlmName(source.llm))} default configuration. You can keep it as-is or move it to a shared Environment / Worker.</span></div>`
            : '';

        editor.hidden = false;
        editor.innerHTML = `
            <form data-job-form class="job-inline-form">
                <div class="job-inline-form-header">
                    <div><span class="jobs-eyebrow">${isEdit ? 'Edit rule' : 'New rule'}</span><h3>${isEdit ? this.escape(source.name) : 'Create an automation rule'}</h3><p>The worker owns the CLI, model, arguments, and initial message. This rule just controls when it runs and its timeout.</p></div>
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="cancel-editor" aria-label="Close automation editor"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>
                </div>
                ${legacyNotice}
                <div class="row g-3">
                    <div class="col-lg-5"><label class="form-label" for="job-name">Automation name</label><input class="form-control" id="job-name" maxlength="100" required value="${this.escape(source.name || '')}" placeholder="Security review after commit"></div>
                    <div class="col-lg-7">
                        <label class="form-label" for="job-llm-selection">Environment / Worker</label>
                        <div class="job-worker-picker-row">
                            <div class="job-worker-picker"><select class="form-select" id="job-llm-selection" required></select></div>
                            <button class="btn btn-outline-primary text-nowrap" type="button" data-job-action="add-worker-from-editor"><i class="fa-solid fa-plus me-1" aria-hidden="true"></i>Add worker</button>
                            <button class="btn btn-outline-secondary text-nowrap" type="button" data-job-action="edit-selected-environment" hidden disabled>Edit worker</button>
                        </div>
                        <small class="form-text text-muted">New automation rules use a custom worker so its saved model, arguments, CLI settings, and initial message stay synchronized.</small>
                    </div>
                    <div class="col-12"><div class="job-worker-preview" data-job-worker-preview aria-live="polite"></div></div>
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
                    <label class="job-trigger-option"><input class="form-check-input" type="checkbox" id="job-trigger-commit" ${hasCommit ? 'checked' : ''}><span><strong>After every successful commit</strong><small>Queued by the native post-commit hook, then started by the scheduler within a minute.</small></span></label>
                    <div class="job-manual-note"><i class="fa-solid fa-play" aria-hidden="true"></i>Run now is always available, even when no automatic trigger is selected.</div>
                </fieldset>

                <details class="job-advanced-settings mt-3">
                    <summary>Advanced job settings</summary>
                    <div class="row g-3 mt-0">
                        <div class="col-md-5">
                            <div class="form-check"><input class="form-check-input" type="checkbox" id="job-timeout-enabled" ${source.timeoutMinutes ? 'checked' : ''}><label class="form-check-label" for="job-timeout-enabled">Stop the run after a time limit</label></div>
                            <div class="input-group mt-2" data-timeout-field ${source.timeoutMinutes ? '' : 'hidden'}><input class="form-control" type="number" id="job-timeout" min="1" max="720" value="${source.timeoutMinutes || 60}"><span class="input-group-text">minutes</span></div>
                            <small class="form-text text-muted">Off by default — the run stays open until the CLI finishes or you close its terminal window.</small>
                        </div>
                        <div class="col-md-7 d-flex align-items-center"><div class="form-check form-switch"><input class="form-check-input" type="checkbox" id="job-enabled" ${source.enabled !== false ? 'checked' : ''}><label class="form-check-label" for="job-enabled">Enabled — allow this automation to run on its triggers</label></div></div>
                    </div>
                </details>

                <div class="job-repository-context"><i class="fa-solid fa-code-branch" aria-hidden="true"></i><span>Runs in the current VibeRails repository</span><code>${this.escape(projectPath || 'No Git repository detected')}</code></div>
                <div class="job-inline-form-actions"><button class="btn btn-outline-secondary" type="button" data-job-action="cancel-editor">Cancel</button><button class="btn btn-primary" type="submit">${isEdit ? 'Save automation' : 'Create automation'}</button></div>
            </form>`;

        const form = editor.querySelector('[data-job-form]');
        const selection = form?.querySelector('#job-llm-selection');
        this.refreshEditorWorkerPicker(null, selectedValue);

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
        selection?.addEventListener('change', () => this.updateEditorWorkerPreview());
        form?.querySelector('[data-job-action="add-worker-from-editor"]')?.addEventListener('click', () => this.createWorkerFromEditor());
        form?.querySelector('[data-job-action="edit-selected-environment"]')?.addEventListener('click', () => this.editSelectedWorker());
        form?.querySelectorAll('[data-job-action="cancel-editor"]')?.forEach(button => button.addEventListener('click', () => this.closeEditor()));
        form?.querySelector('#job-trigger-schedule')?.addEventListener('change', updateScheduleFields);
        form?.querySelector('#job-schedule-kind')?.addEventListener('change', updateScheduleFields);
        form?.addEventListener('submit', event => this.saveJob(event, job));
        updateScheduleFields();
        this.updateEditorWorkerPreview();

        editor.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
        form?.querySelector('#job-name')?.focus?.();
    }

    refreshEditorWorkerPicker(selectedEnvironmentId = null, selectedValue = null) {
        const selection = this.root?.querySelector('[data-job-editor] #job-llm-selection');
        if (!selection) return;

        const currentValue = selectedValue
            || (selectedEnvironmentId == null ? (selection.tomselect?.getValue?.() || selection.value) : null);
        const preferredEnvironment = this.findEnvironment(selectedEnvironmentId);
        const valueToRestore = preferredEnvironment
            ? buildLlmSelectionValue(preferredEnvironment.cli, preferredEnvironment.id)
            : currentValue || '';

        this.disposeEditorModal();
        populateLlmSelectionSelect(selection, this.environments, {
            placeholder: this.environments.length > 0 ? 'Select a custom worker...' : 'Add a worker to continue...',
            selectedValue: valueToRestore,
            includeBase: false,
            enhance: false
        });

        if (valueToRestore.startsWith('base:') && this.activeEditorJob && !this.activeEditorJob.environmentId) {
            const cli = parseLlmSelection(valueToRestore, this.environments).cli || getJobCliForLlm(this.activeEditorJob.llm) || 'unknown';
            const option = document.createElement('option');
            option.value = valueToRestore;
            option.textContent = `${getLlmName(this.activeEditorJob.llm)} default (legacy)`;
            option.dataset.cli = cli;
            selection.appendChild(option);
            selection.value = valueToRestore;
        } else if (valueToRestore.startsWith('env:') && !this.findEnvironment(parseLlmSelection(valueToRestore, this.environments).envId)) {
            const parsed = parseLlmSelection(valueToRestore, this.environments);
            const option = document.createElement('option');
            option.value = valueToRestore;
            option.textContent = `${this.activeEditorJob?.environmentName || 'Deleted worker'} (missing)`;
            option.dataset.cli = parsed.cli || '';
            selection.appendChild(option);
            selection.value = valueToRestore;
        }

        enhanceLlmSelectWithTomSelect(selection, {
            placeholder: this.environments.length > 0 ? 'Select a custom worker...' : 'Add a worker to continue...',
            searchPlaceholder: 'Search workers...'
        });
        this.registerEditorModalCleanup(selection);
        this.updateEditorWorkerPreview();
    }

    updateEditorWorkerPreview() {
        const form = this.root?.querySelector('[data-job-editor] [data-job-form]');
        if (!form) return;
        const selection = form.querySelector('#job-llm-selection');
        const preview = form.querySelector('[data-job-worker-preview]');
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
                ? `<div><span><i class="fa-solid fa-robot" aria-hidden="true"></i><strong>${this.escape(environment.name)}</strong> owns this initial message</span><button class="btn btn-link btn-sm p-0" type="button" data-job-action="edit-preview-worker">Edit worker settings</button></div><pre></pre>`
                : `<div><span><i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i><strong>${this.escape(environment.name)}</strong> needs an Initial Message before it can run as a Job.</span><button class="btn btn-link btn-sm p-0" type="button" data-job-action="edit-preview-worker">Add initial message</button></div>`;
            const promptTarget = preview.querySelector('pre');
            if (promptTarget) promptTarget.textContent = prompt;
            preview.querySelector('[data-job-action="edit-preview-worker"]')?.addEventListener('click', () => this.editSelectedWorker());
            return;
        }

        if (parsed.kind === 'base' && this.activeEditorJob && !this.activeEditorJob.environmentId) {
            preview.dataset.tone = 'legacy';
            preview.innerHTML = `<div><span><i class="fa-solid fa-circle-info" aria-hidden="true"></i><strong>Legacy base worker</strong> — its existing message is kept read-only until you choose a shared worker.</span></div>${this.activeEditorSource?.prompt ? `<pre>${this.escape(this.activeEditorSource.prompt)}</pre>` : ''}`;
            return;
        }

        preview.dataset.tone = 'empty';
        preview.innerHTML = '<span>Select a custom Environment / Worker, or add one with the full Environment settings form.</span>';
    }

    async createWorkerFromEditor() {
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

    async editSelectedWorker() {
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

    captureEditorState(form, { validate = false, source = null } = {}) {
        const editorSource = source || this.activeEditorSource || this.activeEditorJob || {};
        const parsedSelection = parseLlmSelection(
            form.querySelector('#job-llm-selection')?.tomselect?.getValue?.() || form.querySelector('#job-llm-selection')?.value,
            this.environments
        );
        const selectedEnvironment = parsedSelection.kind === 'environment'
            ? this.findEnvironment(parsedSelection.envId)
            : null;
        const isLegacyBase = parsedSelection.kind === 'base'
            && Boolean(this.activeEditorJob)
            && !this.activeEditorJob.environmentId;

        if (parsedSelection.kind === 'environment' && !selectedEnvironment) {
            if (validate) this.app.showError('The selected Environment / Worker no longer exists. Choose another worker.');
            return null;
        }
        if (!selectedEnvironment && !isLegacyBase) {
            if (validate) this.app.showError('Choose a custom Environment / Worker.');
            return null;
        }

        const llm = getJobLlmForCli(selectedEnvironment?.cli || parsedSelection.cli);
        const prompt = selectedEnvironment
            ? (selectedEnvironment.customPrompt || '').trim()
            : (editorSource.prompt || '').trim();
        if (llm === null) {
            if (validate) this.app.showError('The selected worker uses an unsupported CLI.');
            return null;
        }
        if (!prompt) {
            if (validate) this.app.showError('Edit this Environment / Worker and add an Initial Message before creating the automation.');
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
            environmentId: selectedEnvironment ? Number(selectedEnvironment.id) : null,
            prompt,
            // null = no time limit, which is the default. Only send a number when the user opted in.
            timeoutMinutes: form.querySelector('#job-timeout-enabled')?.checked
                ? Number(form.querySelector('#job-timeout').value)
                : null,
            enabled: form.querySelector('#job-enabled').checked,
            triggers
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
            this.app.showToast('Jobs', existingJob ? 'Automation updated.' : 'Automation created.', 'success');
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
            this.app.showToast('Job queued', response?.message || 'The job will start shortly.', 'success');
            await this.refreshRuns({ quiet: true });
        });
    }

    async toggleJob(jobId, button) {
        const job = this.jobs.find(item => item.id === jobId);
        if (!job) return;
        const environment = this.findEnvironment(job.environmentId);
        const payload = {
            name: job.name, projectPath: this.currentProjectPath(), llm: environment ? getJobLlmForCli(environment.cli) : job.llm,
            environmentId: job.environmentId, prompt: environment ? (environment.customPrompt || '') : job.prompt,
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
        if (!job || !window.confirm(`Delete the job “${job.name}”? Its run history will be kept.`)) return;
        try {
            await this.app.apiCall(`/api/v1/jobs/${jobId}`, 'DELETE');
            this.app.showToast('Jobs', 'Job deleted.', 'info');
            await this.refreshAll({ quiet: true });
        } catch (error) { this.app.showError(error?.message || 'Could not delete the job.'); }
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
        this.app.showModal('Job run', `
            <div class="job-run-detail">
                <div class="job-run-detail-summary">
                    <div><strong>${this.escape(run?.jobName || 'Job run')}</strong><span class="job-run-status" data-tone="${status.tone}">${status.label}</span></div>
                    <small>${this.escape(run?.projectPath || '')}</small>
                    ${run?.errorMessage ? `<p class="text-danger mb-0 mt-2">${this.escape(run.errorMessage)}</p>` : ''}
                    <p class="text-muted mb-0 mt-2">${active ? 'This run is starting — its recorded terminal will be replayable here shortly.' : 'This run has no recorded terminal to replay.'}</p>
                </div>
                <div class="d-flex justify-content-end gap-2 mt-3">
                    ${active ? '<button class="btn btn-outline-danger" type="button" data-run-cancel>Cancel run</button>' : '<button class="btn btn-outline-secondary" type="button" data-run-retry>Retry</button>'}
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

    async attachRulesAutomation(root) {
        const host = root?.querySelector('[data-jobs-automation-host]');
        if (!host) return;
        const projectPath = this.app.data.configs?.rootPath;
        if (!projectPath || !this.app.data.isInGit) {
            host.innerHTML = '<div class="jobs-automation-empty">Open a Git repository to attach Jobs to VCA and commit events.</div>';
            return;
        }
        host.innerHTML = '<div class="jobs-automation-empty"><span class="spinner-border spinner-border-sm"></span> Loading project automations…</div>';
        try {
            const response = await this.app.apiCall(`/api/v1/jobs?projectPath=${encodeURIComponent(projectPath)}`, 'GET', null, { showLoading: false });
            const jobs = (response?.jobs || []).filter(job => (job.triggers || []).some(trigger => Number(trigger.kind) === TRIGGER.COMMIT));
            host.innerHTML = jobs.length === 0
                ? `<div class="jobs-automation-empty"><strong>No commit-triggered automation yet</strong><span>Run a Worker automatically after every successful commit.</span></div>`
                : `<div class="jobs-automation-list">${jobs.map(job => `<article><div><strong>${this.escape(job.name)}</strong><small>${this.escape(job.environmentName || getLlmName(Number(job.llm)))} · After successful commit</small></div><span class="job-state" data-tone="${job.enabled ? 'success' : 'neutral'}">${job.enabled ? 'On' : 'Off'}</span><button class="btn btn-sm btn-outline-secondary" type="button" data-rules-job-run="${this.escape(job.id)}">Run now</button></article>`).join('')}</div>`;
            host.querySelectorAll('[data-rules-job-run]').forEach(button => button.addEventListener('click', () => this.runNow(Number(button.dataset.rulesJobRun), button)));
        } catch (error) {
            host.innerHTML = `<div class="jobs-automation-empty text-danger">${this.escape(error?.message || 'Could not load project Jobs.')}</div>`;
        }

        root.querySelector('[data-action="open-jobs"]')?.addEventListener('click', () => this.app.navigate('jobs', {}, { resetStack: true }));
        root.querySelector('[data-action="new-commit-job"]')?.addEventListener('click', () => this.app.navigate('jobs', { newJob: true, triggerKind: TRIGGER.COMMIT }, { resetStack: true }));
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
        if (button) { button.disabled = true; button.textContent = label; }
        try { await operation(); }
        catch (error) { this.app.showError(error?.message || 'The Jobs action failed.'); }
        finally { if (button?.isConnected) { button.disabled = false; button.innerHTML = original; } }
    }

    // ----- Recipes: export/import a Custom Env + Job as a shareable {name}.recipe.md file -----
    // The human-readable Markdown renders on GitHub; a machine block (JSON inside an HTML comment)
    // is the source of truth for import. Only the env DEFINITION travels — never its config dir,
    // which holds the exporter's credentials; import rebuilds the dir locally.

    exportRecipe(jobId) {
        const job = this.jobs.find(item => item.id === jobId);
        if (!job) return;
        const environment = this.findEnvironment(job.environmentId);
        const cli = environment?.cli || getJobCliForLlm(job.llm) || 'claude';
        const customArgs = environment?.customArgs || '';
        const prompt = (environment?.customPrompt || job.prompt || '').trim();
        const model = this.extractArg(customArgs, ['--model', '-m']) || '';
        const effort = this.extractArg(customArgs, ['--effort']) || this.extractConfig(customArgs, 'model_reasoning_effort') || '';
        const triggers = (job.triggers || []).map(({ kind, scheduleKind, intervalMinutes, localTime, daysOfWeekMask, timeZoneId }) =>
            ({ kind, scheduleKind, intervalMinutes, localTime, daysOfWeekMask, timeZoneId }));

        const recipe = {
            recipeVersion: 'V1',
            name: job.name,
            llm: getLlmName(Number(job.llm)),
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

Import this file from the **Jobs** screen in VibeRails to add the worker and this automation.

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
                    <li>Worker: <strong>${this.escape(recipe.name)}</strong>${recipe.model ? ` · ${this.escape(recipe.model)}` : ''}${existingEnv ? ' <em>(already exists — will be reused)</em>' : ''}</li>
                    <li>Runs: ${this.escape(whenText)} · ${recipe.timeoutMinutes ? `${this.escape(String(recipe.timeoutMinutes))} min limit` : 'no time limit'}</li>
                </ul>
                <div class="alert alert-warning small" role="alert">
                    <strong>Review the worker content before importing.</strong>
                    Custom arguments can change approval or sandbox permissions, and the initial message becomes instructions to the agent.
                    ${existingEnv
                        ? 'The matching worker already exists, so the recipe content below will not overwrite its settings.'
                        : 'The new worker will import these fields exactly as shown.'}
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
                <label class="form-check"><input class="form-check-input" type="checkbox" id="recipe-add-env" ${existingEnv ? '' : 'checked'} ${existingEnv ? 'disabled' : ''}><span class="form-check-label">Add the Environment / Worker</span></label>
                <label class="form-check"><input class="form-check-input" type="checkbox" id="recipe-add-job" checked><span class="form-check-label">Add the automation Job (created disabled)</span></label>
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
                if (!environment) throw new Error('The recipe worker could not be created.');
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
            this.app.showToast('Recipe imported', 'The worker and automation were added.', 'success');
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
}
