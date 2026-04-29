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
                        select.classList.remove('vb-terminal-selection-shake');
                        void select.offsetWidth;
                        select.classList.add('vb-terminal-selection-shake');
                        select.focus();
                        if (typeof select.showPicker === 'function') {
                            try { select.showPicker(); } catch {}
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

        return `
            <div class="table-responsive">
                <table class="table table-hover align-middle">
                    <thead>
                        <tr>
                            <th>Name</th>
                            <th>CLI</th>
                            <th>Custom Args</th>
                            <th>Last Used</th>
                            <th>Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        ${this.app.data.environments.map(env => {
                            return `
                            <tr>
                                <td><strong>${env.name}</strong></td>
                                <td>${env.cli}</td>
                                <td><code>${env.customArgs || '-'}</code></td>
                                <td class="small text-muted text-nowrap">${env.lastUsed || 'Never'}</td>
                                <td>
                                    <div class="d-inline-flex gap-1">
                                        <button class="btn btn-xs btn-outline-secondary d-inline-flex align-items-center" type="button" data-action="launch-environment" data-env-name="${env.name}" data-env-cli="${env.cli}" title="Launch in external terminal">
                                            <i class="fa-solid fa-terminal"></i>
                                        </button>
                                        <button class="btn btn-xs btn-outline-success d-inline-flex align-items-center" type="button" data-action="launch-in-webui" data-env-id="${env.id}" data-env-name="${env.name}" data-env-cli="${env.cli}" title="Launch in Web Terminal">
                                            <i class="fa-solid fa-display"></i>
                                        </button>
                                        <button class="btn btn-xs btn-outline-secondary d-inline-flex align-items-center" type="button" data-action="edit-environment" data-env-name="${env.name}" title="Settings">
                                            <i class="fa-solid fa-sliders"></i>
                                        </button>
                                        <button class="btn btn-xs btn-outline-danger d-inline-flex align-items-center" type="button" data-action="remove-environment" data-env-name="${env.name}" title="Delete">
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
        if ((env.cli || '').toLowerCase() === 'codex' && env.customPrompt) {
            cliSettings.prompt = env.customPrompt;
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
        return cliLower === 'codex' || cliLower === 'claude';
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
            // The Codex panel's prompt field is the source of truth for both
            // config.toml and env.customPrompt — keep them in sync.
            return {
                customArgs: this.buildCodexCustomArgs(codexSettings),
                customPrompt: codexSettings?.prompt ?? ''
            };
        }

        if (cliLower === 'claude') {
            const claudeSettings = settingsPayload || this.extractCliSettingsPayload(cli);
            // Claude has no UI surface for env.customPrompt — its system prompt is a
            // separate, settings-managed concept. Omit customPrompt from the payload
            // so the backend preserves whatever was set via `vb env update --prompt`.
            return {
                customArgs: this.buildClaudeCustomArgs(claudeSettings)
            };
        }

        return {
            customArgs: document.getElementById('env-custom-args')?.value || ''
        };
    }

    buildCodexCustomArgs(settings) {
        const s = settings || {};
        const args = [];

        if (s.yolo) {
            args.push('--yolo');
        } else if (s.fullAuto) {
            args.push('--full-auto');
        } else {
            args.push('--ask-for-approval', s.askForApproval || 'untrusted');
        }

        if (s.noAltScreen) {
            args.push('--no-alt-screen');
        }

        if (s.oss) {
            args.push('--oss');
        }

        return args.join(' ');
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

        this.pushListArg(args, '--dangerously-load-development-channels', s.dangerouslyLoadDevelopmentChannels, { splitWhitespace: true });

        if (s.dangerouslySkipPermissions) {
            args.push('--dangerously-skip-permissions');
        }

        this.pushListArg(args, '--allowedTools', s.allowedTools);
        this.pushStringArg(args, '--append-system-prompt', s.appendSystemPrompt);

        if (s.bare) {
            args.push('--bare');
        }

        this.pushListArg(args, '--betas', s.betas, { splitWhitespace: true });
        this.pushListArg(args, '--channels', s.channels, { splitWhitespace: true });

        if (s.debug || s.debugFilter) {
            const filter = this.normalizeCustomArgValue(s.debugFilter);
            if (filter) {
                args.push('--debug-filter', filter);
            } else {
                args.push('--debug');
            }
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
            return {
                theme: document.getElementById('gemini-theme').value,
                sandboxEnabled: document.getElementById('gemini-sandbox').checked,
                autoApproveTools: document.getElementById('gemini-auto-approve').checked,
                vimMode: document.getElementById('gemini-vim').checked,
                checkForUpdates: document.getElementById('gemini-updates').checked,
                yoloMode: document.getElementById('gemini-yolo').checked
            };
        }
        if (cliLower === 'codex') {
            return {
                askForApproval: document.getElementById('codex-approval').value,
                yolo: document.getElementById('codex-yolo').checked,
                fullAuto: document.getElementById('codex-full-auto').checked,
                noAltScreen: document.getElementById('codex-no-alt-screen').checked,
                oss: document.getElementById('codex-oss').checked,
                prompt: document.getElementById('codex-prompt').value
            };
        }
        if (cliLower === 'claude') {
            return {
                effort: document.getElementById('claude-effort').value,
                noSessionPersistence: document.getElementById('claude-no-session-persistence').checked,
                permissionMode: document.getElementById('claude-permission-mode').value,
                systemPrompt: document.getElementById('claude-system-prompt').value,
                allowDangerouslySkipPermissions: document.getElementById('claude-allow-dangerously-skip-permissions').checked,
                dangerouslyLoadDevelopmentChannels: document.getElementById('claude-development-channels').value,
                dangerouslySkipPermissions: document.getElementById('claude-dangerously-skip-permissions').checked,
                allowedTools: document.getElementById('claude-allowed-tools').value,
                appendSystemPrompt: document.getElementById('claude-append-system-prompt').value,
                bare: document.getElementById('claude-bare').checked,
                betas: document.getElementById('claude-betas').value,
                channels: document.getElementById('claude-channels').value,
                debug: document.getElementById('claude-debug').checked,
                debugFilter: document.getElementById('claude-debug-filter').value
            };
        }
        return null;
    }

    buildCliSettingsHtml(cli, settings) {
        const s = settings || {};
        const cliLower = (cli || '').toLowerCase();

        if (cliLower === 'gemini') {
            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Gemini CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Theme</label>
                    <select class="form-select" id="gemini-theme">
                        <option value="Default" ${s.theme === 'Default' ? 'selected' : ''}>Default</option>
                        <option value="Dark" ${s.theme === 'Dark' ? 'selected' : ''}>Dark</option>
                        <option value="Light" ${s.theme === 'Light' ? 'selected' : ''}>Light</option>
                    </select>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="gemini-sandbox" ${s.sandboxEnabled ? 'checked' : ''}>
                        <label class="form-check-label" for="gemini-sandbox">Sandbox Mode</label>
                    </div>
                    <small class="form-text text-muted">Run tools in a containerized sandbox for safety</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="gemini-auto-approve" ${s.autoApproveTools ? 'checked' : ''}>
                        <label class="form-check-label" for="gemini-auto-approve">Auto-Approve Tools</label>
                    </div>
                    <small class="form-text text-muted">Automatically execute safe operations without confirmation</small>
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
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="gemini-yolo" ${s.yoloMode ? 'checked' : ''}>
                        <label class="form-check-label" for="gemini-yolo">YOLO Mode</label>
                    </div>
                    <small class="form-text text-muted text-warning">Auto-approve ALL operations (dangerous!)</small>
                </div>
            `;
        }

        if (cliLower === 'codex') {
            const promptValue = this.app.escapeHtml(s.prompt || '');
            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Codex CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Ask For Approval</label>
                    <select class="form-select" id="codex-approval">
                        <option value="untrusted" ${(s.askForApproval || 'untrusted') === 'untrusted' ? 'selected' : ''}>Untrusted</option>
                        <option value="on-request" ${s.askForApproval === 'on-request' ? 'selected' : ''}>On Request</option>
                        <option value="never" ${s.askForApproval === 'never' ? 'selected' : ''}>Never</option>
                    </select>
                    <small class="form-text text-muted">Controls when Codex pauses for human approval before running a command</small>
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
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="codex-oss" ${s.oss ? 'checked' : ''}>
                        <label class="form-check-label" for="codex-oss">OSS Provider</label>
                    </div>
                    <small class="form-text text-muted">Use the local open source model provider</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Prompt</label>
                    <textarea class="form-control" id="codex-prompt" rows="3" placeholder="Optional text instruction to start the session">${promptValue}</textarea>
                    <small class="form-text text-muted">Leave empty to launch Codex without a pre-filled message</small>
                </div>
            `;
        }

        if (cliLower === 'claude') {
            const effort = s.effort || '';
            const permissionMode = s.permissionMode || 'default';
            const systemPrompt = this.app.escapeHtml(s.systemPrompt || '');
            const developmentChannels = this.app.escapeHtml(s.dangerouslyLoadDevelopmentChannels || '');
            const allowedTools = this.app.escapeHtml(s.allowedTools || '');
            const appendSystemPrompt = this.app.escapeHtml(s.appendSystemPrompt || '');
            const betas = this.app.escapeHtml(s.betas || '');
            const channels = this.app.escapeHtml(s.channels || '');
            const debugFilter = this.app.escapeHtml(s.debugFilter || '');

            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Claude CLI Settings</h6>
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
                        <input class="form-check-input" type="checkbox" id="claude-allow-dangerously-skip-permissions" ${s.allowDangerouslySkipPermissions ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-allow-dangerously-skip-permissions">Allow Dangerous Skip Permissions</label>
                    </div>
                    <small class="form-text text-muted">Adds bypassPermissions to the mode cycle without starting in it</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Development Channels</label>
                    <textarea class="form-control" id="claude-development-channels" rows="2" placeholder="server:webhook">${developmentChannels}</textarea>
                    <small class="form-text text-muted">Entries for --dangerously-load-development-channels</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-dangerously-skip-permissions" ${s.dangerouslySkipPermissions ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-dangerously-skip-permissions">Dangerously Skip Permissions</label>
                    </div>
                    <small class="form-text text-muted text-warning">Starts Claude with permission prompts bypassed</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Allowed Tools</label>
                    <textarea class="form-control" id="claude-allowed-tools" rows="3" placeholder="Bash(git log *)&#10;Bash(git diff *)&#10;Read">${allowedTools}</textarea>
                    <small class="form-text text-muted">One --allowedTools entry per line</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Append System Prompt</label>
                    <textarea class="form-control" id="claude-append-system-prompt" rows="2" placeholder="Append text to the default system prompt">${appendSystemPrompt}</textarea>
                    <small class="form-text text-muted">Passed as --append-system-prompt when launching Claude</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-bare" ${s.bare ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-bare">Bare Mode</label>
                    </div>
                    <small class="form-text text-muted">Skip discovery of hooks, skills, plugins, MCP servers, memory, and CLAUDE.md</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Betas</label>
                    <input type="text" class="form-control" id="claude-betas" value="${betas}" placeholder="interleaved-thinking">
                    <small class="form-text text-muted">Entries for --betas</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Channels</label>
                    <textarea class="form-control" id="claude-channels" rows="2" placeholder="plugin:my-notifier@my-marketplace">${channels}</textarea>
                    <small class="form-text text-muted">Entries for --channels</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-debug" ${s.debug ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-debug">Debug Mode</label>
                    </div>
                    <small class="form-text text-muted">Enable --debug for this launch</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Debug Filter</label>
                    <input type="text" class="form-control" id="claude-debug-filter" value="${debugFilter}" placeholder="api,mcp">
                    <small class="form-text text-muted">Optional category filter passed after --debug</small>
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

