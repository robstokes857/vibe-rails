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
                const sandboxCount = this.app.data.sandboxes.length;
                const agentCount = this.app.data.agents.length;
                const gitBranch = this.app.data.configs?.gitBranch;
                const isSandbox = this.app.data.configs?.isSandbox === true;

                headingContainer.innerHTML = `
                    <div class="context-header-card position-relative overflow-hidden py-3 px-4" style="background: ${isSandbox ? 'rgba(20, 14, 5, 0.5)' : 'rgba(15, 23, 42, 0.4)'}; border: 1px solid ${isSandbox ? 'rgba(251, 146, 60, 0.25)' : 'rgba(255, 255, 255, 0.05)'}; border-radius: 12px; backdrop-filter: blur(10px);">
                        ${isSandbox ? `<div style="position: absolute; top: 0; left: 0; right: 0; height: 2px; background: linear-gradient(90deg, transparent, rgba(251,146,60,0.6), transparent);"></div>` : ''}
                        <div class="d-flex align-items-center justify-content-between flex-wrap gap-3">
                            <div class="d-flex align-items-center gap-3">
                                <div class="project-logo-wrapper d-flex align-items-center justify-content-center flex-shrink-0" style="width: 48px; height: 48px; background: ${isSandbox ? 'rgba(251,146,60,0.08)' : 'rgba(59, 130, 246, 0.08)'}; border: 1px solid ${isSandbox ? 'rgba(251,146,60,0.25)' : 'rgba(59, 130, 246, 0.2)'}; border-radius: 12px; color: ${isSandbox ? '#fb923c' : 'var(--color-primary)'}; box-shadow: 0 4px 12px rgba(0,0,0,0.15); font-size: 20px;">
                                    <i class="fa-solid fa-folder-open"></i>
                                </div>
                                <div class="d-flex flex-column gap-1">
                                    <div class="d-flex align-items-center gap-2">
                                        <h5 class="mb-0 text-white fw-bold" style="font-size: 1.15rem;">${this.app.escapeHtml(projectName)}</h5>
                                        ${isSandbox ? `<span style="font-size: 0.6rem; font-weight: 700; letter-spacing: 0.12em; text-transform: uppercase; color: #fb923c; background: rgba(251,146,60,0.12); border: 1px solid rgba(251,146,60,0.3); border-radius: 4px; padding: 2px 6px;">Sandbox</span>` : ''}
                                        <button class="btn btn-link btn-sm p-0 text-muted hover-accent ms-1 d-flex align-items-center" type="button" data-action="set-custom-name" title="Rename project">
                                            <i class="fa-solid fa-pen-to-square" style="font-size: 12px;"></i>
                                        </button>
                                    </div>
                                    <div class="d-flex align-items-center flex-wrap gap-2">
                                        ${gitRemoteUrl ? `
                                        <div class="d-flex align-items-center gap-1">
                                            <i class="fa-brands fa-github text-muted opacity-75" style="font-size: 12px;"></i>
                                            <span class="xx-small text-muted opacity-50 text-uppercase fw-bold" style="letter-spacing: 0.08em;">Repo</span>
                                            <a href="${gitRemoteUrl}" target="_blank" class="text-decoration-none small text-muted hover-accent">
                                                ${this.app.escapeHtml(repoName)}
                                            </a>
                                        </div>` : ''}
                                        ${gitBranch ? `
                                        <div class="d-flex align-items-center gap-1">
                                            <i class="fa-solid fa-code-branch text-muted opacity-75" style="font-size: 11px;"></i>
                                            <span class="xx-small text-muted opacity-50 text-uppercase fw-bold" style="letter-spacing: 0.08em;">Branch</span>
                                            <span class="small text-muted">${this.app.escapeHtml(gitBranch)}</span>
                                        </div>` : ''}
                                        <div class="d-flex align-items-center gap-1 min-w-0">
                                            <i class="fa-solid fa-folder text-muted opacity-75 flex-shrink-0" style="font-size: 11px;"></i>
                                            <span class="xx-small text-muted opacity-50 text-uppercase fw-bold flex-shrink-0" style="letter-spacing: 0.08em;">Path</span>
                                            <div class="text-muted small font-monospace text-truncate">${rootPath}</div>
                                            <button class="btn btn-link btn-sm p-0 text-muted hover-accent opacity-50 ms-1 flex-shrink-0 d-flex align-items-center" type="button" data-action="copy-path" title="Copy path">
                                                <i class="fa-regular fa-copy" style="font-size: 11px;"></i>
                                            </button>
                                        </div>
                                    </div>
                                </div>
                            </div>

                            <div class="d-flex gap-2">
                                <div class="d-flex align-items-center gap-2 px-3 py-2 rounded" style="background: rgba(255, 255, 255, 0.03); border: 1px solid rgba(255, 255, 255, 0.05);">
                                    <span class="text-muted xx-small text-uppercase fw-bold" style="letter-spacing: 0.1em;">Agents</span>
                                    <span class="fw-bold mb-0" style="color: var(--color-accent); font-size: 1rem;">${agentCount}</span>
                                </div>
                                <div class="d-flex align-items-center gap-2 px-3 py-2 rounded" style="background: rgba(255, 255, 255, 0.03); border: 1px solid rgba(255, 255, 255, 0.05);">
                                    <span class="text-muted xx-small text-uppercase fw-bold" style="letter-spacing: 0.1em;">Sandboxes</span>
                                    <span class="fw-bold mb-0" style="color: var(--color-primary); font-size: 1rem;">${sandboxCount}</span>
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
                        cliSelect.classList.remove('terminal-selection-shake');
                        void cliSelect.offsetWidth;
                        cliSelect.classList.add('terminal-selection-shake');
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
