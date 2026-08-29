import { getEnabledLlmItems, mountLlmPicker } from './pickers/llm-picker.js';
import { isConfirmDialogOpen } from './utils.js';
import {
    normalizeSteps,
    openStepsEditor,
    renderStepsSummaryButton,
    serializeSteps,
    stepDisplayName,
    summarizeSteps
} from './environment-steps.js';

// Mirrors EnvironmentWorkspaceMode in the backend. Persistent and PerRun are the same
// mechanism — a git clone of the project — differing only in how long a clone survives.
export const WORKSPACE_MODE = Object.freeze({
    PROJECT: 0,
    PERSISTENT: 1,
    PER_RUN: 2
});

const WORKSPACE_MODE_LABELS = Object.freeze({
    [WORKSPACE_MODE.PROJECT]: 'Project directory',
    [WORKSPACE_MODE.PERSISTENT]: 'Its own clone (kept)',
    [WORKSPACE_MODE.PER_RUN]: 'Fresh clone each run'
});

export class EnvironmentController {
    constructor(app) {
        this.app = app;
        this.environmentFormRequestGeneration = 0;
        this.environmentFormLifecycleCleanup = null;
    }

    unload() {
        this.environmentFormRequestGeneration += 1;
        this.disposeEnvironmentFormLifecycle();
    }

    disposeEnvironmentFormLifecycle() {
        const cleanup = this.environmentFormLifecycleCleanup;
        this.environmentFormLifecycleCleanup = null;
        cleanup?.();
    }

    beginEnvironmentFormRequest() {
        this.environmentFormRequestGeneration += 1;
        this.disposeEnvironmentFormLifecycle();
        return this.environmentFormRequestGeneration;
    }

    captureEnvironmentFormOrigin() {
        if (typeof document === 'undefined') {
            return { container: null, modal: null, view: this.app.currentView, hasDocument: false };
        }

        const container = document.getElementById('modal-container');
        return {
            container,
            modal: container?.firstElementChild || null,
            view: this.app.currentView,
            hasDocument: true
        };
    }

    environmentFormOriginIsCurrent(origin, requestGeneration) {
        if (requestGeneration !== this.environmentFormRequestGeneration) return false;
        if (origin.view !== this.app.currentView) return false;
        if (!origin.hasDocument) return true;

        const container = document.getElementById('modal-container');
        return container === origin.container
            && (container?.firstElementChild || null) === origin.modal;
    }

    async loadEnvironments() {
        const content = document.getElementById('app-content');
        if (!content) return;

        // One concurrent refresh covers everything this page renders: environments
        // (including the LLM picker catalog re-resolve) plus sandboxes. Calling
        // refreshEnvironments() first duplicated the environments fetch and the
        // picker refresh, serially, on every visit.
        await this.app.refreshDashboardData();

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('environments-template');
        const root = fragment.querySelector('[data-view="environments"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            this.app.bindAction(root, '[data-action="create-environment"]', () => this.createEnvironment());
            this.app.bindAction(root, '[data-action="customize-llm-list"]', (event) => {
                this.app.llmPickerController.openCustomizationModal({
                    triggerElement: event?.currentTarget || null
                });
            });
            this.app.bindAction(root, '[data-action="create-sandbox"]', () => {
                this.app.sandboxController.createSandbox();
            });

            const tableSlot = root.querySelector('[data-environments-table]');
            if (tableSlot) {
                tableSlot.innerHTML = this.renderEnvironmentsTable();
                this.bindEnvironmentTableActions(tableSlot);
                // Environment rows carry the same git actions for their workspace, pointing at
                // the same sandbox ids, so they get the same handlers.
                this.bindSandboxGitActions(tableSlot);
            }

            const sandboxesTableSlot = root.querySelector('[data-sandboxes-table]');
            if (sandboxesTableSlot) {
                sandboxesTableSlot.innerHTML = this.renderSandboxesTable();

                // Populate selects
                sandboxesTableSlot.querySelectorAll('[data-sb-cli-select]').forEach(select => {
                    this.app.sandboxController.populateSandboxCliSelect(select);
                });

                this.bindSandboxGitActions(sandboxesTableSlot);
                this.app.bindActions(sandboxesTableSlot, '[data-action="sandbox-vscode"]', (el) => {
                    this.app.sandboxController.launchVSCode(el.dataset.sbId, el.dataset.sbName);
                });
                this.app.bindActions(sandboxesTableSlot, '[data-action="sandbox-delete"]', (el) => {
                    this.app.sandboxController.deleteSandbox(el.dataset.sbId, el.dataset.sbName);
                });

                const resolveCli = (el) => {
                    const row = el.closest('tr');
                    const select = row.querySelector('[data-sb-cli-select]');
                    const selection = this.app.sandboxController.parseSandboxCliSelection(select);
                    
                    if (!selection) {
                        const ts = select.tomselect;
                        const shakeTarget = ts?.wrapper || select;
                        shakeTarget.classList.remove('vb-terminal-selection-shake');
                        void shakeTarget.offsetWidth;
                        shakeTarget.classList.add('vb-terminal-selection-shake');
                        if (ts) {
                            try { ts.focus(); ts.open(); } catch {}
                        } else {
                            select.focus();
                            if (typeof select.showPicker === 'function') {
                                try { select.showPicker(); } catch {}
                            }
                        }
                        return null;
                    }
                    return selection;
                };

                this.app.bindActions(sandboxesTableSlot, '[data-action="sandbox-launch-cli"]', (el) => {
                    const selection = resolveCli(el);
                    if (selection) {
                        this.app.sandboxController.launchInExternalTerminal(el.dataset.sbId, el.dataset.sbName, selection.cli, selection.environmentName);
                    }
                });

                this.app.bindActions(sandboxesTableSlot, '[data-action="sandbox-launch-web"]', (el) => {
                    const selection = resolveCli(el);
                    if (selection) {
                        this.app.sandboxController.launchInWebUI(el.dataset.sbId, el.dataset.sbName, selection.cli, selection.environmentName);
                    }
                });
            }
        }

        content.appendChild(fragment);
    }

    bindEnvironmentTableActions(tableSlot, { onChanged = null } = {}) {
        if (!tableSlot) return;

        this.app.bindActions(tableSlot, '[data-action="remove-environment"]', (element) => {
            const name = element.dataset.envName;
            if (name) {
                this.removeEnvironment(name, { onChanged });
            }
        });
        this.app.bindActions(tableSlot, '[data-action="edit-environment"]', (element) => {
            const name = element.dataset.envName;
            if (name) {
                this.editEnvironment(name, { onChanged });
            }
        });
        this.app.bindActions(tableSlot, '[data-action="launch-environment"]', (element) => {
            const name = element.dataset.envName;
            const cli = element.dataset.envCli;
            if (name && cli) {
                this.launchEnvironment(name, cli);
            }
        });
        this.app.bindActions(tableSlot, '[data-action="launch-in-webui"]', (element) => {
            const envId = parseInt(element.dataset.envId);
            const envName = element.dataset.envName;
            const envCli = element.dataset.envCli;
            if (envId && envName && envCli) {
                this.launchInWebUI(envId, envName, envCli);
            }
        });
    }

    renderEnvironmentsTable() {
        if (this.app.data.environments.length === 0) {
            return '<p class="text-muted text-center py-3">No Environment / Workers configured. Create your first worker to get started.</p>';
        }

        const escape = (value) => this.app.escapeHtml(value);
        const renderRows = environments => environments.map(env => {
                            const brand = this.app.getCliBrand(env.cli);
                            const safeName = escape(env.name);
                            const safeCli = escape(env.cli);
                            const safeEnvId = escape(env.id);
                            const safeLastUsed = escape(env.lastUsed || 'Never');
                            const safeBrandLabel = escape(brand.label || env.cli || 'CLI');
                            const safeLogo = escape(brand.logo || '');
                            const cliKey = (env.cli || '').toLowerCase();
                            const iconLightClass = ['codex', 'chatgpt', 'openai'].includes(cliKey) ? ' icon-light' : '';
                            const logoMarkup = safeLogo
                                ? `<img class="env-cli-logo${iconLightClass}" src="${safeLogo}" alt="${safeBrandLabel} logo" loading="lazy">`
                                : `<i class="fa-solid fa-terminal env-cli-logo-fallback" aria-hidden="true"></i>`;
                            // Workers get the robot badge; the eye-slash hidden badge only
                            // applies to regular envs (a Worker is never in launch pickers,
                            // so "hidden" would be redundant noise on it).
                            const hiddenBadge = env.automationWorker
                                ? `<span class="env-worker-badge" title="Automation Worker — appears in the automation editor's Worker picker, never in launch pickers" aria-label="Automation Worker"><i class="fa-solid fa-robot" aria-hidden="true"></i></span>`
                                : env.hidden
                                    ? `<span class="env-hidden-badge" title="Hidden from launch pickers" aria-label="Hidden from launch pickers"><i class="fa-solid fa-eye-slash" aria-hidden="true"></i></span>`
                                    : '';
                            // Clone-mode environments get their own badge. It reports the mode
                            // even before the clone exists, because the mode is what the user
                            // chose — the directory is just when it happens.
                            const workspaceMode = env.workspaceMode || WORKSPACE_MODE.PROJECT;
                            const workspaceBadge = workspaceMode === WORKSPACE_MODE.PROJECT
                                ? ''
                                : `<span class="env-workspace-badge" title="${escape(
                                      workspaceMode === WORKSPACE_MODE.PER_RUN
                                          ? `Clones the project fresh for every run${env.workspaceBranch ? ` — latest branch ${env.workspaceBranch}` : ''}`
                                          : `Runs in its own clone of the project${env.workspaceBranch ? ` on branch ${env.workspaceBranch}` : ' (created on first launch)'}`
                                  )}" aria-label="${escape(WORKSPACE_MODE_LABELS[workspaceMode])}"><i class="fa-solid fa-code-branch" aria-hidden="true"></i></span>`;
                            // The workspace's git actions are the sandbox actions, on the
                            // sandbox row that backs it — same handlers, same ids.
                            const safeWorkspaceId = escape(env.workspaceSandboxId ?? '');
                            const safeWorkspaceBranch = escape(env.workspaceBranch || '');
                            const workspaceActions = env.workspaceSandboxId
                                ? `<button class="btn btn-xs btn-outline-secondary env-action-btn" type="button" data-action="sandbox-diff" data-sb-id="${safeWorkspaceId}" data-sb-name="${safeName}" title="View workspace diff" aria-label="View ${safeName} workspace diff">
                                            <i class="fa-solid fa-code-compare"></i>
                                        </button>
                                        <button class="btn btn-xs btn-outline-secondary env-action-btn" type="button" data-action="sandbox-merge" data-sb-id="${safeWorkspaceId}" data-sb-name="${safeName}" data-sb-branch="${safeWorkspaceBranch}" title="Merge workspace into local branch" aria-label="Merge ${safeName} workspace">
                                            <i class="fa-solid fa-code-merge"></i>
                                        </button>
                                        <button class="btn btn-xs btn-outline-secondary env-action-btn" type="button" data-action="sandbox-push" data-sb-id="${safeWorkspaceId}" data-sb-name="${safeName}" title="Push workspace to remote" aria-label="Push ${safeName} workspace">
                                            <i class="fa-solid fa-cloud-arrow-up"></i>
                                        </button>`
                                : '';
                            const customArgs = env.customArgs || '';
                            const prompt = env.customPrompt || '';
                            const promptMarkup = prompt.trim()
                                ? `<details class="env-prompt-details">
                                        <summary class="env-prompt-toggle">
                                            <span class="env-prompt-toggle-icon" aria-hidden="true"></span>
                                            <span>Initial prompt</span>
                                        </summary>
                                        <pre class="env-prompt-preview">${escape(prompt)}</pre>
                                   </details>`
                                : '';

                            return `
                            <tr>
                                <td class="env-name-cell">
                                    <div class="env-name-wrap">
                                        <div class="env-cli-logo-wrap" title="${safeBrandLabel}" aria-label="${safeBrandLabel}">
                                            ${logoMarkup}
                                        </div>
                                        <strong class="env-name-text">${safeName}</strong>
                                        ${hiddenBadge}
                                        ${workspaceBadge}
                                    </div>
                                </td>
                                <td class="env-command-cell">
                                    ${customArgs ? `<code class="env-command-code">${escape(customArgs)}</code>` : '<span class="env-empty-value">-</span>'}
                                    ${promptMarkup}
                                </td>
                                <td class="small text-muted text-nowrap">${safeLastUsed}</td>
                                <td class="env-actions-cell">
                                    <div class="env-actions">
                                        <button class="btn btn-xs btn-outline-secondary env-action-btn" type="button" data-action="launch-environment" data-env-name="${safeName}" data-env-cli="${safeCli}" title="Launch in external terminal" aria-label="Launch ${safeName} in external terminal">
                                            <i class="fa-solid fa-terminal"></i>
                                        </button>
                                        <button class="btn btn-xs btn-outline-success env-action-btn" type="button" data-action="launch-in-webui" data-env-id="${safeEnvId}" data-env-name="${safeName}" data-env-cli="${safeCli}" title="Launch in Web Terminal" aria-label="Launch ${safeName} in Web Terminal">
                                            <i class="fa-solid fa-display"></i>
                                        </button>
                                        ${workspaceActions}
                                        <button class="btn btn-xs btn-outline-secondary env-action-btn" type="button" data-action="edit-environment" data-env-name="${safeName}" title="Settings" aria-label="Edit ${safeName} settings">
                                            <i class="fa-solid fa-sliders"></i>
                                        </button>
                                        <button class="btn btn-xs btn-outline-danger env-action-btn" type="button" data-action="remove-environment" data-env-name="${safeName}" title="Delete" aria-label="Delete ${safeName}">
                                            <i class="fa-solid fa-trash"></i>
                                        </button>
                                    </div>
                                </td>
                            </tr>
                        `}).join('');

        const renderTable = (title, description, environments, workerList = false, emptyText = 'No regular environments configured.') => `
            <section class="environment-list-group${workerList ? ' is-workers' : ''}" aria-label="${escape(title)}">
                <header class="environment-list-heading">
                    <span class="environment-list-heading-icon" aria-hidden="true"><i class="fa-solid ${workerList ? 'fa-robot' : 'fa-layer-group'}"></i></span>
                    <div>
                        <h5>${escape(title)} <span>${environments.length}</span></h5>
                        <p>${escape(description)}</p>
                    </div>
                </header>
                ${environments.length > 0 ? `
                    <div class="table-responsive">
                        <table class="table table-hover align-middle environments-table">
                            <colgroup>
                                <col class="env-col-name">
                                <col class="env-col-commands">
                                <col class="env-col-last-used">
                                <col class="env-col-actions">
                            </colgroup>
                            <thead>
                                <tr>
                                    <th>Name</th>
                                    <th>Custom Args</th>
                                    <th>Last Used</th>
                                    <th>Actions</th>
                                </tr>
                            </thead>
                            <tbody>${renderRows(environments)}</tbody>
                        </table>
                    </div>` : `<p class="environment-list-empty">${escape(emptyText)}</p>`}
            </section>`;

        const regularEnvironments = this.app.data.environments.filter(environment => environment.automationWorker !== true);
        const workers = this.app.data.environments.filter(environment => environment.automationWorker === true);

        return `
            <div class="environment-list-groups">
                ${renderTable('Environments', 'Profiles you launch directly from terminals and sandboxes.', regularEnvironments)}
                ${workers.length > 0
                    ? renderTable('Workers', 'Robot-marked profiles used by Automations.', workers, true, 'No Workers configured.')
                    : ''}
            </div>`;
    }

