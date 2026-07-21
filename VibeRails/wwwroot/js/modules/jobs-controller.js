import {
    buildLlmSelectionValue,
    enhanceLlmSelectWithTomSelect,
    getLlmName,
    parseLlmSelection,
    populateLlmSelectionSelect
} from './utils.js';

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
const EXECUTION = Object.freeze({ REVIEW: 0, ISOLATED: 1, LIVE: 2 });
const TRIGGER = Object.freeze({ SCHEDULE: 0, VCA: 1, COMMIT: 2, MANUAL: 3 });
const SCHEDULE = Object.freeze({ INTERVAL: 0, DAILY: 1, WEEKLY: 2 });

const EXECUTION_MODE_WARNINGS = Object.freeze({
    [EXECUTION.REVIEW]: 'Requests read-only behavior from the CLI, but the process still has your operating-system account\'s permissions. Configuration, tools, MCP servers, or scripts may read or modify files outside this repository.',
    [EXECUTION.ISOLATED]: 'Uses a throwaway Git clone to reduce accidental changes to the selected working tree. This is not an operating-system or process sandbox; the agent may access or modify files outside the clone.',
    [EXECUTION.LIVE]: 'Can modify this working tree and anything else your operating-system account can access. Use only with trusted prompts, environments, tools, and repositories.'
});

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
                        <p>Choose a reusable Environment / Worker, then decide whether it runs on a schedule, around commits, or only on demand.</p>
                    </div>
                    <button class="btn btn-primary" type="button" data-job-action="new">
                        <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>New automation
                    </button>
                </header>

                <section class="jobs-security-notice" role="alert" aria-labelledby="jobs-security-title">
                    <i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i>
                    <div>
                        <strong id="jobs-security-title">Jobs are not sandboxed</strong>
                        <p>Jobs run unattended with your operating-system account's permissions. Execution modes reduce accidental working-tree changes; they are not security boundaries. Workers, custom arguments, CLI configuration, MCP servers, tools, and scripts can access or modify files outside the selected repository or clone. Review every worker, and use a disposable account or machine for untrusted work.</p>
                    </div>
                </section>

                <section class="jobs-worker-card" data-jobs-worker aria-live="polite" hidden>
                    <span class="spinner-border spinner-border-sm" aria-hidden="true"></span>
                    <span>Checking the always-on worker…</span>
                </section>

                <section class="jobs-section" aria-labelledby="job-environments-title" data-job-environments>
                    <div class="jobs-section-heading">
                        <div><span class="jobs-eyebrow">Shared configuration</span><h2 id="job-environments-title">Environment / Workers</h2></div>
                        <button class="btn btn-sm btn-outline-primary" type="button" data-job-action="new-environment">
                            <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>Add worker
                        </button>
                    </div>
                    <p class="jobs-section-intro">These are the same workers shown on the Workers screen. Their CLI configuration, model, arguments, and initial message stay in sync everywhere; each automation may add an unattended workspace policy.</p>
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
                        <div><span class="jobs-eyebrow">Separate history</span><h2 id="job-runs-title">Recent runs</h2></div>
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
            if (action === 'edit') return this.openEditor(this.jobs.find(job => job.id === jobId));
            if (action === 'run') return this.runNow(jobId, actionElement);
            if (action === 'toggle') return this.toggleJob(jobId, actionElement);
            if (action === 'delete') return this.deleteJob(jobId);
            if (action === 'repair-worker') return this.repairWorker(actionElement);
            if (action === 'disable-worker') return this.disableWorker();
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
            const [jobsResponse, runsResponse, workerResponse, environmentsResponse] = await Promise.all([
                jobsRequest,
                this.app.apiCall('/api/v1/jobs/runs?limit=100', 'GET', null, { showLoading: false }),
                this.app.apiCall('/api/v1/jobs/worker', 'GET', null, { showLoading: false }),
                this.app.apiCall('/api/v1/environments', 'GET', null, { showLoading: false })
            ]);
            this.jobs = jobsResponse?.jobs || [];
            this.runs = runsResponse?.runs || [];
            this.workerStatus = workerResponse || null;
            const environmentRecords = environmentsResponse?.environments || [];
            this.environments = this.app.environmentController?.setEnvironments
                ? this.app.environmentController.setEnvironments(environmentRecords)
                : environmentRecords;
            this.app.data.environments = this.environments;
            this.renderJobs();
            this.renderRuns();
            this.renderWorker();
            this.renderEnvironments();
        } catch (error) {
            if (!quiet) this.app.showError(error?.message || 'Could not load Jobs.');
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

    renderWorker() {
        const target = this.root?.querySelector('[data-jobs-worker]');
        if (!target) return;
        // The worker is created on demand when a job needs it. With no jobs there is
        // nothing to repair, so avoid presenting an alarming inactive-worker prompt.
        target.hidden = this.jobs.length === 0;
        if (target.hidden) return;

        const status = this.workerStatus || {};
        const healthy = status.running && !status.needsRepair;
        const tone = healthy ? 'success' : status.installed ? 'warning' : 'neutral';
        target.dataset.tone = tone;
        target.innerHTML = `
            <span class="jobs-worker-icon" aria-hidden="true"><i class="fa-solid ${healthy ? 'fa-circle-check' : 'fa-triangle-exclamation'}"></i></span>
            <div class="jobs-worker-copy">
                <strong>${healthy ? 'Jobs worker is running' : status.installed ? 'Jobs worker needs attention' : 'Jobs worker is off'}</strong>
                <span>${this.escape(status.message || 'The worker starts automatically when you enable or run a job.')}</span>
                ${status.heartbeatUtc ? `<small>Last heartbeat ${this.escape(this.relativeTime(status.heartbeatUtc))}</small>` : ''}
            </div>
            <div class="jobs-worker-actions">
                ${status.needsRepair || !status.running ? '<button class="btn btn-sm btn-primary" type="button" data-job-action="repair-worker">Repair & start</button>' : ''}
                ${status.installed ? '<button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="disable-worker">Disable worker</button>' : ''}
            </div>`;
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
            const mode = ['Review requested', 'Throwaway clone (not sandboxed)', 'Live repository write'][Number(job.executionMode)] || 'Unknown mode';
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
                    <div class="job-meta"><span><i class="fa-solid fa-shield-halved"></i>${this.escape(mode)}</span><span><i class="fa-regular fa-hourglass-half"></i>${job.timeoutMinutes} min</span></div>
                    <div class="job-card-actions">
                        <button class="btn btn-sm btn-primary" type="button" data-job-action="run" data-job-id="${jobId}"><i class="fa-solid fa-play me-1"></i>Run now</button>
                        <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="edit" data-job-id="${jobId}">Edit</button>
                        <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="toggle" data-job-id="${jobId}">${job.enabled ? 'Disable' : 'Enable'}</button>
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
        const trigger = ['Schedule', 'Pre-commit VCA', 'After successful commit', 'Manual'][Number(run.triggerKind)] || 'Unknown';
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
        const hasVca = triggers.some(trigger => Number(trigger.kind) === TRIGGER.VCA) || preferredTrigger === TRIGGER.VCA;
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
                    <div><span class="jobs-eyebrow">${isEdit ? 'Edit rule' : 'New rule'}</span><h3>${isEdit ? this.escape(source.name) : 'Create an automation rule'}</h3><p>The worker owns the agent configuration and initial message. This rule controls when it runs, with optional timeout and workspace-policy overrides.</p></div>
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
                    <label class="job-trigger-option"><input class="form-check-input" type="checkbox" id="job-trigger-vca" ${hasVca ? 'checked' : ''}><span><strong>After each real pre-commit VCA check</strong><small>Queues in the background whether VCA passes or blocks. The Job itself never blocks the commit, and Rules previews do not trigger it.</small></span></label>
                    <label class="job-trigger-option"><input class="form-check-input" type="checkbox" id="job-trigger-commit" ${hasCommit ? 'checked' : ''}><span><strong>After every successful commit</strong><small>Runs only after Git completes the commit through the native post-commit hook.</small></span></label>
                    <div class="job-manual-note"><i class="fa-solid fa-play" aria-hidden="true"></i>Run now is always available, even when no automatic trigger is selected.</div>
                </fieldset>

                <details class="job-advanced-settings mt-3">
                    <summary>Advanced job settings</summary>
                    <div class="row g-3 mt-0">
                        <div class="col-md-4"><label class="form-label" for="job-timeout">Timeout</label><div class="input-group"><input class="form-control" type="number" id="job-timeout" min="1" max="120" required value="${source.timeoutMinutes || 30}"><span class="input-group-text">minutes</span></div></div>
                        <div class="col-md-8"><label class="form-label" for="job-mode">Workspace mode</label><select class="form-select" id="job-mode"><option value="0" ${Number(source.executionMode ?? 0) === 0 ? 'selected' : ''}>Review only — requests read-only behavior; not a sandbox</option><option value="1" ${Number(source.executionMode) === 1 ? 'selected' : ''}>Isolated write — throwaway clone only; not a sandbox</option><option value="2" ${Number(source.executionMode) === 2 ? 'selected' : ''}>Live repository write — edits this working tree</option></select></div>
                        <div class="col-12"><div class="job-mode-warning" data-mode-warning><i class="fa-solid fa-triangle-exclamation"></i><span data-mode-warning-copy></span></div></div>
                        <div class="col-12"><small class="form-text text-muted">Workspace mode is an automation-specific override used to keep unattended runs non-interactive; the shared worker remains the source for its other CLI settings.</small></div>
                        <div class="col-12"><div class="form-check form-switch"><input class="form-check-input" type="checkbox" id="job-enabled" ${source.enabled !== false ? 'checked' : ''}><label class="form-check-label" for="job-enabled">Enabled</label></div></div>
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
        const updateModeWarning = () => {
            const mode = Number(form.querySelector('#job-mode').value);
            form.querySelector('[data-mode-warning-copy]').textContent = EXECUTION_MODE_WARNINGS[mode] || EXECUTION_MODE_WARNINGS[EXECUTION.REVIEW];
        };

        selection?.addEventListener('change', () => this.updateEditorWorkerPreview());
        form?.querySelector('[data-job-action="add-worker-from-editor"]')?.addEventListener('click', () => this.createWorkerFromEditor());
        form?.querySelector('[data-job-action="edit-selected-environment"]')?.addEventListener('click', () => this.editSelectedWorker());
        form?.querySelectorAll('[data-job-action="cancel-editor"]')?.forEach(button => button.addEventListener('click', () => this.closeEditor()));
        form?.querySelector('#job-trigger-schedule')?.addEventListener('change', updateScheduleFields);
        form?.querySelector('#job-schedule-kind')?.addEventListener('change', updateScheduleFields);
        form?.querySelector('#job-mode')?.addEventListener('change', updateModeWarning);
        form?.addEventListener('submit', event => this.saveJob(event, job));
        updateScheduleFields();
        updateModeWarning();
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
        if (form.querySelector('#job-trigger-vca').checked) triggers.push({ kind: TRIGGER.VCA });
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
            executionMode: Number(form.querySelector('#job-mode').value),
            timeoutMinutes: Number(form.querySelector('#job-timeout').value),
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
            executionMode: job.executionMode, timeoutMinutes: job.timeoutMinutes,
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

    async repairWorker(button) {
        return this.withBusy(button, 'Repairing…', async () => {
            await this.app.apiCall('/api/v1/jobs/worker/repair', 'POST');
            this.workerStatus = await this.app.apiCall('/api/v1/jobs/worker', 'GET', null, { showLoading: false });
            this.renderWorker();
            this.app.showToast('Jobs worker', 'Worker registration repaired.', 'success');
        });
    }

    async disableWorker() {
        if (!window.confirm('Disable the always-on Jobs worker? Enabled jobs stay saved but will not run until you repair it.')) return;
        try {
            await this.app.apiCall('/api/v1/jobs/worker', 'DELETE');
            this.workerStatus = await this.app.apiCall('/api/v1/jobs/worker', 'GET', null, { showLoading: false });
            this.renderWorker();
        } catch (error) { this.app.showError(error?.message || 'Could not disable the worker.'); }
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
        const generation = ++this.runModalGeneration;
        if (this.logTimer) window.clearInterval(this.logTimer);
        this.logTimer = null;
        this.app.showModal('Job run', `
            <div class="job-run-detail" data-job-run-detail>
                <div class="job-run-detail-summary" data-run-summary>Loading run…</div>
                <pre class="job-run-log" data-run-log aria-live="polite"></pre>
                <div class="d-flex justify-content-end gap-2 mt-3" data-run-modal-actions></div>
            </div>`);
        let cursor = 0;
        // In-flight guard: a scheduled tick must not start while the previous refresh is still
        // awaiting — both would read the same `cursor` before either advances it and append the same
        // log slice twice whenever a round trip exceeds the 1.5s interval.
        let refreshing = false;
        const refresh = async () => {
            if (refreshing || generation !== this.runModalGeneration) return;
            const detail = document.querySelector('[data-job-run-detail]');
            if (!detail) {
                if (generation === this.runModalGeneration && this.logTimer) {
                    window.clearInterval(this.logTimer);
                    this.logTimer = null;
                }
                return;
            }
            refreshing = true;
            try {
                const [run, logs] = await Promise.all([
                    this.app.apiCall(`/api/v1/jobs/runs/${encodeURIComponent(runId)}`, 'GET', null, { showLoading: false }),
                    this.app.apiCall(`/api/v1/jobs/runs/${encodeURIComponent(runId)}/logs?afterSequence=${cursor}&limit=2000`, 'GET', null, { showLoading: false })
                ]);
                if (generation !== this.runModalGeneration) return;
                const status = RUN_STATUS[Number(run.status)] || { label: 'Unknown', tone: 'neutral' };
                detail.querySelector('[data-run-summary]').innerHTML = `<div><strong>${this.escape(run.jobName)}</strong><span class="job-run-status" data-tone="${status.tone}">${status.label}</span></div><small>${this.escape(run.workspacePath || run.projectPath)}</small>${run.errorMessage ? `<p class="text-danger mb-0 mt-2">${this.escape(run.errorMessage)}</p>` : ''}${run.resultText ? `<details class="mt-2"><summary>Final response</summary><pre class="job-run-result"></pre></details>` : ''}`;
                const resultTarget = detail.querySelector('.job-run-result');
                if (resultTarget) resultTarget.textContent = run.resultText;
                const logTarget = detail.querySelector('[data-run-log]');
                (logs?.logs || []).forEach(entry => { logTarget.textContent += entry.content; cursor = Math.max(cursor, Number(entry.sequence)); });
                if ((logs?.logs || []).length > 0) logTarget.scrollTop = logTarget.scrollHeight;
                const active = Number(run.status) <= 1;
                const actions = detail.querySelector('[data-run-modal-actions]');
                actions.innerHTML = `${active ? '<button class="btn btn-outline-danger" type="button" data-modal-cancel>Cancel run</button>' : '<button class="btn btn-outline-secondary" type="button" data-modal-retry>Retry</button>'}<button class="btn btn-primary" type="button" data-action="close-modal">Close</button>`;
                actions.querySelector('[data-action="close-modal"]')?.addEventListener('click', () => this.app.closeModal());
                actions.querySelector('[data-modal-cancel]')?.addEventListener('click', async event => { await this.cancelRun(runId, event.currentTarget); await refresh(); });
                actions.querySelector('[data-modal-retry]')?.addEventListener('click', event => this.retryRun(runId, event.currentTarget));
                if (!active && this.logTimer) { window.clearInterval(this.logTimer); this.logTimer = null; }
            } catch (error) {
                if (generation === this.runModalGeneration) {
                    detail.querySelector('[data-run-summary]').textContent = error?.message || 'Could not load this run.';
                }
            } finally {
                refreshing = false;
            }
        };
        await refresh();
        if (generation === this.runModalGeneration && !this.logTimer) {
            this.logTimer = window.setInterval(refresh, 1500);
        }
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
            const jobs = (response?.jobs || []).filter(job => (job.triggers || []).some(trigger => [TRIGGER.VCA, TRIGGER.COMMIT].includes(Number(trigger.kind))));
            host.innerHTML = jobs.length === 0
                ? `<div class="jobs-automation-empty"><strong>No Git-triggered automation yet</strong><span>Run a Worker after a real pre-commit VCA check or after a successful commit.</span></div>`
                : `<div class="jobs-automation-list">${jobs.map(job => `<article><div><strong>${this.escape(job.name)}</strong><small>${this.escape(job.environmentName || getLlmName(Number(job.llm)))} · ${(job.triggers || []).filter(trigger => [1, 2].includes(Number(trigger.kind))).map(trigger => Number(trigger.kind) === 1 ? 'Pre-commit VCA (non-blocking Job)' : 'After successful commit').join(' + ')}</small></div><span class="job-state" data-tone="${job.enabled ? 'success' : 'neutral'}">${job.enabled ? 'On' : 'Off'}</span><button class="btn btn-sm btn-outline-secondary" type="button" data-rules-job-run="${this.escape(job.id)}">Run now</button></article>`).join('')}</div>`;
            host.querySelectorAll('[data-rules-job-run]').forEach(button => button.addEventListener('click', () => this.runNow(Number(button.dataset.rulesJobRun), button)));
        } catch (error) {
            host.innerHTML = `<div class="jobs-automation-empty text-danger">${this.escape(error?.message || 'Could not load project Jobs.')}</div>`;
        }

        root.querySelector('[data-action="open-jobs"]')?.addEventListener('click', () => this.app.navigate('jobs', {}, { resetStack: true }));
        root.querySelector('[data-action="new-vca-job"]')?.addEventListener('click', () => this.app.navigate('jobs', { newJob: true, triggerKind: TRIGGER.VCA }, { resetStack: true }));
        root.querySelector('[data-action="new-commit-job"]')?.addEventListener('click', () => this.app.navigate('jobs', { newJob: true, triggerKind: TRIGGER.COMMIT }, { resetStack: true }));
    }

    formatTrigger(trigger) {
        const kind = Number(trigger.kind);
        if (kind === TRIGGER.VCA) return 'Pre-commit VCA · non-blocking Job';
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

    escape(value) { return this.app.escapeHtml(String(value ?? '')); }
}
