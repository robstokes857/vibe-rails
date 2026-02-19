export class DashboardController {
    constructor(app) {
        this.app = app;
    }

    async loadDashboard(data = {}) {
        await this.app.refreshDashboardData();

        // Fetch custom project name if in local context
        if (this.app.data.isLocal) {
            const path = this.app.data.configs?.rootPath;
            if (path) {
                try {
                    const result = await this.app.apiCall(`/api/v1/projects/name?path=${encodeURIComponent(path)}`);
                    this._customProjectName = result.customName || null;
                } catch {
                    this._customProjectName = null;
                }
            }
        }

        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        content.appendChild(this.renderUnifiedDashboard(data));
    }

    renderUnifiedDashboard(data = {}) {
        const fragment = this.app.cloneTemplate('dashboard-template');
        const root = fragment.querySelector('[data-dashboard]');
        if (!root) return fragment;

        const isLocal = this.app.data.isLocal;

        // Context Heading
        const headingContainer = root.querySelector('[data-context-heading-container]');
        if (headingContainer) {
            if (isLocal) {
                const path = this.app.data.configs?.rootPath || 'Unknown Path';

                const projectName = this._customProjectName || this.app.getProjectNameFromPath(path);
                const sandboxCount = this.app.data.sandboxes.length;
                const agentCount = this.app.data.agents.length;

                headingContainer.innerHTML = `
                    <div class="context-header-card position-relative overflow-hidden">
                        <div class="d-flex align-items-center justify-content-between flex-wrap gap-3 position-relative" style="z-index: 2;">
                            <div class="d-flex align-items-center gap-4">
                                <div class="project-logo-wrapper shadow-lg" style="width: 64px; height: 64px; background: linear-gradient(135deg, rgba(91, 42, 134, 0.4), rgba(119, 133, 172, 0.3)); border: 1px solid rgba(154, 198, 197, 0.15);">
                                    <svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" fill="currentColor" viewBox="0 0 16 16" style="color: var(--color-accent); opacity: 0.8;">
                                        <path d="M9.828 3h3.982a2 2 0 0 1 1.992 2.181l-.637 7A2 2 0 0 1 13.174 14H2.825a2 2 0 0 1-1.991-1.819l-.637-7a2 2 0 0 1 .342-1.31L.5 3a2 2 0 0 1 2-2h3.672a2 2 0 0 1 1.414.586l.828.828A2 2 0 0 0 9.828 3m-8.322.12C1.72 3.042 1.95 3 2.19 3h5.396l-.707-.707A1 1 0 0 0 6.172 2H2.5a1 1 0 0 0-1 1z"/>
                                    </svg>
                                </div>
                                <div>
                                    <div class="d-flex align-items-center gap-3 mb-1">
                                        <h3 class="mb-0 text-white fw-bold">${projectName}</h3>
                                        <button class="btn btn-link btn-sm p-0 text-muted" type="button" data-action="set-custom-name" title="Rename project">
                                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                                <path d="M12.146.146a.5.5 0 0 1 .708 0l3 3a.5.5 0 0 1 0 .708l-10 10a.5.5 0 0 1-.168.11l-5 2a.5.5 0 0 1-.65-.65l2-5a.5.5 0 0 1 .11-.168zM11.207 2.5 13.5 4.793 14.793 3.5 12.5 1.207zm1.586 3L10.5 3.207 4 9.707V10h.5a.5.5 0 0 1 .5.5v.5h.5a.5.5 0 0 1 .5.5v.5h.293zm-9.761 5.175-.106.106-1.528 3.821 3.821-1.528.106-.106A.5.5 0 0 1 5 12.5V12h-.5a.5.5 0 0 1-.5-.5V11h-.5a.5.5 0 0 1-.468-.325"/>
                                            </svg>
                                        </button>
                                    </div>
                                    <div class="d-flex align-items-center gap-2">
                                        <span class="path-badge py-1 px-2" style="font-size: 0.8rem; opacity: 0.8;">${path}</span>
                                    </div>
                                </div>
                            </div>
                            
                            <div class="d-flex gap-4 me-3">
                                <div class="text-center">
                                    <div class="h4 mb-0 fw-bold text-accent" style="color: var(--color-accent);">${agentCount}</div>
                                    <div class="text-muted small text-uppercase" style="font-size: 0.65rem; letter-spacing: 0.1em;">Agents</div>
                                </div>
                                <div class="text-center border-start ps-4 border-secondary" style="border-opacity: 0.2;">
                                    <div class="h4 mb-0 fw-bold text-info" style="color: #0dcaf0;">${sandboxCount}</div>
                                    <div class="text-muted small text-uppercase" style="font-size: 0.65rem; letter-spacing: 0.1em;">Sandboxes</div>
                                </div>
                            </div>
                        </div>
                        <!-- Background glow effect -->
                        <div class="position-absolute" style="top: -50px; right: -50px; width: 200px; height: 200px; background: radial-gradient(circle, rgba(91, 42, 134, 0.15) 0%, transparent 70%); z-index: 1;"></div>
                    </div>
                `;

                this.app.bindAction(headingContainer, '[data-action="set-custom-name"]', () => this.app.showCustomNameModal());
            } else {
                headingContainer.innerHTML = `
                    <div class="context-header-card">
                        <div class="d-flex align-items-center gap-3">
                            <div class="project-logo-wrapper" style="width: 48px; height: 48px;">
                                <span style="font-size: 1.5rem;">&#x1F310;</span>
                            </div>
                            <div>
                                <h4 class="mb-0 text-white fw-semibold">Global Context</h4>
                                <span class="text-muted">Manage settings and view history across all projects</span>
                            </div>
                        </div>
                    </div>
                `;
            }
        }

        // Show/hide local-specific sections
        const localQuickActions = root.querySelector('[data-local-quick-actions]');
        const localHistorySection = root.querySelector('[data-local-history-section]');
        const localFileTreeSection = root.querySelector('[data-local-file-tree-section]');
        const localAgentCountCol = root.querySelector('[data-local-agent-count-col]');
        const localEnvCountCol = root.querySelector('[data-local-env-count-col]');

        if (isLocal) {
            if (localQuickActions) localQuickActions.style.removeProperty('display');
            if (localFileTreeSection) localFileTreeSection.style.removeProperty('display');
            if (localAgentCountCol) localAgentCountCol.style.removeProperty('display');
            if (localEnvCountCol) localEnvCountCol.style.removeProperty('display');

            // Populate local data
            const agentCount = root.querySelector('[data-agent-count]');
            if (agentCount) {
                agentCount.textContent = this.app.data.agents.length;
            }

            const envCountLocal = root.querySelector('[data-env-count-local]');
            if (envCountLocal) {
                envCountLocal.textContent = this.app.data.environments.length;
            }

            const fileTree = root.querySelector('[data-local-file-tree]');
            if (fileTree) {
                fileTree.innerHTML = this.app.renderLocalFileTree(fileTree);
            }
        }

        // Environments section — show for both local and global contexts
        if (localHistorySection) {
            localHistorySection.style.removeProperty('display');
            const envList = root.querySelector('[data-local-project-history]');
            if (envList) {
                this.populateEnvironmentsList(envList);
            }
            // Bind create environment quick button
            this.app.bindAction(localHistorySection, '[data-action="create-environment-quick"]', () => {
                this.app.environmentController.createEnvironment();
            });
            // Bind settings button to navigate to environments page
            this.app.bindAction(localHistorySection, '[data-action="open-environments-settings"]', () => {
                this.app.navigate('environments');
            });
        }

        // Global environment count
        const envCount = root.querySelector('[data-env-count]');
        if (envCount) {
            envCount.textContent = this.app.data.environments.length;
        }

        this.app.bindAction(root, '[data-action="launch-vscode"]', () => this.launchVSCode());
        this.app.bindActions(root, '[data-action="launch-cli"]', (element) => {
            const cli = element.dataset.cli;
            if (cli) {
                this.app.cliLauncher.launchCLI(cli);
            }
        });
        this.app.bindActions(root, '[data-action="navigate"]', (element) => {
            const view = element.dataset.view;
            if (view) {
                this.app.navigate(view);
            }
        });

        // Add handler for navigate-to-sandboxes
        this.app.bindAction(root, '[data-action="navigate-to-sandboxes"]', () => {
            const sandboxSection = document.querySelector('[data-sandbox-section]');
            if (sandboxSection) {
                sandboxSection.scrollIntoView({ behavior: 'smooth' });
                // Add a temporary highlight effect
                sandboxSection.querySelector('.card')?.classList.add('border-primary');
                setTimeout(() => {
                    sandboxSection.querySelector('.card')?.classList.remove('border-primary');
                }, 2000);
            }
        });

        // Sandboxes section - only show in local context
        const sandboxSection = root.querySelector('[data-sandbox-section]');
        if (sandboxSection && isLocal) {
            sandboxSection.style.removeProperty('display');
            const sandboxList = root.querySelector('[data-sandbox-list]');
            if (sandboxList) {
                this.populateSandboxesList(sandboxList);
            }
            this.app.bindAction(sandboxSection, '[data-action="create-sandbox"]', () => {
                this.app.sandboxController.createSandbox();
            });
        }

        // Terminal section
        const terminalSection = root.querySelector('[data-terminal-section]');
        if (terminalSection) {
            terminalSection.style.removeProperty('display');
            const terminalContent = terminalSection.querySelector('[data-terminal-content]');
            if (terminalContent) {
                terminalContent.innerHTML = this.app.terminalController.renderTerminalPanel();
                // Pass preselected environment ID if navigating from environments page
                this.app.terminalController.bindTerminalActions(
                    terminalContent,
                    data.preselectedEnvId || null
                );
            }
        }

        return fragment;
    }

    populateEnvironmentsList(container) {
        if (!container) return;
        container.innerHTML = '';

        const environments = this.app.data.environments || [];

        if (environments.length === 0) {
            container.innerHTML = '<p class="text-muted text-center">No custom environments yet. Create one from the Environments page.</p>';
            return;
        }

        const template = document.getElementById('environment-history-item-template');
        if (!template) {
            container.innerHTML = '<p class="text-muted text-center">No environments template found.</p>';
            return;
        }

        const fragment = document.createDocumentFragment();

        environments.forEach((env) => {
            const node = template.content.cloneNode(true);
            const brand = this.app.getCliBrand(env.cli);

            const name = node.querySelector('[data-env-name]');
            if (name) name.textContent = env.name;

            const badge = node.querySelector('[data-env-badge]');
            if (badge) {
                badge.textContent = env.cli;
                if (brand.className) badge.classList.add(brand.className);
            }

            const logo = node.querySelector('[data-env-logo]');
            if (logo && brand.logo) {
                logo.src = brand.logo;
                logo.alt = `${brand.label} logo`;
            } else if (logo) {
                logo.remove();
            }

            const time = node.querySelector('[data-env-time]');
            if (time) time.textContent = env.lastUsed;

            const launchButton = node.querySelector('[data-env-launch]');
            if (launchButton) {
                const launchText = launchButton.querySelector('[data-env-launch-text]');
                if (launchText) {
                    launchText.textContent = 'Launch in CLI';
                }

                launchButton.addEventListener('click', (event) => {
                    event.stopPropagation();
                    this.app.cliLauncher.launchCLI(env.cli, env.name);
                });
            }

            const webUIButton = node.querySelector('[data-env-launch-webui]');
            if (webUIButton) {
                webUIButton.addEventListener('click', (event) => {
                    event.stopPropagation();
                    this.launchEnvInWebUI(env.id, env.name, env.cli);
                });
            }

            fragment.appendChild(node);
        });

        container.appendChild(fragment);
    }

    populateSandboxesList(container) {
        if (!container) return;
        container.innerHTML = '';

        const sandboxes = this.app.data.sandboxes || [];

        if (sandboxes.length === 0) {
            container.innerHTML = '<p class="text-muted text-center">No sandboxes yet. Create one to work in an isolated copy of your project.</p>';
            return;
        }

        const template = document.getElementById('sandbox-item-template');
        if (!template) {
            container.innerHTML = '<p class="text-muted text-center">Template not found.</p>';
            return;
        }

        const fragment = document.createDocumentFragment();

        sandboxes.forEach((sb) => {
            const node = template.content.cloneNode(true);

            const name = node.querySelector('[data-sandbox-name]');
            if (name) name.textContent = sb.name;

            const branch = node.querySelector('[data-sandbox-branch]');
            if (branch) branch.textContent = sb.branch;

            const sourceBranchContainer = node.querySelector('[data-sandbox-source-branch]');
            const sourceBranchText = node.querySelector('[data-sandbox-source-branch-text]');
            if (sourceBranchContainer && sourceBranchText && sb.sourceBranch) {
                sourceBranchContainer.style.removeProperty('display');
                sourceBranchText.textContent = `from ${sb.sourceBranch}`;
            }

            const time = node.querySelector('[data-sandbox-time]');
            if (time) time.textContent = sb.created;

            const path = node.querySelector('[data-sandbox-path]');
            if (path) path.textContent = sb.path;

            // Show remote URL if available
            const remoteContainer = node.querySelector('[data-sandbox-remote]');
            const remoteUrlEl = node.querySelector('[data-sandbox-remote-url]');
            if (remoteContainer && remoteUrlEl && sb.remoteUrl) {
                remoteContainer.style.removeProperty('display');
                remoteUrlEl.textContent = sb.remoteUrl;
            }

            // Populate CLI select with environments
            const cliSelect = node.querySelector('[data-sandbox-cli-select]');
            if (cliSelect) {
                this.populateSandboxCliSelect(cliSelect);
            }

            // Web Terminal launch button
            const webUiBtn = node.querySelector('[data-sandbox-launch-webui]');
            if (webUiBtn) {
                webUiBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const { cli, environmentName } = this.parseSandboxCliSelection(cliSelect);
                    this.app.sandboxController.launchInWebUI(sb.id, sb.name, cli, environmentName);
                });
            }

            // External Terminal launch button
            const terminalBtn = node.querySelector('[data-sandbox-launch-terminal]');
            if (terminalBtn) {
                terminalBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const { cli, environmentName } = this.parseSandboxCliSelection(cliSelect);
                    this.app.sandboxController.launchInExternalTerminal(sb.id, sb.name, cli, environmentName);
                });
            }

            // Shell button
            const shellBtn = node.querySelector('[data-sandbox-launch-shell]');
            if (shellBtn) {
                shellBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.app.sandboxController.launchShell(sb.id, sb.name);
                });
            }

            // VS Code button
            const vscodeBtn = node.querySelector('[data-sandbox-vscode]');
            if (vscodeBtn) {
                vscodeBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.app.sandboxController.launchVSCode(sb.id, sb.name);
                });
            }

            // See Changes (diff) button
            const diffBtn = node.querySelector('[data-sandbox-diff]');
            if (diffBtn) {
                diffBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.app.sandboxController.showDiff(sb.id, sb.name);
                });
            }

            // Push to Remote button (only visible when remoteUrl is set)
            const pushBtn = node.querySelector('[data-sandbox-push-remote]');
            if (pushBtn) {
                if (sb.remoteUrl) {
                    pushBtn.style.removeProperty('display');
                    pushBtn.addEventListener('click', (e) => {
                        e.stopPropagation();
                        this.app.sandboxController.pushToRemote(sb.id, sb.name);
                    });
                }
            }

            // Merge Local button (only visible when sourceBranch is set)
            const mergeBtn = node.querySelector('[data-sandbox-merge-local]');
            if (mergeBtn) {
                if (sb.sourceBranch) {
                    mergeBtn.style.removeProperty('display');
                    const mergeLabel = mergeBtn.querySelector('[data-sandbox-merge-label]');
                    if (mergeLabel) mergeLabel.textContent = `Merge \u2192 ${sb.sourceBranch}`;
                    mergeBtn.addEventListener('click', (e) => {
                        e.stopPropagation();
                        this.app.sandboxController.mergeLocally(sb.id, sb.name, sb.sourceBranch);
                    });
                }
            }

            // Delete button
            const deleteBtn = node.querySelector('[data-sandbox-delete]');
            if (deleteBtn) {
                deleteBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.app.sandboxController.deleteSandbox(sb.id, sb.name);
                });
            }

            fragment.appendChild(node);
        });

        container.appendChild(fragment);
    }

    populateSandboxCliSelect(selectEl) {
        const environments = this.app.data.environments || [];

        // Clear and re-add base CLIs
        selectEl.innerHTML = '';

        const baseGroup = document.createElement('optgroup');
        baseGroup.label = 'Base CLIs';
        baseGroup.innerHTML = `
            <option value="base:claude">Claude</option>
            <option value="base:codex">Codex</option>
            <option value="base:gemini">Gemini</option>
        `;
        selectEl.appendChild(baseGroup);

        if (environments.length > 0) {
            const envGroup = document.createElement('optgroup');
            envGroup.label = 'Custom Environments';
            environments.forEach(env => {
                const option = document.createElement('option');
                option.value = `env:${env.id}:${env.cli}`;
                option.textContent = `${env.name} (${env.cli})`;
                envGroup.appendChild(option);
            });
            selectEl.appendChild(envGroup);
        }
    }

    parseSandboxCliSelection(selectEl) {
        const value = selectEl?.value || 'base:claude';
        if (value.startsWith('base:')) {
            return { cli: value.replace('base:', ''), environmentName: null };
        }
        if (value.startsWith('env:')) {
            const parts = value.split(':');
            const envId = parseInt(parts[1]);
            const cli = parts[2];
            const env = (this.app.data.environments || []).find(e => e.id === envId);
            return { cli, environmentName: env?.name || null };
        }
        return { cli: value, environmentName: null };
    }

    async launchEnvInWebUI(envId, envName, cli) {
        this.app.showToast('Web Terminal', `Launching ${envName} (${cli})...`, 'info');

        const terminalContent = document.querySelector('[data-terminal-content]');
        if (terminalContent) {
            const selection = `env:${envId}:${cli}`;
            await this.app.terminalController.startTerminal(terminalContent, selection);
        }
    }

    async launchVSCode() {
        try {
            const response = await this.app.apiCall('/api/v1/cli/launch/vscode', 'POST');
            this.app.showToast('VS Code', response.message || 'VS Code launched successfully', 'success');
        } catch (error) {
            this.app.showError('Failed to launch VS Code');
        }
    }
}