    /**
     * Diff / merge / push for a sandbox, wherever its row is rendered. Both the Sandboxes card
     * (standalone sandboxes) and the Environments table (owned workspaces) emit these actions
     * against a sandbox id, so the handlers are identical and live in one place.
     */
    bindSandboxGitActions(scope) {
        if (!scope) return;
        this.app.bindActions(scope, '[data-action="sandbox-diff"]', (el) => {
            this.app.sandboxController.showDiff(el.dataset.sbId, el.dataset.sbName);
        });
        this.app.bindActions(scope, '[data-action="sandbox-merge"]', (el) => {
            this.app.sandboxController.mergeLocally(el.dataset.sbId, el.dataset.sbName, el.dataset.sbBranch);
        });
        this.app.bindActions(scope, '[data-action="sandbox-push"]', (el) => {
            this.app.sandboxController.pushToRemote(el.dataset.sbId, el.dataset.sbName);
        });
    }

    renderSandboxesTable() {
        // Only standalone sandboxes. A sandbox owned by an environment is that environment's
        // workspace and is shown on its row instead — and because deleting or re-moding an
        // environment releases its workspace, an orphaned one reappears here automatically.
        const sandboxes = (this.app.data.sandboxes || []).filter(sb => !sb.environmentId);
        if (sandboxes.length === 0) {
            return '<p class="text-muted text-center py-3">No sandboxes yet. Create one to work in an isolated copy of your project.</p>';
        }

        return `
            <div class="table-responsive">
                <table class="table table-hover align-middle">
                    <thead>
                        <tr>
                            <th>Name</th>
                            <th>Branch</th>
                            <th>Created</th>
                            <th>Git Actions</th>
                            <th>Launch Tools</th>
                            <th class="text-end">Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        ${sandboxes.map(sb => `
                            <tr>
                                <td>
                                    <div class="fw-bold text-white">${this.app.escapeHtml(sb.name)}</div>
                                </td>
                                <td>
                                    <div class="x-small text-muted"><code class="text-accent">${this.app.escapeHtml(sb.branch)}</code></div>
                                </td>
                                <td class="small text-muted">${sb.created}</td>
                                <td>
                                    <div class="d-flex gap-1">
                                        <button class="btn btn-xs btn-outline-secondary" data-action="sandbox-diff" data-sb-id="${sb.id}" data-sb-name="${this.app.escapeHtml(sb.name)}" title="View Diff">Diff</button>
                                        <button class="btn btn-xs btn-outline-secondary" data-action="sandbox-merge" data-sb-id="${sb.id}" data-sb-name="${this.app.escapeHtml(sb.name)}" data-sb-branch="${this.app.escapeHtml(sb.branch)}" title="Merge into local branch">Merge</button>
                                        <button class="btn btn-xs btn-outline-secondary" data-action="sandbox-push" data-sb-id="${sb.id}" data-sb-name="${this.app.escapeHtml(sb.name)}" title="Push to remote">Push</button>
                                    </div>
                                </td>
                                <td>
                                    <div class="d-flex gap-1 align-items-center">
                                        <select class="form-select form-select-xs" style="width: 140px;" data-sb-cli-select data-sb-id="${sb.id}">
                                            <!-- To be populated -->
                                        </select>
                                        <button class="btn btn-sm btn-outline-secondary d-inline-flex align-items-center gap-1" type="button" data-action="sandbox-launch-cli" data-sb-id="${sb.id}" data-sb-name="${this.app.escapeHtml(sb.name)}" title="Launch in external terminal">
                                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                                <path d="M6 9a.5.5 0 0 1 .5-.5h3a.5.5 0 0 1 0 1h-3A.5.5 0 0 1 6 9M2.854 4.146a.5.5 0 1 0-.708.708L4.293 7 2.146 9.146a.5.5 0 1 0 .708.708l2.5-2.5a.5.5 0 0 0 0-.708z"></path>
                                                <path d="M14 1a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H2a1 1 0 0 1-1-1V2a1 1 0 0 1 1-1zM2 0a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V2a2 2 0 0 0-2-2z"></path>
                                            </svg>
                                            <span>Launch in CLI</span>
                                        </button>
                                        <button class="btn btn-sm btn-outline-success d-inline-flex align-items-center gap-1" type="button" data-action="sandbox-launch-web" data-sb-id="${sb.id}" data-sb-name="${this.app.escapeHtml(sb.name)}" title="Launch in Web Terminal">
                                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                                <path d="M2 1a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V3a2 2 0 0 0-2-2zm12 1a1 1 0 0 1 1 1v1H1V3a1 1 0 0 1 1-1zm1 11a1 1 0 0 1-1 1H2a1 1 0 0 1-1-1V5h14z"></path>
                                                <path d="M4.146 7.146a.5.5 0 0 1 .708 0L6.707 9 4.854 10.854a.5.5 0 0 1-.708-.708L5.293 9 4.146 7.854a.5.5 0 0 1 0-.708M7.5 10.5a.5.5 0 0 1 0-1H10a.5.5 0 0 1 0 1z"></path>
                                            </svg>
                                            <span>Launch in Web</span>
                                        </button>
                                        <button class="btn btn-sm btn-outline-secondary d-inline-flex align-items-center gap-1" type="button" data-action="sandbox-vscode" data-sb-id="${sb.id}" data-sb-name="${this.app.escapeHtml(sb.name)}" title="Open in VS Code">
                                            <img src="assets/img/vscode.svg" alt="" class="icon-light" style="width: 13px; height: 13px;">
                                            <span>Open In VS Code</span>
                                        </button>
                                    </div>
                                </td>
                                <td class="text-end">
                                    <button class="btn btn-xs btn-outline-danger" data-action="sandbox-delete" data-sb-id="${sb.id}" data-sb-name="${this.app.escapeHtml(sb.name)}" title="Delete Sandbox">
                                        <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" fill="currentColor" viewBox="0 0 16 16"><path d="M11 1.5v1h3.5a.5.5 0 0 1 0 1h-.538l-.853 10.66A2 2 0 0 1 11.115 16h-6.23a2 2 0 0 1-1.994-1.84L2.038 3.5H1.5a.5.5 0 0 1 0-1H5v-1A1.5 1.5 0 0 1 6.5 0h3A1.5 1.5 0 0 1 11 1.5m-5 0v1h4v-1a.5.5 0 0 0-.5-.5h-3a.5.5 0 0 0-.5.5M4.5 5.029l.5 8.5a.5.5 0 1 0 .998-.06l-.5-8.5a.5.5 0 1 0-.998.06m6.53-.06a.5.5 0 0 0-.515.479l-.5 8.5a.5.5 0 1 0 .998.06l.5-8.5a.5.5 0 0 0-.484-.539M8 5.5a.5.5 0 0 0-.5.5v8.5a.5.5 0 0 0 1 0V6a.5.5 0 0 0-.5-.5"/></svg>
                                    </button>
                                </td>
                            </tr>
                        `).join('')}
                    </tbody>
                </table>
            </div>
        `;
    }

    createEnvironment(options = {}) {
        this.beginEnvironmentFormRequest();
        this.showEnvironmentForm({ mode: 'create', ...options });
    }

    async editEnvironment(name, options = {}) {
        const env = this.app.data.environments.find(e => e.name === name);
        if (!env) return false;

        const requestGeneration = this.beginEnvironmentFormRequest();
        const origin = this.captureEnvironmentFormOrigin();

        const cliSettings = await this.loadCliSettings(env.cli, env.name);
        if (!this.environmentFormOriginIsCurrent(origin, requestGeneration)) {
            return false;
        }

        // For Codex, env.customPrompt is the source of truth for terminal launch
        // (TerminalRoutes.cs threads it into the initial prompt). The settings panel's
        // "prompt" field represents the same concept; preload it from env.customPrompt
        // so users see the value they'll actually get at launch — and so saving the
        // form doesn't silently clobber a CLI-set prompt with config.toml's empty value.
        const cliLower = (env.cli || '').toLowerCase();
        if (cliLower === 'codex' && env.customPrompt) {
            cliSettings.prompt = env.customPrompt;
        }
        if (cliLower === 'codex') {
            this.mergeCodexSettingsFromCustomArgs(cliSettings, env.customArgs || '');
        }
        if (cliLower === 'claude' && env.customPrompt) {
            cliSettings.initialMessage = env.customPrompt;
        }
        if (cliLower === 'claude') {
            this.mergeClaudeSettingsFromCustomArgs(cliSettings, env.customArgs || '');
        }
        if (cliLower === 'antigravity') {
            if (env.customPrompt) {
                cliSettings.initialMessage = env.customPrompt;
            }
            this.mergeAntigravitySettingsFromCustomArgs(cliSettings, env.customArgs || '');
        }
        if (cliLower === 'copilot') {
            if (env.customPrompt) {
                cliSettings.initialMessage = env.customPrompt;
            }
            this.mergeCopilotSettingsFromCustomArgs(cliSettings, env.customArgs || '');
        }
        if (this.isNativeGrokCli(cliLower)) {
            if (env.customPrompt) {
                cliSettings.initialMessage = env.customPrompt;
            }
            this.mergeGrokSettingsFromCustomArgs(cliSettings, env.customArgs || '');
            cliSettings.model = 'grok-4.6';
        }
        if (this.isOpencodeBackedCli(cliLower)) {
            if (env.customPrompt) {
                cliSettings.initialMessage = env.customPrompt;
            }
            this.mergeOpencodeSettingsFromCustomArgs(cliSettings, env.customArgs || '');
            // For OpenCode-backed pseudo-CLIs, force the model to the pinned value so
            // the form always reflects the env type's contract — a saved --model that
            // drifted to a different provider would otherwise mislead the user.
            const pinnedModel = this.pinnedModelForCli(cliLower);
            if (pinnedModel) {
                cliSettings.model = pinnedModel;
            }
        }

        this.showEnvironmentForm({ mode: 'edit', env, cliSettings, ...options });
        return true;
    }

