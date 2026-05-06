import { enhanceLlmSelectWithTomSelect } from './utils.js';

export class EnvironmentController {
    constructor(app) {
        this.app = app;
    }

    async loadEnvironments() {
        const content = document.getElementById('app-content');
        if (!content) return;

        // Fetch environments from API
        await this.refreshEnvironments();
        // Also refresh sandboxes data
        await this.app.refreshDashboardData();

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('environments-template');
        const root = fragment.querySelector('[data-view="environments"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            this.app.bindAction(root, '[data-action="create-environment"]', () => this.createEnvironment());
            this.app.bindAction(root, '[data-action="create-sandbox"]', () => {
                this.app.sandboxController.createSandbox();
            });

            const tableSlot = root.querySelector('[data-environments-table]');
            if (tableSlot) {
                tableSlot.innerHTML = this.renderEnvironmentsTable();
                this.app.bindActions(tableSlot, '[data-action="remove-environment"]', (element) => {
                    const name = element.dataset.envName;
                    if (name) {
                        this.removeEnvironment(name);
                    }
                });
                this.app.bindActions(tableSlot, '[data-action="edit-environment"]', (element) => {
                    const name = element.dataset.envName;
                    if (name) {
                        this.editEnvironment(name);
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

            const sandboxesTableSlot = root.querySelector('[data-sandboxes-table]');
            if (sandboxesTableSlot) {
                sandboxesTableSlot.innerHTML = this.renderSandboxesTable();

                // Populate selects
                sandboxesTableSlot.querySelectorAll('[data-sb-cli-select]').forEach(select => {
                    this.app.dashboardController.populateSandboxCliSelect(select);
                });

                // Bind actions
                this.app.bindActions(sandboxesTableSlot, '[data-action="sandbox-diff"]', (el) => {
                    this.app.sandboxController.showDiff(el.dataset.sbId, el.dataset.sbName);
                });
                this.app.bindActions(sandboxesTableSlot, '[data-action="sandbox-merge"]', (el) => {
                    this.app.sandboxController.mergeLocally(el.dataset.sbId, el.dataset.sbName, el.dataset.sbBranch);
                });
                this.app.bindActions(sandboxesTableSlot, '[data-action="sandbox-push"]', (el) => {
                    this.app.sandboxController.pushToRemote(el.dataset.sbId, el.dataset.sbName);
                });
                this.app.bindActions(sandboxesTableSlot, '[data-action="sandbox-vscode"]', (el) => {
                    this.app.sandboxController.launchVSCode(el.dataset.sbId, el.dataset.sbName);
                });
                this.app.bindActions(sandboxesTableSlot, '[data-action="sandbox-delete"]', (el) => {
                    this.app.sandboxController.deleteSandbox(el.dataset.sbId, el.dataset.sbName);
                });

                const resolveCli = (el) => {
                    const row = el.closest('tr');
                    const select = row.querySelector('[data-sb-cli-select]');
                    const selection = this.app.dashboardController.parseSandboxCliSelection(select);
                    
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

    renderEnvironmentsTable() {
        if (this.app.data.environments.length === 0) {
            return '<p class="text-muted text-center py-3">No environments configured. Create your first environment to get started.</p>';
        }

        const escape = (value) => this.app.escapeHtml(value);

        return `
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
                    <tbody>
                        ${this.app.data.environments.map(env => {
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
                                        <button class="btn btn-xs btn-outline-secondary env-action-btn" type="button" data-action="edit-environment" data-env-name="${safeName}" title="Settings" aria-label="Edit ${safeName} settings">
                                            <i class="fa-solid fa-sliders"></i>
                                        </button>
                                        <button class="btn btn-xs btn-outline-danger env-action-btn" type="button" data-action="remove-environment" data-env-name="${safeName}" title="Delete" aria-label="Delete ${safeName}">
                                            <i class="fa-solid fa-trash"></i>
                                        </button>
                                    </div>
                                </td>
                            </tr>
                        `}).join('')}
                    </tbody>
                </table>
            </div>
        `;
    }

    renderSandboxesTable() {
        const sandboxes = this.app.data.sandboxes || [];
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

    createEnvironment() {
        this.showEnvironmentForm({ mode: 'create' });
    }

    async editEnvironment(name) {
        const env = this.app.data.environments.find(e => e.name === name);
        if (!env) return;

        const cliSettings = await this.loadCliSettings(env.cli, env.name);

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
        if (cliLower === 'gemini') {
            if (env.customPrompt) {
                cliSettings.initialMessage = env.customPrompt;
            }
            this.mergeGeminiSettingsFromCustomArgs(cliSettings, env.customArgs || '');
        }
        if (cliLower === 'copilot') {
            if (env.customPrompt) {
                cliSettings.initialMessage = env.customPrompt;
            }
            this.mergeCopilotSettingsFromCustomArgs(cliSettings, env.customArgs || '');
        }

        this.showEnvironmentForm({ mode: 'edit', env, cliSettings });
    }

    showEnvironmentForm({ mode, env = null, cliSettings = {} }) {
        const isEdit = mode === 'edit';
        const escapeHtml = (text) => {
            const div = document.createElement('div');
            div.textContent = text ?? '';
            return div.innerHTML;
        };

        const cliOptions = ['codex', 'claude', 'gemini', 'copilot'];
        const initialCli = isEdit ? env.cli : cliOptions[0];
        const title = isEdit ? `Edit Environment: ${env.name}` : 'Create New Environment';
        const submitLabel = isEdit ? 'Save Changes' : 'Create Environment';
        const submitIcon = isEdit
            ? `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                <path d="M16 8A8 8 0 1 1 0 8a8 8 0 0 1 16 0m-3.97-3.03a.75.75 0 0 0-1.08.022L7.477 9.417 5.384 7.323a.75.75 0 0 0-1.06 1.06L6.97 11.03a.75.75 0 0 0 1.079-.02l3.992-4.99a.75.75 0 0 0-.01-1.05z"/>
              </svg>`
            : `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                <path d="M8 4a.5.5 0 0 1 .5.5v3h3a.5.5 0 0 1 0 1h-3v3a.5.5 0 0 1-1 0v-3h-3a.5.5 0 0 1 0-1h3v-3A.5.5 0 0 1 8 4"/>
              </svg>`;

        const nameField = isEdit
            ? `<input type="text" class="form-control" value="${escapeHtml(env.name)}" disabled>`
            : `<input type="text" class="form-control" id="env-name" required>`;

        const cliField = isEdit
            ? `<input type="text" class="form-control" value="${escapeHtml(env.cli)}" disabled>`
            : `<select class="form-select" id="env-cli" required>
                ${cliOptions.map(c => `<option value="${c}">${c.charAt(0).toUpperCase() + c.slice(1)}</option>`).join('')}
              </select>`;

        const customArgsValue = isEdit ? escapeHtml(env.customArgs || '') : '';
        const usesManagedArgs = this.usesManagedCustomArgs(initialCli);

        this.app.showModal(title, `
            <form id="env-form">
                <div class="mb-3">
                    <label class="form-label">Environment Name</label>
                    ${nameField}
                </div>
                <div class="mb-3">
                    <label class="form-label">CLI Type</label>
                    ${cliField}
                </div>
                <div class="mb-3" data-custom-args-group ${usesManagedArgs ? 'style="display: none;"' : ''}>
                    <label class="form-label">Custom Arguments</label>
                    <input type="text" class="form-control" id="env-custom-args" value="${customArgsValue}" placeholder="e.g., --yolo --sandbox">
                    <small class="form-text text-muted">Arguments passed to the CLI when launching with this environment</small>
                </div>
                <div data-cli-settings-slot>${this.buildCliSettingsHtml(initialCli, cliSettings || {})}</div>
                <button type="submit" class="btn btn-primary d-flex align-items-center gap-2">
                    ${submitIcon}
                    ${submitLabel}
                </button>
            </form>
        `);

        const slot = document.querySelector('[data-cli-settings-slot]');

        if (!isEdit) {
            const cliSelect = document.getElementById('env-cli');
            enhanceLlmSelectWithTomSelect(cliSelect, {
                placeholder: 'Select CLI...',
                cliKey: 'value'
            });
            cliSelect.addEventListener('change', () => {
                const cli = cliSelect.value;
                const customArgsGroup = document.querySelector('[data-custom-args-group]');
                if (customArgsGroup) {
                    customArgsGroup.style.display = this.usesManagedCustomArgs(cli) ? 'none' : '';
                }
                slot.innerHTML = this.buildCliSettingsHtml(cli, {});
            });
        }

        document.getElementById('env-form').addEventListener('submit', async (e) => {
            e.preventDefault();

            try {
                if (isEdit) {
                    const settingsPayload = this.extractCliSettingsPayload(env.cli);
                    const payload = this.buildEnvironmentSavePayload(env.cli, settingsPayload);
                    await this.app.apiCall(`/api/v1/environments/${encodeURIComponent(env.name)}`, 'PUT', payload);
                    await this.saveCliSettings(env.cli, env.name, settingsPayload);
                } else {
                    const name = document.getElementById('env-name').value;
                    const cli = document.getElementById('env-cli').value;
                    const settingsPayload = this.extractCliSettingsPayload(cli);
                    const payload = {
                        name,
                        cli,
                        ...this.buildEnvironmentSavePayload(cli, settingsPayload)
                    };
                    await this.app.apiCall('/api/v1/environments', 'POST', payload);
                    await this.saveCliSettings(cli, name, settingsPayload);
                }

                this.app.closeModal();
                await this.refreshEnvironments();
                this.app.navigate('environments');
            } catch (error) {
                const verb = isEdit ? 'update' : 'create';
                this.app.showError(`Failed to ${verb} environment: ${error.message}`);
            }
        });
    }

    cliSettingsEndpoint(cli) {
        const cliLower = (cli || '').toLowerCase();
        if (cliLower === 'gemini' || cliLower === 'codex' || cliLower === 'claude') {
            return cliLower;
        }
        return null;
    }

    usesManagedCustomArgs(cli) {
        const cliLower = (cli || '').toLowerCase();
        return cliLower === 'codex' || cliLower === 'claude' || cliLower === 'gemini' || cliLower === 'copilot';
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

        if (cliLower === 'gemini') {
            const geminiSettings = settingsPayload || this.extractCliSettingsPayload(cli);
            return {
                customArgs: this.buildGeminiCustomArgs(geminiSettings),
                customPrompt: geminiSettings?.initialMessage ?? ''
            };
        }

        if (cliLower === 'copilot') {
            const copilotSettings = settingsPayload || this.extractCliSettingsPayload(cli);
            return {
                customArgs: this.buildCopilotCustomArgs(copilotSettings),
                customPrompt: copilotSettings?.initialMessage ?? ''
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
        const approval = this.normalizeCodexApproval(s.askForApproval);

        if (model) {
            args.push('--model', model);
        }

        if (s.effort) {
            args.push('-c', `model_reasoning_effort=${s.effort}`);
        }

        if (s.yolo) {
            args.push('--dangerously-bypass-approvals-and-sandbox');
        } else {
            if (s.fullAuto) {
                args.push('--sandbox', 'workspace-write');
            }

            const approvalToUse = approval || (s.fullAuto ? 'on-request' : '');
            if (approvalToUse) {
                args.push('--ask-for-approval', approvalToUse);
            }
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

    buildGeminiCustomArgs(settings) {
        const s = settings || {};
        const args = [];
        const approvalMode = this.normalizeGeminiApprovalMode(s.approvalMode || (s.yoloMode ? 'yolo' : (s.autoApproveTools ? 'auto_edit' : '')));

        if (s.sandboxEnabled) {
            args.push('--sandbox');
        }

        if (approvalMode && approvalMode !== 'default') {
            args.push('--approval-mode', approvalMode);
        }

        args.push(...this.parseArgString(s.additionalArgs || ''));

        return args.map(arg => this.quoteCustomArg(arg)).join(' ');
    }

    mergeGeminiSettingsFromCustomArgs(settings, customArgs) {
        const args = this.parseArgString(customArgs);
        if (args.length === 0) return settings;

        const additionalArgs = [];

        for (let i = 0; i < args.length; i++) {
            const arg = args[i];
            const next = args[i + 1];

            if (arg === '--sandbox' || arg === '-s') {
                settings.sandboxEnabled = true;
                continue;
            }

            if (arg === '--yolo' || arg === '-y') {
                settings.approvalMode = 'yolo';
                settings.yoloMode = true;
                continue;
            }

            if (arg.startsWith('--approval-mode=')) {
                const approvalMode = this.normalizeGeminiApprovalMode(arg.slice('--approval-mode='.length));
                settings.approvalMode = approvalMode;
                settings.autoApproveTools = approvalMode === 'auto_edit';
                settings.yoloMode = approvalMode === 'yolo';
                continue;
            }

            if (arg === '--approval-mode' && next) {
                const approvalMode = this.normalizeGeminiApprovalMode(next);
                settings.approvalMode = approvalMode;
                settings.autoApproveTools = approvalMode === 'auto_edit';
                settings.yoloMode = approvalMode === 'yolo';
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

    normalizeGeminiApprovalMode(value) {
        const raw = (value || '').trim().toLowerCase();
        if (['default', 'auto_edit', 'yolo', 'plan'].includes(raw)) return raw;
        return '';
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

            if ((arg === '--ask-for-approval' || arg === '-a') && next) {
                settings.askForApproval = this.normalizeCodexApproval(next);
                i++;
                continue;
            }

            if (arg === '--enable' && next === 'fast_mode') {
                sawFastFeature = true;
                i++;
                continue;
            }

            if (arg === '--dangerously-bypass-approvals-and-sandbox' || arg === '--yolo') {
                settings.yolo = true;
                continue;
            }

            if (arg === '--no-alt-screen') {
                settings.noAltScreen = true;
                continue;
            }

            if ((arg === '--sandbox' || arg === '-s') && next === 'workspace-write') {
                settings.fullAuto = true;
                i++;
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
        const raw = (model || '').trim();
        const lower = raw.toLowerCase();
        if (lower === 'gpt-5') return 'gpt-5.4';
        if (lower === 'gpt-5-codex') return 'gpt-5.3-codex';
        return raw;
    }

    normalizeCodexApproval(value) {
        const raw = (value || '').trim().toLowerCase();
        if (raw === 'on-failure') return 'on-request';
        if (['untrusted', 'on-request', 'never'].includes(raw)) return raw;
        return '';
    }

    renderCodexModelOptions(selectedModel) {
        const selected = this.normalizeCodexModel(selectedModel);
        const options = [
            ['', 'Default (Codex recommended)'],
            ['gpt-5.5', 'gpt-5.5'],
            ['gpt-5.4', 'gpt-5.4'],
            ['gpt-5.4-mini', 'gpt-5.4-mini'],
            ['gpt-5.3-codex', 'gpt-5.3-codex'],
            ['gpt-5.3-codex-spark', 'gpt-5.3-codex-spark'],
            ['gpt-5.2', 'gpt-5.2']
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
        const options = [
            ['', 'Default (auto)'],
            ['claude-sonnet-4.6', 'claude-sonnet-4.6'],
            ['claude-sonnet-4.5', 'claude-sonnet-4.5'],
            ['claude-haiku-4.5', 'claude-haiku-4.5'],
            ['claude-opus-4.7', 'claude-opus-4.7'],
            ['claude-opus-4.6', 'claude-opus-4.6'],
            ['claude-opus-4.6-fast', 'claude-opus-4.6-fast'],
            ['claude-opus-4.5', 'claude-opus-4.5'],
            ['claude-sonnet-4', 'claude-sonnet-4'],
            ['gpt-5.5', 'gpt-5.5'],
            ['gpt-5.4', 'gpt-5.4'],
            ['gpt-5.3-codex', 'gpt-5.3-codex'],
            ['gpt-5.2-codex', 'gpt-5.2-codex'],
            ['gpt-5.2', 'gpt-5.2'],
            ['gpt-5.1', 'gpt-5.1'],
            ['gpt-5.4-mini', 'gpt-5.4-mini'],
            ['gpt-5-mini', 'gpt-5-mini'],
            ['gpt-4.1', 'gpt-4.1'],
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

    buildClaudeCustomArgs(settings) {
        const s = settings || {};
        const args = [];

        if (s.effort) {
            args.push('--effort', s.effort);
        }

        if (s.noSessionPersistence) {
            args.push('--no-session-persistence');
        }

        // Only emit --permission-mode when the user picked a non-default value;
        // otherwise the generated args list is noisier than what they configured.
        const permissionMode = s.permissionMode || 'default';
        if (permissionMode !== 'default') {
            args.push('--permission-mode', permissionMode);
        }

        this.pushStringArg(args, '--system-prompt', s.systemPrompt);

        if (s.allowDangerouslySkipPermissions) {
            args.push('--allow-dangerously-skip-permissions');
        }

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
        if (cliLower === 'gemini') {
            const approvalMode = this.normalizeGeminiApprovalMode(document.getElementById('gemini-approval-mode').value);
            return {
                initialMessage: document.getElementById('gemini-initial-message').value,
                sandboxEnabled: document.getElementById('gemini-sandbox').checked,
                approvalMode,
                autoApproveTools: approvalMode === 'auto_edit',
                vimMode: document.getElementById('gemini-vim').checked,
                checkForUpdates: document.getElementById('gemini-updates').checked,
                yoloMode: approvalMode === 'yolo',
                additionalArgs: document.getElementById('gemini-additional-args').value
            };
        }
        if (cliLower === 'codex') {
            return {
                yolo: document.getElementById('codex-yolo').checked,
                fullAuto: document.getElementById('codex-full-auto').checked,
                noAltScreen: document.getElementById('codex-no-alt-screen').checked,
                prompt: document.getElementById('codex-prompt').value,
                askForApproval: this.normalizeCodexApproval(document.getElementById('codex-approval').value),
                model: this.normalizeCodexModel(document.getElementById('codex-model').value),
                effort: document.getElementById('codex-effort').value,
                fastMode: document.getElementById('codex-fast-mode').checked
            };
        }
        if (cliLower === 'claude') {
            return {
                effort: document.getElementById('claude-effort').value,
                initialMessage: document.getElementById('claude-initial-message').value,
                noSessionPersistence: document.getElementById('claude-no-session-persistence').checked,
                permissionMode: document.getElementById('claude-permission-mode').value,
                systemPrompt: document.getElementById('claude-system-prompt').value,
                allowDangerouslySkipPermissions: document.getElementById('claude-allow-dangerously-skip-permissions').checked,
                dangerouslySkipPermissions: document.getElementById('claude-dangerously-skip-permissions').checked,
                bare: document.getElementById('claude-bare').checked,
                debug: document.getElementById('claude-debug').checked
            };
        }
        if (cliLower === 'copilot') {
            return {
                initialMessage: document.getElementById('copilot-initial-message').value,
                mode: this.normalizeCopilotMode(document.getElementById('copilot-mode').value),
                model: document.getElementById('copilot-model').value.trim(),
                permissionPreset: this.normalizeCopilotPermissionPreset(document.getElementById('copilot-permission-preset').value),
                noAskUser: document.getElementById('copilot-no-ask-user').checked,
                additionalArgs: document.getElementById('copilot-additional-args').value
            };
        }
        return null;
    }

    buildCliSettingsHtml(cli, settings) {
        const s = settings || {};
        const cliLower = (cli || '').toLowerCase();

        if (cliLower === 'gemini') {
            const geminiInitialMessage = this.app.escapeHtml(s.initialMessage || '');
            const geminiApprovalMode = this.normalizeGeminiApprovalMode(s.approvalMode || (s.yoloMode ? 'yolo' : (s.autoApproveTools ? 'auto_edit' : '')));
            const geminiAdditionalArgs = this.app.escapeHtml(s.additionalArgs || '');
            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Gemini CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Initial Message</label>
                    <textarea class="form-control" id="gemini-initial-message" rows="3" maxlength="6000" placeholder="Optional. Sent to Gemini as soon as the session starts.">${geminiInitialMessage}</textarea>
                    <small class="form-text text-muted">Passed as <code>gemini "&lt;text&gt;"</code> and recorded as the session's first user input.</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="gemini-sandbox" ${s.sandboxEnabled ? 'checked' : ''}>
                        <label class="form-check-label" for="gemini-sandbox">Sandbox Mode</label>
                    </div>
                    <small class="form-text text-muted">Run tools in a containerized sandbox for safety</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Approval Mode</label>
                    <select class="form-select" id="gemini-approval-mode">
                        <option value="" ${geminiApprovalMode === '' ? 'selected' : ''}>Default prompts</option>
                        <option value="auto_edit" ${geminiApprovalMode === 'auto_edit' ? 'selected' : ''}>Auto-approve edits</option>
                        <option value="yolo" ${geminiApprovalMode === 'yolo' ? 'selected' : ''}>YOLO: auto-approve all actions</option>
                        <option value="plan" ${geminiApprovalMode === 'plan' ? 'selected' : ''}>Plan: read-only</option>
                    </select>
                    <small class="form-text text-muted">Passed as <code>--approval-mode</code>; YOLO uses <code>--approval-mode yolo</code></small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="gemini-vim" ${s.vimMode ? 'checked' : ''}>
                        <label class="form-check-label" for="gemini-vim">Vim Mode</label>
                    </div>
                    <small class="form-text text-muted">Enable Vim keybindings</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="gemini-updates" ${s.checkForUpdates ? 'checked' : ''}>
                        <label class="form-check-label" for="gemini-updates">Check for Updates</label>
                    </div>
                    <small class="form-text text-muted">Automatically check for CLI updates</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Additional Arguments</label>
                    <input type="text" class="form-control" id="gemini-additional-args" value="${geminiAdditionalArgs}" placeholder="Optional extra Gemini flags">
                    <small class="form-text text-muted">Preserves advanced flags not covered above</small>
                </div>
            `;
        }

        if (cliLower === 'codex') {
            const promptValue = this.app.escapeHtml(s.prompt || '');
            const codexModel = this.normalizeCodexModel(s.model);
            const codexEffort = s.effort || '';
            const codexApproval = this.normalizeCodexApproval(s.askForApproval);
            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Codex CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Starting Message</label>
                    <textarea class="form-control" id="codex-prompt" rows="3" maxlength="6000" placeholder="Please review the code changes. Look for bugs, code smells, and security issues.">${promptValue}</textarea>
                    <small class="form-text text-muted">Passed as <code>codex "&lt;text&gt;"</code> and recorded as the session's first user input.</small>
                </div>
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
                    </select>
                    <small class="form-text text-muted">Passed as <code>-c model_reasoning_effort=&lt;level&gt;</code></small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Ask For Approval</label>
                    <select class="form-select" id="codex-approval">
                        <option value="" ${codexApproval === '' ? 'selected' : ''}>Default</option>
                        <option value="on-request" ${codexApproval === 'on-request' ? 'selected' : ''}>On Request</option>
                        <option value="untrusted" ${codexApproval === 'untrusted' ? 'selected' : ''}>Untrusted</option>
                        <option value="never" ${codexApproval === 'never' ? 'selected' : ''}>Never</option>
                    </select>
                    <small class="form-text text-muted">Passed as <code>--ask-for-approval</code>; ignored when YOLO mode is enabled</small>
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
                    <small class="form-text text-muted text-warning">Runs commands without approvals or sandboxing</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="codex-full-auto" ${s.fullAuto ? 'checked' : ''}>
                        <label class="form-check-label" for="codex-full-auto">Full-Auto Mode</label>
                    </div>
                    <small class="form-text text-muted">Sets approval to on-request and sandbox to workspace-write</small>
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
            const initialMessage = this.app.escapeHtml(s.initialMessage || '');
            const mode = this.normalizeCopilotMode(s.mode);
            const permissionPreset = this.normalizeCopilotPermissionPreset(s.permissionPreset);
            const model = s.model || '';
            const additionalArgs = this.app.escapeHtml(s.additionalArgs || '');

            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Copilot CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Initial Message</label>
                    <textarea class="form-control" id="copilot-initial-message" rows="3" maxlength="6000" placeholder="Optional. Sent to Copilot as soon as the session starts.">${initialMessage}</textarea>
                    <small class="form-text text-muted">Passed as <code>copilot --interactive=&lt;text&gt;</code> and recorded as the session's first user input.</small>
                </div>
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

        if (cliLower === 'claude') {
            const effort = s.effort || '';
            const permissionMode = s.permissionMode || 'default';
            const initialMessage = this.app.escapeHtml(s.initialMessage || '');
            const systemPrompt = this.app.escapeHtml(s.systemPrompt || '');

            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Claude CLI Settings</h6>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-allow-dangerously-skip-permissions" ${s.allowDangerouslySkipPermissions ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-allow-dangerously-skip-permissions">Allow Dangerous Skip Permissions</label>
                    </div>
                    <small class="form-text text-muted">Adds bypassPermissions to the mode cycle without starting in it</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-dangerously-skip-permissions" ${s.dangerouslySkipPermissions ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-dangerously-skip-permissions">Dangerously Skip Permissions</label>
                    </div>
                    <small class="form-text text-muted text-warning">Starts Claude with permission prompts bypassed</small>
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
                    <label class="form-label">Initial Message</label>
                    <textarea class="form-control" id="claude-initial-message" rows="3" maxlength="6000" placeholder="Optional. Sent to Claude as soon as the session starts.">${initialMessage}</textarea>
                    <small class="form-text text-muted">Passed as <code>claude "&lt;text&gt;"</code> and recorded as the session's first user input.</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-no-session-persistence" ${s.noSessionPersistence ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-no-session-persistence">No Session Persistence</label>
                    </div>
                    <small class="form-text text-muted">Do not save sessions to disk for print-mode runs</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Permission Mode</label>
                    <select class="form-select" id="claude-permission-mode">
                        <option value="default" ${permissionMode === 'default' ? 'selected' : ''}>Default</option>
                        <option value="acceptEdits" ${permissionMode === 'acceptEdits' ? 'selected' : ''}>Accept Edits</option>
                        <option value="plan" ${permissionMode === 'plan' ? 'selected' : ''}>Plan</option>
                        <option value="auto" ${permissionMode === 'auto' ? 'selected' : ''}>Auto</option>
                        <option value="dontAsk" ${permissionMode === 'dontAsk' ? 'selected' : ''}>Don't Ask</option>
                        <option value="bypassPermissions" ${permissionMode === 'bypassPermissions' ? 'selected' : ''}>Bypass Permissions</option>
                    </select>
                    <small class="form-text text-muted">Controls permission handling behavior</small>
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

    async removeEnvironment(name) {
        this.app.showModal('Remove Environment', `
            <div class="text-center py-3">
                <div class="mb-3 text-danger">
                    <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" fill="currentColor" viewBox="0 0 16 16">
                        <path d="M11 1.5v1h3.5a.5.5 0 0 1 0 1h-.538l-.853 10.66A2 2 0 0 1 11.115 16h-6.23a2 2 0 0 1-1.994-1.84L2.038 3.5H1.5a.5.5 0 0 1 0-1H5v-1A1.5 1.5 0 0 1 6.5 0h3A1.5 1.5 0 0 1 11 1.5m-5 0v1h4v-1a.5.5 0 0 0-.5-.5h-3a.5.5 0 0 0-.5.5M4.5 5.029l.5 8.5a.5.5 0 1 0 .998-.06l-.5-8.5a.5.5 0 1 0-.998.06m6.53-.06a.5.5 0 0 0-.515.479l-.5 8.5a.5.5 0 1 0 .998.06l.5-8.5a.5.5 0 0 0-.484-.539M8 5.5a.5.5 0 0 0-.5.5v8.5a.5.5 0 0 0 1 0V6a.5.5 0 0 0-.5-.5"/>
                    </svg>
                </div>
                <h5>Remove environment "${this.app.escapeHtml(name)}"?</h5>
                <p class="text-muted small px-4">This will permanently delete this environment profile. This action cannot be undone.</p>
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
                this.app.navigate('environments');
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

    async refreshEnvironments() {
        try {
            const response = await this.app.apiCall('/api/v1/environments', 'GET');
            this.app.data.environments = (response.environments || []).map(env => ({
                id: env.id,
                name: env.name,
                cli: env.cli,
                customArgs: env.customArgs,
                customPrompt: env.customPrompt,
                defaultPrompt: env.defaultPrompt,
                lastUsed: this.app.formatRelativeTime(env.lastUsedUTC)
            }));
        } catch (error) {
            console.error('Failed to refresh environments:', error);
            this.app.data.environments = [];
        }
    }

    async launchInWebUI(envId, envName, cli) {
        // The terminal panel only exists on the dashboard. `goBack()` was unreliable —
        // if the user reached the env page via the top sub-nav (rather than from the
        // dashboard), `goBack()` either does nothing or returns to a different view,
        // and the subsequent [data-terminal-content] lookup silently fails. Navigate
        // explicitly so the user always lands where the terminal panel lives, and
        // preselect the env so the dropdown is right even if auto-start races.
        if (this.app.currentView !== 'dashboard') {
            this.app.navigate('dashboard', { preselectedEnvId: envId });
        }

        this.app.showToast('Web Terminal',
            `Launching ${envName} (${cli})...`,
            'info');

        // The dashboard renders its terminal container asynchronously. Poll briefly
        // until the container is mounted, then hand off to startTerminal — which
        // itself awaits the TerminalManager's init promise, so we don't need to
        // wait for that here.
        const terminalContent = await this._waitForTerminalContent(2000);
        if (!terminalContent) {
            this.app.showError('Could not find the terminal panel — open the dashboard and try again.');
            return;
        }

        document.querySelector('[data-terminal-section]')
            ?.scrollIntoView({ behavior: 'smooth', block: 'start' });

        await this.app.terminalController.startTerminal(terminalContent, `env:${envId}:${cli}`);
    }

    async _waitForTerminalContent(timeoutMs) {
        const deadline = Date.now() + timeoutMs;
        while (Date.now() < deadline) {
            const el = document.querySelector('[data-terminal-content]');
            if (el) return el;
            await new Promise(resolve => setTimeout(resolve, 50));
        }
        return document.querySelector('[data-terminal-content]');
    }
}

