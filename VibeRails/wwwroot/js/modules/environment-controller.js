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
                                <td class="small text-muted">${env.lastUsed || 'Never'}</td>
                                <td>
                                    <div class="d-flex gap-2">
                                        <button class="btn btn-sm btn-outline-secondary d-inline-flex align-items-center gap-1" type="button" data-action="launch-environment" data-env-name="${env.name}" data-env-cli="${env.cli}" title="Launch in external terminal">
                                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                                <path d="M6 9a.5.5 0 0 1 .5-.5h3a.5.5 0 0 1 0 1h-3A.5.5 0 0 1 6 9M2.854 4.146a.5.5 0 1 0-.708.708L4.293 7 2.146 9.146a.5.5 0 1 0 .708.708l2.5-2.5a.5.5 0 0 0 0-.708z"/>
                                                <path d="M14 1a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H2a1 1 0 0 1-1-1V2a1 1 0 0 1 1-1zM2 0a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V2a2 2 0 0 0-2-2z"/>
                                            </svg>
                                            <span>Launch in CLI</span>
                                        </button>
                                        <button class="btn btn-sm btn-outline-success d-inline-flex align-items-center gap-1" type="button" data-action="launch-in-webui" data-env-id="${env.id}" data-env-name="${env.name}" data-env-cli="${env.cli}" title="Launch in Web Terminal">
                                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                                <path d="M2 1a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V3a2 2 0 0 0-2-2zm12 1a1 1 0 0 1 1 1v1H1V3a1 1 0 0 1 1-1zm1 11a1 1 0 0 1-1 1H2a1 1 0 0 1-1-1V5h14z"/>
                                                <path d="M4.146 7.146a.5.5 0 0 1 .708 0L6.707 9 4.854 10.854a.5.5 0 0 1-.708-.708L5.293 9 4.146 7.854a.5.5 0 0 1 0-.708M7.5 10.5a.5.5 0 0 1 0-1H10a.5.5 0 0 1 0 1z"/>
                                            </svg>
                                            <span>Launch in Web</span>
                                        </button>
                                        <button class="btn btn-sm btn-outline-secondary d-inline-flex align-items-center gap-1" type="button" data-action="edit-environment" data-env-name="${env.name}" title="Settings">
                                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                                <path d="M9.405 1.05c-.413-1.4-2.397-1.4-2.81 0l-.1.34a1.464 1.464 0 0 1-1.905.928l-.34-.12c-1.353-.475-2.749.921-2.274 2.274l.12.34a1.464 1.464 0 0 1-.928 1.905l-.34.1c-1.4.413-1.4 2.397 0 2.81l.34.1a1.464 1.464 0 0 1 .928 1.905l-.12.34c-.475 1.353.921 2.749 2.274 2.274l.34-.12a1.464 1.464 0 0 1 1.905.928l.1.34c.413 1.4 2.397 1.4 2.81 0l.1-.34a1.464 1.464 0 0 1 1.905-.928l.34.12c1.353.475 2.749-.921 2.274-2.274l-.12-.34a1.464 1.464 0 0 1 .928-1.905l.34-.1c1.4-.413 1.4-2.397 0-2.81l-.34-.1a1.464 1.464 0 0 1-.928-1.905l.12-.34c.475-1.353-.921-2.749-2.274-2.274l-.34.12a1.464 1.464 0 0 1-1.905-.928zM8 10.466a2.466 2.466 0 1 1 0-4.932 2.466 2.466 0 0 1 0 4.932"/>
                                            </svg>
                                            <span>Settings</span>
                                        </button>
                                        <button class="btn btn-sm btn-outline-danger d-inline-flex align-items-center gap-1" type="button" data-action="remove-environment" data-env-name="${env.name}" title="Delete">
                                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                                <path d="M11 1.5v1h3.5a.5.5 0 0 1 0 1h-.538l-.853 10.66A2 2 0 0 1 11.115 16h-6.23a2 2 0 0 1-1.994-1.84L2.038 3.5H1.5a.5.5 0 0 1 0-1H5v-1A1.5 1.5 0 0 1 6.5 0h3A1.5 1.5 0 0 1 11 1.5m-5 0v1h4v-1a.5.5 0 0 0-.5-.5h-3a.5.5 0 0 0-.5.5M4.5 5.029l.5 8.5a.5.5 0 1 0 .998-.06l-.5-8.5a.5.5 0 1 0-.998.06m6.53-.06a.5.5 0 0 0-.515.479l-.5 8.5a.5.5 0 1 0 .998.06l.5-8.5a.5.5 0 0 0-.484-.539M8 5.5a.5.5 0 0 0-.5.5v8.5a.5.5 0 0 0 1 0V6a.5.5 0 0 0-.5-.5"/>
                                            </svg>
                                            <span>Delete</span>
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
                <div class="mb-3">
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
                slot.innerHTML = this.buildCliSettingsHtml(cliSelect.value, {});
            });
        }

        document.getElementById('env-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            const customArgs = document.getElementById('env-custom-args').value;

            try {
                if (isEdit) {
                    await this.app.apiCall(`/api/v1/environments/${encodeURIComponent(env.name)}`, 'PUT', { customArgs });
                    await this.saveCliSettings(env.cli, env.name);
                } else {
                    const name = document.getElementById('env-name').value;
                    const cli = document.getElementById('env-cli').value;
                    await this.app.apiCall('/api/v1/environments', 'POST', { name, cli, customArgs });
                    await this.saveCliSettings(cli, name);
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

    async saveCliSettings(cli, envName) {
        const endpoint = this.cliSettingsEndpoint(cli);
        if (!endpoint) return;
        const payload = this.extractCliSettingsPayload(cli);
        if (!payload) return;
        await this.app.apiCall(`/api/v1/${endpoint}/settings/${encodeURIComponent(envName)}`, 'PUT', payload);
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
                model: document.getElementById('codex-model').value,
                sandbox: document.getElementById('codex-sandbox').value,
                approval: document.getElementById('codex-approval').value,
                fullAuto: document.getElementById('codex-full-auto').checked,
                search: document.getElementById('codex-search').checked
            };
        }
        if (cliLower === 'claude') {
            return {
                model: document.getElementById('claude-model').value,
                permissionMode: document.getElementById('claude-permission-mode').value,
                allowedTools: document.getElementById('claude-allowed-tools').value,
                disallowedTools: document.getElementById('claude-disallowed-tools').value,
                skipPermissions: document.getElementById('claude-skip-permissions').checked,
                verbose: document.getElementById('claude-verbose').checked
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
            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Codex CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Model</label>
                    <input type="text" class="form-control" id="codex-model" value="${s.model || ''}" placeholder="e.g., o3, gpt-5-codex">
                    <small class="form-text text-muted">Override the default model</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Sandbox Policy</label>
                    <select class="form-select" id="codex-sandbox">
                        <option value="read-only" ${s.sandbox === 'read-only' ? 'selected' : ''}>Read-Only</option>
                        <option value="workspace-write" ${s.sandbox === 'workspace-write' ? 'selected' : ''}>Workspace Write</option>
                        <option value="danger-full-access" ${s.sandbox === 'danger-full-access' ? 'selected' : ''}>Full Access (Dangerous)</option>
                    </select>
                    <small class="form-text text-muted">Controls sandbox policy for shell commands</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Approval Mode</label>
                    <select class="form-select" id="codex-approval">
                        <option value="untrusted" ${s.approval === 'untrusted' ? 'selected' : ''}>Untrusted (Always Ask)</option>
                        <option value="on-failure" ${s.approval === 'on-failure' ? 'selected' : ''}>On Failure</option>
                        <option value="on-request" ${s.approval === 'on-request' ? 'selected' : ''}>On Request</option>
                        <option value="never" ${s.approval === 'never' ? 'selected' : ''}>Never (Auto-approve All)</option>
                    </select>
                    <small class="form-text text-muted">When to pause for human approval</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="codex-full-auto" ${s.fullAuto ? 'checked' : ''}>
                        <label class="form-check-label" for="codex-full-auto">Full-Auto Mode</label>
                    </div>
                    <small class="form-text text-muted">Shortcut for approval=on-request + sandbox=workspace-write</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="codex-search" ${s.search ? 'checked' : ''}>
                        <label class="form-check-label" for="codex-search">Web Search</label>
                    </div>
                    <small class="form-text text-muted">Enable web search capabilities</small>
                </div>
            `;
        }

        if (cliLower === 'claude') {
            return `
                <hr class="my-4">
                <h6 class="text-muted mb-3">Claude CLI Settings</h6>
                <div class="mb-3">
                    <label class="form-label">Model</label>
                    <select class="form-select" id="claude-model">
                        <option value="" ${!s.model ? 'selected' : ''}>(default)</option>
                        <option value="sonnet" ${s.model === 'sonnet' ? 'selected' : ''}>Sonnet</option>
                        <option value="opus" ${s.model === 'opus' ? 'selected' : ''}>Opus</option>
                        <option value="haiku" ${s.model === 'haiku' ? 'selected' : ''}>Haiku</option>
                    </select>
                    <small class="form-text text-muted">Override the default model</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Permission Mode</label>
                    <select class="form-select" id="claude-permission-mode">
                        <option value="default" ${s.permissionMode === 'default' ? 'selected' : ''}>Default</option>
                        <option value="plan" ${s.permissionMode === 'plan' ? 'selected' : ''}>Plan Mode</option>
                        <option value="bypassPermissions" ${s.permissionMode === 'bypassPermissions' ? 'selected' : ''}>Bypass Permissions (Dangerous)</option>
                    </select>
                    <small class="form-text text-muted">Controls permission handling behavior</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Allowed Tools</label>
                    <input type="text" class="form-control" id="claude-allowed-tools" value="${s.allowedTools || ''}" placeholder="e.g., Read,Glob,Grep">
                    <small class="form-text text-muted">Comma-separated list of tools to auto-approve</small>
                </div>
                <div class="mb-3">
                    <label class="form-label">Disallowed Tools</label>
                    <input type="text" class="form-control" id="claude-disallowed-tools" value="${s.disallowedTools || ''}" placeholder="e.g., Bash,Write">
                    <small class="form-text text-muted">Comma-separated list of tools to disable</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-skip-permissions" ${s.skipPermissions ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-skip-permissions">Skip Permissions</label>
                    </div>
                    <small class="form-text text-muted text-warning">Skip all permission prompts (dangerous!)</small>
                </div>
                <div class="mb-3">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" id="claude-verbose" ${s.verbose ? 'checked' : ''}>
                        <label class="form-check-label" for="claude-verbose">Verbose Logging</label>
                    </div>
                    <small class="form-text text-muted">Enable verbose logging output</small>
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
        // Go back to dashboard to show the launched terminal
        this.app.goBack();

        this.app.showToast('Web Terminal',
            `Launching ${envName} (${cli})...`,
            'info');

        // Scroll to terminal and auto-start the session
        setTimeout(async () => {
            const terminalSection = document.querySelector('[data-terminal-section]');
            if (terminalSection) {
                terminalSection.scrollIntoView({ behavior: 'smooth', block: 'start' });
            }

            const terminalContent = document.querySelector('[data-terminal-content]');
            if (terminalContent) {
                const selection = `env:${envId}:${cli}`;
                await this.app.terminalController.startTerminal(terminalContent, selection);
            }
        }, 300);
    }
}