    showEnvironmentForm({ mode, env = null, cliSettings = {}, initialName = null, automationWorker = false, onChanged = null, onCancel = null }) {
        this.disposeEnvironmentFormLifecycle();
        const isEdit = mode === 'edit';
        // Automation Workers share this modal but are never launch-picker visible,
        // so the "Hide from launch pickers" switch is meaningless for them.
        const workerOnly = automationWorker === true || Boolean(env?.automationWorker);

        // Provider creation reuses the centralized catalog/order, but deliberately
        // ignores launch visibility preferences and never includes plain Terminal.
        const cliOptions = getEnabledLlmItems(this.app, 'environment-provider');
        const initialCli = isEdit ? env.cli : (cliOptions[0]?.cli || 'claude');
        // showModal escapes the title itself — pass it raw.
        const title = isEdit
            ? `${workerOnly ? 'Edit Worker' : 'Edit Environment / Worker'}: ${env.name}`
            : (workerOnly ? 'Create Worker' : 'Create Environment / Worker');
        // Say what will actually be created. Only the Automation editor's flow passes
        // automationWorker, so labelling this button "Create Worker" everywhere promised a
        // Worker while the Environments page produced a plain Environment — one that the
        // Worker picker then refused to list.
        const submitLabel = isEdit
            ? 'Save Changes'
            : (workerOnly ? 'Create Worker' : 'Create Environment');
        const submitIcon = isEdit
            ? `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                <path d="M16 8A8 8 0 1 1 0 8a8 8 0 0 1 16 0m-3.97-3.03a.75.75 0 0 0-1.08.022L7.477 9.417 5.384 7.323a.75.75 0 0 0-1.06 1.06L6.97 11.03a.75.75 0 0 0 1.079-.02l3.992-4.99a.75.75 0 0 0-.01-1.05z"/>
              </svg>`
            : `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                <path d="M8 4a.5.5 0 0 1 .5.5v3h3a.5.5 0 0 1 0 1h-3v3a.5.5 0 0 1-1 0v-3h-3a.5.5 0 0 1 0-1h3v-3A.5.5 0 0 1 8 4"/>
              </svg>`;

        // The name is an immutable backend identifier, so the input renders only at
        // creation; edit mode carries the name in the title. A Worker created from
        // the Automation editor may arrive with a prefill (the automation's name as
        // a convenient default), but the field stays editable — the Worker's name
        // belongs to this modal, not to the automation.
        const nameRow = isEdit
            ? ''
            : `<div class="mb-3">
                    <label class="form-label">${workerOnly ? 'Worker Name' : 'Environment / Worker Name'}</label>
                    <input type="text" class="form-control" id="env-name" required value="${this.app.escapeHtml(initialName || '')}">
                </div>`;

        const cliField = isEdit
            ? `<input type="text" class="form-control" value="${this.app.escapeHtml(env.cli)}" disabled>`
            : '<select class="form-select" id="env-cli" required></select>';

        const customArgsValue = isEdit ? this.app.escapeHtml(env.customArgs || '') : '';
        const usesManagedArgs = this.usesManagedCustomArgs(initialCli);
        const hiddenChecked = isEdit ? Boolean(env.hidden) : false;
        // Workers are excluded from launch pickers unconditionally, so the switch
        // would be a no-op for them.
        const hiddenRow = workerOnly ? '' : `
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="env-hidden" ${hiddenChecked ? 'checked' : ''}>
                        <label class="form-check-label" for="env-hidden">Hide from launch pickers</label>
                    </div>
                    <small class="form-text text-muted">Keeps this environment out of the terminal/sandbox LLM dropdowns when they get too full. It can still be launched from here and used by Automations, and you can change this later from the picker's "Customize LLM list".</small>
                </div>`;

        // Automation Workers only need two honest choices: run in the live project or start
        // from a clean checkout for every run. Persistent clones remain supported by the
        // backend and the regular Environment editor, but are intentionally not offered here.
        const currentWorkspaceMode = isEdit
            ? (env.workspaceMode || WORKSPACE_MODE.PROJECT)
            : WORKSPACE_MODE.PROJECT;
        const workspaceLabel = workerOnly ? 'Workspace' : 'Where it runs';
        const workspaceHelp = 'A clone is made on first launch, on its own branch, and gets Diff / Merge / Push buttons on this list. Fresh each run clones the last commit only — no uncommitted work and no gitignored files such as .env — and only the newest few runs are kept. Changing this later releases the old workspace as a standalone sandbox rather than deleting it.';
        const workspaceRow = this.app.data.isInGit
            ? (workerOnly
                ? `<fieldset class="mb-3 env-workspace-fieldset">
                        <legend class="form-label">Workspace</legend>
                        <div class="env-workspace-choices">
                            <label class="env-workspace-choice">
                                <input type="radio" name="env-workspace-mode" id="env-workspace-project" value="${WORKSPACE_MODE.PROJECT}" ${currentWorkspaceMode === WORKSPACE_MODE.PROJECT ? 'checked' : ''} required>
                                <span class="env-workspace-choice-card">
                                    <span class="env-workspace-choice-copy">
                                        <strong>Project directory</strong>
                                        <small>Run in the project directory on whatever Git branch is checked out when the automation starts.</small>
                                    </span>
                                </span>
                            </label>
                            <label class="env-workspace-choice">
                                <input type="radio" name="env-workspace-mode" id="env-workspace-per-run" value="${WORKSPACE_MODE.PER_RUN}" ${currentWorkspaceMode === WORKSPACE_MODE.PER_RUN ? 'checked' : ''} required>
                                <span class="env-workspace-choice-card">
                                    <span class="env-workspace-choice-copy">
                                        <strong>Clean Git checkout every run</strong>
                                        <small>Create a clean checkout of the current branch for every run. Changes from one run do not carry into the next.</small>
                                    </span>
                                </span>
                            </label>
                        </div>
                    </fieldset>`
                : `<div class="mb-3">
                        <label class="form-label" for="env-workspace-mode">${workspaceLabel}</label>
                        <select class="form-select" id="env-workspace-mode">
                            <option value="${WORKSPACE_MODE.PROJECT}" ${currentWorkspaceMode === WORKSPACE_MODE.PROJECT ? 'selected' : ''}>Project directory — run directly in this project</option>
                            <option value="${WORKSPACE_MODE.PERSISTENT}" ${currentWorkspaceMode === WORKSPACE_MODE.PERSISTENT ? 'selected' : ''}>Its own clone — one workspace, reused every launch</option>
                            <option value="${WORKSPACE_MODE.PER_RUN}" ${currentWorkspaceMode === WORKSPACE_MODE.PER_RUN ? 'selected' : ''}>Git clone and start fresh each run</option>
                        </select>
                        <small class="form-text text-muted d-block">${workspaceHelp}</small>
                    </div>`)
            : `<div class="mb-3">
                    <label class="form-label">${workspaceLabel}</label>
                    <div class="form-control-plaintext text-muted small">This project is not a git repository, so it can only run in the project directory.</div>
                </div>`;

        const customArgsRow = `
                <div class="mb-3" data-custom-args-group ${usesManagedArgs ? 'style="display: none;"' : ''}>
                    <label class="form-label">${workerOnly ? 'Extra CLI arguments' : 'Custom Arguments'}</label>
                    <input type="text" class="form-control" id="env-custom-args" value="${customArgsValue}" placeholder="e.g., --yolo --sandbox">
                    <small class="form-text text-muted">Optional arguments passed directly to the CLI.</small>
                </div>`;

        // Steps are edited in their own nested modal — this form is dense enough already, and a
        // step list with drag-reordering and a test console does not belong inlined in it.
        // `editedSteps` stays null until that editor is actually opened and saved, which is what
        // lets the PUT's nullable guard leave stored steps untouched.
        const initialSteps = isEdit ? normalizeSteps(env.steps) : [];
        let editedSteps = null;
        const stepsRow = renderStepsSummaryButton(initialSteps);

        // One shared Initial Message field, rendered directly under the CLI picker rather than
        // inside the per-CLI settings block: it maps to the single Environments.CustomPrompt
        // column whichever CLI is chosen, and it is the first thing a Worker exists to say.
        // Living outside [data-cli-settings-slot] also means typed text survives switching CLI.
        // Codex's settings payload calls the same concept "prompt" — accept either key.
        const initialMessageValue = this.app.escapeHtml(
            cliSettings?.initialMessage ?? cliSettings?.prompt ?? '');
        const initialMessageRow = this.renderInitialMessageField(initialCli, initialMessageValue);

        // Worker creation is already a small CRUD form. Keep every field in one clear
        // flow instead of hiding two ordinary settings behind an "Advanced" disclosure.
        const formBody = workerOnly
            ? `
                <div data-cli-settings-slot>${this.buildCliSettingsHtml(initialCli, cliSettings || {})}</div>
                ${workspaceRow}
                ${customArgsRow}
                ${stepsRow}`
            : `
                ${hiddenRow}
                ${workspaceRow}
                ${customArgsRow}
                ${stepsRow}
                <div data-cli-settings-slot>${this.buildCliSettingsHtml(initialCli, cliSettings || {})}</div>`;

        this.app.showModal(title, `
            <form id="env-form" class="${workerOnly ? 'env-worker-form' : ''}">
                ${nameRow}
                <div class="mb-3">
                    <label class="form-label">CLI Type</label>
                    ${cliField}
                </div>
                ${initialMessageRow}
                ${formBody}
                <button type="submit" class="btn btn-primary d-flex align-items-center gap-2">
                    ${submitIcon}
                    ${submitLabel}
                </button>
            </form>
        `);

        const slot = document.querySelector('[data-cli-settings-slot]');
        let cliSelect = null;
        let cliPickerDisposer = null;

        if (!isEdit) {
            cliSelect = document.getElementById('env-cli');
            cliPickerDisposer = mountLlmPicker(this.app, cliSelect, {
                context: 'environment-provider',
                placeholder: null,
                selectedValue: initialCli,
                includeGroups: false
            });
            cliSelect.addEventListener('change', () => {
                const cli = cliSelect.value;
                const customArgsGroup = document.querySelector('[data-custom-args-group]');
                if (customArgsGroup) {
                    customArgsGroup.style.display = this.usesManagedCustomArgs(cli) ? 'none' : '';
                }
                slot.innerHTML = this.buildCliSettingsHtml(cli, {});
                this.bindCliSettingsInteractions(cli);
                // The shared Initial Message field lives outside the slot, so its typed text
                // survives the switch; only the CLI-specific wording follows the picker.
                const initialMessage = document.getElementById('env-initial-message');
                if (initialMessage) initialMessage.placeholder = this.initialMessagePlaceholder(cli);
                const cliNameSpan = document.querySelector('[data-initial-message-cli]');
                if (cliNameSpan) cliNameSpan.textContent = this.cliDisplayName(cli);
            });
        }

        this.bindCliSettingsInteractions(initialCli);
        const refreshInitialMessageRefs = this.bindInitialMessageField(() => editedSteps ?? initialSteps);

        const form = document.getElementById('env-form');
        const modalContainer = document.getElementById('modal-container');
        const closeButtons = [...(modalContainer?.querySelectorAll('[data-action="close-modal"]') || [])];
        const keydownTarget = typeof window !== 'undefined' ? window : document;
        let completed = false;
        let lifecycleDisposed = false;
        let stepsEditor = null;

        document.querySelector('[data-env-steps-open]')?.addEventListener('click', () => {
            stepsEditor = openStepsEditor(this.app, {
                steps: editedSteps ?? initialSteps,
                // An environment with a clone runs its steps inside the clone, so a test should
                // too. Null lets the server fall back to the project root.
                workingDirectory: isEdit ? (env.workspacePath || null) : null,
                onSave: steps => {
                    editedSteps = steps;
                    stepsEditor = null;
                    const summary = document.querySelector('[data-env-steps-summary]');
                    if (summary) summary.textContent = summarizeSteps(steps);
                    // Step names/deletions may have changed what the Initial Message references.
                    refreshInitialMessageRefs();
                }
            });
        });

        const cleanupLifecycle = () => {
            if (lifecycleDisposed) return;
            lifecycleDisposed = true;
            keydownTarget.removeEventListener('keydown', handleEscape, true);
            closeButtons.forEach(button => button.removeEventListener('click', handleClose));
            cliPickerDisposer?.();
            // The nested layer lives in #modal-container beside this form; closing the form
            // without it would leave an orphan modal (and a live test stream) behind.
            stepsEditor?.close({ restoreFocus: false });
            stepsEditor = null;
            if (this.environmentFormLifecycleCleanup === disposeLifecycle) {
                this.environmentFormLifecycleCleanup = null;
            }
        };
        const handleCancel = () => {
            if (completed) return;
            completed = true;
            this.environmentFormRequestGeneration += 1;
            cleanupLifecycle();
            onCancel?.();
        };
        const handleClose = () => handleCancel();
        const handleEscape = event => {
            if (event.key !== 'Escape' || completed) return;
            // A confirmDialog overlay owns Escape while it is up; this listener
            // registered earlier on the same window/capture phase, so it must
            // stand down itself (see utils.js).
            if (isConfirmDialogOpen()) return;
            if (modalContainer && form && !modalContainer.contains(form)) {
                completed = true;
                cleanupLifecycle();
                return;
            }

            if (!onCancel) {
                handleCancel();
                return;
            }

            event.preventDefault();
            event.stopImmediatePropagation();
            this.app.closeModal();
            handleCancel();
        };
        const disposeLifecycle = () => {
            completed = true;
            cleanupLifecycle();
        };

        keydownTarget.addEventListener('keydown', handleEscape, true);
        closeButtons.forEach(button => button.addEventListener('click', handleClose));
        this.environmentFormLifecycleCleanup = disposeLifecycle;

        form.addEventListener('submit', async (e) => {
            e.preventDefault();
            const submissionGeneration = this.environmentFormRequestGeneration;

            try {
                // Absent for Workers — omit `hidden` so the PUT's nullable guard
                // leaves the stored value untouched.
                const hiddenInput = document.getElementById('env-hidden');
                // Absent outside a git repo, where the only legal mode is Project. Omitting it
                // means the PUT's nullable guard leaves the stored mode alone, so opening the
                // editor in a non-git project can never silently downgrade a clone environment.
                const workspaceInput = document.querySelector('input[name="env-workspace-mode"]:checked')
                    || document.getElementById('env-workspace-mode');
                const workspaceMode = workspaceInput ? Number(workspaceInput.value) : null;
                if (isEdit) {
                    const settingsPayload = this.extractCliSettingsPayload(env.cli);
                    const payload = this.buildEnvironmentSavePayload(env.cli, settingsPayload);
                    if (hiddenInput) payload.hidden = hiddenInput.checked;
                    if (workspaceMode !== null) payload.workspaceMode = workspaceMode;
                    // Omitted entirely when the steps editor was never opened, so the PUT's
                    // nullable guard leaves the stored list alone.
                    if (editedSteps) payload.steps = serializeSteps(editedSteps);
                    await this.app.apiCall(`/api/v1/environments/${encodeURIComponent(env.name)}`, 'PUT', payload);
                    await this.saveCliSettings(env.cli, env.name, settingsPayload);
                } else {
                    const name = document.getElementById('env-name').value.trim();
                    const cli = document.getElementById('env-cli').value;
                    const settingsPayload = this.extractCliSettingsPayload(cli);
                    const payload = {
                        name,
                        cli,
                        ...this.buildEnvironmentSavePayload(cli, settingsPayload),
                        ...(hiddenInput ? { hidden: hiddenInput.checked } : {}),
                        ...(workspaceMode !== null ? { workspaceMode } : {}),
                        ...(editedSteps ? { steps: serializeSteps(editedSteps) } : {}),
                        ...(automationWorker === true ? { automationWorker: true } : {})
                    };
                    await this.app.apiCall('/api/v1/environments', 'POST', payload);
                    await this.saveCliSettings(cli, name, settingsPayload);
                }

                if (submissionGeneration !== this.environmentFormRequestGeneration
                    || (modalContainer && form && !modalContainer.contains(form))) {
                    return;
                }
                completed = true;
                cleanupLifecycle();
                this.app.closeModal();
                await this.refreshEnvironments();
                if (submissionGeneration !== this.environmentFormRequestGeneration) {
                    return;
                }
                if (onChanged) {
                    await onChanged();
                } else {
                    this.app.navigate('environments');
                }
            } catch (error) {
                if (submissionGeneration !== this.environmentFormRequestGeneration) {
                    return;
                }
                const verb = isEdit ? 'update' : 'create';
                this.app.showError(`Failed to ${verb} environment: ${error.message}`);
            }
        });
    }

