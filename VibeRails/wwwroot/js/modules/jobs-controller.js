const LLM = Object.freeze({ CODEX: 1, CLAUDE: 2 });
const EXECUTION = Object.freeze({ REVIEW: 0, ISOLATED: 1, LIVE: 2 });
const TRIGGER = Object.freeze({ SCHEDULE: 0, VCA: 1, COMMIT: 2, MANUAL: 3 });
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
        if (this.pollTimer) window.clearInterval(this.pollTimer);
        if (this.logTimer) window.clearInterval(this.logTimer);
        this.pollTimer = null;
        this.logTimer = null;
        this.root = null;
    }

    renderPage() {
        return `
            <div class="view jobs-view" data-view="jobs">
                <header class="jobs-page-header">
                    <div>
                        <span class="jobs-eyebrow">Durable local automation</span>
                        <h1>Jobs</h1>
                        <p>Run Codex or Claude on a schedule, after VCA checks, after successful commits, or whenever you click Run now.</p>
                    </div>
                    <button class="btn btn-primary" type="button" data-job-action="new">
                        <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>New job
                    </button>
                </header>

                <section class="jobs-worker-card" data-jobs-worker aria-live="polite">
                    <span class="spinner-border spinner-border-sm" aria-hidden="true"></span>
                    <span>Checking the always-on worker…</span>
                </section>

                <section class="jobs-section" aria-labelledby="jobs-list-title">
                    <div class="jobs-section-heading">
                        <div><span class="jobs-eyebrow">Definitions</span><h2 id="jobs-list-title">Your jobs</h2></div>
                        <span class="jobs-count" data-jobs-count>0 jobs</span>
                    </div>
                    <div class="jobs-grid" data-jobs-list>
                        <div class="jobs-empty"><span class="spinner-border spinner-border-sm"></span> Loading jobs…</div>
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
            const [jobsResponse, runsResponse, workerResponse, environmentsResponse] = await Promise.all([
                this.app.apiCall('/api/v1/jobs', 'GET', null, { showLoading: !quiet }),
                this.app.apiCall('/api/v1/jobs/runs?limit=100', 'GET', null, { showLoading: false }),
                this.app.apiCall('/api/v1/jobs/worker', 'GET', null, { showLoading: false }),
                this.app.apiCall('/api/v1/environments', 'GET', null, { showLoading: false })
            ]);
            this.jobs = jobsResponse?.jobs || [];
            this.runs = runsResponse?.runs || [];
            this.workerStatus = workerResponse || null;
            this.environments = environmentsResponse?.environments || [];
            this.app.data.environments = this.environments;
            this.renderJobs();
            this.renderRuns();
            this.renderWorker();
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

    renderWorker() {
        const target = this.root?.querySelector('[data-jobs-worker]');
        if (!target) return;
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
        if (count) count.textContent = `${this.jobs.length} ${this.jobs.length === 1 ? 'job' : 'jobs'}`;
        if (!target) return;
        if (this.jobs.length === 0) {
            target.innerHTML = `
                <div class="jobs-empty jobs-empty-large">
                    <i class="fa-regular fa-clock" aria-hidden="true"></i>
                    <strong>No jobs yet</strong>
                    <span>Create one scheduled job or attach an LLM review to VCA and commit events.</span>
                    <button class="btn btn-primary" type="button" data-job-action="new">Create your first job</button>
                </div>`;
            return;
        }

        target.innerHTML = this.jobs.map(job => {
            const triggers = (job.triggers || []).map(trigger => `<span class="jobs-trigger-chip">${this.escape(this.formatTrigger(trigger))}</span>`).join('');
            const mode = ['Review only', 'Isolated write', 'Live repository write'][Number(job.executionMode)] || 'Unknown mode';
            const llm = Number(job.llm) === LLM.CODEX ? 'Codex' : 'Claude';
            return `
                <article class="job-card" data-enabled="${job.enabled === true}">
                    <div class="job-card-topline">
                        <div class="job-card-title">
                            <span class="job-provider" data-provider="${llm.toLowerCase()}">${llm}</span>
                            <h3>${this.escape(job.name)}</h3>
                        </div>
                        <span class="job-state" data-tone="${job.enabled ? 'success' : 'neutral'}">${job.enabled ? 'Enabled' : 'Disabled'}</span>
                    </div>
                    <p class="job-prompt">${this.escape(job.prompt)}</p>
                    <div class="job-meta"><span><i class="fa-solid fa-shield-halved"></i>${this.escape(mode)}</span><span><i class="fa-regular fa-hourglass-half"></i>${job.timeoutMinutes} min</span></div>
                    <div class="job-path" title="${this.escape(job.projectPath)}"><i class="fa-solid fa-code-branch"></i>${this.escape(job.projectPath)}</div>
                    <div class="job-triggers">${triggers || '<span class="jobs-trigger-chip">Manual only</span>'}</div>
                    <div class="job-card-actions">
                        <button class="btn btn-sm btn-primary" type="button" data-job-action="run" data-job-id="${job.id}"><i class="fa-solid fa-play me-1"></i>Run now</button>
                        <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="edit" data-job-id="${job.id}">Edit</button>
                        <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="toggle" data-job-id="${job.id}">${job.enabled ? 'Disable' : 'Enable'}</button>
                        <button class="btn btn-sm btn-outline-danger ms-auto" type="button" data-job-action="delete" data-job-id="${job.id}" aria-label="Delete ${this.escape(job.name)}"><i class="fa-solid fa-trash"></i></button>
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
        const trigger = ['Schedule', 'VCA', 'Commit', 'Manual'][Number(run.triggerKind)] || 'Unknown';
        const active = Number(run.status) === 0 || Number(run.status) === 1;
        const retryable = Number(run.status) >= 2;
        return `<tr>
            <td><button class="job-run-link" type="button" data-job-action="view-run" data-run-id="${this.escape(run.id)}">${this.escape(run.jobName)}</button><small>${Number(run.llm) === 1 ? 'Codex' : 'Claude'}</small></td>
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

    openEditor(job = null, preferredTrigger = null) {
        const isEdit = Boolean(job);
        const triggers = job?.triggers || [];
        const scheduled = triggers.find(trigger => Number(trigger.kind) === TRIGGER.SCHEDULE);
        const hasVca = triggers.some(trigger => Number(trigger.kind) === TRIGGER.VCA) || preferredTrigger === TRIGGER.VCA;
        const hasCommit = triggers.some(trigger => Number(trigger.kind) === TRIGGER.COMMIT) || preferredTrigger === TRIGGER.COMMIT;
        const timezone = scheduled?.timeZoneId || Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
        const projectPath = job?.projectPath || this.app.data.configs?.rootPath || this.app.data.configs?.launchDirectory || '';
        const scheduleKind = Number(scheduled?.scheduleKind ?? SCHEDULE.INTERVAL);
        const llm = Number(job?.llm ?? LLM.CODEX);

        this.app.showModal(isEdit ? `Edit ${job.name}` : 'Create a Job', `
            <form data-job-form>
                <div class="row g-3">
                    <div class="col-md-7"><label class="form-label" for="job-name">Name</label><input class="form-control" id="job-name" maxlength="100" required value="${this.escape(job?.name || '')}" placeholder="Security review"></div>
                    <div class="col-md-5"><label class="form-label" for="job-llm">LLM</label><select class="form-select" id="job-llm"><option value="1" ${llm === 1 ? 'selected' : ''}>Codex</option><option value="2" ${llm === 2 ? 'selected' : ''}>Claude</option></select></div>
                    <div class="col-12"><label class="form-label" for="job-project">Git repository root</label><input class="form-control font-monospace" id="job-project" required value="${this.escape(projectPath)}"></div>
                    <div class="col-md-7"><label class="form-label" for="job-environment">Environment</label><select class="form-select" id="job-environment"></select><small class="form-text text-muted">Uses that environment's saved authentication and CLI settings.</small></div>
                    <div class="col-md-5"><label class="form-label" for="job-timeout">Timeout</label><div class="input-group"><input class="form-control" type="number" id="job-timeout" min="1" max="120" required value="${job?.timeoutMinutes || 30}"><span class="input-group-text">minutes</span></div></div>
                    <div class="col-12"><label class="form-label" for="job-prompt">Initial message</label><textarea class="form-control" id="job-prompt" rows="5" maxlength="50000" required placeholder="Please review the changed code for security issues.">${this.escape(job?.prompt || '')}</textarea></div>
                    <div class="col-12"><label class="form-label" for="job-mode">Execution mode</label><select class="form-select" id="job-mode"><option value="0" ${Number(job?.executionMode ?? 0) === 0 ? 'selected' : ''}>Review only — cannot edit files</option><option value="1" ${Number(job?.executionMode) === 1 ? 'selected' : ''}>Isolated write — edits a private clone</option><option value="2" ${Number(job?.executionMode) === 2 ? 'selected' : ''}>Live repository write — edits this working tree</option></select><div class="job-live-warning" data-live-warning hidden><i class="fa-solid fa-triangle-exclamation"></i><span>This agent can modify your live working tree. Use it only when that is intentional.</span></div></div>
                </div>

                <fieldset class="job-trigger-fieldset mt-4"><legend>Triggers</legend>
                    <label class="job-trigger-option"><input class="form-check-input" type="checkbox" id="job-trigger-schedule" ${scheduled ? 'checked' : ''}><span><strong>On a timer</strong><small>Intervals or a local daily/weekly time.</small></span></label>
                    <div class="job-schedule-editor" data-schedule-editor ${scheduled ? '' : 'hidden'}>
                        <div class="row g-2">
                            <div class="col-md-4"><label class="form-label" for="job-schedule-kind">Schedule</label><select class="form-select" id="job-schedule-kind"><option value="0" ${scheduleKind === 0 ? 'selected' : ''}>Every interval</option><option value="1" ${scheduleKind === 1 ? 'selected' : ''}>Daily</option><option value="2" ${scheduleKind === 2 ? 'selected' : ''}>Weekly</option></select></div>
                            <div class="col-md-4" data-interval-field><label class="form-label" for="job-interval">Every</label><div class="input-group"><input class="form-control" type="number" id="job-interval" min="5" max="43200" value="${scheduled?.intervalMinutes || 60}"><span class="input-group-text">min</span></div></div>
                            <div class="col-md-4" data-clock-field><label class="form-label" for="job-local-time">Local time</label><input class="form-control" type="time" id="job-local-time" value="${this.escape(scheduled?.localTime || '09:00')}"></div>
                            <div class="col-12" data-weekdays-field><label class="form-label">Weekdays</label><div class="job-weekdays">${WEEKDAYS.map((day, index) => `<label><input class="form-check-input" type="checkbox" data-weekday="${index}" ${(Number(scheduled?.daysOfWeekMask || 0) & (1 << index)) !== 0 ? 'checked' : ''}><span>${day}</span></label>`).join('')}</div></div>
                            <div class="col-12" data-timezone-field><label class="form-label" for="job-timezone">Time zone</label><input class="form-control" id="job-timezone" value="${this.escape(timezone)}" required></div>
                        </div>
                    </div>
                    <label class="job-trigger-option"><input class="form-check-input" type="checkbox" id="job-trigger-vca" ${hasVca ? 'checked' : ''}><span><strong>After every real VCA check</strong><small>Queues whether VCA passes or blocks; Rules previews never trigger it.</small></span></label>
                    <label class="job-trigger-option"><input class="form-check-input" type="checkbox" id="job-trigger-commit" ${hasCommit ? 'checked' : ''}><span><strong>After every successful commit</strong><small>Runs from the native post-commit hook.</small></span></label>
                </fieldset>
                <div class="form-check form-switch mt-3"><input class="form-check-input" type="checkbox" id="job-enabled" ${job?.enabled !== false ? 'checked' : ''}><label class="form-check-label" for="job-enabled">Enabled</label></div>
                <div class="d-flex justify-content-end gap-2 mt-4"><button class="btn btn-outline-secondary" type="button" data-action="close-modal">Cancel</button><button class="btn btn-primary" type="submit">${isEdit ? 'Save changes' : 'Create job'}</button></div>
            </form>`);

        const modal = document.getElementById('modal-container');
        const form = modal?.querySelector('[data-job-form]');
        const llmSelect = modal?.querySelector('#job-llm');
        const environmentSelect = modal?.querySelector('#job-environment');
        const populateEnvironments = () => {
            const selectedLlm = Number(llmSelect?.value || llm);
            const options = this.environments.filter(environment => String(environment.cli || '').toLowerCase() === (selectedLlm === 1 ? 'codex' : 'claude'));
            environmentSelect.innerHTML = `<option value="">Base ${selectedLlm === 1 ? 'Codex' : 'Claude'} configuration</option>${options.map(environment => `<option value="${environment.id}" ${Number(job?.environmentId) === Number(environment.id) ? 'selected' : ''}>${this.escape(environment.name)}</option>`).join('')}`;
        };
        const updateScheduleFields = () => {
            const enabled = modal.querySelector('#job-trigger-schedule').checked;
            const kind = Number(modal.querySelector('#job-schedule-kind').value);
            modal.querySelector('[data-schedule-editor]').hidden = !enabled;
            modal.querySelector('[data-interval-field]').hidden = kind !== SCHEDULE.INTERVAL;
            modal.querySelector('[data-clock-field]').hidden = kind === SCHEDULE.INTERVAL;
            modal.querySelector('[data-timezone-field]').hidden = kind === SCHEDULE.INTERVAL;
            modal.querySelector('[data-weekdays-field]').hidden = kind !== SCHEDULE.WEEKLY;
        };
        const updateModeWarning = () => modal.querySelector('[data-live-warning]').hidden = Number(modal.querySelector('#job-mode').value) !== EXECUTION.LIVE;
        populateEnvironments();
        updateScheduleFields();
        updateModeWarning();
        llmSelect?.addEventListener('change', populateEnvironments);
        modal?.querySelector('#job-trigger-schedule')?.addEventListener('change', updateScheduleFields);
        modal?.querySelector('#job-schedule-kind')?.addEventListener('change', updateScheduleFields);
        modal?.querySelector('#job-mode')?.addEventListener('change', updateModeWarning);
        form?.addEventListener('submit', event => this.saveJob(event, job));
    }

    async saveJob(event, existingJob) {
        event.preventDefault();
        const form = event.currentTarget;
        const submit = form.querySelector('[type="submit"]');
        const triggers = [];
        if (form.querySelector('#job-trigger-schedule').checked) {
            const scheduleKind = Number(form.querySelector('#job-schedule-kind').value);
            let mask = 0;
            form.querySelectorAll('[data-weekday]:checked').forEach(input => { mask |= 1 << Number(input.dataset.weekday); });
            if (scheduleKind === SCHEDULE.WEEKLY && mask === 0) {
                this.app.showError('Choose at least one weekday.');
                return;
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

        const payload = {
            name: form.querySelector('#job-name').value.trim(),
            projectPath: form.querySelector('#job-project').value.trim(),
            llm: Number(form.querySelector('#job-llm').value),
            environmentId: form.querySelector('#job-environment').value ? Number(form.querySelector('#job-environment').value) : null,
            prompt: form.querySelector('#job-prompt').value.trim(),
            executionMode: Number(form.querySelector('#job-mode').value),
            timeoutMinutes: Number(form.querySelector('#job-timeout').value),
            enabled: form.querySelector('#job-enabled').checked,
            triggers
        };

        submit.disabled = true;
        submit.textContent = existingJob ? 'Saving…' : 'Creating…';
        try {
            await this.app.apiCall(existingJob ? `/api/v1/jobs/${existingJob.id}` : '/api/v1/jobs', existingJob ? 'PUT' : 'POST', payload);
            this.app.closeModal();
            this.app.showToast('Jobs', existingJob ? 'Job updated.' : 'Job created.', 'success');
            await this.refreshAll({ quiet: true });
        } catch (error) {
            this.app.showError(error?.message || 'Could not save the job.');
            submit.disabled = false;
            submit.textContent = existingJob ? 'Save changes' : 'Create job';
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
        const payload = {
            name: job.name, projectPath: job.projectPath, llm: job.llm,
            environmentId: job.environmentId, prompt: job.prompt,
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
        if (this.logTimer) window.clearInterval(this.logTimer);
        this.app.showModal('Job run', `
            <div class="job-run-detail" data-job-run-detail>
                <div class="job-run-detail-summary" data-run-summary>Loading run…</div>
                <pre class="job-run-log" data-run-log aria-live="polite"></pre>
                <div class="d-flex justify-content-end gap-2 mt-3" data-run-modal-actions></div>
            </div>`);
        let cursor = 0;
        const refresh = async () => {
            const detail = document.querySelector('[data-job-run-detail]');
            if (!detail) {
                if (this.logTimer) window.clearInterval(this.logTimer);
                this.logTimer = null;
                return;
            }
            try {
                const [run, logs] = await Promise.all([
                    this.app.apiCall(`/api/v1/jobs/runs/${encodeURIComponent(runId)}`, 'GET', null, { showLoading: false }),
                    this.app.apiCall(`/api/v1/jobs/runs/${encodeURIComponent(runId)}/logs?afterSequence=${cursor}&limit=2000`, 'GET', null, { showLoading: false })
                ]);
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
                detail.querySelector('[data-run-summary]').textContent = error?.message || 'Could not load this run.';
            }
        };
        await refresh();
        if (!this.logTimer) this.logTimer = window.setInterval(refresh, 1500);
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
                ? `<div class="jobs-automation-empty"><strong>No Git-triggered Jobs yet</strong><span>Attach Codex or Claude to every real VCA check or successful commit.</span></div>`
                : `<div class="jobs-automation-list">${jobs.map(job => `<article><div><strong>${this.escape(job.name)}</strong><small>${Number(job.llm) === 1 ? 'Codex' : 'Claude'} · ${(job.triggers || []).filter(trigger => [1, 2].includes(Number(trigger.kind))).map(trigger => Number(trigger.kind) === 1 ? 'VCA' : 'Commit').join(' + ')}</small></div><span class="job-state" data-tone="${job.enabled ? 'success' : 'neutral'}">${job.enabled ? 'On' : 'Off'}</span><button class="btn btn-sm btn-outline-secondary" type="button" data-rules-job-run="${job.id}">Run now</button></article>`).join('')}</div>`;
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
        if (kind === TRIGGER.VCA) return 'Every VCA check';
        if (kind === TRIGGER.COMMIT) return 'Every commit';
        if (kind !== TRIGGER.SCHEDULE) return 'Manual';
        const scheduleKind = Number(trigger.scheduleKind);
        if (scheduleKind === SCHEDULE.INTERVAL) return `Every ${trigger.intervalMinutes} min`;
        if (scheduleKind === SCHEDULE.DAILY) return `Daily ${trigger.localTime}`;
        const days = WEEKDAYS.filter((_, index) => (Number(trigger.daysOfWeekMask) & (1 << index)) !== 0).join(', ');
        return `${days} ${trigger.localTime}`;
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
