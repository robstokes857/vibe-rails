import { buildLlmSelectionValue, parseLlmSelection, populateLlmSelectionSelect } from './utils.js';

export class DashboardController {
    constructor(app) {
        this.app = app;
    }

    async loadDashboard(data = {}) {
        await this.app.refreshDashboardData();

        // Fetch custom project name if in local context
        if (this.app.data.isInGit) {
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

        // Ensure we are at the top on load
        window.scrollTo(0, 0);
    }

    renderUnifiedDashboard(data = {}) {
        const fragment = this.app.cloneTemplate('dashboard-template');
        const root = fragment.querySelector('[data-dashboard]');
        if (!root) return fragment;

        const isInGit = this.app.data.isInGit;
        const showPrereleaseContextHeader = this.app.appSettings?.enablePrerelease === true;

        // Context Heading
        const headingContainer = root.querySelector('[data-context-heading-container]');
        const headingRow = headingContainer?.closest('.row');
        if (headingRow) {
            headingRow.hidden = !showPrereleaseContextHeader;
        }

        if (headingContainer && showPrereleaseContextHeader) {
            if (isInGit) {
                const rootPath = this.app.data.configs?.rootPath || 'Unknown Path';
                const gitRemoteUrl = this.app.data.configs?.gitRemoteUrl;
                const repoName = gitRemoteUrl ? 
                    gitRemoteUrl.split('/').pop().replace(/\.git$/, '') : 
                    this.app.getProjectNameFromPath(rootPath);

                const folderName = this.app.getProjectNameFromPath(rootPath);
                const projectName = this._customProjectName || folderName;
                const sandboxCount = this.app.data.sandboxes.length;
                const agentCount = this.app.data.agents.length;
                const gitBranch = this.app.data.configs?.gitBranch;
                const isSandbox = this.app.data.configs?.isSandbox === true;

                headingContainer.innerHTML = `
                    <div class="context-header-card position-relative overflow-hidden py-4 px-4 ${isSandbox ? 'context-header-sandbox' : ''}">
                        ${isSandbox ? '<div class="context-header-accent-bar"></div>' : ''}

                        <div class="d-flex align-items-center gap-3 mb-4">
                            <div class="dash-project-icon ${isSandbox ? 'dash-project-icon-sandbox' : ''}">
                                <i class="fa-solid fa-folder-tree"></i>
                            </div>
                            <div class="d-flex flex-column gap-1">
                                <div class="d-flex align-items-center gap-2">
                                    <h4 class="mb-0 text-white fw-bold">${this.app.escapeHtml(projectName)}</h4>
                                    ${isSandbox ? '<span class="dash-sandbox-badge">Sandbox</span>' : ''}
                                    <button class="btn btn-link btn-sm p-0 text-muted hover-accent ms-1 d-flex align-items-center opacity-75" type="button" data-action="set-custom-name" title="Rename project">
                                        <i class="fa-solid fa-pen-to-square" style="font-size: 13px;"></i>
                                    </button>
                                </div>
                                <div class="d-flex align-items-center flex-wrap gap-4 mt-1">
                                    ${gitRemoteUrl ? `
                                    <div class="d-flex flex-column">
                                        <span class="dash-meta-label">Remote</span>
                                        <div class="d-flex align-items-center gap-1">
                                            <i class="fa-brands fa-github text-muted opacity-50" style="font-size: 12px;"></i>
                                            <a href="${gitRemoteUrl}" target="_blank" class="text-decoration-none small text-white hover-accent opacity-75 fw-medium">${this.app.escapeHtml(repoName)}</a>
                                        </div>
                                    </div>` : ''}
                                    ${gitBranch ? `
                                    <div class="d-flex flex-column">
                                        <span class="dash-meta-label">Branch</span>
                                        <div class="d-flex align-items-center gap-1">
                                            <i class="fa-solid fa-code-branch text-muted opacity-50" style="font-size: 11px;"></i>
                                            <span class="small text-white opacity-75 fw-medium">${this.app.escapeHtml(gitBranch)}</span>
                                        </div>
                                    </div>` : ''}
                                    <div class="d-flex flex-column min-w-0">
                                        <span class="dash-meta-label">Working Dir</span>
                                        <div class="d-flex align-items-center gap-1">
                                            <i class="fa-solid fa-location-dot text-muted opacity-50 flex-shrink-0" style="font-size: 11px;"></i>
                                            <div class="text-white small font-monospace text-truncate opacity-75 fw-medium" style="max-width: 400px;">${rootPath}</div>
                                            <button class="btn btn-link btn-sm p-0 text-muted hover-accent opacity-50 ms-1 flex-shrink-0 d-flex align-items-center" type="button" data-action="copy-path" title="Copy path">
                                                <i class="fa-regular fa-copy" style="font-size: 11px;"></i>
                                            </button>
                                        </div>
                                    </div>
                                </div>
                            </div>
                        </div>

                        <div class="row g-3">
                            <div class="col-md-2">
                                <div class="dash-insight-card">
                                    <div class="dash-insight-label">Environments</div>
                                    <div class="d-flex align-items-baseline gap-2">
                                        <span class="dash-insight-value text-accent" data-env-count>${this.app.data.environments?.length || 0}</span>
                                        <span class="dash-insight-sub">Custom</span>
                                    </div>
                                    <button class="dash-insight-link" data-action="navigate" data-view="environments">
                                        <i class="fa-solid fa-arrow-right me-1" style="font-size: 9px;"></i>Manage
                                    </button>
                                </div>
                            </div>
                            <div class="col-md-2">
                                <div class="dash-insight-card">
                                    <div class="dash-insight-label">Sandboxes</div>
                                    <div class="d-flex align-items-baseline gap-2">
                                        <span class="dash-insight-value text-primary">${sandboxCount}</span>
                                        <span class="dash-insight-sub">Live</span>
                                    </div>
                                    <div class="dash-insight-sub mt-1">Isolated environments</div>
                                </div>
                            </div>
                            <div class="col-md-2">
                                <div class="dash-insight-card">
                                    <div class="dash-insight-label">Active Agents</div>
                                    <div class="d-flex align-items-baseline gap-2">
                                        <span class="dash-insight-value text-accent">${agentCount}</span>
                                        <span class="dash-insight-sub">Live</span>
                                    </div>
                                    <div class="dash-insight-sub mt-1">Managing project rules</div>
                                </div>
                            </div>
                            <div class="col-md-2">
                                <div class="dash-insight-card">
                                    <div class="dash-insight-label">VCA Status</div>
                                    <div class="d-flex align-items-baseline gap-2">
                                        <span class="dash-insight-value text-info">Clean</span>
                                    </div>
                                    <div class="dash-insight-sub mt-1">0 active violations</div>
                                </div>
                            </div>
                            <div class="col-md-2">
                                <div class="dash-insight-card">
                                    <div class="dash-insight-label">Shared AI Learning</div>
                                    <div class="d-flex align-items-baseline gap-2">
                                        <span class="dash-insight-value text-warning"><i class="fa-solid fa-brain"></i></span>
                                        <span class="dash-insight-sub">Local embeddings</span>
                                    </div>
                                    <a href="/bert.html" target="_blank" class="dash-insight-link">
                                        <i class="fa-solid fa-arrow-right me-1" style="font-size: 9px;"></i>Explorer
                                    </a>
                                </div>
                            </div>
                            <div class="col-md-2">
                                <div class="dash-insight-card">
                                    <div class="dash-insight-label">MCP Server</div>
                                    <div class="d-flex align-items-baseline gap-2">
                                        <span class="dash-insight-value text-accent"><i class="fa-solid fa-plug"></i></span>
                                        <span class="dash-insight-sub">Tool server</span>
                                    </div>
                                    <a href="/mcp.html" target="_blank" class="dash-insight-link">
                                        <i class="fa-solid fa-arrow-right me-1" style="font-size: 9px;"></i>Explorer
                                    </a>
                                </div>
                            </div>
                        </div>
                    </div>
                `;

                this.app.bindAction(headingContainer, '[data-action="set-custom-name"]', () => this.app.showCustomNameModal());
                this.app.bindAction(headingContainer, '[data-action="copy-path"]', () => {
                    navigator.clipboard.writeText(rootPath);
                    this.app.showToast('Copied', 'Path copied to clipboard', 'info');
                });

            } else {
                headingContainer.innerHTML = `
                    <div class="context-header-card position-relative overflow-hidden py-3 px-4" style="background: rgba(15, 23, 42, 0.4); border: 1px solid rgba(255, 255, 255, 0.05); border-radius: 12px; backdrop-filter: blur(10px);">
                        <div class="d-flex align-items-center gap-3">
                            <div class="project-logo-wrapper d-flex align-items-center justify-content-center flex-shrink-0" style="width: 48px; height: 48px; background: rgba(59, 130, 246, 0.08); border: 1px solid rgba(59, 130, 246, 0.2); border-radius: 12px; color: var(--color-primary); box-shadow: 0 4px 12px rgba(0,0,0,0.15); font-size: 20px;">
                                <i class="fa-solid fa-globe"></i>
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

        if (isInGit) {
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
        this.app.bindActions(root, '[data-action="launch-web-terminal"]', (element) => {
            const cli = element.dataset.cli;
            if (cli) {
                const terminalContent = document.querySelector('[data-terminal-content]');
                if (terminalContent) {
                    this.app.terminalController.startTerminal(terminalContent, buildLlmSelectionValue(cli));
                }
            }
        });
        this.app.bindActions(root, '[data-action="launch-native-terminal"]', (element) => {
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
        if (sandboxSection && isInGit) {
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
        const terminalContent = root.querySelector('[data-terminal-content]');
        if (terminalContent) {
            terminalContent.innerHTML = this.app.terminalController.renderTerminalPanel();
            // Pass preselected environment ID if navigating from environments page
            this.app.terminalController.bindTerminalActions(
                terminalContent,
                data.preselectedEnvId || null
            );
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
                if ((env.cli || '').toLowerCase() === 'codex' || (env.cli || '').toLowerCase() === 'chatgpt' || (env.cli || '').toLowerCase() === 'openai') {
                    logo.classList.add('icon-light');
                }
            } else if (logo) {
                logo.remove();
            }

            const time = node.querySelector('[data-env-time]');
            if (time) time.textContent = env.lastUsed;

            const launchButton = node.querySelector('[data-env-launch]');
            if (launchButton) {
                const launchText = launchButton.querySelector('[data-env-launch-text]');
                if (launchText) {
                    launchText.textContent = 'Native';
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

            const deleteButton = node.querySelector('[data-env-delete]');
            if (deleteButton) {
                deleteButton.addEventListener('click', (event) => {
                    event.stopPropagation();
                    this.app.environmentController.removeEnvironment(env.name);
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
            container.innerHTML = '<p class="text-muted text-center py-3">No sandboxes yet. Create one to work in an isolated copy of your project.</p>';
            return;
        }

        const template = document.getElementById('sandbox-item-template');
        if (!template) {
            container.innerHTML = '<p class="text-muted text-center py-3">Template not found.</p>';
            return;
        }

        const fragment = document.createDocumentFragment();

        sandboxes.forEach((sb) => {
            const node = template.content.cloneNode(true);

            const name = node.querySelector('[data-sandbox-name]');
            if (name) name.textContent = sb.name;

            const branch = node.querySelector('[data-sandbox-branch]');
            if (branch) branch.textContent = sb.branch;

            const path = node.querySelector('[data-sandbox-path]');
            if (path) path.textContent = sb.path;

            const time = node.querySelector('[data-sandbox-time]');
            if (time) time.textContent = sb.created;

            // Diff button
            const diffBtn = node.querySelector('[data-sandbox-diff]');
            if (diffBtn) {
                diffBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.app.sandboxController.showDiff(sb.id, sb.name);
                });
            }

            // Merge local button
            const mergeLocalBtn = node.querySelector('[data-sandbox-merge-local]');
            if (mergeLocalBtn) {
                mergeLocalBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.app.sandboxController.mergeLocally(sb.id, sb.name, sb.branch);
                });
            }

            // Push to remote button
            const pushRemoteBtn = node.querySelector('[data-sandbox-push-remote]');
            if (pushRemoteBtn) {
                pushRemoteBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.app.sandboxController.pushToRemote(sb.id, sb.name);
                });
            }

            // CLI select
            const cliSelect = node.querySelector('[data-sandbox-cli-select]');
            if (cliSelect) {
                this.populateSandboxCliSelect(cliSelect);
            }

            // Helper to resolve CLI selection — returns null and shakes if nothing selected
            const resolveCli = () => {
                const result = this.parseSandboxCliSelection(cliSelect);
                if (!result) {
                    if (cliSelect) {
                        cliSelect.classList.remove('vb-terminal-selection-shake');
                        void cliSelect.offsetWidth;
                        cliSelect.classList.add('vb-terminal-selection-shake');
                        cliSelect.focus();
                        if (typeof cliSelect.showPicker === 'function') {
                            try { cliSelect.showPicker(); } catch {}
                        }
                    }
                    return null;
                }
                return result;
            };

            // CLI launch button
            const cliBtn = node.querySelector('[data-sandbox-launch-cli]');
            if (cliBtn) {
                cliBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const result = resolveCli();
                    if (!result) return;
                    this.app.sandboxController.launchInExternalTerminal(sb.id, sb.name, result.cli, result.environmentName);
                });
            }

            // Web Terminal launch button
            const webUiBtn = node.querySelector('[data-sandbox-launch-webui]');
            if (webUiBtn) {
                webUiBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const result = resolveCli();
                    if (!result) return;
                    this.app.sandboxController.launchInWebUI(sb.id, sb.name, result.cli, result.environmentName);
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
        populateLlmSelectionSelect(selectEl, this.app.data.environments || [], {
            placeholder: 'Select CLI...',
            includeDefaultSuffix: false
        });
    }

    parseSandboxCliSelection(selectEl) {
        const parsed = parseLlmSelection(selectEl?.value, this.app.data.environments || []);
        return parsed.cli
            ? { cli: parsed.cli, environmentName: parsed.environmentName }
            : null;
    }

    async launchEnvInWebUI(envId, envName, cli) {
        this.app.showToast('Web Terminal', `Launching ${envName} (${cli})...`, 'info');

        const terminalContent = document.querySelector('[data-terminal-content]');
        if (terminalContent) {
            const selection = buildLlmSelectionValue(cli, envId);
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