    cliSettingsEndpoint(cli) {
        const cliLower = (cli || '').toLowerCase();
        // Antigravity (agy) is launch-flag-only — no settings-file API (like Copilot).
        if (cliLower === 'codex' || cliLower === 'claude') {
            return cliLower;
        }
        return null;
    }

    // GLM 5.2 and GLM 5.3 are OpenCode-backed pseudo-CLIs: they launch `opencode`
    // with a pinned --model flag. They share the OpenCode settings form, env handling, and arg
    // builder, so most call sites route through this helper instead of checking === 'opencode'.
    // Native Grok 4.6 is NOT OpenCode-backed — use isNativeGrokCli.
    isOpencodeBackedCli(cli) {
        const cliLower = (cli || '').toLowerCase();
        return cliLower === 'opencode' || cliLower === 'glm-5.2' || cliLower === 'glm-5.3';
    }

    isNativeGrokCli(cli) {
        return (cli || '').toLowerCase() === 'grok-4.6';
    }

    // Returns the pinned provider/model ID for a pseudo-CLI, or null for plain OpenCode
    // (which lets the user pick any model from the dropdown).
    pinnedModelForCli(cli) {
        const cliLower = (cli || '').toLowerCase();
        if (cliLower === 'glm-5.2') return 'zai/glm-5.2';
        if (cliLower === 'glm-5.3') return 'zai-coding-plan/glm-5.3';
        return null;
    }

    usesManagedCustomArgs(cli) {
        return this.isOpencodeBackedCli(cli)
            || this.isNativeGrokCli(cli)
            || (cli || '').toLowerCase() === 'codex'
            || (cli || '').toLowerCase() === 'claude'
            || (cli || '').toLowerCase() === 'antigravity'
            || (cli || '').toLowerCase() === 'copilot';
    }

    async loadCliSettings(cli, envName) {
        const endpoint = this.cliSettingsEndpoint(cli);
        if (!endpoint) return {};
        try {
            return await this.app.apiCall(`/api/v1/${endpoint}/settings/${encodeURIComponent(envName)}`, 'GET');
        } catch (error) {
            console.warn(`Failed to load ${cli} settings:`, error);
            return {};
        }
    }

    async saveCliSettings(cli, envName, payload = null) {
        const endpoint = this.cliSettingsEndpoint(cli);
        if (!endpoint) return;
        const settingsPayload = payload || this.extractCliSettingsPayload(cli);
        if (!settingsPayload) return;
        await this.app.apiCall(`/api/v1/${endpoint}/settings/${encodeURIComponent(envName)}`, 'PUT', settingsPayload);
    }

    buildEnvironmentSavePayload(cli, settingsPayload = null) {
        const cliLower = (cli || '').toLowerCase();
        if (cliLower === 'codex') {
            const codexSettings = settingsPayload || this.extractCliSettingsPayload(cli);
            return {
                customArgs: this.buildCodexCustomArgs(codexSettings),
                customPrompt: codexSettings?.prompt ?? ''
            };
        }

        if (cliLower === 'claude') {
            const claudeSettings = settingsPayload || this.extractCliSettingsPayload(cli);
            return {
                customArgs: this.buildClaudeCustomArgs(claudeSettings),
                customPrompt: claudeSettings?.initialMessage ?? ''
            };
        }

        if (cliLower === 'antigravity') {
            const antigravitySettings = settingsPayload || this.extractCliSettingsPayload(cli);
            return {
                customArgs: this.buildAntigravityCustomArgs(antigravitySettings),
                customPrompt: antigravitySettings?.initialMessage ?? ''
            };
        }

        if (cliLower === 'copilot') {
            const copilotSettings = settingsPayload || this.extractCliSettingsPayload(cli);
            return {
                customArgs: this.buildCopilotCustomArgs(copilotSettings),
                customPrompt: copilotSettings?.initialMessage ?? ''
            };
        }

        if (this.isNativeGrokCli(cliLower)) {
            const grokSettings = settingsPayload || this.extractCliSettingsPayload(cli);
            return {
                customArgs: this.buildGrokCustomArgs(grokSettings),
                customPrompt: grokSettings?.initialMessage ?? ''
            };
        }

        if (this.isOpencodeBackedCli(cliLower)) {
            const opencodeSettings = settingsPayload || this.extractCliSettingsPayload(cli);
            // Pseudo-CLIs always pin their model — override whatever the form had so the
            // saved CustomArgs carry the right --model value.
            const pinnedModel = this.pinnedModelForCli(cliLower);
            if (pinnedModel && opencodeSettings) {
                opencodeSettings.model = pinnedModel;
            }
            return {
                customArgs: this.buildOpencodeCustomArgs(opencodeSettings),
                customPrompt: opencodeSettings?.initialMessage ?? ''
            };
        }

        return {
            customArgs: document.getElementById('env-custom-args')?.value || ''
        };
    }

    buildCodexCustomArgs(settings) {
        const s = settings || {};
        const args = [];
        const model = this.normalizeCodexModel(s.model);
        const effort = this.normalizeCodexEffort(model, s.effort);

        if (model) {
            args.push('--model', model);
        }

        if (effort) {
            args.push('-c', `model_reasoning_effort=${effort}`);
        }

        // YOLO Mode for Codex: bypass approvals and sandboxing entirely.
        if (s.yolo) {
            args.push('--dangerously-bypass-approvals-and-sandbox');
        }

        if (s.noAltScreen) {
            args.push('--no-alt-screen');
        }

        if (s.fastMode) {
            args.push('-c', 'service_tier=fast');
            args.push('--enable', 'fast_mode');
        }

        return args.join(' ');
    }

    renderAntigravityModelOptions(selectedModel) {
        const selected = (selectedModel || '').trim();
        // Hand-maintained pinned list — see runbooks/custom_envs/CLI_OPTIONS.md ("Model Lists").
        // Values are the exact display strings `agy models` prints and that `--model` accepts,
        // spaces + parens included (e.g. "Gemini 3.5 Flash (Low)"). `agy models` is an
        // interactive picker with no scriptable/JSON output, so this list is updated by hand.
        const options = [
            ['', 'Default (Antigravity recommended)'],
            ['Gemini 3.5 Flash (Medium)', 'Gemini 3.5 Flash (Medium)'],
            ['Gemini 3.5 Flash (High)', 'Gemini 3.5 Flash (High)'],
            ['Gemini 3.5 Flash (Low)', 'Gemini 3.5 Flash (Low)'],
            ['Gemini 3.1 Pro (Low)', 'Gemini 3.1 Pro (Low)'],
            ['Gemini 3.1 Pro (High)', 'Gemini 3.1 Pro (High)'],
            ['Claude Sonnet 4.6 (Thinking)', 'Claude Sonnet 4.6 (Thinking)'],
            ['Claude Opus 4.6 (Thinking)', 'Claude Opus 4.6 (Thinking)'],
            ['GPT-OSS 120B (Medium)', 'GPT-OSS 120B (Medium)']
        ];
        const known = new Set(options.map(([value]) => value));
        const rendered = options.map(([value, label]) =>
            `<option value="${this.app.escapeHtml(value)}" ${selected === value ? 'selected' : ''}>${this.app.escapeHtml(label)}</option>`
        );

        if (selected && !known.has(selected)) {
            rendered.push(`<option value="${this.app.escapeHtml(selected)}" selected>${this.app.escapeHtml(selected)} (custom)</option>`);
        }

        return rendered.join('');
    }

    buildAntigravityCustomArgs(settings) {
        const s = settings || {};
        const args = [];

        // Model is the full display string from `agy models`, e.g. "Gemini 3.5 Flash (Low)"
        // (spaces + parens) — quoteCustomArg wraps it so it survives as a single argument.
        if (s.model) {
            args.push('--model', s.model);
        }

        if (s.sandboxEnabled) {
            args.push('--sandbox');
        }

        // YOLO Mode for Antigravity (agy): auto-approve all tool permission requests.
        if (s.yoloMode) {
            args.push('--dangerously-skip-permissions');
        }

        args.push(...this.parseArgString(s.additionalArgs || ''));

        return args.map(arg => this.quoteCustomArg(arg)).join(' ');
    }

