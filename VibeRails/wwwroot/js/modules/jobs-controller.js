import { automationWorkerName } from './environment-editor.js';
import { isCheck, checkName, checkFields, validCheckScope } from './job-checks.js';
import {
    confirmDialog,
    getLlmName,
    parseLlmSelection
} from './utils.js';
import { showReplayModal } from './session-viewer.js';
import { PythonScriptsController } from './python-scripts-controller.js';
import { agentDisplayName, classifyCatalog, jobLaunchKind, workerActionOf } from './launch-kind.js';
import { workerModelSummary } from './llm-display.js';

const LLM = Object.freeze({
    CODEX: 1,
    CLAUDE: 2,
    ANTIGRAVITY: 3,
    COPILOT: 4,
    OPENCODE: 6,
    GLM_52: 7,
    GROK_46: 8,
    GLM_53: 9,
    DEEPSEEK_V4_PRO: 10,
    KIMI_K3: 11
});

const LLM_BY_CLI = Object.freeze({
    codex: LLM.CODEX,
    claude: LLM.CLAUDE,
    antigravity: LLM.ANTIGRAVITY,
    copilot: LLM.COPILOT,
    opencode: LLM.OPENCODE,
    'glm-5.2': LLM.GLM_52,
    'glm-5.3': LLM.GLM_53,
    'deepseek-v4-pro': LLM.DEEPSEEK_V4_PRO,
    'kimi-k3': LLM.KIMI_K3,
    // grok-4.6 is the retired wire name. grok must stay last so CLI_BY_LLM emits it.
    'grok-4.6': LLM.GROK_46,
    grok: LLM.GROK_46
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
const TRIGGER = Object.freeze({ SCHEDULE: 0, COMMIT: 2, MANUAL: 3, PRECOMMIT: 4, BOARD_LANE: 5 });

// The two pages this controller serves. Their copy differs; their machinery does not.
const PAGES = Object.freeze({
    agents: {
        viewId: 'ai-agents',
        noun: 'agent',
        title: 'Agents',
        description: 'Saved CLI setups with a starting message. Run one now, on a schedule, around commits or from a Board lane.',
        newLabel: 'New agent',
        listTitle: 'Your agents',
        listHint: 'Run, edit, pause or remove an agent from one place.',
        emptyTitle: 'No agents yet',
        emptyText: 'Save a CLI with the message it should start with, then run it whenever you like.',
        emptyAction: 'Create your first agent',
        disabledTitle: 'Disabled agents',
        historyHint: 'Latest result for each agent'
    },
    automations: {
        viewId: 'automations',
        noun: 'automation',
        // Scripts share this page's one list (VIBE-159); the Jobs in it are its workflows.
        countNoun: 'workflow',
        title: 'Automations',
        description: 'Scripts you write and sign, and workflows that run them with checks, without an LLM — on demand, on a schedule, around commits or from a Board lane.',
        newLabel: 'New automation',
        listTitle: 'Your automations',
        listHint: 'Run, edit, sign, pause or remove a script or workflow from one place.',
        emptyTitle: 'No automations yet',
        emptyText: 'Run a repository script, VCA or a Code quality check on a timer, around commits or on demand.',
        emptyAction: 'Create your first automation',
        disabledTitle: 'Disabled automations',
        historyHint: 'Latest result for each automation'
    }
});
const SCHEDULE = Object.freeze({ INTERVAL: 0, DAILY: 1, WEEKLY: 2 });
const JOB_ACTION = Object.freeze({ WORKER: 0, SCRIPT: 1, QUALITY: 2, VCA: 3 });
const SCRIPT_RUNTIME = Object.freeze({ PYTHON: 0, POWERSHELL: 1, BASH: 2 });
const SCRIPT_RUNTIME_META = Object.freeze({
    [SCRIPT_RUNTIME.PYTHON]: { label: 'Python', extension: '.py', icon: 'fa-brands fa-python' },
    [SCRIPT_RUNTIME.POWERSHELL]: { label: 'PowerShell', extension: '.ps1', icon: 'fa-solid fa-terminal' },
    [SCRIPT_RUNTIME.BASH]: { label: 'Bash', extension: '.sh', icon: 'fa-solid fa-terminal' }
});

const RUN_ACTION_STATUS = Object.freeze({
    0: { label: 'Pending', tone: 'neutral' },
    1: { label: 'Running', tone: 'info' },
    2: { label: 'Succeeded', tone: 'success' },
    3: { label: 'Failed', tone: 'danger' },
    4: { label: 'Skipped', tone: 'neutral' },
    5: { label: 'Cancelled', tone: 'neutral' },
    6: { label: 'Timed out', tone: 'warning' }
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
        // window.confirm is a silent no-op inside the VS Code webview, so destructive
        // actions confirm through this in-app dialog instead. Held as a field so tests
        // can substitute a resolved value.
        this.confirm = confirmDialog;
        this.pythonScripts = new PythonScriptsController(app);
        this.root = null;
        this.mode = 'agents';
        this.jobs = [];
        this.jobMutationGeneration = 0;
        // What the pages show: Jobs and saved setups split into Agents, Automations and
        // Quick Launches (launch-kind.js). Rebuilt whenever either list is refreshed.
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
        this.editorRoot = null;
        this.editorRequestGeneration = 0;
        this.editorActions = [];
        this.editorWorkerOptional = false;
        this.workerEditor = null;
        this.activeEditorJob = null;
        this.activeEditorSource = null;
        this.activeEditorPreferredTrigger = null;
        this._lastJobsListHtml = null;
        this._lastRunsHtml = null;
    }

    get page() { return PAGES[this.mode]; }

    /** 'Agent' or 'Automation' for toasts about one Job, by what it runs; the page's noun otherwise. */
    kindTitle(job = null) {
        const kind = job ? jobLaunchKind(job) : this.page.noun;
        return kind === 'agent' ? 'Agent' : 'Automation';
    }

    async loadView(data = {}) {
        this.unload();
        this.mode = data?.mode === 'automations' ? 'automations' : 'agents';
        const content = document.getElementById('app-content');
        if (!content) return;
        content.innerHTML = this.renderPage();
        this.root = content.querySelector(`[data-view="${this.page.viewId}"]`);
        this.bindPageActions();
        // The list now holds the loading placeholder; a stale render cache from a previous
        // visit would make the first renderJobs() skip and leave the spinner forever.
        this._lastJobsListHtml = null;
        this._lastRunsHtml = null;
        if (this.mode === 'automations') {
            // Scripts and workflows are one list: the Scripts controller keeps its actions on the
            // page root and hands its rows to renderJobs instead of drawing a section of its own.
            void this.pythonScripts.mount(this.root, { onListChanged: () => this.renderJobs() });
        }
        const root = this.root;
        await this.refreshAll();
        if (this.root !== root) return;

        if (data?.newJob) {
            this.openEditor(null, Number(data.triggerKind));
        }
        if (data?.editJobId) {
            const job = this.jobs.find(item => Number(item.id) === Number(data.editJobId));
            if (!job) {
                this.app.showError(`This ${this.page.noun} is no longer available.`);
            } else if (jobLaunchKind(job) !== (this.mode === 'agents' ? 'agent' : 'automation')) {
                // A Board lane or the play button sent the id here under the historical 'jobs'
                // route; the entry lives on the other page.
                this.app.navigate(this.mode === 'agents' ? 'automations' : 'ai-agents', data);
                return;
            } else {
                this.openEditor(job);
            }
        }
        if (data?.editEnvironmentId) {
            // An Agent without an Automation behind it edits its saved setup directly.
            const environment = this.findEnvironment(data.editEnvironmentId);
            if (environment) void this.editStandaloneAgent(environment);
        }
        // The nav launcher's customize modal hands off here: it cannot host the picker itself
        // (it is a nested layer over #modal-container), so it navigates and asks us to open it.
        if (data?.importFromRepository) {
            void this.openImportFromRepository();
        }
        this.pollTimer = window.setInterval(() => void this.pollTick(), 5000);
    }

    /** One 5 s poll. Returns the round it started, or null when it skipped. */
    pollTick() {
        if (this.app.currentView !== this.page.viewId && this.app.currentView !== 'jobs') return null;
        // Like the Board's poll: nothing while the page is hidden, and a slow server never
        // has a second round queued behind the first.
        if (document.hidden || this.pollPending) return null;
        return this.pollRunsAndJobs();
    }

    async pollRunsAndJobs() {
        this.pollPending = true;
        try {
            // Jobs too, not just run history: the scheduler advances nextRunUtc server-side,
            // so a "Next run" rendered once at load drifts into "Due now" and stays there.
            await Promise.allSettled([this.refreshRuns({ quiet: true }), this.refreshJobs({ quiet: true })]);
        } finally {
            this.pollPending = false;
        }
    }

    unload() {
        this.closeEditor();
        this.pythonScripts.unmount();
        this.runModalGeneration += 1;
        this.historyRequestGeneration += 1;
        this.activeEditorJob = null;
        this.activeEditorSource = null;
        this.activeEditorPreferredTrigger = null;
        if (this.pollTimer) window.clearInterval(this.pollTimer);
        if (this.logTimer) window.clearInterval(this.logTimer);
        this.pollTimer = null;
        this.logTimer = null;
        this.root = null;
    }

    renderPage() {
        const page = this.page;
        const plural = `${page.noun}s`;
        const automations = this.mode === 'automations';
        return `
            <div class="view jobs-view" data-view="${page.viewId}" data-mode="${this.mode}">
                <header class="jobs-page-header">
                    <div>
                        <h1 class="jobs-page-title">${page.title}</h1>
                        <p>${page.description}</p>
                    </div>
                    ${automations ? this.renderAutomationsHeaderActions() : `<div class="jobs-page-actions" role="group" aria-label="Add an ${page.noun}">
                        <button class="btn btn-outline-secondary" type="button" data-job-action="import-from-repository"
                                title="Copy an ${page.noun} from another repository on this machine">
                            <i class="fa-solid fa-code-branch me-1" aria-hidden="true"></i>Import from repository
                        </button>
                        <button class="btn btn-outline-secondary" type="button" data-job-action="import-recipe"
                                title="Import a .recipe.md file exported from VibeRails">
                            <i class="fa-solid fa-file-import me-1" aria-hidden="true"></i>Import recipe
                        </button>
                        <button class="btn btn-primary" type="button" data-job-action="new">
                            <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>${page.newLabel}
                        </button>
                    </div>`}
                </header>

                <section class="jobs-section jobs-panel${automations ? ' automations-panel' : ''}" aria-labelledby="jobs-list-title">
                    <div class="jobs-section-heading">
                        <div>
                            <div class="jobs-section-title-row">
                                <h2 id="jobs-list-title">${page.listTitle}</h2>
                                ${automations
                                    ? '<span class="jobs-count"><span data-jobs-count>0 workflows</span> · <span data-python-scripts-count>0 scripts</span></span>'
                                    : `<span class="jobs-count" data-jobs-count>0 ${plural}</span>`}
                            </div>
                            <p>${page.listHint}</p>
                        </div>
                        ${automations ? this.renderAutomationsListToolbar() : ''}
                    </div>
                    ${automations ? `<div class="jobs-grid automations-list" data-jobs-list data-automations-list data-filter="all" data-python-scripts-drop>
                        <div class="jobs-empty" role="status"><span class="spinner-border spinner-border-sm"></span> Loading ${plural}…</div>
                    </div>
                    <p class="python-scripts-runtime-help">Scripts run with the interpreter their extension names:
                        <strong>pwsh</strong> (PowerShell 7) runs <code>.ps1</code>, <strong>bash</strong> runs <code>.sh</code>
                        (Git Bash on Windows) and <strong>python</strong> runs <code>.py</code>. Sign one with your PIN to run it.
                        New scripts go to <code data-python-scripts-dir></code> unless you choose a folder; drop a file
                        on the list to add it.</p>` : `<div class="jobs-grid" data-jobs-list>
                        <div class="jobs-empty" role="status"><span class="spinner-border spinner-border-sm"></span> Loading ${plural}…</div>
                    </div>`}
                </section>

                <details class="jobs-history-panel">
                    <summary>
                        <span class="jobs-history-summary-title">
                            <i class="fa-solid fa-clock-rotate-left" aria-hidden="true"></i>
                            <span><strong>Run history</strong><small>${page.historyHint}</small></span>
                        </span>
                        <i class="fa-solid fa-chevron-down jobs-history-chevron" aria-hidden="true"></i>
                    </summary>
                    <div class="jobs-history-body">
                        <div class="jobs-history-toolbar">
                            <span>Select an ${page.noun} to see all of its runs.</span>
                            <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="refresh">
                                <i class="fa-solid fa-rotate-right me-1" aria-hidden="true"></i>Refresh
                            </button>
                        </div>
                        <div class="jobs-runs" data-job-runs></div>
                    </div>
                </details>
            </div>`;
    }

    /**
     * One New and one Import for everything on the Automations page (VIBE-159). Script items carry
     * the Scripts controller's own actions, which it handles from the page root; workflow items
     * are this controller's. Both menus are native <details>, like a card's More menu.
     */
    renderAutomationsHeaderActions() {
        const fromDisk = this.pythonScripts._canImportFromHost()
            ? `<button type="button" role="menuitem" data-python-scripts-action="import" title="Register a .ps1, .sh or .py file where it already lives">
                    <i class="fa-solid fa-file-code" aria-hidden="true"></i>Script from disk…
                </button>`
            : '';
        return `<div class="jobs-page-actions" role="group" aria-label="Add an automation">
                <details class="job-more jobs-page-menu">
                    <summary class="btn btn-outline-secondary" aria-haspopup="menu">
                        <i class="fa-solid fa-file-import me-1" aria-hidden="true"></i>Import<i class="fa-solid fa-chevron-down ms-1" aria-hidden="true"></i>
                    </summary>
                    <div class="job-more-menu" role="menu" aria-label="Import">
                        <button type="button" role="menuitem" data-job-action="import-from-repository" title="Copy an automation from another repository on this machine">
                            <i class="fa-solid fa-code-branch" aria-hidden="true"></i>From repository…
                        </button>
                        <button type="button" role="menuitem" data-job-action="import-recipe" title="Import a .recipe.md file exported from VibeRails">
                            <i class="fa-solid fa-file-lines" aria-hidden="true"></i>Recipe file…
                        </button>
                        ${fromDisk}
                    </div>
                </details>
                <details class="job-more jobs-page-menu">
                    <summary class="btn btn-primary" aria-haspopup="menu" data-automations-new>
                        <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>New<i class="fa-solid fa-chevron-down ms-1" aria-hidden="true"></i>
                    </summary>
                    <div class="job-more-menu jobs-new-menu" role="menu" aria-label="New">
                        <button type="button" role="menuitem" data-python-scripts-action="new">
                            <i class="fa-solid fa-file-code" aria-hidden="true"></i>
                            <span><strong>Script</strong><small>A pwsh, bash or python file, written with an agent beside the editor. Sign it, then run it.</small></span>
                        </button>
                        <button type="button" role="menuitem" data-job-action="new">
                            <i class="fa-solid fa-list-check" aria-hidden="true"></i>
                            <span><strong>Workflow</strong><small>Scripts and checks (VCA, Code quality) in order: on demand, on a schedule, around commits or from a Board lane.</small></span>
                        </button>
                    </div>
                </details>
            </div>`;
    }

    renderAutomationsListToolbar() {
        const filter = (value, label) =>
            `<button class="btn btn-sm btn-outline-secondary" type="button" data-automations-filter="${value}" aria-pressed="${value === 'all'}">${label}</button>`;
        return `<div class="automations-toolbar">
                <div class="btn-group automations-filter" role="group" aria-label="Show">
                    ${filter('all', 'All')}${filter('workflows', 'Workflows')}${filter('scripts', 'Scripts')}
                </div>
                <button class="btn btn-sm btn-outline-secondary" type="button" data-python-scripts-action="pin"
                        title="The signing PIN approves scripts to run">
                    <i class="fa-solid fa-key me-1" aria-hidden="true"></i><span data-python-scripts-pin-label>Set PIN</span>
                </button>
                <button class="btn btn-sm btn-outline-secondary" type="button" data-python-scripts-action="refresh"
                        title="Refresh" aria-label="Refresh the script list">
                    <i class="fa-solid fa-rotate-right" aria-hidden="true"></i>
                </button>
            </div>`;
    }

    setAutomationsFilter(value) {
        const list = this.root?.querySelector('[data-automations-list]');
        if (!list || !['all', 'workflows', 'scripts'].includes(value)) return;
        list.dataset.filter = value;
        this.root.querySelectorAll('[data-automations-filter]').forEach(button =>
            button.setAttribute('aria-pressed', String(button.dataset.automationsFilter === value)));
    }

    closePageMenus(except = null) {
        this.root?.querySelectorAll('.jobs-page-menu[open]').forEach(menu => {
            if (menu !== except) menu.removeAttribute('open');
        });
    }

    bindPageActions() {
        // A pick, a click elsewhere or Escape closes the New / Import menus.
        this.root?.addEventListener('click', event => {
            const menu = event.target.closest?.('.jobs-page-menu');
            if (menu && event.target.closest('[role="menuitem"]')) menu.removeAttribute('open');
            this.closePageMenus(menu);
            const filter = event.target.closest?.('[data-automations-filter]');
            if (filter) this.setAutomationsFilter(filter.dataset.automationsFilter);
        });
        this.root?.addEventListener('keydown', event => {
            const menu = event.target.closest?.('.jobs-page-menu[open]');
            if (!menu) return;
            if (event.key === 'Escape') {
                event.preventDefault();
                menu.removeAttribute('open');
                menu.querySelector('summary')?.focus();
                return;
            }
            // Arrow keys move through the items, from the toggle too.
            if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
            const items = [...menu.querySelectorAll('[role="menuitem"]')];
            if (items.length === 0) return;
            event.preventDefault();
            const current = items.indexOf(event.target.closest('[role="menuitem"]'));
            const step = event.key === 'ArrowDown' ? 1 : -1;
            const next = current < 0 ? (step > 0 ? 0 : items.length - 1) : (current + step + items.length) % items.length;
            items[next].focus();
        });
        this.root?.addEventListener('click', async event => {
            const actionElement = event.target.closest('[data-job-action]');
            if (!actionElement) return;
            const action = actionElement.dataset.jobAction;
            const jobId = Number(actionElement.dataset.jobId);
            const runId = actionElement.dataset.runId;
            actionElement.closest('.job-more')?.removeAttribute('open');

            const environmentId = Number(actionElement.dataset.envId);

            if (action === 'new') return this.openEditor();
            if (action === 'refresh') return this.refreshAll();
            if (action === 'edit') return this.openEditor(this.jobs.find(job => job.id === jobId));
            if (action === 'run') return this.runNow(jobId, actionElement);
            if (action === 'toggle') return this.toggleJob(jobId, actionElement);
            if (action === 'toggle-environment') return this.toggleStandaloneAgent(environmentId, actionElement);
            if (action === 'delete') return this.deleteJob(jobId);
            // An Agent with no Automation behind it: its setup is launched and edited directly.
            if (action === 'edit-environment') {
                const environment = this.findEnvironment(environmentId);
                return environment ? this.editStandaloneAgent(environment) : undefined;
            }
            if (action === 'run-environment' || action === 'open-terminal') {
                const environment = this.findEnvironment(environmentId);
                return environment ? this.app.environmentController.launchInWebUI(environment.id, environment.name, environment.cli) : undefined;
            }
            if (action === 'open-external') {
                const environment = this.findEnvironment(environmentId);
                return environment ? this.app.environmentController.launchEnvironment(environment.name, environment.cli) : undefined;
            }
            if (action === 'delete-agent') return this.deleteAgent({ jobId, environmentId });
            if (action === 'export-recipe') return this.exportRecipe(jobId);
            if (action === 'import-recipe') return this.importRecipe();
            if (action === 'import-from-repository') return this.openImportFromRepository();
            if (action === 'view-run' && runId) return this.openRun(runId);
            if (action === 'view-history' && Number.isFinite(jobId)) return this.openRunHistory(jobId);
            if (action === 'cancel-run' && runId) return this.cancelRun(runId, actionElement);
            if (action === 'retry-run' && runId) return this.retryRun(runId, actionElement);
        });
    }

    async refreshAll({ quiet = false } = {}) {
        const generation = this.jobMutationGeneration;
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
            // Reads started before a successful toggle must not restore its old state.
            if (generation !== this.jobMutationGeneration) return;
            this.jobs = jobsResponse?.jobs || [];
            this.runSummaries = runsResponse?.summaries || [];
            const environmentRecords = environmentsResponse?.environments || [];
            this.environments = this.app.environmentController?.setEnvironments
                ? this.app.environmentController.setEnvironments(environmentRecords)
                : environmentRecords;
            this.app.data.environments = this.environments;
            this.renderJobs();
            this.renderRuns();
            // Automations were (re)loaded after a create, edit or delete: the VS Code
            // Activity Bar launcher view lists the same catalog and re-reads it.
            globalThis.__viberails_launcherChanged__?.();
        } catch (error) {
            if (!quiet) this.app.showError(error?.message || `Could not load ${this.page.noun}s.`);
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
            if (!quiet) this.app.showToast('Run history', 'Refreshed.', 'info');
        } catch (error) {
            if (!quiet) this.app.showError(error?.message || 'Could not refresh the run history.');
        }
    }

    async refreshJobs({ quiet = false } = {}) {
        const generation = this.jobMutationGeneration;
        try {
            const projectPath = this.currentProjectPath();
            if (projectPath) {
                const response = await this.app.apiCall(
                    `/api/v1/jobs?projectPath=${encodeURIComponent(projectPath)}`,
                    'GET', null, { showLoading: false });
                if (generation === this.jobMutationGeneration) this.jobs = response?.jobs || [];
            }
        } catch (error) {
            if (!quiet) this.app.showError(error?.message || `Could not refresh ${this.page.noun}s.`);
        }
        // Render even when the fetch failed or was skipped: the countdown labels
        // ("in 12 min") age out of the cached data on their own.
        this.renderJobs();
    }

    async environmentChanged() {
        this.environments = this.app.data.environments || [];
        this.renderJobs();
    }

    /**
     * What the two pages show, derived from the latest environments and jobs every time so a
     * direct assignment to either (a poll, a test) never leaves a stale list behind.
     */
    get catalog() {
        return classifyCatalog(this.environments ?? [], this.jobs ?? []);
    }

    /**
     * The entries the current page lists. A saved setup shared by several Automations is one
     * card per Automation (each has its own name, schedule and switch); without one, the
     * setup is a card of its own.
     */
    pageEntries() {
        if (this.mode !== 'agents') return this.catalog.automations;
        return this.catalog.agents.flatMap(entry => entry.jobs.length
            ? entry.jobs.map(job => ({ environment: entry.environment, jobs: [job] }))
            : [entry]);
    }

    renderJobs() {
        const target = this.root?.querySelector('[data-jobs-list]');
        const count = this.root?.querySelector('[data-jobs-count]');
        const total = this.pageEntries().length;
        const noun = this.page.countNoun || this.page.noun;
        if (count) count.textContent = `${total} ${total === 1 ? noun : `${noun}s`}`;
        if (!target) return;
        const html = this.renderJobsListHtml();
        // The 5s poll calls this constantly. Only touch the DOM when the markup actually
        // changed (data or a countdown label), so hover/focus and a busy "Run now" button
        // aren't wiped by an identical re-render.
        if (html === this._lastJobsListHtml) return;
        // Nor while a row menu is open: the next poll repaints once it closes.
        if (this.root.querySelector('.python-script-menu:not([hidden]), [data-jobs-list] .job-more[open]')) return;
        this._lastJobsListHtml = html;
        const openGroups = ['[data-disabled-jobs]', '[data-unsigned-scripts]']
            .filter(selector => this.root.querySelector(selector)?.open === true);
        target.innerHTML = html;
        for (const selector of openGroups) {
            const group = this.root.querySelector(selector);
            if (group) group.open = true;
        }
    }

    renderJobsListHtml() {
        const page = this.page;
        const entries = this.pageEntries();
        if (this.mode === 'automations') return this.renderAutomationsListHtml(entries);
        if (entries.length === 0) {
            return `
                <div class="jobs-empty jobs-empty-large">
                    <i class="fa-solid ${this.mode === 'agents' ? 'fa-robot' : 'fa-list-check'}" aria-hidden="true"></i>
                    <strong>${page.emptyTitle}</strong>
                    <span>${page.emptyText}</span>
                    <button class="btn btn-primary" type="button" data-job-action="new">${page.emptyAction}</button>
                </div>`;
        }

        const render = this.mode === 'agents' ? entry => this.renderAgentCard(entry) : job => this.renderAutomationCard(job);
        // An Agent with no Automation behind it has no switch to turn off, so it counts as on.
        const isOn = this.mode === 'agents' ? entry => !entry.jobs[0] || entry.jobs[0].enabled === true : job => job.enabled === true;
        const enabled = entries.filter(isOn);
        const disabled = entries.filter(entry => !isOn(entry));
        return enabled.map(render).join('') + (disabled.length ? `
            <details class="jobs-collapsible-list" data-disabled-jobs>
                <summary>
                    <i class="fa-solid fa-chevron-right" aria-hidden="true"></i>
                    <span>${page.disabledTitle}</span><span class="jobs-count">${disabled.length}</span>
                </summary>
                <div>${disabled.map(render).join('')}</div>
            </details>` : '');
    }

    /**
     * The Automations page's one list (VIBE-159): enabled workflows and signed scripts, then
     * the unsigned scripts and the disabled workflows behind their collapsed groups.
     */
    renderAutomationsListHtml(workflows) {
        const scripts = this.pythonScripts.listParts();
        if (workflows.length === 0 && scripts.loaded && scripts.count === 0) {
            const fromDisk = this.pythonScripts._canImportFromHost()
                ? `<button class="btn btn-sm btn-outline-secondary" type="button" data-python-scripts-action="import">
                        <i class="fa-solid fa-file-import me-1" aria-hidden="true"></i>Script from disk
                    </button>`
                : '';
            return `
                <div class="jobs-empty jobs-empty-large automations-empty">
                    <i class="fa-solid fa-list-check" aria-hidden="true"></i>
                    <strong>No automations yet</strong>
                    <span>Write a pwsh, bash or python script, or build a workflow that runs repository scripts and checks
                        (VCA, Code quality). You can also drop a <code>.ps1</code>, <code>.sh</code> or <code>.py</code> file here.</span>
                    <div class="python-scripts-empty-actions">
                        <button class="btn btn-sm btn-primary" type="button" data-python-scripts-action="new">
                            <i class="fa-solid fa-file-code me-1" aria-hidden="true"></i>New script
                        </button>
                        <button class="btn btn-sm btn-outline-primary" type="button" data-job-action="new">
                            <i class="fa-solid fa-list-check me-1" aria-hidden="true"></i>New workflow
                        </button>
                        ${fromDisk}
                    </div>
                </div>`;
        }
        const enabled = workflows.filter(job => job.enabled === true);
        const disabled = workflows.filter(job => job.enabled !== true);
        return enabled.map(job => this.renderAutomationCard(job)).join('')
            + scripts.signedHtml
            + (scripts.loaded ? '' : scripts.failed
                ? '<div class="jobs-empty" role="status" data-entry-kind="script">Could not load scripts. Refresh to try again.</div>'
                : '<div class="jobs-empty" role="status" data-entry-kind="script"><span class="spinner-border spinner-border-sm"></span> Loading scripts…</div>')
            + scripts.unsignedHtml
            + (disabled.length ? `
            <details class="jobs-collapsible-list" data-disabled-jobs data-entry-kind="workflow">
                <summary>
                    <i class="fa-solid fa-chevron-right" aria-hidden="true"></i>
                    <span>${this.page.disabledTitle}</span><span class="jobs-count">${disabled.length}</span>
                </summary>
                <div>${disabled.map(job => this.renderAutomationCard(job)).join('')}</div>
            </details>` : '');
    }

    /** The CLI's brand mark: the one thing that makes a list of agents scannable at a glance. */
    renderBrandMark(cli, fallbackIcon = 'fa-robot') {
        const brand = this.app.getCliBrand(cli || '');
        const label = this.escape(brand.label || cli || 'CLI');
        if (!brand.logo) {
            return `<span class="job-card-mark job-card-mark-fallback" data-provider="${this.escape(cli || '')}" title="${label}" aria-label="${label}"><i class="fa-solid ${fallbackIcon}" aria-hidden="true"></i></span>`;
        }
        const filter = brand.logoFilter ? ` style="filter: ${this.escape(brand.logoFilter)};"` : '';
        return `<span class="job-card-mark" data-provider="${this.escape(cli || '')}" title="${label}" aria-label="${label}"><img src="${this.escape(brand.logo)}" alt="" loading="lazy"${filter}></span>`;
    }

    /** Scripts and checks a Job runs besides its agent step, as one short line. */
    extraStepsLabel(actions) {
        return actions
            .filter(action => action.kind !== JOB_ACTION.WORKER)
            .map(action => isCheck(action.kind)
                ? `${checkName(action.kind)} · ${(action.arguments || []).join(' ')}`.trim()
                : (action.scriptPath?.split(/[\\/]/).pop() || 'Unselected script'))
            .join(' → ');
    }

    renderTriggerMeta(job) {
        const triggers = (job.triggers || []).map(trigger => `<span class="jobs-trigger-chip">${this.escape(this.formatTrigger(trigger))}</span>`).join('');
        const nextRun = this.nextRunSummary(job);
        const nextRunTitle = nextRun.utc ? ` title="${this.escape(new Date(nextRun.utc).toLocaleString())}"` : '';
        return `<span class="job-card-trigger" title="When it runs"><i class="fa-regular fa-clock" aria-hidden="true"></i><span class="job-triggers">${triggers || '<span class="jobs-trigger-chip">On demand</span>'}</span></span>
                <span title="Next run"><i class="fa-solid fa-forward" aria-hidden="true"></i><span>Next: <strong${nextRunTitle}>${this.escape(nextRun.label)}</strong></span></span>`;
    }

    /**
     * One Agent: a saved CLI setup with its starting message, plus the Automation (if any)
     * that gives it a schedule. Without one, Run now opens it in a terminal tab right away.
     */
    renderAgentCard(entry) {
        const { environment, jobs } = entry;
        const job = jobs[0] || null;
        const worker = job ? workerActionOf(job) : null;
        const cli = environment?.cli || (worker ? getJobCliForLlm(worker.llm || job.llm) : null) || '';
        const brand = this.app.getCliBrand(cli);
        // The same CLI names the rest of the app uses ("GLM 5.2", not a brand mark's caption).
        const llmName = getLlmName(Number(getJobLlmForCli(cli) ?? worker?.llm ?? job?.llm ?? 0));
        const providerLabel = llmName === 'Unknown' ? (brand.label || cli || 'Agent') : llmName;
        const name = agentDisplayName(entry);
        const safeName = this.escape(name);
        const jobId = job ? this.escape(job.id) : '';
        const envId = environment ? this.escape(environment.id) : '';
        const settings = environment ? workerModelSummary(cli, environment.customArgs) : '';
        const message = (environment?.customPrompt || job?.prompt || '').trim();
        const extras = job ? this.extraStepsLabel(this.normalizeJobActions(job)) : '';
        const enabled = !job || job.enabled === true;
        // The setup's own name, when it differs from the Automation's — from the loaded setup, or
        // from the Job's snapshot of it while the setups are still loading or it has been deleted.
        const storedName = environment?.name || worker?.environmentName || '';
        const setupName = job && storedName && storedName !== job.name ? storedName : '';
        const more = [
            environment ? `<button type="button" data-job-action="open-terminal" data-env-id="${envId}" aria-label="Open ${safeName} in a terminal tab"><i class="fa-solid fa-display" aria-hidden="true"></i>Open in a terminal tab</button>
                <button type="button" data-job-action="open-external" data-env-id="${envId}" aria-label="Open ${safeName} in a separate window"><i class="fa-solid fa-terminal" aria-hidden="true"></i>Open in a separate window</button>` : '',
            job ? `<button type="button" data-job-action="export-recipe" data-job-id="${jobId}" aria-label="Export ${safeName} as a recipe"><i class="fa-solid fa-file-export" aria-hidden="true"></i>Export recipe</button>` : '',
            `<button class="job-more-danger" type="button" data-job-action="delete-agent" data-job-id="${jobId}" data-env-id="${envId}" aria-label="Delete ${safeName}"><i class="fa-solid fa-trash" aria-hidden="true"></i>Delete</button>`
        ].join('');
        return `
            <article class="job-card job-card-agent" data-enabled="${enabled}" data-kind="agent">
                ${this.renderBrandMark(cli)}
                <div class="job-card-main">
                    <div class="job-card-topline">
                        <div class="job-card-title">
                            <h3>${safeName}</h3>
                            <span class="job-provider" data-provider="${this.escape(cli)}">${this.escape(providerLabel)}</span>
                        </div>
                    </div>
                    ${settings || setupName ? `<p class="job-card-settings"><i class="fa-solid fa-sliders" aria-hidden="true"></i>${settings ? `<span>${this.escape(settings)}</span>` : ''}${setupName ? `<span class="job-card-setup-name" title="Saved setup">· ${this.escape(setupName)}</span>` : ''}</p>` : ''}
                    ${environment ? '' : '<p class="job-card-missing" data-tone="warning"><i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i><span>Its saved CLI setup is missing. Edit to choose the CLI again.</span></p>'}
                    ${message ? `<p class="job-card-prompt" title="${this.escape(this.truncate(message, 400))}"><i class="fa-regular fa-message" aria-hidden="true"></i><span>${this.escape(message)}</span></p>` : ''}
                    <div class="job-card-meta">
                        ${job ? this.renderTriggerMeta(job) : '<span class="job-card-trigger" title="When it runs"><i class="fa-regular fa-clock" aria-hidden="true"></i><span class="job-triggers"><span class="jobs-trigger-chip">On demand</span></span></span>'}
                        ${extras ? `<span title="Also runs"><i class="fa-solid fa-list-check" aria-hidden="true"></i><span class="job-card-meta-text">${this.escape(extras)}</span></span>` : ''}
                    </div>
                </div>
                <div class="job-card-actions">
                    ${this.renderEnabledSwitch(job || { enabled: true }, jobId, safeName, envId)}
                    ${job
                        ? `<button class="btn btn-sm btn-primary" type="button" data-job-action="run" data-job-id="${jobId}" aria-label="Run ${safeName} now"><i class="fa-solid fa-play me-1" aria-hidden="true"></i>Run now</button>`
                        : `<button class="btn btn-sm btn-primary" type="button" data-job-action="run-environment" data-env-id="${envId}" aria-label="Run ${safeName} now"><i class="fa-solid fa-play me-1" aria-hidden="true"></i>Run now</button>`}
                    ${job
                        ? `<button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="edit" data-job-id="${jobId}" aria-label="Edit ${safeName}"><i class="fa-solid fa-pen me-1" aria-hidden="true"></i>Edit</button>`
                        : `<button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="edit-environment" data-env-id="${envId}" aria-label="Edit ${safeName}"><i class="fa-solid fa-pen me-1" aria-hidden="true"></i>Edit</button>`}
                    <details class="job-more">
                        <summary class="btn btn-sm btn-outline-secondary" aria-label="More actions for ${safeName}">More<i class="fa-solid fa-chevron-down ms-1" aria-hidden="true"></i></summary>
                        <div class="job-more-menu">${more}</div>
                    </details>
                </div>
            </article>`;
    }

    /** One Automation: scripts and checks with no LLM step. */
    renderAutomationCard(job) {
        const actions = this.normalizeJobActions(job);
        const scriptActions = actions.filter(action => action.kind === JOB_ACTION.SCRIPT);
        const checkActions = actions.filter(action => isCheck(action.kind));
        const tag = scriptActions.length === 1 && checkActions.length === 0
            ? (SCRIPT_RUNTIME_META[scriptActions[0].scriptRuntime]?.label || 'Script')
            : checkActions.length === actions.length ? 'Checks' : 'Workflow';
        const stepsLabel = this.extraStepsLabel(actions);
        const jobId = this.escape(job.id);
        const safeName = this.escape(job.name);
        const icon = scriptActions.length > 0 && checkActions.length === 0 ? 'fa-code' : 'fa-list-check';
        return `
            <article class="job-card job-card-automation" data-enabled="${job.enabled === true}" data-kind="automation">
                <span class="job-card-mark job-card-mark-fallback" data-provider="automation" title="Automation" aria-label="Automation"><i class="fa-solid ${icon}" aria-hidden="true"></i></span>
                <div class="job-card-main">
                    <div class="job-card-topline">
                        <div class="job-card-title">
                            <h3>${safeName}</h3>
                            <span class="job-provider" data-provider="automation">${this.escape(tag)}</span>
                        </div>
                    </div>
                    ${stepsLabel ? `<p class="job-card-workflow" title="${this.escape(stepsLabel)}"><i class="fa-solid fa-code-branch" aria-hidden="true"></i><span>${this.escape(stepsLabel)}</span></p>` : ''}
                    ${job.description ? `<p class="job-card-prompt" title="${this.escape(this.truncate(job.description, 400))}"><i class="fa-regular fa-message" aria-hidden="true"></i><span>${this.escape(job.description)}</span></p>` : ''}
                    <div class="job-card-meta">
                        <span title="Steps"><i class="fa-solid fa-list-check" aria-hidden="true"></i><span class="job-card-meta-text">${actions.length} ${actions.length === 1 ? 'step' : 'steps'}</span></span>
                        ${this.renderTriggerMeta(job)}
                    </div>
                </div>
                <div class="job-card-actions">
                    ${this.renderEnabledSwitch(job, jobId, safeName)}
                    <button class="btn btn-sm btn-primary" type="button" data-job-action="run" data-job-id="${jobId}" aria-label="Run ${safeName} now"><i class="fa-solid fa-play me-1" aria-hidden="true"></i>Run now</button>
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-job-action="edit" data-job-id="${jobId}" aria-label="Edit ${safeName}"><i class="fa-solid fa-pen me-1" aria-hidden="true"></i>Edit</button>
                    <details class="job-more">
                        <summary class="btn btn-sm btn-outline-secondary" aria-label="More actions for ${safeName}">More<i class="fa-solid fa-chevron-down ms-1" aria-hidden="true"></i></summary>
                        <div class="job-more-menu">
                            <button type="button" data-job-action="export-recipe" data-job-id="${jobId}" aria-label="Export ${safeName} as a recipe"><i class="fa-solid fa-file-export" aria-hidden="true"></i>Export recipe</button>
                            <button class="job-more-danger" type="button" data-job-action="delete" data-job-id="${jobId}" aria-label="Delete ${safeName}"><i class="fa-solid fa-trash" aria-hidden="true"></i>Delete</button>
                        </div>
                    </details>
                </div>
            </article>`;
    }

    /** An Agent with no Automation behind it edits its saved setup directly. */
    async editStandaloneAgent(environment) {
        return this.app.environmentController.editEnvironment(environment.name, {
            variant: 'agent',
            onChanged: () => this.refreshAll({ quiet: true })
        });
    }

    /**
     * Delete an Agent card: its Automation first, then its saved setup when no other Automation
     * uses it. A card with no Automation is the setup itself. The server refuses a setup that an
     * Automation still points at, and its message is shown as is.
     */
    async deleteAgent({ jobId, environmentId }) {
        // Standalone cards have data-job-id="", which the click handler converts to 0.
        // Only a positive ID identifies a Job; otherwise delete by the saved setup's ID.
        const byJob = Number.isInteger(jobId) && jobId > 0;
        const entry = this.catalog.agents.find(candidate => byJob
            ? candidate.jobs.some(job => Number(job.id) === jobId)
            : Number.isFinite(environmentId) && Number(candidate.environment?.id) === environmentId);
        if (!entry) return;
        const jobs = byJob ? entry.jobs.filter(job => Number(job.id) === jobId) : [];
        const others = entry.jobs.length - jobs.length;
        const name = jobs[0]?.name || agentDisplayName(entry);
        let message = 'The agent and its saved CLI settings are removed. Recordings are kept.';
        if (jobs.length) {
            message = others > 0
                ? `The agent and its schedule are removed. Its CLI settings stay: ${others === 1 ? 'another agent uses' : `${others} other agents use`} them.`
                : 'The agent, its schedule and its CLI settings are removed. Run history and recordings are kept.';
        }
        const confirmed = await this.confirm({
            title: `Delete “${name}”?`,
            message,
            confirmLabel: 'Delete',
            danger: true
        });
        if (!confirmed) return;
        try {
            for (const job of jobs) {
                await this.app.apiCall(`/api/v1/jobs/${job.id}`, 'DELETE');
            }
            if (entry.environment && others === 0) {
                await this.app.apiCall(`/api/v1/environments/${encodeURIComponent(entry.environment.name)}`, 'DELETE');
            }
            this.app.showToast('Agent', 'Deleted.', 'info');
            await this.app.environmentController.refreshEnvironments();
            await this.refreshAll({ quiet: true });
        } catch (error) {
            this.app.showError(error?.message || 'Could not delete the agent.');
            await this.refreshAll({ quiet: true });
        }
    }

    // One control carries both jobs: it reads the state (green ON / red OFF) and flips it.
    // The old pair of Pause/Enable buttons named the action instead, which meant the label
    // said the opposite of what the automation was actually doing. It renders in the
    // card's action row so it sits on the same line as Run now / Edit / More — floating
    // it in the topline left it visibly off-center from the buttons it belongs with.
    renderEnabledSwitch(job, jobId, safeName, envId = '') {
        const enabled = job.enabled === true;
        const title = enabled
            ? 'Enabled — scheduled and Git-triggered runs are on. Click to disable.'
            : 'Disabled — scheduled and Git-triggered runs are off. Click to enable.';
        const target = jobId
            ? `data-job-action="toggle" data-job-id="${jobId}"`
            : `data-job-action="toggle-environment" data-env-id="${envId}"`;
        const switchTitle = jobId ? title : 'Enabled — on demand. Click to disable in this project; Run now remains available.';
        return `<button class="job-switch" type="button" role="switch" aria-checked="${enabled}" ${target} title="${switchTitle}" aria-label="${safeName} automatic runs"><span class="job-switch-track" aria-hidden="true"><span class="job-switch-thumb"></span></span><span class="job-switch-text">${enabled ? 'Enabled' : 'Disabled'}</span></button>`;
    }

    nextRunSummary(job) {
        if (job.enabled !== true) return { label: 'Disabled', utc: null };

        const triggers = job.triggers || [];
        const scheduled = triggers.filter(trigger => Number(trigger.kind) === TRIGGER.SCHEDULE);
        const next = scheduled
            .map(trigger => trigger.nextRunUtc)
            .filter(value => value && Number.isFinite(new Date(value).getTime()))
            .sort((left, right) => new Date(left).getTime() - new Date(right).getTime())[0];

        if (next) return { label: this.formatFutureTime(next), utc: next };
        if (scheduled.length > 0) return { label: 'Waiting for schedule', utc: null };
        const hasPreCommit = triggers.some(trigger => Number(trigger.kind) === TRIGGER.PRECOMMIT);
        const hasCommit = triggers.some(trigger => Number(trigger.kind) === TRIGGER.COMMIT);
        if (hasPreCommit && hasCommit) return { label: 'On next commit', utc: null };
        if (hasPreCommit) return { label: 'Before next commit', utc: null };
        if (hasCommit) return { label: 'After next commit', utc: null };
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

    /**
     * This page's share of the run history. Agent runs belong to the Agents page; everything
     * else, including runs of Jobs that were since deleted, stays on the Automations page so
     * no recording becomes unreachable.
     */
    pageRunSummaries() {
        const agentJobIds = new Set(this.catalog.agents.flatMap(entry => entry.jobs.map(job => Number(job.id))));
        return this.runSummaries.filter(summary =>
            agentJobIds.has(Number(summary.jobId)) === (this.mode === 'agents'));
    }

    renderRunsHtml() {
        const summaries = this.pageRunSummaries();
        if (summaries.length === 0) {
            // The summary endpoint returns nothing at all without a project path, so "no runs yet"
            // would be a guess here — say which of the two it actually is.
            return this.currentProjectPath()
                ? `<div class="jobs-empty">No ${this.page.noun} runs yet.</div>`
                : `<div class="jobs-empty">Open a project to see its ${this.page.noun} runs.</div>`;
        }
        return `
            <div class="table-responsive"><table class="table jobs-runs-table align-middle">
                <thead><tr><th>${this.page.title.replace(/s$/, '')}</th><th>Last run</th><th>When</th><th>Runs</th><th><span class="visually-hidden">Actions</span></th></tr></thead>
                <tbody>${summaries.map(summary => this.renderSummaryRow(summary)).join('')}</tbody>
            </table></div>`;
    }

    // One row per automation. Listing every run put a job on a 15-minute timer at the top of the
    // table forever and buried everything else, so the individual runs moved into a modal that is
    // opened per automation and fetched on demand.
    renderSummaryRow(summary) {
        const statusCode = Number(summary.lastStatus);
        const jobId = this.escape(summary.jobId);
        const safeName = this.escape(summary.jobName);
        const detail = this.runDetail({ errorMessage: summary.lastErrorMessage, exitCode: summary.lastExitCode });
        const total = Number(summary.totalRuns) || 0;
        const activeRuns = Number(summary.activeRuns) || 0;
        const openTitle = `View this automation's ${total} ${total === 1 ? 'run' : 'runs'}`;
        const whyAction = summary.lastRunId ? `data-job-action="view-run" data-run-id="${this.escape(summary.lastRunId)}"` : '';
        return `<tr>
            <td><button class="job-run-link" type="button" data-job-action="view-history" data-job-id="${jobId}" title="${openTitle}">${safeName}</button><small>${this.escape(getLlmName(Number(summary.lastLlm)))}${summary.lastEnvironmentName ? ` · ${this.escape(summary.lastEnvironmentName)}` : ''}</small></td>
            <td>${this.renderRunStatus(statusCode, detail, whyAction)}</td>
            <td title="${this.escape(summary.lastQueuedUtc)}">${this.escape(this.relativeTime(summary.lastQueuedUtc))}</td>
            <td><span class="jobs-run-count">${total}</span>${activeRuns > 0 ? `<small class="job-run-status-detail">${activeRuns} active</small>` : ''}</td>
            <td class="text-end jobs-run-actions">
                <button class="btn btn-sm btn-outline-secondary job-icon-action" type="button" data-job-action="view-history" data-job-id="${jobId}" title="${openTitle}" aria-label="${openTitle}"><i class="fa-solid fa-clock-rotate-left" aria-hidden="true"></i></button>
            </td>
        </tr>`;
    }

    // ----- Run history modal: every run of one automation, with watch / run again / delete -----

    async openRunHistory(jobId) {
        const summary = this.runSummaries.find(entry => Number(entry.jobId) === jobId);
        const name = this.jobs.find(job => job.id === jobId)?.name || summary?.jobName || this.kindTitle();
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
            if (action === 'copy-session') return this.copySessionId(element.dataset.sessionId);
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
        const confirmed = await this.confirm({
            title: `Remove ${subject}?`,
            message: `Removed from this ${this.page.noun}’s history only — the recorded terminal and logs stay in Chat History.`,
            confirmLabel: 'Remove',
            danger: true
        });
        if (!confirmed) return;

        try {
            const response = await this.app.apiCall('/api/v1/jobs/runs/delete', 'POST', { runIds: ids });
            this.app.showToast('Run history', response?.message || 'Runs removed from the history.', 'info');
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
            return `<div class="jobs-empty">This ${this.page.noun} has no recorded runs.</div>`;
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
        const active = statusCode === RUN_STATUS_CODE.QUEUED || statusCode === RUN_STATUS_CODE.RUNNING;
        // Succeeded is terminal but it is not a failure, and offering a re-run on it invited
        // repeating work that had already landed. Running again starts at Failed.
        const retryable = statusCode >= RUN_STATUS_CODE.FAILED;
        const runId = this.escape(run.id);
        const detail = this.runDetail(run);
        const trigger = { 0: 'Schedule', 2: 'After commit', 3: 'Manual', 4: 'Before commit', 5: 'Board lane' }[Number(run.triggerKind)] || 'Manual';
        const watchTitle = active ? 'Watch this run live' : 'Watch this recording';
        const selected = this.historySelection.has(run.id);
        return `<tr>
            <td class="job-history-check"><input type="checkbox" class="form-check-input" data-run-select="${runId}"${selected ? ' checked' : ''}${active ? ' disabled title="A run that is still going cannot be removed"' : ''} aria-label="Select this run"></td>
            <td>${this.renderRunStatus(statusCode, detail, `data-history-action="watch" data-run-id="${runId}"`)}</td>
            <td>${trigger}<small class="d-block mt-1">${run.terminalSessionId || run.sessionId
                ? `<button class="btn btn-sm btn-link p-0 text-start" type="button" data-history-action="copy-session" data-session-id="${this.escape(run.terminalSessionId || run.sessionId)}" title="Copy session ID"><code>${this.escape(run.terminalSessionId || run.sessionId)}</code> <i class="fa-regular fa-copy" aria-hidden="true"></i></button>`
                : '<span class="text-muted">No session recorded</span>'}</small></td>
            <td title="${this.escape(run.queuedUtc)}">${this.escape(this.relativeTime(run.queuedUtc))}</td>
            <td${this.durationTitle(run)}>${this.escape(this.runDuration(run))}</td>
            <td class="text-end jobs-run-actions">
                ${active ? `<button class="btn btn-sm btn-outline-danger job-icon-action" type="button" data-history-action="stop" data-run-id="${runId}" title="Stop this run" aria-label="Stop this run"><i class="fa-solid fa-stop" aria-hidden="true"></i></button>` : ''}
                ${retryable ? `<button class="btn btn-sm btn-outline-secondary job-icon-action" type="button" data-history-action="run-again" data-run-id="${runId}" title="Run this ${this.page.noun} again" aria-label="Run this ${this.page.noun} again"><i class="fa-solid fa-play" aria-hidden="true"></i></button>` : ''}
                ${active ? '' : `<button class="btn btn-sm btn-outline-danger job-icon-action" type="button" data-history-action="delete" data-run-id="${runId}" title="Remove this run from the history" aria-label="Remove this run from the history"><i class="fa-solid fa-trash" aria-hidden="true"></i></button>`}
                <button class="btn btn-sm btn-outline-secondary job-icon-action job-run-watch" type="button" data-history-action="watch" data-run-id="${runId}" title="${watchTitle}" aria-label="${watchTitle}"><i class="fa-solid fa-eye-slash" data-watch-idle aria-hidden="true"></i><i class="fa-solid fa-eye" data-watch-live aria-hidden="true"></i></button>
            </td>
        </tr>`;
    }

    async copySessionId(sessionId) {
        if (!sessionId) return;
        const copied = await this.app.copyTextToClipboard(sessionId);
        this.app.showToast('Session', copied ? 'Session ID copied.' : 'Could not copy session ID.', copied ? 'success' : 'warning');
    }

    // A run that ended without succeeding opens its detail from the status itself. The reason
    // line is cut to fit this column; the detail shows it whole, beside the CLI's last screen.
    renderRunStatus(statusCode, detail, openAttributes) {
        const status = RUN_STATUS[statusCode] || { label: 'Unknown', tone: 'neutral' };
        const content = `<span class="job-run-status" data-tone="${status.tone}">${status.label}</span>${detail ? `<small class="job-run-status-detail" title="${this.escape(detail)}">${this.escape(detail)}</small>` : ''}`;
        if (!openAttributes || statusCode < RUN_STATUS_CODE.FAILED) return content;
        return `<button class="job-run-why" type="button" ${openAttributes} title="Show why this run stopped">${content}</button>`;
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

    async openEditor(job = null, preferredTrigger = null, editorState = null) {
        this.closeEditor();
        if (!this.root) return;
        const origin = this.root;
        const generation = ++this.editorRequestGeneration;
        const previousModal = this.app.modalState;
        const isEdit = Boolean(job);
        const agentMode = this.mode === 'agents';
        const page = this.page;
        const source = editorState || job || {};
        // An Agent always has exactly one agent step, configured inline (users never name a
        // separate setup); it is never removable. An Automation never has one: its steps are
        // scripts and checks only. The two pages never convert one into the other.
        const actions = this.normalizeJobActions(source, { defaultWorker: agentMode })
            .filter(action => agentMode || action.kind !== JOB_ACTION.WORKER);
        if (agentMode && !actions.some(action => action.kind === JOB_ACTION.WORKER)) {
            actions.push({ id: this.newActionId(), kind: JOB_ACTION.WORKER, environmentId: null });
        }
        const workerOptional = false;
        const worker = actions.find(action => action.kind === JOB_ACTION.WORKER);
        const environment = worker ? this.findEnvironment(worker.environmentId) : null;
        const cliSettings = environment ? await this.app.environmentController.loadEditorSettings(environment) : {};
        if (generation !== this.editorRequestGeneration || this.root !== origin || this.app.modalState !== previousModal) return;
        this.editorActions = actions;
        this.editorWorkerOptional = workerOptional;
        this.activeEditorJob = job;
        this.activeEditorSource = source;
        this.activeEditorPreferredTrigger = preferredTrigger;
        this.editorEnvironment = environment;
        this.editorCliSettings = cliSettings;
        this.editorScriptChoices = null;

        const triggers = source.triggers || [];
        const scheduled = triggers.find(trigger => Number(trigger.kind) === TRIGGER.SCHEDULE);
        const hasCommit = triggers.some(trigger => Number(trigger.kind) === TRIGGER.COMMIT) || preferredTrigger === TRIGGER.COMMIT;
        const hasPreCommit = triggers.some(trigger => Number(trigger.kind) === TRIGGER.PRECOMMIT) || preferredTrigger === TRIGGER.PRECOMMIT;
        const timezone = scheduled?.timeZoneId || Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
        const scheduleKind = Number(scheduled?.scheduleKind ?? SCHEDULE.INTERVAL);
        const projectPath = this.currentProjectPath();

        this.app.showModal(isEdit ? `Edit ${source.name}` : `New ${page.noun}`, `
            <div data-job-editor data-mode="${this.mode}">
            <form data-job-form class="job-inline-form job-creation-form">
                <div class="job-form-fields">
                    <div><label class="form-label" for="job-name">Name</label><input class="form-control" id="job-name" maxlength="100" required value="${this.escape(source.name || '')}" placeholder="${agentMode ? 'Security review after commit' : 'Lint before commit'}"></div>

                </div>

                <fieldset class="job-actions-fieldset">
                    <legend>What runs <small>Top to bottom</small></legend>
                    <div class="job-actions-list" data-job-actions aria-live="polite"></div>
                    <div class="job-actions-toolbar"><label class="form-label">${agentMode ? 'Add checks or scripts <span class="text-muted">(optional)</span>' : 'Add a check or a script'}</label>
                        <div>
                            <button class="btn btn-sm btn-outline-primary" type="button" data-job-action="add-quality-action">Add Code quality</button>
                            <button class="btn btn-sm btn-outline-primary" type="button" data-job-action="add-vca-action">Add VCA</button>
                            <button class="btn btn-sm btn-outline-primary" type="button" data-job-action="add-script-action"><i class="fa-solid fa-code me-1" aria-hidden="true"></i>Add script</button>
                        </div>
                    </div>

                </fieldset>

                <fieldset class="job-trigger-fieldset"><legend>When should it run? <small>Run now is always available</small></legend><div class="job-trigger-choices">
                    <label class="job-trigger-option"><span><strong>On a schedule</strong><small>Run every few minutes, daily, or weekly.</small></span><input class="job-switch-input" type="checkbox" id="job-trigger-schedule" ${scheduled ? 'checked' : ''}></label>
                    <label class="job-trigger-option"><span><strong>Before each commit</strong><small>Queue when Git starts a commit. The commit is not delayed while the automation runs.</small></span><input class="job-switch-input" type="checkbox" id="job-trigger-precommit" ${hasPreCommit ? 'checked' : ''}></label>
                    <label class="job-trigger-option"><span><strong>After each commit</strong><small>Run after a successful Git commit in this repository.</small></span><input class="job-switch-input" type="checkbox" id="job-trigger-commit" ${hasCommit ? 'checked' : ''}></label>
                    </div>
                    <div class="job-schedule-editor" data-schedule-editor ${scheduled ? '' : 'hidden'}>
                        <div class="row g-2">
                            <div class="col-md-4"><label class="form-label" for="job-schedule-kind">Schedule</label><select class="form-select" id="job-schedule-kind"><option value="0" ${scheduleKind === 0 ? 'selected' : ''}>Every interval</option><option value="1" ${scheduleKind === 1 ? 'selected' : ''}>Daily</option><option value="2" ${scheduleKind === 2 ? 'selected' : ''}>Weekly</option></select></div>
                            <div class="col-md-4" data-interval-field><label class="form-label" for="job-interval">Every</label><div class="input-group"><input class="form-control" type="number" id="job-interval" min="5" max="43200" value="${scheduled?.intervalMinutes || 60}"><span class="input-group-text">min</span></div></div>
                            <div class="col-md-4" data-clock-field><label class="form-label" for="job-local-time">Local time</label><input class="form-control" type="time" id="job-local-time" value="${this.escape(scheduled?.localTime || '09:00')}"></div>
                            <fieldset class="col-12 job-weekday-fieldset" data-weekdays-field><legend class="form-label">Days</legend><div class="job-weekdays">${WEEKDAYS.map((day, index) => `<label><input type="checkbox" data-weekday="${index}" ${(Number(scheduled?.daysOfWeekMask || 0) & (1 << index)) !== 0 ? 'checked' : ''}><span>${day}</span></label>`).join('')}</div></fieldset>
                            <div class="col-12" data-timezone-field><label class="form-label" for="job-timezone">Time zone</label><input class="form-control" id="job-timezone" value="${this.escape(timezone)}"></div>
                        </div>
                    </div>

                </fieldset>

                <section class="job-more-options" aria-label="Run options">

                <div class="job-run-options">
                    <div class="job-timeout-option">
                        <label class="form-label" for="job-timeout">Time limit</label>
                        <div class="input-group"><input class="form-control" type="number" id="job-timeout" min="1" max="720" value="${source.timeoutMinutes || ''}" placeholder="No limit"><span class="input-group-text">min</span></div>
                        <small>Leave blank to let the CLI finish on its own.</small>
                    </div>
                    <label class="job-option-toggle" for="job-enabled"><span><strong>Enabled</strong><small>Allow automatic runs</small></span><input class="job-switch-input" type="checkbox" id="job-enabled" ${source.enabled !== false ? 'checked' : ''}></label>
                    <label class="job-option-toggle" for="job-launch-minimized"><span><strong>Launch minimized</strong><small>Keep the run in the background</small></span><input class="job-switch-input" type="checkbox" id="job-launch-minimized" ${source.launchMinimized === true ? 'checked' : ''}></label>
                </div>

                </section>
                <div class="job-repository-context" title="Runs always happen in the repository VibeRails is open in."><i class="fa-solid fa-code-branch" aria-hidden="true"></i>${projectPath
                    ? `<span>Runs in</span><code>${this.escape(projectPath)}</code>`
                    : `<span>No Git repository detected — open one to run ${page.noun}s.</span>`}</div>
                <div class="job-inline-form-actions"><button class="btn btn-outline-secondary" type="button" data-job-action="cancel-editor">Cancel</button><button class="btn btn-primary" type="submit">${isEdit ? `Save ${page.noun}` : `Create ${page.noun}`}</button></div>
            </form></div>`, { onClose: () => this.resetEditor() });
        const editor = document.querySelector('#modal-container [data-job-editor]');
        this.editorRoot = editor;
        editor.closest('.modal').classList.add('job-creation-modal');
        const form = editor.querySelector('[data-job-form]');
        this.renderEditorActions();

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
        form?.querySelector('[data-job-action="add-quality-action"]')?.addEventListener('click', () => this.addEditorAction(JOB_ACTION.QUALITY));
        form?.querySelector('[data-job-action="add-vca-action"]')?.addEventListener('click', () => this.addEditorAction(JOB_ACTION.VCA));
        form?.querySelector('[data-job-action="add-script-action"]')?.addEventListener('click', () => this.addEditorAction(JOB_ACTION.SCRIPT));
        form?.querySelectorAll('[data-job-action="cancel-editor"]')?.forEach(button => button.addEventListener('click', () => this.closeEditor()));
        form?.querySelector('#job-trigger-schedule')?.addEventListener('change', updateScheduleFields);
        form?.querySelector('#job-schedule-kind')?.addEventListener('change', updateScheduleFields);
        const beforeCommitTrigger = form?.querySelector('#job-trigger-precommit');
        const afterCommitTrigger = form?.querySelector('#job-trigger-commit');
        beforeCommitTrigger?.addEventListener('change', () => {
            if (beforeCommitTrigger.checked && afterCommitTrigger) afterCommitTrigger.checked = false;
        });
        afterCommitTrigger?.addEventListener('change', () => {
            if (afterCommitTrigger.checked && beforeCommitTrigger) beforeCommitTrigger.checked = false;
        });
        form?.addEventListener('submit', event => this.saveJob(event, job));
        updateScheduleFields();

        form?.querySelector('#job-name')?.focus?.();
    }

    normalizeJobActions(source = {}, { defaultWorker = false } = {}) {
        const supplied = Array.isArray(source.actions) ? source.actions : [];
        const raw = supplied.length > 0
            ? supplied
            : source.environmentId
                ? [{
                    id: null,
                    kind: JOB_ACTION.WORKER,
                    environmentId: source.environmentId,
                    environmentName: source.environmentName,
                    llm: source.llm
                }]
                : defaultWorker
                    ? [{ id: null, kind: JOB_ACTION.WORKER, environmentId: null }]
                    : [];

        return raw.slice(0, 20).map(action => ({
            id: String(action?.id || this.newActionId()),
            kind: [0, 1, 2, 3].includes(Number(action?.kind)) ? Number(action.kind) : JOB_ACTION.WORKER,
            environmentId: action?.environmentId == null ? null : Number(action.environmentId),
            environmentName: action?.environmentName || null,
            llm: Number(action?.llm) || 0,
            scriptPath: String(action?.scriptPath || ''),
            scriptRuntime: Number.isFinite(Number(action?.scriptRuntime))
                ? Number(action.scriptRuntime)
                : SCRIPT_RUNTIME.PYTHON,
            arguments: Array.isArray(action?.arguments) ? action.arguments.map(value => String(value ?? '')) : [],
            workingDirectory: String(action?.workingDirectory || ''),
            timeoutSeconds: action?.timeoutSeconds == null || action.timeoutSeconds === ''
                ? null
                : Number(action.timeoutSeconds),
            approvedHash: action?.approvedHash || null
        }));
    }

    newActionId() {
        if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
        return `action-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
    }

    addEditorAction(kind) {
        if (this.editorActions.length >= 20) {
            this.app.showError(`An ${this.page.noun} can have at most 20 steps.`);
            return;
        }
        if (kind === JOB_ACTION.WORKER && this.editorActions.some(action => action.kind === JOB_ACTION.WORKER)) {
            this.app.showError('An agent runs one CLI; it already has its agent step.');
            return;
        }

        this.editorActions.push(isCheck(kind) ? { id: this.newActionId(), kind, arguments: ["unpushed"] } : kind === JOB_ACTION.WORKER
            ? { id: this.newActionId(), kind, environmentId: null, environmentName: null, llm: 0 }
            : {
                id: this.newActionId(),
                kind,
                scriptPath: '',
                scriptRuntime: SCRIPT_RUNTIME.PYTHON,
                arguments: [],
                workingDirectory: '',
                timeoutSeconds: null,
                approvedHash: null
            });
        this.renderEditorActions();
    }

    renderEditorActions() {
        const form = this.editorRoot?.querySelector('[data-job-form]');
        const target = form?.querySelector('[data-job-actions]');
        if (!target) return;

        const hasWorker = this.editorActions.some(action => action.kind === JOB_ACTION.WORKER);
        this.workerEditor?.element.remove();
        form.classList.toggle('job-simple-workflow', this.editorActions.length === 1 && hasWorker);
        target.innerHTML = this.editorActions.length > 0
            ? this.editorActions.map((action, index) => this.renderEditorAction(action, index)).join('')
            : `<div class="job-actions-empty"><i class="fa-solid fa-diagram-project" aria-hidden="true"></i><strong>No steps yet</strong><span>Add a repository script or a check. Need an LLM? Create an agent instead.</span></div>`;

        target.oninput = event => this.updateEditorActionField(event);
        target.onchange = event => this.updateEditorActionField(event);
        target.onclick = event => { void this.handleEditorActionClick(event); };

        this.mountWorkerEditor();
        if (this.editorActions.some(action => action.kind === JOB_ACTION.SCRIPT)) this.loadEditorScriptChoices();
    }

    /**
     * What a script step can pick (VIBE-159): the script library and the repository's own script
     * files, read once per open editor. A failed read leaves Browse repository… working.
     */
    loadEditorScriptChoices() {
        if (this.editorScriptChoices) return;
        const generation = this.editorRequestGeneration;
        this.editorScriptChoices = { loading: true, library: [], repository: [], hasMore: false };
        void (async () => {
            const [catalog, library] = await Promise.all([
                this.app.apiCall('/api/v1/jobs/scripts', 'GET', null, { showLoading: false }).catch(() => null),
                this.pythonScripts.ensureState().catch(() => null)
            ]);
            if (generation !== this.editorRequestGeneration || !this.editorRoot) return;
            this.editorScriptChoices = {
                loading: false,
                library: library?.scripts || [],
                repository: catalog?.scripts || [],
                hasMore: catalog?.hasMore === true
            };
            this.renderEditorActions();
        })();
    }

    /** `path` relative to the open repository with forward slashes, or null when it lies outside. */
    repositoryRelativePath(path) {
        const file = String(path || '').replaceAll('\\', '/');
        const root = String(this.currentProjectPath() || '').replaceAll('\\', '/').replace(/\/+$/, '');
        if (!file || !root) return null;
        if (!/^([a-z]:)?\//i.test(file)) return file.replace(/^\.\//, '');
        return file.toLowerCase().startsWith(`${root.toLowerCase()}/`) ? file.slice(root.length + 1) : null;
    }

    /** The options a script step's picker offers, with the step's current file always among them. */
    scriptStepChoices(action) {
        const choices = this.editorScriptChoices || { loading: true, library: [], repository: [] };
        const key = value => String(value || '').replaceAll('\\', '/').replace(/^\.\//, '').toLowerCase();
        const current = key(this.repositoryRelativePath(action.scriptPath) ?? action.scriptPath);
        const listed = new Set();
        const yours = [];
        const outside = [];
        for (const script of choices.library) {
            const label = script.displayName || script.fileName || script.name;
            const relative = this.repositoryRelativePath(script.path);
            if (!relative) {
                outside.push({ value: '', label: `${label} — outside this repository`, disabled: true });
                continue;
            }
            listed.add(key(relative));
            yours.push({ value: relative, label: label === relative.split('/').pop() ? relative : `${label} — ${relative}` });
        }
        const repository = choices.repository
            .filter(entry => !listed.has(key(entry.path)))
            .map(entry => ({
                value: entry.path,
                label: entry.unavailableReason ? `${entry.path} — ${entry.unavailableReason}` : entry.path,
                disabled: Boolean(entry.unavailableReason)
            }));
        repository.forEach(entry => listed.add(key(entry.value)));
        const mark = item => ({ ...item, selected: Boolean(current) && !item.disabled && key(item.value) === current });
        return {
            loading: choices.loading,
            hasMore: choices.hasMore === true,
            current,
            // A file chosen with Browse…, or one the lists do not reach, stays selectable.
            unlisted: current && !listed.has(current) ? [{ value: action.scriptPath, label: action.scriptPath, selected: true }] : [],
            groups: [['Your scripts', [...yours, ...outside].map(mark)], ['Other scripts in this repository', repository.map(mark)]]
        };
    }

    renderScriptStepPicker(action, actionId) {
        const choices = this.scriptStepChoices(action);
        const option = item =>
            `<option value="${this.escape(item.value)}"${item.selected ? ' selected' : ''}${item.disabled ? ' disabled' : ''}>${this.escape(item.label)}</option>`;
        const groups = choices.groups.filter(([, items]) => items.length)
            .map(([label, items]) => `<optgroup label="${this.escape(label)}">${items.map(option).join('')}</optgroup>`).join('');
        return `
            <div class="job-script-path-field">
                <label class="form-label" for="job-script-${actionId}">Which script?</label>
                <div class="job-script-picker">
                    <select class="form-select" id="job-script-${actionId}" data-action-field="scriptChoice">
                        <option value=""${choices.current ? '' : ' selected'} disabled>${choices.loading ? 'Loading scripts…' : 'Choose a script…'}</option>
                        ${choices.unlisted.map(option).join('')}${groups}
                    </select>
                    <button class="btn btn-outline-secondary" type="button" data-workflow-action="new-script"><i class="fa-solid fa-plus me-1" aria-hidden="true"></i>New script…</button>
                    <button class="btn btn-outline-secondary" type="button" data-workflow-action="browse-script">Browse repository…</button>
                </div>
                <small>A workflow runs a file inside this repository, as it was when you saved the workflow: save again after changing the script.${choices.hasMore ? ' Not every repository script is listed; use Browse repository… for the rest.' : ''}</small>
            </div>`;
    }

    /** New script… creates a library script inside this repository and puts it in the step. */
    async newScriptForStep(action) {
        const project = this.currentProjectPath();
        if (!action || !project) {
            this.app.showError('Open VibeRails in a Git repository to add a script to a workflow.');
            return;
        }
        const generation = this.editorRequestGeneration;
        const separator = project.includes('\\') ? '\\' : '/';
        const id = await this.pythonScripts.newScript({ directory: `${project.replace(/[\\/]+$/, '')}${separator}scripts` });
        if (!id || generation !== this.editorRequestGeneration || !this.editorRoot) return;
        const script = this.pythonScripts.scriptByName(id);
        const path = this.repositoryRelativePath(script?.path);
        if (!path) {
            this.app.showError(`${script?.displayName || 'The new script'} was saved outside this repository, so a workflow cannot run it. Create it in a folder inside ${project}.`);
            return;
        }
        this.setScriptStepPath(action, path);
        this.editorScriptChoices = null;
        this.renderEditorActions();
    }

    setScriptStepPath(action, path) {
        action.scriptPath = path;
        const extension = String(path).toLowerCase().match(/\.[^.\\/]+$/)?.[0];
        const runtime = Object.entries(SCRIPT_RUNTIME_META).find(([, meta]) => meta.extension === extension)?.[0];
        if (runtime !== undefined) action.scriptRuntime = Number(runtime);
    }

    renderEditorAction(action, index) {
        const actionId = this.escape(action.id);
        const controls = `
            <div class="job-action-order-controls" aria-label="Reorder action">
                <button class="btn btn-sm btn-outline-secondary" type="button" data-workflow-action="move-up" ${index === 0 ? 'disabled' : ''} aria-label="Move action up"><i class="fa-solid fa-arrow-up" aria-hidden="true"></i></button>
                <button class="btn btn-sm btn-outline-secondary" type="button" data-workflow-action="move-down" ${index === this.editorActions.length - 1 ? 'disabled' : ''} aria-label="Move action down"><i class="fa-solid fa-arrow-down" aria-hidden="true"></i></button>
                ${action.kind !== JOB_ACTION.WORKER || this.editorWorkerOptional ? '<button class="btn btn-sm btn-outline-danger" type="button" data-workflow-action="remove" aria-label="Remove action"><i class="fa-solid fa-trash" aria-hidden="true"></i></button>' : ''}
            </div>`;

        if (action.kind === JOB_ACTION.WORKER) {
            return `<article class="job-action-card job-worker-card" data-job-action-id="${actionId}" data-kind="worker">
                <header><span class="job-action-number">${index + 1}</span><div><strong><i class="fa-solid fa-robot" aria-hidden="true"></i>Agent</strong><small>The CLI, its settings and the message it starts with.</small></div>${controls}</header>
                <div class="job-action-body"><div data-inline-worker></div></div>
            </article>`;
        }

        if (isCheck(action.kind)) return `<article class="job-action-card" data-job-action-id="${actionId}">
            <header><span class="job-action-number">${index + 1}</span><strong>${checkName(action.kind)}</strong>${controls}</header>${checkFields(action)}</article>`;
        const runtime = Number(action.scriptRuntime);
        const argumentRows = (action.arguments || []).map((argument, argumentIndex) => `
            <div class="job-script-argument-row">
                <input class="form-control" data-action-field="argument" data-argument-index="${argumentIndex}" value="${this.escape(argument)}" aria-label="Argument ${argumentIndex + 1}" placeholder="One argv value">
                <button class="btn btn-outline-danger" type="button" data-workflow-action="remove-argument" data-argument-index="${argumentIndex}" aria-label="Remove argument"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>
            </div>`).join('');

        return `
            <article class="job-action-card" data-job-action-id="${actionId}" data-kind="script">
                <header><span class="job-action-number">${index + 1}</span><div><strong><i class="fa-solid fa-code" aria-hidden="true"></i>Script</strong><small>Runs with an explicit interpreter—never as a concatenated shell command.</small></div>${controls}</header>
                <div class="job-action-body job-script-fields">
                    ${this.renderScriptStepPicker(action, actionId)}
                    <div>
                        <label class="form-label">Runtime</label>
                        <select class="form-select" data-action-field="scriptRuntime">
                            ${Object.entries(SCRIPT_RUNTIME_META).map(([value, meta]) => `<option value="${value}" ${runtime === Number(value) ? 'selected' : ''}>${meta.label} (${meta.extension})</option>`).join('')}
                        </select>
                    </div>
                    <div>
                        <label class="form-label">Working directory <small>Optional</small></label>
                        <div class="input-group"><input class="form-control" data-action-field="workingDirectory" value="${this.escape(action.workingDirectory)}" placeholder="Repository root"><button class="btn btn-outline-secondary" type="button" data-workflow-action="browse-working-directory">Browse…</button></div>
                        <small>Stored relative to the repository, or to the agent's copy of it.</small>
                    </div>
                    <div>
                        <label class="form-label">Action time limit <small>Optional</small></label>
                        <div class="input-group"><input class="form-control" type="number" min="1" max="3600" data-action-field="timeoutSeconds" value="${action.timeoutSeconds ?? ''}" placeholder="No action limit"><span class="input-group-text">sec</span></div>
                    </div>
                    <div class="job-script-arguments">
                        <div class="job-script-arguments-header"><label class="form-label mb-0">Arguments <small>Optional</small></label><button class="btn btn-sm btn-outline-secondary" type="button" data-workflow-action="add-argument"><i class="fa-solid fa-plus me-1" aria-hidden="true"></i>Add argument</button></div>
                        <small>Each row is one argv value; spaces and shell characters are passed literally.</small>
                        <div data-script-arguments>${argumentRows || '<span class="job-script-no-arguments">No arguments</span>'}</div>
                    </div>
                </div>
            </article>`;
    }

    updateEditorActionField(event) {
        const row = event.target?.closest?.('[data-job-action-id]');
        const field = event.target?.dataset?.actionField;
        if (!row || !field) return;
        const action = this.editorActions.find(item => item.id === row.dataset.jobActionId);
        if (!action) return;

        if (field === 'checkScope') {
            action.arguments = event.target.value === 'range' ? ['range', '', ''] : [event.target.value];
            this.renderEditorActions(); return;
        }
        if (field === 'checkBase' || field === 'checkHead') {
            action.arguments[field === 'checkBase' ? 1 : 2] = event.target.value.trim(); return;
        }
        if (field === 'argument') {
            const index = Number(event.target.dataset.argumentIndex);
            if (Number.isInteger(index) && index >= 0 && index < action.arguments.length) {
                action.arguments[index] = event.target.value;
            }
            return;
        }
        if (field === 'scriptChoice') {
            // A select fires both input and change; the change re-renders the matching runtime.
            if (event.type !== 'change' || !event.target.value) return;
            this.setScriptStepPath(action, event.target.value);
            this.renderEditorActions();
            return;
        }
        if (field === 'scriptRuntime') action.scriptRuntime = Number(event.target.value);
        else if (field === 'timeoutSeconds') action.timeoutSeconds = event.target.value ? Number(event.target.value) : null;
        else action[field] = event.target.value;
    }

    async handleEditorActionClick(event) {
        const button = event.target?.closest?.('[data-workflow-action]');
        if (!button) return;
        const row = button.closest('[data-job-action-id]');
        const index = row ? this.editorActions.findIndex(item => item.id === row.dataset.jobActionId) : -1;
        const action = index >= 0 ? this.editorActions[index] : null;

        switch (button.dataset.workflowAction) {
            case 'move-up':
                if (index > 0) [this.editorActions[index - 1], this.editorActions[index]] = [this.editorActions[index], this.editorActions[index - 1]];
                return this.renderEditorActions();
            case 'move-down':
                if (index >= 0 && index < this.editorActions.length - 1) [this.editorActions[index + 1], this.editorActions[index]] = [this.editorActions[index], this.editorActions[index + 1]];
                return this.renderEditorActions();
            case 'remove':
                // A removed optional Worker keeps its detached draft, so adding it back restores it.
                if (index >= 0 && (action.kind !== JOB_ACTION.WORKER || this.editorWorkerOptional)) this.editorActions.splice(index, 1);
                return this.renderEditorActions();
            case 'add-argument':
                if (action && action.arguments.length < 64) action.arguments.push('');
                return this.renderEditorActions();
            case 'remove-argument': {
                const argumentIndex = Number(button.dataset.argumentIndex);
                if (action && Number.isInteger(argumentIndex)) action.arguments.splice(argumentIndex, 1);
                return this.renderEditorActions();
            }
            case 'browse-script':
                return this.pickEditorScript(action, button);
            case 'new-script':
                return this.newScriptForStep(action);
            case 'browse-working-directory':
                return this.pickEditorWorkingDirectory(action, button);
            default:
                return undefined;
        }
    }

    async pickEditorScript(action, trigger) {
        if (!action || typeof this.app.pickFileSystemEntry !== 'function') {
            this.app.showError('The file picker is not available in this window.');
            return;
        }
        const picked = await this.app.pickFileSystemEntry({
            mode: 'file',
            title: 'Choose a repository script',
            initialPath: this.currentProjectPath(),
            filters: [{ label: 'Automation scripts (*.py, *.ps1, *.sh)', extensions: ['py', 'ps1', 'sh'] }],
            triggerElement: trigger instanceof HTMLElement ? trigger : undefined
        });
        if (!picked || picked.canceled || !picked.path) return;
        this.setScriptStepPath(action, this.repositoryRelativePath(picked.path) ?? picked.path);
        this.renderEditorActions();
    }

    async pickEditorWorkingDirectory(action, trigger) {
        if (!action || typeof this.app.pickFileSystemEntry !== 'function') {
            this.app.showError('The folder picker is not available in this window.');
            return;
        }
        const picked = await this.app.pickFileSystemEntry({
            mode: 'directory',
            title: 'Choose script working directory',
            initialPath: this.currentProjectPath(),
            triggerElement: trigger instanceof HTMLElement ? trigger : undefined
        });
        if (!picked || picked.canceled || !picked.path) return;
        action.workingDirectory = picked.path;
        this.renderEditorActions();
    }

    disposeWorkerEditor() {
        this.workerEditor?.dispose();
        this.workerEditor = null;
    }

    mountWorkerEditor() {
        const host = this.editorRoot?.querySelector('[data-inline-worker]');
        if (!host) return;
        if (!this.workerEditor) {
            const element = document.createElement('div');
            host.append(element);
            // Same record, same flag, same derived name as always: the inline setup is an
            // Automation Worker to the server. Only the surrounding words changed.
            this.workerEditor = this.app.environmentController.mountEditor(element, {
                env: this.editorEnvironment,
                cliSettings: this.editorCliSettings,
                automationWorker: true,
                variant: 'agent',
                getName: () => automationWorkerName(this.editorRoot?.querySelector('#job-name')?.value, this.environments)
            });
            const message = element.querySelector('#env-initial-message');
            message.required = true;
            message.rows = 5;
            message.placeholder = 'What this agent should do each time it runs.';
        } else {
            host.append(this.workerEditor.element);
        }
    }

    resetEditor() {
        this.editorRequestGeneration += 1;
        this.disposeWorkerEditor();
        this.editorRoot = null;
        this.editorEnvironment = null;
        this.editorCliSettings = null;
        this.activeEditorJob = null;
        this.activeEditorSource = null;
        this.activeEditorPreferredTrigger = null;
        this.editorActions = [];
        this.editorWorkerOptional = false;
        this.editorScriptChoices = null;
    }

    closeEditor() {
        const ownsModal = this.editorRoot?.isConnected;
        this.resetEditor();
        if (ownsModal) this.app.closeModal();
    }

    captureEditorState(form, { validate = false, draftEnvironment = null } = {}) {
        const name = form.querySelector('#job-name').value.trim();
        if (validate && !name) {
            this.app.showError(`Give this ${this.page.noun} a name.`);
            form.querySelector('#job-name').focus?.();
            return null;
        }
        let actions = this.editorActions.map(action => ({
            ...action,
            arguments: [...(action.arguments || [])]
        }));

        // Compatibility for tests and for an editor restored from a pre-workflow DOM snapshot.
        // The real workflow editor keeps this.editorActions authoritative as fields change.
        if (actions.length === 0 && form.querySelector('#job-llm-selection')) {
            const parsedSelection = parseLlmSelection(
                form.querySelector('#job-llm-selection')?.tomselect?.getValue?.() || form.querySelector('#job-llm-selection')?.value,
                this.environments);
            if (parsedSelection.kind === 'environment') {
                actions = [{ id: this.newActionId(), kind: JOB_ACTION.WORKER, environmentId: parsedSelection.envId }];
            }
        }

        if (actions.length === 0) {
            if (validate) this.app.showError(this.mode === 'agents' ? 'Add the agent step before saving.' : 'Add at least one script or check.');
            return null;
        }
        if (actions.length > 20) {
            if (validate) this.app.showError(`An ${this.page.noun} can have at most 20 steps.`);
            return null;
        }
        if (actions.filter(action => action.kind === JOB_ACTION.WORKER).length > 1) {
            if (validate) this.app.showError('An agent runs one CLI; remove the extra agent step.');
            return null;
        }

        let selectedEnvironment = null;
        let llm = 0;
        let prompt = '';
        const normalizedActions = [];
        for (const action of actions) {
            if (action.kind === JOB_ACTION.WORKER) {
                selectedEnvironment = draftEnvironment || this.findEnvironment(action.environmentId);
                if (!selectedEnvironment) {
                    if (validate) this.app.showError('This agent’s saved CLI setup no longer exists. Choose the CLI again.');
                    return null;
                }
                llm = getJobLlmForCli(selectedEnvironment.cli);
                prompt = (selectedEnvironment.customPrompt || '').trim();
                if (llm === null) {
                    if (validate) this.app.showError('This agent uses a CLI that cannot run on a schedule.');
                    return null;
                }
                if (!prompt) {
                    // The server refuses an Automation whose agent has no message; same rule, earlier.
                    if (validate) this.app.showError('Give the agent a starting message before saving.');
                    return null;
                }
                normalizedActions.push({
                    id: action.id,
                    kind: JOB_ACTION.WORKER,
                    environmentId: Number(selectedEnvironment.id)
                });
                continue;
            }

            if (isCheck(action.kind)) {
                if (!validCheckScope(action.arguments)) {
                    if (validate) this.app.showError('Choose a check scope and full 40-character base/head commit SHAs for a range.');
                    return null;
                }
                normalizedActions.push({ id: action.id, kind: action.kind, arguments: [...action.arguments] });
                continue;
            }
            const scriptPath = String(action.scriptPath || '').trim();
            const runtime = Number(action.scriptRuntime);
            const runtimeMeta = SCRIPT_RUNTIME_META[runtime];
            if (!scriptPath) {
                if (validate) this.app.showError('Choose a file for every script action.');
                return null;
            }
            if (!runtimeMeta || !scriptPath.toLowerCase().endsWith(runtimeMeta.extension)) {
                if (validate) this.app.showError(`${runtimeMeta?.label || 'The selected runtime'} requires a ${runtimeMeta?.extension || 'supported'} script file.`);
                return null;
            }
            const timeoutSeconds = action.timeoutSeconds == null || action.timeoutSeconds === ''
                ? null
                : Number(action.timeoutSeconds);
            if (timeoutSeconds !== null && (!Number.isInteger(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 3600)) {
                if (validate) this.app.showError('Each script time limit must be between 1 and 3600 seconds, or blank.');
                return null;
            }
            const scriptArguments = (action.arguments || []).map(value => String(value ?? ''));
            if (scriptArguments.length > 64 || scriptArguments.some(value => value.length > 8000 || value.includes('\0'))) {
                if (validate) this.app.showError('A script can have at most 64 arguments, each no longer than 8000 characters.');
                return null;
            }

            normalizedActions.push({
                id: action.id,
                kind: JOB_ACTION.SCRIPT,
                scriptPath,
                scriptRuntime: runtime,
                arguments: scriptArguments,
                workingDirectory: String(action.workingDirectory || '').trim() || null,
                timeoutSeconds,
                approvedHash: action.approvedHash || null
            });
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
        const beforeCommit = form.querySelector('#job-trigger-precommit')?.checked === true;
        const afterCommit = form.querySelector('#job-trigger-commit')?.checked === true;
        if (validate && beforeCommit && afterCommit) {
            this.app.showError('Choose either Before each commit or After each commit.');
            return null;
        }
        if (beforeCommit) triggers.push({ kind: TRIGGER.PRECOMMIT });
        if (afterCommit) triggers.push({ kind: TRIGGER.COMMIT });

        const projectPath = this.currentProjectPath();
        if (validate && !projectPath) {
            this.app.showError(`Open VibeRails in a Git repository before creating an ${this.page.noun}.`);
            return null;
        }

        return {
            name,
            description: form.querySelector('#job-description')?.value.trim() ?? this.activeEditorSource?.description ?? '',
            projectPath,
            llm,
            environmentId: selectedEnvironment ? Number(selectedEnvironment.id) : null,
            prompt,
            // A blank field is the simple default: no time limit.
            timeoutMinutes: form.querySelector('#job-timeout')?.value
                ? Number(form.querySelector('#job-timeout').value)
                : null,
            enabled: form.querySelector('#job-enabled').checked,
            triggers,
            launchMinimized: form.querySelector('#job-launch-minimized')?.checked === true,
            actions: normalizedActions
        };
    }

    async saveJob(event, existingJob) {
        event.preventDefault();
        const form = event.currentTarget;
        const submit = form.querySelector('[type="submit"]');
        if (submit.disabled) return;
        const worker = this.editorActions.find(action => action.kind === JOB_ACTION.WORKER);
        const inlineEditor = worker ? this.workerEditor : null;
        const draft = inlineEditor?.read();
        const draftEnvironment = draft ? { id: worker.environmentId, cli: draft.cli, customPrompt: draft.payload.customPrompt } : null;
        let payload = this.captureEditorState(form, { validate: true, draftEnvironment });
        if (!payload) return;

        submit.disabled = true;
        submit.textContent = existingJob ? 'Saving…' : 'Creating…';
        form.inert = true;
        try {
            if (inlineEditor) {
                const environment = await inlineEditor.save();
                if (!form.isConnected) return;
                this.environments = [...this.environments.filter(item => item.id !== environment.id), environment];
                this.app.environmentController.setEnvironments(this.environments);
                // Keep the draft mounted after a failed job save; save() remembers the created
                // Worker and writes changes to it on retry instead of creating a duplicate.
                payload.environmentId = Number(environment.id);
                payload.actions = payload.actions.map(action => action.kind === JOB_ACTION.WORKER
                    ? { ...action, environmentId: Number(environment.id) } : action);
            }
            await this.app.apiCall(existingJob ? `/api/v1/jobs/${existingJob.id}` : '/api/v1/jobs', existingJob ? 'PUT' : 'POST', payload);
            if (!form.isConnected) return;
            this.closeEditor();
            this.app.showToast(this.kindTitle(), existingJob ? 'Saved.' : 'Created.', 'success');
            await this.refreshAll({ quiet: true });
        } catch (error) {
            if (form.isConnected) this.app.showError(error?.message || `Could not save the ${this.page.noun}.`);
        } finally {
            form.inert = false;
            submit.disabled = false;
            submit.textContent = existingJob ? `Save ${this.page.noun}` : `Create ${this.page.noun}`;
        }
    }

    /**
     * Queue the automation through the scheduler, which honors the saved native/tab choice
     * for manual, scheduled and retried runs. The toast acknowledges queuing; history and
     * the Automation terminal event report the eventual launch without stealing focus.
     */
    async runNow(jobId, button) {
        return this.withBusy(button, 'Queuing…', async () => {
            const response = await this.app.apiCall(
                `/api/v1/jobs/${jobId}/run`,
                'POST',
                null,
                { preferErrorResponseMessage: true }
            );
            this.app.showToast(this.kindTitle(this.jobs.find(job => Number(job.id) === Number(jobId))), response?.message || 'Queued.', 'success');
            await this.refreshRuns({ quiet: true });
        });
    }

    /**
     * Run an automation from outside the jobs view (the nav launcher). The jobs view populates
     * this.jobs when it loads; from the nav it may be empty or stale, and the run history the
     * toast points at should be current when the user opens the page.
     */
    async launchFromNav(jobId) {
        const id = Number(jobId);
        if (!Number.isFinite(id)) return;
        if (!this.jobs.some(job => Number(job.id) === id)) {
            await this.refreshAll({ quiet: true });
        }
        return this.runNow(id, null);
    }

    async toggleStandaloneAgent(environmentId, button) {
        const environment = this.findEnvironment(environmentId);
        if (!environment || button?.disabled) return;
        // A successful first toggle is retained even if the subsequent refresh fails.
        const existing = this.jobs.find(job => Number(workerActionOf(job)?.environmentId) === Number(environmentId));
        if (existing) return this.toggleJob(existing.id, button);
        const llm = getJobLlmForCli(environment.cli);
        if (llm === null || !this.currentProjectPath()) {
            this.app.showError('This agent needs a supported CLI and an open project before changing its state.');
            return;
        }
        this.pendingAgentToggles ??= new Set();
        if (this.pendingAgentToggles.has(environmentId)) return;
        this.pendingAgentToggles.add(environmentId);
        try {
            return await this.withBusy(button, 'Disabling…', async () => {
                // Enabled state lives on Jobs. An on-demand setup gets its first Job only
                // when the user changes that state; no schedule or setup rewrite is needed.
                const job = await this.app.apiCall('/api/v1/jobs', 'POST', {
                    name: environment.name, projectPath: this.currentProjectPath(), llm,
                    environmentId: Number(environment.id), prompt: environment.customPrompt,
                    timeoutMinutes: null, enabled: false, launchMinimized: false,
                    triggers: [], actions: [{ kind: JOB_ACTION.WORKER, environmentId: Number(environment.id) }]
                }, { preferErrorResponseMessage: true });
                this.jobMutationGeneration++;
                this.jobs.push(job);
                this.renderJobs();
                await this.refreshAll({ quiet: true });
            });
        } finally {
            this.pendingAgentToggles.delete(environmentId);
        }
    }

    async toggleJob(jobId, button) {
        const job = this.jobs.find(item => item.id === jobId);
        if (!job) return;
        const workerAction = this.normalizeJobActions(job).find(action => action.kind === JOB_ACTION.WORKER);
        const environment = workerAction ? this.findEnvironment(workerAction.environmentId) : null;
        if (workerAction && !environment) {
            this.app.showError('This agent’s saved CLI setup no longer exists. Edit it before changing its state.');
            return;
        }
        const llm = environment ? getJobLlmForCli(environment.cli) : 0;
        if (environment && llm === null) {
            this.app.showError('This agent uses a CLI that cannot run on a schedule.');
            return;
        }
        const payload = {
            name: job.name, projectPath: this.currentProjectPath(), llm,
            environmentId: environment ? Number(environment.id) : null,
            prompt: environment?.customPrompt || '',
            timeoutMinutes: job.timeoutMinutes,
            enabled: !job.enabled,
            launchMinimized: job.launchMinimized === true,
            triggers: (job.triggers || []).map(({ kind, scheduleKind, intervalMinutes, localTime, daysOfWeekMask, timeZoneId }) => ({ kind, scheduleKind, intervalMinutes, localTime, daysOfWeekMask, timeZoneId }))
        };
        return this.withBusy(button, job.enabled ? 'Disabling…' : 'Enabling…', async () => {
            const savedJob = await this.app.apiCall(`/api/v1/jobs/${jobId}`, 'PUT', payload);
            // Keep the confirmed state even if a follow-up refresh fails.
            this.jobMutationGeneration++;
            this.jobs = this.jobs.map(item => item.id === jobId ? savedJob : item);
            this.renderJobs();
            await this.refreshAll({ quiet: true });
        });
    }

    async deleteJob(jobId) {
        const job = this.jobs.find(item => item.id === jobId);
        if (!job) return;
        const confirmed = await this.confirm({
            title: `Delete “${job.name}”?`,
            message: `The ${this.page.noun} is removed. Its run history and recordings are kept.`,
            confirmLabel: 'Delete',
            danger: true
        });
        if (!confirmed) return;
        try {
            await this.app.apiCall(`/api/v1/jobs/${jobId}`, 'DELETE');
            this.app.showToast(this.kindTitle(job), 'Deleted.', 'info');
            await this.refreshAll({ quiet: true });
        } catch (error) { this.app.showError(error?.message || `Could not delete the ${this.page.noun}.`); }
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

        const actions = Array.isArray(run?.actions) ? run.actions : [];
        const lastScreen = Array.isArray(run?.lastScreen) ? run.lastScreen : null;
        // Legacy Worker-only runs predate per-action snapshots. Preserve their direct replay path;
        // workflow runs open a detail surface so script output and every action remain visible,
        // and so does any run that ended badly, so its last screen is the first thing shown.
        if (actions.length === 0 && run?.sessionId && !lastScreen) {
            try {
                await showReplayModal(run.sessionId);
                return;
            } catch (error) {
                this.app.showError(error?.message || 'Could not replay this run.');
                return;
            }
        }

        const status = RUN_STATUS[Number(run?.status)] || { label: 'Unknown', tone: 'neutral' };
        const active = Number(run?.status) <= 1;
        const kindTitle = this.kindTitle(this.jobs.find(job => Number(job.id) === Number(run?.jobId)) || null);
        const replaySessionId = run?.terminalSessionId || (actions.length === 0 ? run?.sessionId : null);
        this.app.showModal(`${kindTitle} run`, `
            <div class="job-run-detail">
                <div class="job-run-detail-summary">
                    <div><strong>${this.escape(run?.jobName || `${kindTitle} run`)}</strong><span class="job-run-status" data-tone="${status.tone}">${status.label}</span></div>
                    <small>${this.escape(run?.projectPath || '')}</small>
                    ${run?.errorMessage ? `<p class="text-danger mb-0 mt-2">${this.escape(run.errorMessage)}</p>` : ''}
                    <p class="text-muted mb-0 mt-2">${actions.length > 0
                        ? `${actions.length} ${actions.length === 1 ? 'action' : 'actions'} · ${this.escape(this.runDuration(run))}`
                        : active
                            ? 'This run is starting — its recorded terminal will be ready to watch here shortly.'
                            : replaySessionId
                                ? this.escape(this.runDuration(run))
                                : 'This run has no recorded terminal to watch.'}</p>
                </div>
                ${this.renderLastScreen(lastScreen, status.tone)}
                ${replaySessionId ? `<p class="mt-2"><button class="btn btn-sm btn-outline-secondary" type="button" data-run-action-session="${this.escape(replaySessionId)}"><i class="fa-solid fa-play me-1" aria-hidden="true"></i>${run?.terminalSessionId ? 'Replay workflow terminal' : 'Replay terminal'}</button></p>` : ''}
                ${run?.terminalSessionId || run?.sessionId ? `<p class="small">Session: <code>${this.escape(run.terminalSessionId || run.sessionId)}</code> <button class="btn btn-sm btn-link p-0" type="button" data-copy-session="${this.escape(run.terminalSessionId || run.sessionId)}" aria-label="Copy session ID"><i class="fa-regular fa-copy" aria-hidden="true"></i></button></p>` : ''}
                ${actions.length > 0 ? `<div class="job-run-actions-detail">${actions.map((action, index) => this.renderRunActionDetail(action, index)).join('')}</div>` : ''}
                <div class="d-flex justify-content-end gap-2 mt-3">
                    ${active ? '<button class="btn btn-outline-danger" type="button" data-run-cancel>Stop run</button>' : '<button class="btn btn-outline-secondary" type="button" data-run-retry>Run again</button>'}
                    <button class="btn btn-primary" type="button" data-action="close-modal">Close</button>
                </div>
            </div>`);
        const detail = document.querySelector('.job-run-detail');
        // The newest line is the one that explains the stop; open the screen scrolled to it.
        const screen = detail?.querySelector('[data-run-last-screen] pre');
        if (screen) screen.scrollTop = screen.scrollHeight;
        detail?.querySelector('[data-copy-session]')?.addEventListener('click', event => this.copySessionId(event.currentTarget.dataset.copySession));
        detail?.querySelector('[data-run-cancel]')?.addEventListener('click', async event => {
            await this.cancelRun(runId, event.currentTarget);
            this.app.closeModal();
            await this.refreshRuns({ quiet: true });
        });
        detail?.querySelector('[data-run-retry]')?.addEventListener('click', event => this.retryRun(runId, event.currentTarget));
        detail?.querySelectorAll('[data-run-action-session]')?.forEach(button => {
            button.addEventListener('click', async () => {
                try {
                    await showReplayModal(button.dataset.runActionSession);
                } catch (error) {
                    this.app.showError(error?.message || 'Could not replay this agent step.');
                }
            });
        });
    }

    // The server sends the bottom of the recorded screen only for a run that ended without
    // succeeding (null otherwise). An empty screen is itself an answer: the process went before
    // it printed anything, so there is nothing more to find in the recording.
    renderLastScreen(lines, tone) {
        if (!Array.isArray(lines)) return '';
        if (lines.length === 0) {
            return '<p class="text-muted small mb-2" data-run-last-screen>Its terminal recorded no output before the run stopped.</p>';
        }
        return `
            <article class="job-run-action-detail mb-3" data-tone="${this.escape(tone)}" data-run-last-screen>
                <details open><summary>Last screen before the run stopped</summary><pre>${this.escape(lines.join('\n'))}</pre></details>
            </article>`;
    }

    renderRunActionDetail(action, index) {
        const kind = Number(action?.kind);
        const status = RUN_ACTION_STATUS[Number(action?.status)] || { label: 'Unknown', tone: 'neutral' };
        const runtime = SCRIPT_RUNTIME_META[Number(action?.scriptRuntime)];
        const title = kind === JOB_ACTION.WORKER
            ? `Agent — ${action?.environmentName || getLlmName(Number(action?.llm))}`
            : isCheck(kind) ? `${checkName(kind)} — ${(action.arguments || []).join(' ')}` : `${runtime?.label || 'Script'} — ${action?.scriptPath || 'Unknown script'}`;
        const detail = this.runDetail(action);
        const stdout = String(action?.standardOutput || '');
        const stderr = String(action?.standardError || '');
        const argumentsText = kind === JOB_ACTION.SCRIPT && Array.isArray(action?.arguments) && action.arguments.length > 0
            ? `<code class="job-run-action-argv">${action.arguments.map(value => this.escape(value)).join('</code><code class="job-run-action-argv">')}</code>`
            : '';

        return `
            <article class="job-run-action-detail" data-tone="${status.tone}">
                <header>
                    <span class="job-action-number">${index + 1}</span>
                    <div><strong>${this.escape(title)}</strong>${detail ? `<small>${this.escape(detail)}</small>` : ''}</div>
                    <span class="job-run-status" data-tone="${status.tone}">${status.label}</span>
                </header>
                ${argumentsText ? `<div class="job-run-action-arguments"><span>argv</span>${argumentsText}</div>` : ''}
                ${kind === JOB_ACTION.WORKER && action?.sessionId
                    ? `<button class="btn btn-sm btn-outline-secondary" type="button" data-run-action-session="${this.escape(action.sessionId)}"><i class="fa-solid fa-play me-1" aria-hidden="true"></i>Replay agent terminal</button>`
                    : ''}
                ${stdout ? `<details><summary>Standard output</summary><pre>${this.escape(stdout)}</pre></details>` : ''}
                ${stderr ? `<details><summary>Standard error</summary><pre data-tone="danger">${this.escape(stderr)}</pre></details>` : ''}
            </article>`;
    }

    formatTrigger(trigger) {
        const kind = Number(trigger.kind);
        if (kind === TRIGGER.PRECOMMIT) return 'Before each commit';
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
        const originalChecked = button?.getAttribute?.('aria-checked');
        if (button) {
            button.disabled = true;
            if (button.classList?.contains?.('job-switch')) {
                // The switch is the state read-out, so it flips right away and then goes quiet:
                // swapping it for "Enabling…" would pull the control out from under the pointer.
                // The finally below puts it back if the save fails; a save that works re-renders
                // the card anyway.
                const next = button.getAttribute?.('aria-checked') !== 'true';
                button.setAttribute?.('aria-checked', String(next));
                const switchText = button.querySelector?.('.job-switch-text');
                if (switchText) switchText.textContent = next ? 'Enabled' : 'Disabled';
                button.classList.add('is-busy');
                button.setAttribute?.('title', label);
            // Icon actions are a fixed square, so a text label ("Queueing…") overflows
            // the box. Spin in place instead and put the wording in the tooltip, which is
            // already where an icon-only button explains itself.
            } else if (button.classList?.contains?.('job-icon-action')) {
                button.innerHTML = '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>';
                button.setAttribute?.('title', label);
            } else {
                button.textContent = label;
            }
        }
        try { await operation(); }
        catch (error) { this.app.showError(error?.message || 'The action failed.'); }
        finally {
            if (button?.isConnected) {
                button.disabled = false;
                button.classList?.remove?.('is-busy');
                button.innerHTML = original;
                if (originalTitle !== null && originalTitle !== undefined) {
                    button.setAttribute?.('title', originalTitle);
                }
                // innerHTML restores the label but not the attribute the switch is painted from.
                if (originalChecked !== null && originalChecked !== undefined) {
                    button.setAttribute?.('aria-checked', originalChecked);
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
        const actions = this.normalizeJobActions(job);
        const workerAction = actions.find(action => action.kind === JOB_ACTION.WORKER);
        const environment = workerAction ? this.findEnvironment(workerAction.environmentId) : null;
        if (this.hasLocalReviewerTargets(environment?.reviewerRouting)) {
            this.app.showError('Portable recipes currently support built-in reviewer CLIs. Replace saved-setup reviewers with built-in CLIs before exporting; saved setups are local to this machine.');
            return;
        }
        if (workerAction && !environment) {
            this.app.showError('This agent needs its saved CLI setup before it can be exported.');
            return;
        }
        const cli = environment?.cli || '';
        const customArgs = environment?.customArgs || '';
        const prompt = (environment?.customPrompt || '').trim();
        const model = this.extractArg(customArgs, ['--model', '-m']) || '';
        const effort = this.extractArg(customArgs, ['--effort', '--reasoning-effort']) || this.extractConfig(customArgs, 'model_reasoning_effort') || '';
        const triggers = (job.triggers || []).map(({ kind, scheduleKind, intervalMinutes, localTime, daysOfWeekMask, timeZoneId }) =>
            ({ kind, scheduleKind, intervalMinutes, localTime, daysOfWeekMask, timeZoneId }));

        const recipe = {
            recipeVersion: 'V2',
            name: job.name,
            description: job.description || '',
            worker: environment ? {
                name: environment.name,
                llm: getLlmName(Number(getJobLlmForCli(cli) ?? job.llm)),
                cli,
                model,
                effort,
                customArgs,
                prompt,
                purpose: environment.purpose || 'work',
                reviewerRouting: environment.reviewerRouting || null,
                workspaceMode: Number(environment.workspaceMode) || 0
            } : null,
            actions: actions.map(action => action.kind === JOB_ACTION.WORKER
                ? { kind: JOB_ACTION.WORKER }
                : {
                    kind: Number(action.kind),
                    scriptPath: action.scriptPath,
                    scriptRuntime: Number(action.scriptRuntime),
                    arguments: [...(action.arguments || [])],
                    workingDirectory: action.workingDirectory || null,
                    timeoutSeconds: action.timeoutSeconds ?? null
                }),
            timeoutMinutes: job.timeoutMinutes,
            launchMinimized: job.launchMinimized === true,
            triggers
        };

        const whenText = triggers.length
            ? triggers.map(t => this.formatTrigger(t)).join(', ')
            : 'On demand only';
        const workflowText = recipe.actions.length
            ? recipe.actions.map((action, index) => action.kind === JOB_ACTION.WORKER
                ? `${index + 1}. Agent${environment ? ` — ${environment.name}` : ''}`
                : isCheck(action.kind) ? `${index + 1}. ${checkName(action.kind)} — ${action.arguments.join(' ')}` : `${index + 1}. ${SCRIPT_RUNTIME_META[action.scriptRuntime]?.label || 'Script'} — \`${action.scriptPath}\``).join('\n')
            : '_No steps_';
        const markdown = `# VibeRails Recipe — ${job.name}

- **Recipe version:** V2
- **Agent:** ${environment ? `${recipe.worker.llm}${model ? ` · ${model}` : ''}${effort ? ` · ${effort}` : ''}` : 'none (an Automation: scripts and checks only)'}
- **Runs:** ${whenText}
- **Timeout:** ${job.timeoutMinutes ? `${job.timeoutMinutes} min` : 'none'}

## Steps

${workflowText}

${environment ? `## Starting message

${prompt ? prompt.split('\n').map(line => `> ${line}`).join('\n') : '> _(none)_'}\n` : ''}

Import this file from the **Agents** or **Automations** page in VibeRails. Script paths are resolved and hash-pinned from the local repository during import; script contents and machine-specific approvals are never embedded in the recipe.

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
        this.app.showToast('Recipe exported', `Saved ${slug}.recipe.md — commit it to share this ${this.kindTitle(job).toLowerCase()}.`, 'success');
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

    // ----- Import from another repository on this machine (VB-31) -----
    // The catalog is built server-side because GET /api/v1/environments is project-filtered, so
    // the browser can never see another repository's Worker. The import runs server-side too:
    // POST /api/v1/jobs/import copies scripts this repository lacks, reuses or clones the Worker
    // (steps included, {{step:id}} tokens remapped), and creates the Automation disabled. This is
    // therefore a picker that feeds the same review modal a recipe file goes through.

    async openImportFromRepository() {
        let catalog;
        try {
            catalog = await this.app.apiCall('/api/v1/jobs/catalog', 'GET', null,
                { showLoading: false, preferErrorResponseMessage: true });
        } catch (error) {
            this.app.showError(error?.message || `Could not list the ${this.page.noun}s on this machine.`);
            return;
        }
        this.importCatalog = Array.isArray(catalog?.projects) ? catalog.projects : [];
        this.app.showModal('Import from another repository', this.renderImportPicker(this.importCatalog, ''));
        this.bindImportPicker();
    }

    renderImportPicker(projects, query) {
        const repository = this.currentProjectPath().split(/[\\/]/).filter(Boolean).pop() || 'this repository';
        const agents = this.mode === 'agents';
        return `
            <div class="job-import-picker">
                <p class="text-muted small mb-0">
                    Copy an ${this.page.noun} from another repository on this machine into <strong>${this.escape(repository)}</strong>.
                    ${agents ? 'Its CLI setup comes along, and scripts this repository lacks are copied from the source.' : 'Scripts this repository lacks are copied from the source.'}
                    The copy is independent: later changes in either repository do not affect the other.
                </p>
                <div class="job-import-search">
                    <i class="fa-solid fa-magnifying-glass" aria-hidden="true"></i>
                    <input type="search" class="form-control" id="job-import-search" autocomplete="off"
                           placeholder="Search ${this.page.noun}s, scripts or repositories"
                           aria-label="Search ${this.page.noun}s" value="${this.escape(query)}">
                </div>
                <div class="job-import-groups" data-job-import-groups>${this.renderImportGroups(projects, query)}</div>
            </div>`;
    }

    renderImportGroups(projects, query) {
        const needle = String(query || '').trim().toLowerCase();
        // Each page imports its own kind: an entry with an agent setup is an Agent.
        const forThisPage = entry => Boolean(entry.worker) === (this.mode === 'agents');
        const matches = entry => forThisPage(entry) && (!needle || this.importSearchText(entry).includes(needle));
        const groups = (projects || [])
            .map(group => ({ ...group, automations: (group.automations || []).filter(matches) }))
            .filter(group => group.automations.length > 0);
        if (groups.length === 0) {
            return `<div class="job-import-empty">${(projects || []).every(group => !(group.automations || []).some(forThisPage))
                ? `No other repository on this machine has an ${this.page.noun} yet.`
                : 'Nothing matches that search.'}</div>`;
        }
        return groups.map(group => `
            <section class="job-import-group">
                <header class="job-import-group-header">
                    <i class="fa-solid ${group.directoryExists === false ? 'fa-folder' : 'fa-folder-open'}" aria-hidden="true"></i>
                    <span class="job-import-group-name">${this.escape(group.displayName || group.projectPath)}</span>
                    <span class="job-import-group-path" title="${this.escape(group.projectPath)}">${this.escape(group.projectPath)}</span>
                    ${group.directoryExists === false
                        ? '<span class="job-import-badge is-warning" title="The repository folder no longer exists, so scripts cannot be copied from it">Folder missing</span>'
                        : ''}
                </header>
                <div class="job-import-list">${group.automations.map(entry => this.renderImportEntry(entry)).join('')}</div>
            </section>`).join('');
    }

    renderImportEntry(entry) {
        const worker = entry.worker || null;
        const actions = Array.isArray(entry.actions) ? entry.actions : [];
        const scripts = actions.filter(action => Number(action.kind) === JOB_ACTION.SCRIPT);
        const toCopy = scripts.filter(action => !action.existsInTargetRepository && action.existsInSourceRepository).length;
        const model = worker ? this.extractArg(worker.customArgs, ['--model', '-m']) : '';
        const when = (entry.triggers || []).length
            ? (entry.triggers || []).map(t => this.formatTrigger(t)).join(', ')
            : 'On demand';
        const blocked = entry.canImport === false;
        const flags = [];
        if (worker) {
            flags.push(worker.reusableEnvironmentId != null
                ? '<span class="job-import-badge" title="A CLI setup with this name and CLI already exists here and will be reused">Setup reused</span>'
                : '<span class="job-import-badge is-info" title="The CLI setup is copied into this repository under a new name">Setup copied</span>');
        }
        if (toCopy > 0) {
            flags.push(`<span class="job-import-badge is-info">${toCopy} ${toCopy === 1 ? 'script' : 'scripts'} to copy</span>`);
        }
        if (blocked) {
            flags.push(`<span class="job-import-badge is-danger" title="${this.escape(entry.blocker || '')}">Cannot import</span>`);
        }
        return `
            <button type="button" class="job-import-entry${blocked ? ' is-blocked' : ''}"
                    data-job-import-source="${this.escape(String(entry.sourceJobId))}" ${blocked ? 'disabled' : ''}
                    title="${this.escape(blocked ? (entry.blocker || 'This automation cannot be imported.') : `Import ${entry.name}`)}">
                <span class="job-import-entry-main">
                    <span class="job-import-entry-name">${this.escape(entry.name)}</span>
                    <span class="job-import-entry-meta">
                        ${worker
                            ? `<span class="job-import-chip"><i class="fa-solid fa-robot" aria-hidden="true"></i>${this.escape(worker.name)} · ${this.escape(getLlmName(Number(worker.llm)))}${model ? ` · ${this.escape(model)}` : ''}</span>`
                            : '<span class="job-import-chip"><i class="fa-solid fa-list-check" aria-hidden="true"></i>Scripts and checks</span>'}
                        ${scripts.length ? `<span class="job-import-chip"><i class="fa-solid fa-file-code" aria-hidden="true"></i>${scripts.length} ${scripts.length === 1 ? 'script' : 'scripts'}</span>` : ''}
                        <span class="job-import-chip"><i class="fa-solid fa-clock" aria-hidden="true"></i>${this.escape(when)}</span>
                    </span>
                </span>
                <span class="job-import-entry-flags">
                    ${flags.join('')}
                    <i class="fa-solid fa-chevron-right" aria-hidden="true"></i>
                </span>
            </button>`;
    }

    importSearchText(entry) {
        return [
            entry.name,
            entry.projectDisplayName,
            entry.projectPath,
            entry.worker?.name,
            ...((entry.actions || []).map(action => action.scriptPath))
        ].filter(Boolean).join(' ').toLowerCase();
    }

    bindImportPicker() {
        const container = typeof document !== 'undefined' ? document.querySelector?.('.job-import-picker') : null;
        if (!container) return;
        const search = container.querySelector('#job-import-search');
        const groups = container.querySelector('[data-job-import-groups]');
        search?.addEventListener('input', () => {
            if (groups) groups.innerHTML = this.renderImportGroups(this.importCatalog, search.value);
        });
        container.addEventListener('click', event => {
            const button = event.target.closest?.('[data-job-import-source]');
            if (!button || button.disabled) return;
            const entry = this.findImportEntry(button.dataset.jobImportSource);
            if (entry) this.confirmImportRecipe(this.recipeFromCatalogEntry(entry), { importSource: entry });
        });
        search?.focus?.();
    }

    findImportEntry(sourceJobId) {
        const id = Number(sourceJobId);
        for (const group of this.importCatalog || []) {
            const entry = (group.automations || []).find(item => Number(item.sourceJobId) === id);
            if (entry) return entry;
        }
        return null;
    }

    /** The catalog entry in the shape the recipe review modal already understands. */
    recipeFromCatalogEntry(entry) {
        const worker = entry.worker || null;
        return {
            recipeVersion: 'V2',
            name: entry.name,
            description: entry.description || '',
            worker: worker ? {
                name: worker.name,
                llm: getLlmName(Number(worker.llm)),
                cli: worker.cli,
                model: this.extractArg(worker.customArgs, ['--model', '-m']) || '',
                effort: this.extractArg(worker.customArgs, ['--effort', '--reasoning-effort']) || this.extractConfig(worker.customArgs, 'model_reasoning_effort') || '',
                customArgs: worker.customArgs || '',
                prompt: worker.prompt || '',
                purpose: worker.purpose || 'work',
                    reviewerRouting: worker.reviewerRouting || null,
                workspaceMode: Number(worker.workspaceMode) || 0
            } : null,
            actions: (entry.actions || []).map(action => Number(action.kind) === JOB_ACTION.WORKER
                ? { kind: JOB_ACTION.WORKER }
                : {
                    kind: Number(action.kind),
                    scriptPath: action.scriptPath,
                    scriptRuntime: Number(action.scriptRuntime),
                    arguments: [...(action.arguments || [])],
                    workingDirectory: action.workingDirectory || null,
                    timeoutSeconds: action.timeoutSeconds ?? null
                }),
            timeoutMinutes: entry.timeoutMinutes ?? null,
            launchMinimized: entry.launchMinimized === true,
            triggers: Array.isArray(entry.triggers) ? entry.triggers : []
        };
    }

    async applyRepositoryImport(entry, { workerName, button }) {
        return this.withBusy(button, 'Importing…', async () => {
            const body = { sourceJobId: Number(entry.sourceJobId) };
            if (entry.worker && entry.worker.reusableEnvironmentId == null) {
                const name = String(workerName ?? entry.worker.suggestedCloneName ?? '').trim();
                if (!name) {
                    this.showImportError('Give the copied CLI setup a name.');
                    return;
                }
                body.workerName = name;
            }
            let response;
            try {
                response = await this.app.apiCall('/api/v1/jobs/import', 'POST', body,
                    { showLoading: false, preferErrorResponseMessage: true });
            } catch (error) {
                // Shown inside the modal so a taken Worker name (409) can be corrected in place.
                this.showImportError(error?.message || 'The import failed.');
                return;
            }
            this.app.closeModal();
            const copied = Array.isArray(response?.copiedScripts) ? response.copiedScripts.length : 0;
            const parts = ['Added disabled for review.'];
            if (response?.workerOutcome === 'created') parts.push(`CLI setup copied as “${response.workerName}”.`);
            else if (response?.workerOutcome === 'reused') parts.push(`Reusing the CLI setup “${response.workerName}”.`);
            if (copied > 0) parts.push(`${copied} ${copied === 1 ? 'script' : 'scripts'} copied into this repository.`);
            this.app.showToast(`${this.kindTitle()} imported`, parts.join(' '), 'success');
            await this.refreshAll({ quiet: true });
        });
    }

    showImportError(message) {
        const alert = typeof document !== 'undefined' ? document.querySelector?.('[data-recipe-error]') : null;
        if (!alert) {
            this.app.showError(message);
            return;
        }
        alert.textContent = message;
        alert.classList.remove('d-none');
    }

    parseRecipe(text) {
        const match = /<!--\s*viberails-recipe\s*([\s\S]*?)\s*viberails-recipe\s*-->/.exec(text || '');
        if (!match) return null;
        try {
            const recipe = JSON.parse(match[1]);
            return recipe && recipe.name && (recipe.cli || Array.isArray(recipe.actions)) ? recipe : null;
        } catch {
            return null;
        }
    }

    normalizeRecipe(recipe) {
        if (String(recipe?.recipeVersion || '').toUpperCase() === 'V2' && Array.isArray(recipe.actions)) {
            return {
                ...recipe,
                worker: recipe.worker && typeof recipe.worker === 'object' ? recipe.worker : null,
                actions: recipe.actions.map(action => Number(action?.kind) === JOB_ACTION.WORKER
                    ? { kind: JOB_ACTION.WORKER }
                    : {
                        kind: Number(action.kind),
                        scriptPath: String(action?.scriptPath || ''),
                        scriptRuntime: Number(action?.scriptRuntime),
                        arguments: Array.isArray(action?.arguments) ? action.arguments.map(value => String(value ?? '')) : [],
                        workingDirectory: action?.workingDirectory || null,
                        timeoutSeconds: action?.timeoutSeconds ?? null
                    })
            };
        }

        // V1 carried exactly one Worker in top-level fields. Converting it here keeps every old
        // recipe import path working while the rest of the UI deals only in ordered V2 actions.
        return {
            recipeVersion: 'V2',
            name: recipe?.name,
            worker: {
                name: recipe?.name,
                llm: recipe?.llm,
                cli: recipe?.cli,
                model: recipe?.model || '',
                effort: recipe?.effort || '',
                customArgs: recipe?.customArgs || '',
                prompt: recipe?.prompt || '',
                workspaceMode: 0
            },
            actions: [{ kind: JOB_ACTION.WORKER }],
            timeoutMinutes: recipe?.timeoutMinutes ?? null,
            launchMinimized: recipe?.launchMinimized === true,
            triggers: Array.isArray(recipe?.triggers) ? recipe.triggers : []
        };
    }

    /**
     * The review step for both import paths. A recipe file applies through applyRecipe (two
     * client calls). A catalog entry (`importSource`) applies through applyRepositoryImport, where
     * the server decides Worker reuse vs clone — the modal only adds the clone's name and shows,
     * per script, whether the file is already here, will be copied, or is missing everywhere.
     */
    hasLocalReviewerTargets(routing) {
        return routing && [...(routing.mappings || []).map(m => m?.reviewer), routing.fallback]
            .some(target => !target?.selection?.startsWith('base:'));
    }

    reviewerRoutingMatches(left, right) {
        // JSON responses include optional null/default fields that portable files may omit.
        const target = value => ({ selection: value?.selection?.trim().toLowerCase(), options: {
            model: value?.options?.model || '', effort: value?.options?.effort || '',
            mode: value?.options?.mode || '', yolo: value?.options?.yolo === true
        } });
        const policy = value => !value || value.mode === 'fixed' ? null : {
            mode: value.mode, fallback: target(value.fallback),
            mappings: (value.mappings || []).map(m => ({ source: m.sourceProvider?.toLowerCase(), reviewer: target(m.reviewer) }))
                .sort((a, b) => String(a.source).localeCompare(String(b.source)))
        };
        return JSON.stringify(policy(left)) === JSON.stringify(policy(right));
    }

    confirmImportRecipe(recipe, { importSource = null } = {}) {
        recipe = this.normalizeRecipe(recipe);
        const worker = recipe.worker;
        if (!importSource && this.hasLocalReviewerTargets(worker?.reviewerRouting)) {
            this.app.showError('This recipe references saved reviewer setups from another machine. Use built-in CLI mappings in portable recipes, then pick local setups after import.');
            return;
        }
        const cli = String(worker?.cli || '').toLowerCase();
        const existingEnv = importSource
            ? (worker && importSource.worker?.reusableEnvironmentId != null
                ? { id: importSource.worker.reusableEnvironmentId, name: importSource.worker.reusableEnvironmentName || worker.name }
                : null)
            : worker
                ? (this.environments || []).find(env => (env.name || '').toLowerCase() === String(worker.name).toLowerCase() && (env.cli || '').toLowerCase() === cli && (env.purpose || 'work') === (worker.purpose || 'work') && this.reviewerRoutingMatches(env.reviewerRouting, worker.reviewerRouting))
                : null;
        const whenText = (recipe.triggers || []).length
            ? (recipe.triggers || []).map(t => this.formatTrigger(t)).join(', ')
            : 'On demand only';
        // Preserve the exact strings applyRecipe will persist; trimming here could conceal leading
        // or trailing content while claiming that the user is reviewing the imported value.
        const customArgs = String(worker?.customArgs || '');
        const prompt = String(worker?.prompt || '');
        const scripts = (recipe.actions || []).filter(action => Number(action.kind) === JOB_ACTION.SCRIPT);
        const routingText = worker?.reviewerRouting ? JSON.stringify(worker.reviewerRouting, null, 2) : '';
        const hasReviewableContent = customArgs.trim().length > 0 || prompt.trim().length > 0 || scripts.length > 0 || Boolean(routingText);
        const sourceScripts = importSource
            ? (importSource.actions || []).filter(action => Number(action.kind) === JOB_ACTION.SCRIPT)
            : [];
        const scriptFate = index => {
            const source = sourceScripts[index];
            if (!source) return '';
            if (source.existsInTargetRepository) return '  — already in this repository';
            if (source.existsInSourceRepository) return '  — will be copied from the source repository';
            return '  — MISSING in both repositories';
        };
        const copyCount = sourceScripts.filter(action => !action.existsInTargetRepository && action.existsInSourceRepository).length;
        const cloneWorker = Boolean(importSource && worker && !existingEnv);
        const sourceLabel = importSource ? (importSource.projectDisplayName || importSource.projectPath || 'another repository') : '';
        this.app.showModal(importSource ? 'Import from another repository' : 'Import recipe', `
            <div class="job-recipe-import">
                ${importSource
                    ? `<p>Copy <strong>${this.escape(recipe.name)}</strong> from <strong>${this.escape(sourceLabel)}</strong> into this repository?</p>
                       <div class="job-recipe-source mb-2" title="${this.escape(importSource.projectPath || '')}"><i class="fa-solid fa-folder-open me-1" aria-hidden="true"></i>${this.escape(importSource.projectPath || '')}</div>`
                    : `<p>Import <strong>${this.escape(recipe.name)}</strong> into this repository?</p>`}
                <ul class="text-muted small">
                    <li>Steps: <strong>${recipe.actions.length}</strong></li>
                    ${worker ? `<li>Agent: <strong>${this.escape(worker.name)}</strong>${worker.model ? ` · ${this.escape(worker.model)}` : ''}${existingEnv ? ' <em>(its CLI setup already exists — will be reused)</em>' : cloneWorker ? ' <em>(its CLI setup is copied into this repository under the name below)</em>' : ''}</li>` : '<li>Automation: scripts and checks only, no LLM step</li>'}
                    ${scripts.length ? `<li>Scripts: ${scripts.map(action => this.escape(action.scriptPath || '(missing path)')).join(' → ')}</li>` : ''}
                    <li>Runs: ${this.escape(whenText)} · ${recipe.timeoutMinutes ? `${this.escape(String(recipe.timeoutMinutes))} min limit` : 'no time limit'}</li>
                </ul>
                <div class="alert alert-warning small" role="alert">
                    <strong>Review the steps before importing.</strong>
                    ${worker ? 'CLI arguments can change approval or sandbox permissions, and the starting message becomes instructions to the agent. ' : ''}
                    ${scripts.length && !importSource ? `Script paths refer to files in this local repository. VibeRails verifies and hash-pins their current contents during import; the ${this.page.noun} starts disabled. ` : ''}
                    ${scripts.length && importSource ? `${copyCount > 0 ? `${copyCount} ${copyCount === 1 ? 'script is' : 'scripts are'} copied into this working tree from the source repository; existing files here are never overwritten. ` : 'Every script already exists in this repository and is used as it is here. '}VibeRails hash-pins the copies in this repository; the ${this.page.noun} starts disabled. ` : ''}
                    ${worker ? (existingEnv
                        ? 'A matching CLI setup already exists, so it will be reused without overwriting its settings.'
                        : cloneWorker
                            ? 'The CLI setup is copied with its steps and starting message; the copy is independent of the original.'
                            : 'The new CLI setup will import the fields shown below exactly.') : ''}
                </div>
                ${cloneWorker ? `<div class="job-import-worker-name">
                    <label class="form-label mb-1" for="recipe-worker-name">Name for the copied CLI setup</label>
                    <input class="form-control" id="recipe-worker-name" maxlength="64" autocomplete="off"
                           value="${this.escape(importSource.worker?.suggestedCloneName || worker.name)}">
                    <div class="form-text">Setup names are unique across VibeRails because each one owns a config directory. Letters, digits, spaces, underscores and hyphens.</div>
                </div>` : ''}
                <div class="job-recipe-review">
                    ${routingText ? `<div><div class="form-label mb-1">Reviewer routing and permission options</div><pre class="job-recipe-review-value" data-recipe-routing>${this.escape(routingText)}</pre></div>` : ''}
                    ${worker ? `<div>
                        <div class="form-label mb-1">Custom arguments</div>
                        <pre class="job-recipe-review-value" data-recipe-custom-args>${this.escape(customArgs || '(none)')}</pre>
                    </div>
                    <div>
                        <div class="form-label mb-1">Starting message</div>
                        <pre class="job-recipe-review-value" data-recipe-prompt>${this.escape(prompt || '(none)')}</pre>
                    </div>` : ''}
                    ${scripts.length ? `<div><div class="form-label mb-1">Repository scripts</div><pre class="job-recipe-review-value" data-recipe-scripts>${scripts.map((action, index) => `${index + 1}. ${SCRIPT_RUNTIME_META[action.scriptRuntime]?.label || 'Unknown runtime'}  ${action.scriptPath || '(missing path)'}${scriptFate(index)}`).map(line => this.escape(line)).join('\n')}</pre></div>` : ''}
                </div>
                ${worker && !importSource ? `<label class="form-check"><input class="form-check-input" type="checkbox" id="recipe-add-env" ${existingEnv ? '' : 'checked'} ${existingEnv ? 'disabled' : ''}><span class="form-check-label">Add the CLI setup</span></label>` : ''}
                ${importSource ? '' : `<label class="form-check"><input class="form-check-input" type="checkbox" id="recipe-add-job" checked><span class="form-check-label">Add the ${worker ? 'agent' : 'automation'} (created disabled)</span></label>`}
                ${hasReviewableContent
                    ? '<label class="form-check mt-2"><input class="form-check-input" type="checkbox" id="recipe-reviewed"><span class="form-check-label">I have reviewed the CLI settings and repository script steps above</span></label>'
                    : ''}
                <div class="alert alert-danger small mt-3 mb-0 d-none" role="alert" data-recipe-error></div>
                <div class="d-flex justify-content-end gap-2 mt-3">
                    <button class="btn btn-outline-secondary" type="button" data-action="close-modal">Cancel</button>
                    <button class="btn btn-primary" type="button" id="recipe-import-confirm" ${hasReviewableContent ? 'disabled' : ''}>Import</button>
                </div>
            </div>`);
        // A recipe is an untrusted file from a repository: its custom arguments can turn off
        // approval prompts and its initial message is instructions the agent will follow. Showing
        // them is not the same as having read them, so when there is anything to read the Import
        // button stays disabled until the user says they did. Recipes carrying neither field have
        // nothing to review and are not made to click through a meaningless attestation. The same
        // applies to another repository's automation: it was written for that repository.
        const reviewed = document.getElementById('recipe-reviewed');
        const confirmButton = document.getElementById('recipe-import-confirm');
        reviewed?.addEventListener('change', () => {
            if (confirmButton) confirmButton.disabled = !reviewed.checked;
        });
        confirmButton?.addEventListener('click', async event => {
            if (hasReviewableContent && !reviewed?.checked) return;
            if (importSource) {
                const workerName = document.getElementById('recipe-worker-name')?.value;
                await this.applyRepositoryImport(importSource, { workerName, button: event.currentTarget });
                return;
            }
            const addEnv = document.getElementById('recipe-add-env')?.checked && !existingEnv;
            const addJob = document.getElementById('recipe-add-job')?.checked;
            await this.applyRecipe(recipe, { addEnv, addJob, existingEnv, button: event.currentTarget });
        });
    }

    async applyRecipe(recipe, { addEnv, addJob, existingEnv, button }) {
        return this.withBusy(button, 'Importing…', async () => {
            recipe = this.normalizeRecipe(recipe);
            const worker = recipe.worker;
            if (this.hasLocalReviewerTargets(worker?.reviewerRouting)) throw new Error('Portable recipes cannot resolve reviewer setups saved on another machine. Use built-in CLI mappings.');
            const cli = String(worker?.cli || '').toLowerCase();
            if (worker && addEnv) {
                // Recipes install through the Automation flow, so the imported record is an
                // Automation Worker to the server — exactly what it always was.
                await this.app.apiCall('/api/v1/environments', 'POST', {
                    name: worker.name,
                    cli,
                    customArgs: worker.customArgs || '',
                    customPrompt: worker.prompt || '',
                    automationWorker: true,
                    purpose: worker.purpose || 'work',
                    reviewerRouting: worker.reviewerRouting || null,
                    workspaceMode: Number(worker.workspaceMode) || 0
                });
            }
            await this.refreshAll({ quiet: true });

            if (addJob) {
                const environment = worker
                    ? (this.environments || []).find(env => (env.name || '').toLowerCase() === String(worker.name).toLowerCase() && (env.cli || '').toLowerCase() === cli && (env.purpose || 'work') === (worker.purpose || 'work') && this.reviewerRoutingMatches(env.reviewerRouting, worker.reviewerRouting))
                    : null;
                if (worker && !environment) throw new Error('The recipe’s CLI setup could not be created or found.');
                const llm = environment ? getJobLlmForCli(cli) : 0;
                const actions = (recipe.actions || []).map(action => Number(action.kind) === JOB_ACTION.WORKER
                    ? {
                        id: null,
                        kind: JOB_ACTION.WORKER,
                        environmentId: Number(environment.id)
                    }
                    : {
                        id: null,
                        kind: Number(action.kind),
                        scriptPath: action.scriptPath,
                        scriptRuntime: Number(action.scriptRuntime),
                        arguments: Array.isArray(action.arguments) ? action.arguments : [],
                        workingDirectory: action.workingDirectory || null,
                        timeoutSeconds: action.timeoutSeconds ?? null
                    });
                await this.app.apiCall('/api/v1/jobs', 'POST', {
                    name: recipe.name,
                    description: recipe.description || '',
                    projectPath: this.currentProjectPath(),
                    llm,
                    environmentId: environment ? Number(environment.id) : null,
                    prompt: worker ? (worker.prompt || environment.customPrompt || '') : '',
                    timeoutMinutes: Number(recipe.timeoutMinutes) || null,
                    enabled: false,
                    launchMinimized: recipe.launchMinimized === true,
                    actions,
                    triggers: (recipe.triggers || []).filter(t => {
                        const kind = Number(t.kind);
                        return kind === TRIGGER.SCHEDULE || kind === TRIGGER.COMMIT || kind === TRIGGER.PRECOMMIT;
                    })
                });
            }
            this.app.closeModal();
            this.app.showToast('Recipe imported', `The ${worker ? 'agent' : 'automation'} was added disabled for local review.`, 'success');
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
