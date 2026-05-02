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

        // Context Heading
        const headingContainer = root.querySelector('[data-context-heading-container]');
        if (headingContainer) {
            if (isInGit) {
                const rootPath = this.app.data.configs?.rootPath || 'Unknown Path';
                const gitRemoteUrl = this.app.data.configs?.gitRemoteUrl;
                const repoName = gitRemoteUrl ? 
                    gitRemoteUrl.split('/').pop().replace(/\.git$/, '') : 
                    this.app.getProjectNameFromPath(rootPath);

                const folderName = this.app.getProjectNameFromPath(rootPath);
                const projectName = this._customProjectName || folderName;
                const sandboxCount = this.app.data.sandboxes?.length || 0;
                const agentCount = this.app.data.agents?.length || 0;
                const totalRuleCount = (this.app.data.agents || []).reduce((sum, agent) => sum + (Number(agent.ruleCount) || 0), 0);
                const environmentCount = this.app.data.environments?.length || 0;
                const gitBranch = this.app.data.configs?.gitBranch;
                const isSandbox = this.app.data.configs?.isSandbox === true;
                const contextLabel = isSandbox ? 'Sandbox Context' : 'Project Context';
                const contextIcon = isSandbox ? 'fa-box-archive' : 'fa-folder-tree';
                const safeProjectName = this.app.escapeHtml(projectName);
                const safeRootPath = this.app.escapeHtml(rootPath);
                const safeRepoName = this.app.escapeHtml(repoName);
                const safeRemoteUrl = this.app.escapeHtml(gitRemoteUrl || '');
                const agentLabel = agentCount === 1 ? 'Agent' : 'Agents';
                const sandboxLabel = sandboxCount === 1 ? 'Sandbox' : 'Sandboxes';
                const ruleLabel = totalRuleCount === 1 ? 'rule' : 'rules';
                const remoteHref = /^https?:\/\//i.test(gitRemoteUrl || '') ? safeRemoteUrl : '';
                const remoteBranchValue = gitRemoteUrl ? 'Pending data' : 'No remote';
                const safeRemoteBranchValue = this.app.escapeHtml(remoteBranchValue);
                const branchValue = gitBranch || 'Not detected';
                const safeBranchValue = this.app.escapeHtml(branchValue);

                const remoteRepoMarkup = gitRemoteUrl
                    ? remoteHref
                        ? `<a class="dash-fact-card dash-fact-link" href="${remoteHref}" target="_blank" rel="noopener noreferrer" title="${safeRemoteUrl}">
                                <span class="dash-fact-label"><i class="fa-solid fa-cloud"></i> Remote repo</span>
                                <strong>${safeRepoName}</strong>
                                <small>${safeRemoteUrl}</small>
                            </a>`
                        : `<div class="dash-fact-card" title="${safeRemoteUrl}">
                                <span class="dash-fact-label"><i class="fa-solid fa-cloud"></i> Remote repo</span>
                                <strong>${safeRepoName}</strong>
                                <small>${safeRemoteUrl}</small>
                            </div>`
                    : `<div class="dash-fact-card is-muted">
                            <span class="dash-fact-label"><i class="fa-solid fa-cloud"></i> Remote repo</span>
                            <strong>No remote</strong>
                            <small>origin not detected</small>
                        </div>`;

                headingContainer.innerHTML = `
                    <section class="context-header-card dashboard-context-header ${isSandbox ? 'context-header-sandbox' : ''}">
                        ${isSandbox ? '<div class="context-header-accent-bar"></div>' : ''}
                        <div class="dashboard-context-layout">
                            <div class="dashboard-context-main">
                                <div class="dash-project-icon ${isSandbox ? 'dash-project-icon-sandbox' : ''}" aria-hidden="true">
                                    <i class="fa-solid ${contextIcon}"></i>
                                </div>
                                <div class="dashboard-context-copy">
                                    <div class="dashboard-context-kicker">
                                        <i class="fa-solid ${contextIcon}"></i>
                                        ${contextLabel}
                                    </div>
                                    <div class="dashboard-context-title-line">
                                        <h2 class="dashboard-context-name">${safeProjectName}</h2>
                                        ${isSandbox ? '<span class="dash-sandbox-badge">Sandbox</span>' : ''}
                                        <button class="dash-icon-button" type="button" data-action="set-custom-name" title="Rename project">
                                            <i class="fa-solid fa-pen-to-square"></i>
                                        </button>
                                    </div>
                                    <div class="dash-path-row">
                                        <span class="dash-path-label">
                                            <i class="fa-regular fa-folder-open"></i>
                                            Directory
                                        </span>
                                        <code class="dash-path-badge" title="${safeRootPath}">${safeRootPath}</code>
                                        <button class="dash-icon-button compact" type="button" data-action="copy-path" title="Copy path">
                                            <i class="fa-regular fa-copy"></i>
                                        </button>
                                    </div>
                                </div>
                            </div>
                            <div class="dashboard-context-stats" aria-label="Project counts">
                                <button class="dash-summary-card" type="button" data-action="navigate" data-view="environments">
                                    <span class="dash-summary-icon"><i class="fa-solid fa-layer-group"></i></span>
                                    <strong data-env-count>${environmentCount}</strong>
                                    <span>Custom Environments</span>
                                </button>
                                <button class="dash-summary-card" type="button" data-action="navigate" data-view="agents">
                                    <span class="dash-summary-icon"><i class="fa-solid fa-circle-nodes"></i></span>
                                    <strong>${agentCount}</strong>
                                    <span>${agentLabel}</span>
                                    <small>${totalRuleCount} ${ruleLabel}</small>
                                </button>
                                <button class="dash-summary-card" type="button" data-action="navigate" data-view="environments">
                                    <span class="dash-summary-icon"><i class="fa-solid fa-boxes-stacked"></i></span>
                                    <strong>${sandboxCount}</strong>
                                    <span>${sandboxLabel}</span>
                                </button>
                            </div>
                        </div>
                        <div class="dashboard-context-grid" aria-label="Repository context">
                            <div class="dash-fact-card ${gitBranch ? '' : 'is-muted'}">
                                <span class="dash-fact-label"><i class="fa-solid fa-code-branch"></i> Local branch</span>
                                <strong title="${safeBranchValue}">${safeBranchValue}</strong>
                                <small>${isSandbox ? 'sandbox checkout' : 'working tree'}</small>
                            </div>
                            ${remoteRepoMarkup}
                            <div class="dash-fact-card is-placeholder">
                                <span class="dash-fact-label"><i class="fa-solid fa-diagram-project"></i> Remote branch</span>
                                <strong>${safeRemoteBranchValue}</strong>
                                <small>upstream tracking</small>
                            </div>
                        </div>
                        <div class="dashboard-insight-strip" aria-label="Dashboard insight placeholders">
                            <div class="dash-telemetry-card">
                                <span>Vibe AI Size</span>
                                <strong>--</strong>
                                <small>Index size</small>
                            </div>
                            <div class="dash-telemetry-card">
                                <span>Top CLI</span>
                                <strong>--</strong>
                                <small>Usage leader</small>
                            </div>
                            <div class="dash-telemetry-card">
                                <span>Sessions</span>
                                <strong>--</strong>
                                <small>Total runs</small>
                            </div>
                        </div>
                    </section>
                `;

                this.app.bindAction(headingContainer, '[data-action="set-custom-name"]', () => this.app.showCustomNameModal());
                this.app.bindAction(headingContainer, '[data-action="copy-path"]', () => {
                    navigator.clipboard.writeText(rootPath);
                    this.app.showToast('Copied', 'Path copied to clipboard', 'info');
                });
                headingContainer.querySelectorAll('[data-action="navigate"][data-view]').forEach((link) => {
                    link.addEventListener('click', (e) => {
                        e.preventDefault();
                        const view = link.getAttribute('data-view');
                        if (view) this.app.navigate(view);
                    });
                });

            } else {
                headingContainer.innerHTML = `
                    <section class="context-header-card dashboard-context-header">
                        <div class="dashboard-context-main">
                            <div class="dash-project-icon dash-project-icon-global" aria-hidden="true">
                                <i class="fa-solid fa-globe"></i>
                            </div>
                            <div class="dashboard-context-copy">
                                <div class="dashboard-context-kicker">
                                    <i class="fa-solid fa-globe"></i>
                                    Global Context
                                </div>
                                <h2 class="dashboard-context-name">Global Context</h2>
                                <p class="dashboard-context-description">Manage settings and review activity across all projects.</p>
                            </div>
                        </div>
                    </section>
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