    mergeAntigravitySettingsFromCustomArgs(settings, customArgs) {
        const args = this.parseArgString(customArgs);
        if (args.length === 0) return settings;

        const additionalArgs = [];

        for (let i = 0; i < args.length; i++) {
            const arg = args[i];
            const next = args[i + 1];

            // Model value is a display string with spaces/parens; parseArgString already
            // unquoted it, so `next` is the whole "Gemini 3.5 Flash (Low)" token.
            if (arg === '--model' && next) {
                settings.model = next;
                i++;
                continue;
            }
            if (arg.startsWith('--model=')) {
                settings.model = arg.slice('--model='.length);
                continue;
            }

            if (arg === '--sandbox') {
                settings.sandboxEnabled = true;
                continue;
            }

            // YOLO Mode for Antigravity (agy).
            if (arg === '--dangerously-skip-permissions') {
                settings.yoloMode = true;
                continue;
            }

            additionalArgs.push(arg);
        }

        if (additionalArgs.length > 0) {
            settings.additionalArgs = additionalArgs.map(arg => this.quoteCustomArg(arg)).join(' ');
        }

        return settings;
    }

    bindCliSettingsInteractions(cli) {
        if ((cli || '').toLowerCase() !== 'codex') return;

        const modelSelect = document.getElementById('codex-model');
        const effortSelect = document.getElementById('codex-effort');
        const maxOption = effortSelect?.querySelector('option[value="max"]');
        if (!modelSelect || !effortSelect || !maxOption) return;

        const syncEffortForModel = () => {
            const model = this.normalizeCodexModel(modelSelect.value);
            const supportsMax = this.codexModelSupportsMaxEffort(model);
            maxOption.disabled = !supportsMax;

            if (!supportsMax && effortSelect.value === 'max') {
                effortSelect.value = this.normalizeCodexEffort(model, effortSelect.value);
            }
        };

        modelSelect.addEventListener('change', syncEffortForModel);
        syncEffortForModel();
    }

    mergeCodexSettingsFromCustomArgs(settings, customArgs) {
        const args = this.parseArgString(customArgs);
        if (args.length === 0) return settings;

        let sawFastFeature = false;
        let sawFastTier = false;

        for (let i = 0; i < args.length; i++) {
            const arg = args[i];
            const next = args[i + 1];

            if ((arg === '--model' || arg === '-m') && next) {
                settings.model = this.normalizeCodexModel(next);
                i++;
                continue;
            }

            if ((arg === '--config' || arg === '-c') && next) {
                const [key, value = ''] = next.split('=');
                const cleanValue = value.replace(/^["']|["']$/g, '');
                if (key === 'model_reasoning_effort') {
                    settings.effort = cleanValue;
                }
                if (key === 'service_tier' && cleanValue === 'fast') {
                    sawFastTier = true;
                }
                if (key === 'features.fast_mode' && cleanValue.toLowerCase() !== 'false') {
                    sawFastFeature = true;
                }
                i++;
                continue;
            }

            if (arg === '--enable' && next === 'fast_mode') {
                sawFastFeature = true;
                i++;
                continue;
            }

            // YOLO Mode for Codex.
            if (arg === '--dangerously-bypass-approvals-and-sandbox' || arg === '--yolo') {
                settings.yolo = true;
                continue;
            }

            if (arg === '--no-alt-screen') {
                settings.noAltScreen = true;
                continue;
            }
        }

        // Older VibeRails builds wrote only `--enable fast_mode`; keep the user's
        // intent when they next edit/save the environment by migrating it to the
        // current `service_tier=fast` launch args.
        if (sawFastFeature || sawFastTier) {
            settings.fastMode = true;
        }

        return settings;
    }

    parseArgString(value) {
        const args = [];
        let current = '';
        let inQuote = false;
        let quoteChar = '';
        let escaping = false;
        let hasToken = false;

        for (const ch of value || '') {
            if (escaping) {
                current += ch;
                escaping = false;
                hasToken = true;
                continue;
            }

            if (inQuote) {
                if (ch === '\\') {
                    escaping = true;
                    continue;
                }
                if (ch === quoteChar) {
                    inQuote = false;
                    continue;
                }
                current += ch;
                hasToken = true;
                continue;
            }

            if (ch === '"' || ch === "'") {
                inQuote = true;
                quoteChar = ch;
                hasToken = true;
                continue;
            }

            if (/\s/.test(ch)) {
                if (hasToken) {
                    args.push(current);
                    current = '';
                    hasToken = false;
                }
                continue;
            }

            current += ch;
            hasToken = true;
        }

        if (escaping) current += '\\';
        if (hasToken) args.push(current);
        return args;
    }

    normalizeCodexModel(model) {
        return (model || '').trim();
    }

    codexModelSupportsMaxEffort(model) {
        return this.normalizeCodexModel(model).toLowerCase() !== 'gpt-5.5';
    }

    normalizeCodexEffort(model, effort) {
        const normalizedEffort = (effort || '').trim();
        if (!this.codexModelSupportsMaxEffort(model) && normalizedEffort.toLowerCase() === 'max') {
            return 'xhigh';
        }
        return normalizedEffort;
    }

    renderCodexModelOptions(selectedModel) {
        const selected = this.normalizeCodexModel(selectedModel);
        // Hand-maintained pinned list — see runbooks/custom_envs/CLI_OPTIONS.md
        // ("Model Lists") to add a newly released model or drop a retired one.
        const options = [
            ['', 'Default (Codex recommended)'],
            ['gpt-5.6-sol', 'gpt-5.6-sol'],
            ['gpt-5.6-terra', 'gpt-5.6-terra'],
            ['gpt-5.6-luna', 'gpt-5.6-luna'],
            ['gpt-5.5', 'gpt-5.5']
        ];
        const known = new Set(options.map(([value]) => value));
        const rendered = options.map(([value, label]) =>
            `<option value="${this.app.escapeHtml(value)}" ${selected === value ? 'selected' : ''}>${this.app.escapeHtml(label)}</option>`
        );

        if (selected && !known.has(selected)) {
            rendered.push(`<option value="${this.app.escapeHtml(selected)}" selected>${this.app.escapeHtml(selected)} (custom)</option>`);
        }

        return rendered.join('');
    }

    normalizeClaudeModel(model) {
        return (model || '').trim();
    }

    renderClaudeModelOptions(selectedModel) {
        const selected = this.normalizeClaudeModel(selectedModel);
        // Hand-maintained pinned list — see runbooks/custom_envs/CLI_OPTIONS.md
        // ("Model Lists") to add a newly released model or drop a retired one.
        const options = [
            ['', 'Default (Claude recommended)'],
            ['claude-fable-5', 'claude-fable-5'],
            ['claude-opus-5', 'claude-opus-5'],
            ['claude-opus-4-8', 'claude-opus-4-8'],
            ['claude-opus-4-7', 'claude-opus-4-7'],
            ['claude-sonnet-5', 'claude-sonnet-5'],
            ['claude-sonnet-4-6', 'claude-sonnet-4-6'],
            ['claude-haiku-4-5', 'claude-haiku-4-5']
        ];
        const known = new Set(options.map(([value]) => value));
        const rendered = options.map(([value, label]) =>
            `<option value="${this.app.escapeHtml(value)}" ${selected === value ? 'selected' : ''}>${this.app.escapeHtml(label)}</option>`
        );

        if (selected && !known.has(selected)) {
            rendered.push(`<option value="${this.app.escapeHtml(selected)}" selected>${this.app.escapeHtml(selected)} (custom)</option>`);
        }

        return rendered.join('');
    }

    renderCopilotModelOptions(selectedModel) {
        const selected = (selectedModel || '').trim();
        // Hand-maintained pinned list — see runbooks/custom_envs/CLI_OPTIONS.md ("Model
        // Lists"). Availability varies by Copilot plan/policy, so these are suggestions;
        // an unavailable model errors at launch ("is not available"), and unknown saved
        // values survive via the `(custom)` fallback below.
        const options = [
            ['', 'Default (auto)'],
            ['claude-fable-5', 'claude-fable-5'],
            ['claude-opus-5', 'claude-opus-5'],
            ['claude-sonnet-5', 'claude-sonnet-5'],
            ['claude-sonnet-4.6', 'claude-sonnet-4.6'],
            ['claude-sonnet-4.5', 'claude-sonnet-4.5'],
            ['claude-haiku-4.5', 'claude-haiku-4.5'],
            ['claude-opus-4.8', 'claude-opus-4.8'],
            ['claude-opus-4.8-fast', 'claude-opus-4.8-fast'],
            ['claude-opus-4.7', 'claude-opus-4.7'],
            ['claude-opus-4.6', 'claude-opus-4.6'],
            ['claude-opus-4.5', 'claude-opus-4.5'],
            ['gpt-5.6-sol', 'gpt-5.6-sol'],
            ['gpt-5.6-terra', 'gpt-5.6-terra'],
            ['gpt-5.6-luna', 'gpt-5.6-luna'],
            ['gpt-5.5', 'gpt-5.5'],
            ['gpt-5.4', 'gpt-5.4'],
            ['gpt-5.4-mini', 'gpt-5.4-mini'],
            ['gpt-5.4-nano', 'gpt-5.4-nano'],
            ['gpt-5.3-codex', 'gpt-5.3-codex'],
            ['gpt-5-mini', 'gpt-5-mini'],
            ['gemini-3.7-flash', 'gemini-3.7-flash'],
            ['gemini-3.6-flash', 'gemini-3.6-flash'],
            ['gemini-3.5-flash', 'gemini-3.5-flash'],
            ['gemini-3.1-pro', 'gemini-3.1-pro'],
            ['mai-code-1.1-flash', 'mai-code-1.1-flash'],
            ['mai-code-1-flash', 'mai-code-1-flash'],
            ['raptor-mini', 'raptor-mini'],
            ['kimi-k2.7-code', 'kimi-k2.7-code'],
            ['kimi-k3', 'kimi-k3'],
            ['grok-4.6', 'grok-4.6'],
            ['grok-4.5', 'grok-4.5'],
        ];
        const known = new Set(options.map(([value]) => value));
        const rendered = options.map(([value, label]) =>
            `<option value="${this.app.escapeHtml(value)}" ${selected === value ? 'selected' : ''}>${this.app.escapeHtml(label)}</option>`
        );

        if (selected && !known.has(selected)) {
            rendered.push(`<option value="${this.app.escapeHtml(selected)}" selected>${this.app.escapeHtml(selected)} (custom)</option>`);
        }

        return rendered.join('');
    }

    buildCopilotCustomArgs(settings) {
        const s = settings || {};
        const args = [];
        const mode = this.normalizeCopilotMode(s.mode);
        const permissionPreset = this.normalizeCopilotPermissionPreset(s.permissionPreset);

        if (mode) {
            args.push('--mode', mode);
        }

        this.pushStringArg(args, '--model', s.model);

        if (permissionPreset === 'yolo') {
            args.push('--yolo');
        } else if (permissionPreset === 'allow-all-tools') {
            args.push('--allow-all-tools');
        }

        if (s.noAskUser) {
            args.push('--no-ask-user');
        }

        args.push(...this.parseArgString(s.additionalArgs || ''));

        return args.map(arg => this.quoteCustomArg(arg)).join(' ');
    }

    mergeCopilotSettingsFromCustomArgs(settings, customArgs) {
        const args = this.parseArgString(customArgs);
        if (args.length === 0) return settings;

        const additionalArgs = [];

        for (let i = 0; i < args.length; i++) {
            const arg = args[i];
            const next = args[i + 1];

            if (arg === '--yolo' || arg === '--allow-all') {
                settings.permissionPreset = 'yolo';
                continue;
            }

            if (arg === '--allow-all-tools') {
                settings.permissionPreset = 'allow-all-tools';
                continue;
            }

            if (arg === '--no-ask-user') {
                settings.noAskUser = true;
                continue;
            }

            if (arg === '--plan') {
                settings.mode = 'plan';
                continue;
            }

            if (arg === '--autopilot') {
                settings.mode = 'autopilot';
                continue;
            }

            if (arg.startsWith('--mode=')) {
                settings.mode = this.normalizeCopilotMode(arg.slice('--mode='.length));
                continue;
            }

            if (arg === '--mode' && next) {
                settings.mode = this.normalizeCopilotMode(next);
                i++;
                continue;
            }

            if (arg.startsWith('--model=')) {
                settings.model = arg.slice('--model='.length).trim();
                continue;
            }

            if (arg === '--model' && next) {
                settings.model = next.trim();
                i++;
                continue;
            }

            if (arg.startsWith('--interactive=')) {
                settings.initialMessage ||= arg.slice('--interactive='.length);
                continue;
            }

            if ((arg === '--interactive' || arg === '-i') && next) {
                settings.initialMessage ||= next;
                i++;
                continue;
            }

            additionalArgs.push(arg);
        }

        if (additionalArgs.length > 0) {
            settings.additionalArgs = additionalArgs.map(arg => this.quoteCustomArg(arg)).join(' ');
        }

        return settings;
    }

    normalizeCopilotMode(value) {
        const raw = (value || '').trim().toLowerCase();
        if (['interactive', 'plan', 'autopilot'].includes(raw)) return raw;
        return '';
    }

    normalizeCopilotPermissionPreset(value) {
        const raw = (value || '').trim().toLowerCase();
        if (raw === 'allow-all-tools' || raw === 'yolo') return raw;
        return '';
    }

    renderOpencodeModelOptions(selectedModel) {
        const selected = (selectedModel || '').trim();
        // Hand-maintained pinned list — see runbooks/custom_envs/CLI_OPTIONS.md ("Model Lists").
        // OpenCode model IDs are `provider/model` (e.g. anthropic/claude-sonnet-4-5). Verify the
        // current catalog with `opencode models` and refresh when providers ship/retire models;
        // unknown saved values survive via the `(custom)` fallback below.
        const options = [
            ['', 'Default (OpenCode recommended)'],
            ['anthropic/claude-opus-5', 'anthropic/claude-opus-5'],
            ['anthropic/claude-sonnet-5', 'anthropic/claude-sonnet-5'],
            ['anthropic/claude-opus-4-5', 'anthropic/claude-opus-4-5'],
            ['anthropic/claude-sonnet-4-5', 'anthropic/claude-sonnet-4-5'],
            ['openai/gpt-5.6', 'openai/gpt-5.6'],
            ['openai/gpt-5.5', 'openai/gpt-5.5'],
            ['openai/gpt-5.2', 'openai/gpt-5.2'],
            ['openai/gpt-5.1-codex', 'openai/gpt-5.1-codex'],
            ['google/gemini-3-pro', 'google/gemini-3-pro'],
            ['zai/glm-5.2', 'zai/glm-5.2'],
            ['zai-coding-plan/glm-5.3', 'zai-coding-plan/glm-5.3'],
            ['xai/grok-4.6', 'xai/grok-4.6'],
            ['opencode/gpt-5.1-codex', 'opencode/gpt-5.1-codex (Zen)'],
        ];
        const known = new Set(options.map(([value]) => value));
        const rendered = options.map(([value, label]) =>
            `<option value="${this.app.escapeHtml(value)}" ${selected === value ? 'selected' : ''}>${this.app.escapeHtml(label)}</option>`
        );

        if (selected && !known.has(selected)) {
            rendered.push(`<option value="${this.app.escapeHtml(selected)}" selected>${this.app.escapeHtml(selected)} (custom)</option>`);
        }

        return rendered.join('');
    }

    buildOpencodeCustomArgs(settings) {
        const s = settings || {};
        const args = [];

        this.pushStringArg(args, '--model', s.model);
        this.pushStringArg(args, '--agent', s.agent);

        // YOLO Mode for OpenCode: auto-approve permissions not explicitly denied (--auto).
        if (s.yoloMode) {
            args.push('--auto');
        }

        // Run without external plugins (--pure). Useful for isolated/reproducible envs
        // where third-party plugin behavior would otherwise leak in.
        if (s.pureMode) {
            args.push('--pure');
        }

        args.push(...this.parseArgString(s.additionalArgs || ''));

        return args.map(arg => this.quoteCustomArg(arg)).join(' ');
    }

    mergeOpencodeSettingsFromCustomArgs(settings, customArgs) {
        const args = this.parseArgString(customArgs);
        if (args.length === 0) return settings;

        const additionalArgs = [];

        for (let i = 0; i < args.length; i++) {
            const arg = args[i];
            const next = args[i + 1];

            // YOLO Mode for OpenCode.
            if (arg === '--auto') {
                settings.yoloMode = true;
                continue;
            }

            // Run without external plugins.
            if (arg === '--pure') {
                settings.pureMode = true;
                continue;
            }

            if (arg.startsWith('--model=')) {
                settings.model = arg.slice('--model='.length).trim();
                continue;
            }

            if (arg === '--model' && next) {
                settings.model = next.trim();
                i++;
                continue;
            }

            if (arg.startsWith('--agent=')) {
                settings.agent = arg.slice('--agent='.length).trim();
                continue;
            }

            if (arg === '--agent' && next) {
                settings.agent = next.trim();
                i++;
                continue;
            }

            // Initial message rides on --prompt at launch (see LlmPromptArgvBuilder); preserve
            // it if a user hand-wrote --prompt into the saved args.
            if (arg.startsWith('--prompt=')) {
                settings.initialMessage ||= arg.slice('--prompt='.length);
                continue;
            }

            if (arg === '--prompt' && next) {
                settings.initialMessage ||= next;
                i++;
                continue;
            }

            additionalArgs.push(arg);
        }

        if (additionalArgs.length > 0) {
            settings.additionalArgs = additionalArgs.map(arg => this.quoteCustomArg(arg)).join(' ');
        }

        return settings;
    }

    buildGrokCustomArgs(settings) {
        const s = settings || {};
        const args = ['-m', 'grok-4.6'];

        if (s.yoloMode) {
            args.push('--yolo');
        }

        args.push(...this.parseArgString(s.additionalArgs || ''));

        return args.map(arg => this.quoteCustomArg(arg)).join(' ');
    }

    mergeGrokSettingsFromCustomArgs(settings, customArgs) {
        const args = this.parseArgString(customArgs);
        if (args.length === 0) return settings;

        const additionalArgs = [];

        for (let i = 0; i < args.length; i++) {
            const arg = args[i];
            const next = args[i + 1];

            if (arg === '--yolo' || arg === '--always-approve' || arg === '--auto') {
                settings.yoloMode = true;
                continue;
            }

            if (arg === '-m' || arg === '--model') {
                if (next) i++;
                continue;
            }

            if (arg.startsWith('-m=') || arg.startsWith('--model=')) {
                continue;
            }

            if (arg === '--pure' || arg.startsWith('--agent=') || arg === '--agent') {
                if (arg === '--agent' && next) i++;
                continue;
            }

            if (arg.startsWith('--prompt=')) {
                settings.initialMessage ||= arg.slice('--prompt='.length);
                continue;
            }

            if (arg === '--prompt' && next) {
                settings.initialMessage ||= next;
                i++;
                continue;
            }

            additionalArgs.push(arg);
        }

        if (additionalArgs.length > 0) {
            settings.additionalArgs = additionalArgs.map(arg => this.quoteCustomArg(arg)).join(' ');
        }

        return settings;
    }

    mergeClaudeSettingsFromCustomArgs(settings, customArgs) {
        const args = this.parseArgString(customArgs);
        if (args.length === 0) return settings;

        for (let i = 0; i < args.length; i++) {
            const arg = args[i];
            const next = args[i + 1];

            if (arg === '--model' && next) {
                settings.model = this.normalizeClaudeModel(next);
                i++;
                continue;
            }

            if (arg === '--effort' && next) {
                settings.effort = next;
                i++;
                continue;
            }

            if (arg === '--no-session-persistence') {
                settings.noSessionPersistence = true;
                continue;
            }

            if (arg === '--system-prompt' && next) {
                settings.systemPrompt = next;
                i++;
                continue;
            }

            // YOLO Mode for Claude.
            if (arg === '--dangerously-skip-permissions') {
                settings.dangerouslySkipPermissions = true;
                continue;
            }

            if (arg === '--bare') {
                settings.bare = true;
                continue;
            }

            if (arg === '--debug') {
                settings.debug = true;
            }
        }

        return settings;
    }

    buildClaudeCustomArgs(settings) {
        const s = settings || {};
        const args = [];
        const model = this.normalizeClaudeModel(s.model);

        if (model) {
            args.push('--model', model);
        }

        if (s.effort) {
            args.push('--effort', s.effort);
        }

        if (s.noSessionPersistence) {
            args.push('--no-session-persistence');
        }

        this.pushStringArg(args, '--system-prompt', s.systemPrompt);

        // YOLO Mode for Claude: bypass all permission prompts.
        if (s.dangerouslySkipPermissions) {
            args.push('--dangerously-skip-permissions');
        }

        if (s.bare) {
            args.push('--bare');
        }

        if (s.debug) {
            args.push('--debug');
        }

        return args.map(arg => this.quoteCustomArg(arg)).join(' ');
    }

    pushStringArg(args, flag, value) {
        const normalized = this.normalizeCustomArgValue(value);
        if (normalized) {
            args.push(flag, normalized);
        }
    }

    pushListArg(args, flag, value, options = {}) {
        const values = this.splitCustomArgList(value, options);
        if (values.length > 0) {
            args.push(flag, ...values);
        }
    }

    pushRawValue(args, value) {
        const normalized = this.normalizeCustomArgValue(value);
        if (normalized) {
            args.push(normalized);
        }
    }

    splitCustomArgList(value, options = {}) {
        const text = (value || '').trim();
        if (!text) {
            return [];
        }

        if (options.splitWhitespace) {
            return text.split(/\s+/).map(v => this.normalizeCustomArgValue(v)).filter(Boolean);
        }

        return text
            .split(/\r?\n/)
            .map(v => this.normalizeCustomArgValue(v))
            .filter(Boolean);
    }

    normalizeCustomArgValue(value) {
        return (value || '').replace(/\s+/g, ' ').trim();
    }

    quoteCustomArg(value) {
        const text = String(value ?? '');
        if (!text) {
            return '""';
        }

        if (!/[\s"'\\]/.test(text)) {
            return text;
        }

        return `"${text.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
    }

    extractCliSettingsPayload(cli) {
        const cliLower = (cli || '').toLowerCase();
        // The Initial Message is one shared field under the CLI picker (see
        // renderInitialMessageField); every payload keeps its historical key — `prompt` for
        // Codex, `initialMessage` elsewhere — so the settings PUT endpoints stay untouched.
        const initialMessage = document.getElementById('env-initial-message')?.value ?? '';
        if (cliLower === 'antigravity') {
            return {
                initialMessage,
                model: document.getElementById('antigravity-model').value,
                sandboxEnabled: document.getElementById('antigravity-sandbox').checked,
                yoloMode: document.getElementById('antigravity-yolo').checked,
                additionalArgs: document.getElementById('antigravity-additional-args').value
            };
        }
        if (cliLower === 'codex') {
            const model = this.normalizeCodexModel(document.getElementById('codex-model').value);
            return {
                yolo: document.getElementById('codex-yolo').checked,
                noAltScreen: document.getElementById('codex-no-alt-screen').checked,
                prompt: initialMessage,
                model,
                effort: this.normalizeCodexEffort(model, document.getElementById('codex-effort').value),
                fastMode: document.getElementById('codex-fast-mode').checked
            };
        }
        if (cliLower === 'claude') {
            return {
                model: this.normalizeClaudeModel(document.getElementById('claude-model').value),
                effort: document.getElementById('claude-effort').value,
                fastMode: document.getElementById('claude-fast-mode').checked,
                initialMessage,
                noSessionPersistence: document.getElementById('claude-no-session-persistence').checked,
                systemPrompt: document.getElementById('claude-system-prompt').value,
                dangerouslySkipPermissions: document.getElementById('claude-dangerously-skip-permissions').checked,
                bare: document.getElementById('claude-bare').checked,
                debug: document.getElementById('claude-debug').checked
            };
        }
        if (cliLower === 'copilot') {
            return {
                initialMessage,
                mode: this.normalizeCopilotMode(document.getElementById('copilot-mode').value),
                model: document.getElementById('copilot-model').value.trim(),
                permissionPreset: this.normalizeCopilotPermissionPreset(document.getElementById('copilot-permission-preset').value),
                noAskUser: document.getElementById('copilot-no-ask-user').checked,
                additionalArgs: document.getElementById('copilot-additional-args').value
            };
        }
        if (this.isNativeGrokCli(cliLower)) {
            return {
                initialMessage,
                yoloMode: document.getElementById('grok-yolo').checked,
                additionalArgs: document.getElementById('grok-additional-args').value
            };
        }
        if (this.isOpencodeBackedCli(cliLower)) {
            return {
                initialMessage,
                model: document.getElementById('opencode-model').value.trim(),
                agent: document.getElementById('opencode-agent').value.trim(),
                yoloMode: document.getElementById('opencode-yolo').checked,
                pureMode: document.getElementById('opencode-pure')?.checked ?? false,
                additionalArgs: document.getElementById('opencode-additional-args').value
            };
        }
        return null;
    }

    buildCliSettingsHtml(cli, settings) {
        const s = settings || {};
        const cliLower = (cli || '').toLowerCase();

        if (cliLower === 'antigravity') {
            const antigravityYoloMode = Boolean(s.yoloMode);
            const antigravityAdditionalArgs = this.app.escapeHtml(s.additionalArgs || '');
            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Antigravity CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Model</label>
                    <select class="form-select" id="antigravity-model">
                        ${this.renderAntigravityModelOptions(s.model)}
                    </select>
                    <small class="form-text text-muted">Passed as <code>--model</code> when launching agy (names from <code>agy models</code>)</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="antigravity-sandbox" ${s.sandboxEnabled ? 'checked' : ''}>
                        <label class="form-check-label" for="antigravity-sandbox">Sandbox Mode</label>
                    </div>
                    <small class="form-text text-muted">Launches with <code>--sandbox</code> to run with terminal restrictions enabled</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="antigravity-yolo" ${antigravityYoloMode ? 'checked' : ''}>
                        <label class="form-check-label" for="antigravity-yolo">YOLO Mode</label>
                    </div>
                    <small class="form-text text-muted text-warning">Launches with <code>--dangerously-skip-permissions</code>, auto-approving every tool permission request</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Additional Arguments</label>
                    <input type="text" class="form-control" id="antigravity-additional-args" value="${antigravityAdditionalArgs}" placeholder="Optional extra agy flags (e.g. --add-dir ...)">
                    <small class="form-text text-muted">Preserves advanced flags not covered above</small>
                </div>
            `;
        }

        if (cliLower === 'codex') {
            const codexModel = this.normalizeCodexModel(s.model);
            const codexEffort = this.normalizeCodexEffort(codexModel, s.effort);
            const codexMaxEffortDisabled = !this.codexModelSupportsMaxEffort(codexModel);
            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Codex CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Model</label>
                    <select class="form-select" id="codex-model">
                        ${this.renderCodexModelOptions(codexModel)}
                    </select>
                    <small class="form-text text-muted">Passed as <code>--model</code> when launching Codex</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Effort</label>
                    <select class="form-select" id="codex-effort">
                        <option value="" ${codexEffort === '' ? 'selected' : ''}>Default</option>
                        <option value="minimal" ${codexEffort === 'minimal' ? 'selected' : ''}>Minimal</option>
                        <option value="low" ${codexEffort === 'low' ? 'selected' : ''}>Low</option>
                        <option value="medium" ${codexEffort === 'medium' ? 'selected' : ''}>Medium</option>
                        <option value="high" ${codexEffort === 'high' ? 'selected' : ''}>High</option>
                        <option value="xhigh" ${codexEffort === 'xhigh' ? 'selected' : ''}>XHigh</option>
                        <option value="max" ${codexEffort === 'max' ? 'selected' : ''} ${codexMaxEffortDisabled ? 'disabled' : ''}>Max</option>
                        <option value="ultra" ${codexEffort === 'ultra' ? 'selected' : ''}>Ultra</option>
                    </select>
                    <small class="form-text text-muted">Passed as <code>-c model_reasoning_effort=&lt;level&gt;</code></small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="codex-fast-mode" ${s.fastMode ? 'checked' : ''}>
                        <label class="form-check-label" for="codex-fast-mode">Fast Mode</label>
                    </div>
                    <small class="form-text text-muted">Launches with <code>service_tier=fast</code> for supported ChatGPT-backed models</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="codex-yolo" ${s.yolo ? 'checked' : ''}>
                        <label class="form-check-label" for="codex-yolo">YOLO Mode</label>
                    </div>
                    <small class="form-text text-muted text-warning">Launches with <code>--dangerously-bypass-approvals-and-sandbox</code>, running commands without approvals or sandboxing</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="codex-no-alt-screen" ${s.noAltScreen ? 'checked' : ''}>
                        <label class="form-check-label" for="codex-no-alt-screen">No Alternate Screen</label>
                    </div>
                    <small class="form-text text-muted">Disable alternate screen mode for the TUI</small>
                </div>
            `;
        }

        if (cliLower === 'copilot') {
            const mode = this.normalizeCopilotMode(s.mode);
            const permissionPreset = this.normalizeCopilotPermissionPreset(s.permissionPreset);
            const model = s.model || '';
            const additionalArgs = this.app.escapeHtml(s.additionalArgs || '');

            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Copilot CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Mode</label>
                    <select class="form-select" id="copilot-mode">
                        <option value="" ${mode === '' ? 'selected' : ''}>Default</option>
                        <option value="interactive" ${mode === 'interactive' ? 'selected' : ''}>Interactive</option>
                        <option value="plan" ${mode === 'plan' ? 'selected' : ''}>Plan</option>
                        <option value="autopilot" ${mode === 'autopilot' ? 'selected' : ''}>Autopilot</option>
                    </select>
                    <small class="form-text text-muted">Passed as <code>--mode</code> when launching Copilot</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Model</label>
                    <select class="form-select" id="copilot-model">
                        ${this.renderCopilotModelOptions(model)}
                    </select>
                    <small class="form-text text-muted">Passed as <code>--model</code>; leave blank to use Copilot's default</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Permissions</label>
                    <select class="form-select" id="copilot-permission-preset">
                        <option value="" ${permissionPreset === '' ? 'selected' : ''}>Default prompts</option>
                        <option value="allow-all-tools" ${permissionPreset === 'allow-all-tools' ? 'selected' : ''}>Auto-approve tools</option>
                        <option value="yolo" ${permissionPreset === 'yolo' ? 'selected' : ''}>YOLO: all permissions</option>
                    </select>
                    <small class="form-text text-muted">Uses <code>--allow-all-tools</code> or <code>--yolo</code>; YOLO is equivalent to allowing all permissions</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="copilot-no-ask-user" ${s.noAskUser ? 'checked' : ''}>
                        <label class="form-check-label" for="copilot-no-ask-user">Don't Ask User</label>
                    </div>
                    <small class="form-text text-muted">Disables Copilot's <code>ask_user</code> tool with <code>--no-ask-user</code></small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Additional Arguments</label>
                    <input type="text" class="form-control" id="copilot-additional-args" value="${additionalArgs}" placeholder="Optional extra Copilot flags">
                    <small class="form-text text-muted">Preserves advanced flags not covered above</small>
                </div>
            `;
        }

        if (this.isNativeGrokCli(cliLower)) {
            const additionalArgs = this.app.escapeHtml(s.additionalArgs || '');
            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Grok 4.6 CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Model</label>
                    <input type="text" class="form-control" id="grok-model" value="grok-4.6" disabled>
                    <small class="form-text text-muted">Pinned to <code>grok-4.6</code> — launched as <code>-m grok-4.6</code>. OpenCode still offers <code>xai/grok-4.6</code> in its own model list.</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="grok-yolo" ${s.yoloMode ? 'checked' : ''}>
                        <label class="form-check-label" for="grok-yolo">YOLO Mode</label>
                    </div>
                    <small class="form-text text-muted text-warning">Launches with <code>--yolo</code>, auto-approving tool executions</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Additional Arguments</label>
                    <input type="text" class="form-control" id="grok-additional-args" value="${additionalArgs}" placeholder="Optional extra grok flags">
                    <small class="form-text text-muted">Preserves advanced flags not covered above</small>
                </div>
            `;
        }

        if (this.isOpencodeBackedCli(cliLower)) {
            const pinnedModel = this.pinnedModelForCli(cliLower);
            // For OpenCode-backed pseudo-CLIs the model is pinned — show a read-only
            // display of the pinned provider/model instead of the editable dropdown, so
            // users can't accidentally switch the env to a different model.
            const model = pinnedModel || (s.model || '');
            const modelField = pinnedModel
                ? `<input type="text" class="form-control" id="opencode-model" value="${this.app.escapeHtml(pinnedModel)}" disabled>
                   <small class="form-text text-muted">Pinned to <code>${this.app.escapeHtml(pinnedModel)}</code> — this env always launches with this model. Use a plain OpenCode env for other models.</small>`
                : `<select class="form-select" id="opencode-model">
                       ${this.renderOpencodeModelOptions(model)}
                   </select>
                   <small class="form-text text-muted">Passed as <code>--model provider/model</code>; leave blank for OpenCode's default</small>`;
            const headerLabel = `${this.cliDisplayName(cliLower)} CLI Settings`;
            const agent = this.app.escapeHtml(s.agent || '');
            const additionalArgs = this.app.escapeHtml(s.additionalArgs || '');

            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">${headerLabel}</h6>
                <div class="mb-3">
                    <label class="form-label">Model</label>
                    ${modelField}
                </div>
                <div class="mb-3">
                    <label class="form-label">Agent</label>
                    <input type="text" class="form-control" id="opencode-agent" value="${agent}" placeholder="Optional, e.g. build, plan, or a custom agent">
                    <small class="form-text text-muted">Passed as <code>--agent</code> when launching OpenCode</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="opencode-yolo" ${s.yoloMode ? 'checked' : ''}>
                        <label class="form-check-label" for="opencode-yolo">YOLO Mode</label>
                    </div>
                    <small class="form-text text-muted text-warning">Launches with <code>--auto</code>, auto-approving permissions not explicitly denied</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="opencode-pure" ${s.pureMode ? 'checked' : ''}>
                        <label class="form-check-label" for="opencode-pure">Run Without Plugins</label>
                    </div>
                    <small class="form-text text-muted">Launches with <code>--pure</code>, skipping external plugins for a clean/reproducible environment</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Additional Arguments</label>
                    <input type="text" class="form-control" id="opencode-additional-args" value="${additionalArgs}" placeholder="Optional extra opencode flags">
                    <small class="form-text text-muted">Preserves advanced flags not covered above</small>
                </div>
            `;
        }

        if (cliLower === 'claude') {
            const effort = s.effort || '';
            const claudeModel = this.normalizeClaudeModel(s.model);
            const systemPrompt = this.app.escapeHtml(s.systemPrompt || '');

            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Claude CLI Settings</h6>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-dangerously-skip-permissions" ${s.dangerouslySkipPermissions ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-dangerously-skip-permissions">YOLO Mode</label>
                    </div>
                    <small class="form-text text-muted text-warning">Launches with <code>--dangerously-skip-permissions</code>, bypassing all permission prompts</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Model</label>
                    <select class="form-select" id="claude-model">
                        ${this.renderClaudeModelOptions(claudeModel)}
                    </select>
                    <small class="form-text text-muted">Passed as <code>--model</code> when launching Claude</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Effort</label>
                    <select class="form-select" id="claude-effort">
                        <option value="" ${effort === '' ? 'selected' : ''}>Default</option>
                        <option value="low" ${effort === 'low' ? 'selected' : ''}>Low</option>
                        <option value="medium" ${effort === 'medium' ? 'selected' : ''}>Medium</option>
                        <option value="high" ${effort === 'high' ? 'selected' : ''}>High</option>
                        <option value="xhigh" ${effort === 'xhigh' ? 'selected' : ''}>XHigh</option>
                        <option value="max" ${effort === 'max' ? 'selected' : ''}>Max</option>
                    </select>
                    <small class="form-text text-muted">Sets the effort level for this session</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-fast-mode" ${s.fastMode ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-fast-mode">Fast Mode</label>
                    </div>
                    <small class="form-text text-muted">Sets <code>fastMode</code> in settings.json (same as <code>/fast</code>). Opus-only — Claude switches to Opus when on. Research preview; billed via usage credits.</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-no-session-persistence" ${s.noSessionPersistence ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-no-session-persistence">No Session Persistence</label>
                    </div>
                    <small class="form-text text-muted">Do not save sessions to disk for print-mode runs</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">System Prompt</label>
                    <textarea class="form-control" id="claude-system-prompt" rows="3" placeholder="Replace the entire system prompt">${systemPrompt}</textarea>
                    <small class="form-text text-muted">Passed as --system-prompt when launching Claude</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-bare" ${s.bare ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-bare">Bare Mode</label>
                    </div>
                    <small class="form-text text-muted">Skip discovery of hooks, skills, plugins, MCP servers, memory, and CLAUDE.md</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-debug" ${s.debug ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-debug">Debug Mode</label>
                    </div>
                    <small class="form-text text-muted">Enable --debug for this launch</small>
                </div>
            `;
        }

        return '';
    }

    // The display names the Initial Message wording uses — matching the product's own voice
    // (the Antigravity CLI is spoken of as "agy", GLM 5.2 / GLM 5.3 are not called OpenCode).
    cliDisplayName(cli) {
        const cliLower = (cli || '').toLowerCase();
        if (cliLower === 'claude') return 'Claude';
        if (cliLower === 'codex') return 'Codex';
        if (cliLower === 'copilot') return 'Copilot';
        if (cliLower === 'antigravity') return 'agy';
        if (cliLower === 'glm-5.2') return 'GLM 5.2';
        if (cliLower === 'glm-5.3') return 'GLM 5.3';
        if (cliLower === 'grok-4.6') return 'Grok 4.6';
        if (cliLower === 'opencode') return 'OpenCode';
        return 'the CLI';
    }

    initialMessagePlaceholder(cli) {
        return `Optional. Sent to ${this.cliDisplayName(cli)} as soon as the session starts.`;
    }

    /**
     * The one shared Initial Message block, rendered directly under the CLI picker for every
     * CLI (it backs the single Environments.CustomPrompt column). `escapedValue` is already
     * HTML-escaped by the caller.
     */
    renderInitialMessageField(cli, escapedValue) {
        const cliName = this.app.escapeHtml(this.cliDisplayName(cli));
        return `
                <div class="mb-3">
                    <div class="env-initial-message-head">
                        <label class="form-label" for="env-initial-message">Initial Message</label>
                        <button type="button" class="btn btn-sm btn-outline-secondary" data-insert-step-output aria-haspopup="menu">
                            Insert step output
                        </button>
                    </div>
                    <textarea class="form-control" id="env-initial-message" rows="6" maxlength="6000"
                              placeholder="${this.app.escapeHtml(this.initialMessagePlaceholder(cli))}">${escapedValue}</textarea>
                    <div class="env-initial-message-refs text-muted small d-none" data-initial-message-refs></div>
                    <small class="form-text text-muted">
                        Sent to <span data-initial-message-cli>${cliName}</span> as your first chat message the moment the session starts — exactly as if you typed it.
                        <code>{{name}}</code> asks you for a value at launch (<code>{{name default=&quot;…&quot;}}</code> pre-fills it);
                        <code>{{datetime}}</code>, <code>{{date}}</code>, <code>{{time}}</code>, <code>{{git_branch}}</code> and <code>{{env_name}}</code> fill in automatically.
                    </small>
                </div>`;
    }

    /**
     * Wires the shared Initial Message field: the referenced-steps caption (refreshed on typing)
     * and the "Insert step output" picker. `getSteps` is a closure over the form's live step list
     * (editedSteps ?? initialSteps) so steps added in the editor are insertable before saving.
     * Returns the caption refresher so the steps editor's onSave can re-run it.
     */
    bindInitialMessageField(getSteps) {
        const textarea = document.getElementById('env-initial-message');
        if (!textarea) return () => { };

        const refresh = () => this.updateInitialMessageStepRefs(textarea.value, getSteps());
        textarea.addEventListener('input', refresh);
        refresh();

        document.querySelector('[data-insert-step-output]')?.addEventListener('click', event => {
            this.toggleStepOutputMenu(event.currentTarget, textarea, getSteps, refresh);
        });

        return refresh;
    }

    /** "Uses step output: Run Tests" under the textarea, with a warning for dangling references. */
    updateInitialMessageStepRefs(value, steps) {
        const refs = document.querySelector('[data-initial-message-refs]');
        if (!refs) return;

        const tokens = [...String(value ?? '').matchAll(/\{\{\s*step\s*:\s*([0-9a-fA-F-]+)\s*\}\}/gi)];
        if (tokens.length === 0) {
            refs.classList.add('d-none');
            refs.innerHTML = '';
            return;
        }

        const list = Array.isArray(steps) ? steps : [];
        const names = [];
        let missing = 0;
        const seen = new Set();
        for (const match of tokens) {
            const id = match[1].toLowerCase();
            if (seen.has(id)) continue;
            seen.add(id);
            const step = list.find(candidate => String(candidate?.id ?? '').toLowerCase() === id);
            if (step) names.push(this.app.escapeHtml(stepDisplayName(step)));
            else missing++;
        }

        const parts = [];
        if (names.length > 0) parts.push(`Uses step output: <strong>${names.join('</strong>, <strong>')}</strong>`);
        if (missing > 0) parts.push(`<span class="text-warning">references ${missing === 1 ? 'a deleted step' : `${missing} deleted steps`}</span>`);
        refs.innerHTML = parts.join(' · ');
        refs.classList.remove('d-none');
    }

    /**
     * A small menu under the "Insert step output" button listing the form's current steps by
     * name; picking one inserts {{step:<id>}} at the cursor. Plain DOM rather than a Bootstrap
     * dropdown so it works inside the modal without extra plumbing.
     */
    toggleStepOutputMenu(button, textarea, getSteps, onInserted) {
        const existing = document.querySelector('.env-step-output-menu');
        if (existing) {
            // Close through the menu's own close() so the two capture-phase document
            // listeners registered on open are removed; a bare .remove() leaked them
            // (and their closures) on every toggle-closed.
            if (typeof existing._vbCloseMenu === 'function') {
                existing._vbCloseMenu();
            } else {
                existing.remove();
            }
            return;
        }

        const steps = (getSteps() || []).filter(step => step?.id);
        const menu = document.createElement('div');
        menu.className = 'env-step-output-menu';
        menu.setAttribute('role', 'menu');
        menu.innerHTML = steps.length === 0
            ? '<div class="env-step-output-empty text-muted small">No steps yet — add one with the Steps button below, then insert its output here.</div>'
            : steps.map(step => `
                <button type="button" class="env-step-output-item" role="menuitem"
                        data-step-id="${this.app.escapeHtml(step.id)}">${this.app.escapeHtml(stepDisplayName(step))}</button>`).join('');
        button.closest('.env-initial-message-head')?.appendChild(menu);

        const close = () => {
            menu.remove();
            document.removeEventListener('click', onDocClick, true);
            document.removeEventListener('keydown', onKeydown, true);
        };
        menu._vbCloseMenu = close;
        const onDocClick = event => {
            if (!menu.contains(event.target) && !button.contains(event.target)) close();
        };
        const onKeydown = event => {
            if (event.key !== 'Escape') return;
            // Swallowed so Escape closes the menu, not the whole environment modal.
            event.stopPropagation();
            close();
            button.focus();
        };
        document.addEventListener('click', onDocClick, true);
        document.addEventListener('keydown', onKeydown, true);

        menu.addEventListener('click', event => {
            const item = event.target.closest('[data-step-id]');
            if (!item) return;
            const token = `{{step:${item.dataset.stepId}}}`;
            const start = textarea.selectionStart ?? textarea.value.length;
            const end = textarea.selectionEnd ?? start;
            textarea.setRangeText(token, start, end, 'end');
            textarea.focus();
            close();
            onInserted();
        });
    }

    async removeEnvironment(name, { onChanged = null } = {}) {
        // Steps cascade with the environment row, so say so rather than letting a configured
        // setup chain disappear silently behind a generic "delete this profile".
        const stepCount = (this.app.data.environments.find(env => env.name === name)?.steps || []).length;
        const stepNote = stepCount === 0
            ? ''
            : ` and its ${stepCount} step${stepCount === 1 ? '' : 's'}`;

        this.app.showModal('Remove Environment', `
            <div class="text-center py-3">
                <div class="mb-3 text-danger">
                    <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" fill="currentColor" viewBox="0 0 16 16">
                        <path d="M11 1.5v1h3.5a.5.5 0 0 1 0 1h-.538l-.853 10.66A2 2 0 0 1 11.115 16h-6.23a2 2 0 0 1-1.994-1.84L2.038 3.5H1.5a.5.5 0 0 1 0-1H5v-1A1.5 1.5 0 0 1 6.5 0h3A1.5 1.5 0 0 1 11 1.5m-5 0v1h4v-1a.5.5 0 0 0-.5-.5h-3a.5.5 0 0 0-.5.5M4.5 5.029l.5 8.5a.5.5 0 1 0 .998-.06l-.5-8.5a.5.5 0 1 0-.998.06m6.53-.06a.5.5 0 0 0-.515.479l-.5 8.5a.5.5 0 1 0 .998.06l.5-8.5a.5.5 0 0 0-.484-.539M8 5.5a.5.5 0 0 0-.5.5v8.5a.5.5 0 0 0 1 0V6a.5.5 0 0 0-.5-.5"/>
                    </svg>
                </div>
                <h5>Remove environment "${this.app.escapeHtml(name)}"?</h5>
                <p class="text-muted small px-4">This will permanently delete this environment profile${stepNote}. This action cannot be undone.</p>
            </div>
            <div class="d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="button" class="btn btn-danger" id="confirm-delete-btn">Remove Environment</button>
            </div>
        `);

        // Wait for user confirmation
        document.getElementById('confirm-delete-btn').onclick = async () => {
            this.app.closeModal();
            try {
                await this.app.apiCall(`/api/v1/environments/${encodeURIComponent(name)}`, 'DELETE');
                await this.refreshEnvironments();
                if (onChanged) {
                    await onChanged();
                } else {
                    this.app.navigate('environments');
                }
            } catch (error) {
                this.app.showError(`Failed to remove environment: ${error.message}`);
            }
        };
    }

    launchEnvironment(name, cli) {
        if (this.app.data.isInGit) {
            // In git repo - launch in current working directory
            this.doLaunchEnvironment(name, cli, this.app.data.configs.launchDirectory);
        } else {
            // Not in git - prompt user for directory
            this.showDirectorySelectModal(name, cli);
        }
    }

    showDirectorySelectModal(envName, cli) {
        this.app.showModal(`Launch ${envName}`, `
            <form id="launch-env-form">
                <div class="mb-3">
                    <label class="form-label">Working Directory</label>
                    <input type="text" class="form-control" id="launch-directory" required placeholder="Enter the project directory path">
                    <small class="form-text text-muted">The directory where the CLI will be launched</small>
                </div>
                <button type="submit" class="btn btn-success">Launch</button>
            </form>
        `);

        document.getElementById('launch-env-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            const directory = document.getElementById('launch-directory').value;
            if (directory) {
                this.app.closeModal();
                await this.doLaunchEnvironment(envName, cli, directory);
            }
        });
    }

    async doLaunchEnvironment(envName, cli, workingDirectory) {
        try {
            const requestBody = {
                workingDirectory: workingDirectory,
                environmentName: envName,
                args: []
            };

            const response = await this.app.apiCall(`/api/v1/cli/launch/${cli.toLowerCase()}`, 'POST', requestBody);

            if (response.success) {
                this.app.showToast('Environment Launched', `${envName} launched successfully`, 'success');
            } else {
                this.app.showToast('Launch Error', response.message, 'error');
            }
        } catch (error) {
            this.app.showError(`Failed to launch environment: ${error.message}`);
        }
    }

    setEnvironments(environments = []) {
        this.app.data.environments = (environments || []).map(env => ({
            id: env.id,
            name: env.name,
            cli: env.cli,
            customArgs: env.customArgs,
            customPrompt: env.customPrompt,
            defaultPrompt: env.defaultPrompt,
            hidden: Boolean(env.hidden),
            automationWorker: Boolean(env.automationWorker),
            // Workspace mode is set the moment the environment is created, but the clone is
            // only made on first launch — so a mode of 1/2 with a null sandbox id is the
            // normal "configured, not provisioned yet" state, not an error.
            workspaceMode: Number(env.workspaceMode) || WORKSPACE_MODE.PROJECT,
            workspaceSandboxId: env.workspaceSandboxId ?? null,
            workspacePath: env.workspacePath || null,
            workspaceBranch: env.workspaceBranch || null,
            // Normalized here so the edit form and the delete confirm both read the same shape,
            // whether the row came from the list endpoint or a single-environment fetch.
            steps: normalizeSteps(env.steps),
            lastUsed: env.lastUsed || this.app.formatRelativeTime(env.lastUsedUTC)
        }));
        return this.app.data.environments;
    }

    async refreshEnvironments() {
        try {
            const response = await this.app.apiCall('/api/v1/environments', 'GET');
            this.setEnvironments(response.environments || []);
            await this.app.llmPickerController.refreshFromEnvironments(response.environments || []);
        } catch (error) {
            console.error('Failed to refresh environments:', error);
            // Keep the last known-good profiles. Clearing them after a successful edit can make
            // a Jobs draft silently reopen on the base CLI and drop its environment reference if
            // the user saves before the next refresh succeeds.
        }
    }

    async launchInWebUI(envId, envName, cli) {
        this.app.showToast('Web Terminal',
            `Launching ${envName} (${cli})...`,
            'info');
        return this.app.terminalController.launchInFocus({
            cli,
            environmentName: envName,
            title: envName,
            tabLabel: envName,
            forceNewTab: true
        }, { preselectedEnvId: envId });
    }
}

