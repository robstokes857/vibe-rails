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

                headingContainer.innerHTML = `
                    <div class="context-header-card">
                        <div class="d-flex align-items-center justify-content-between flex-wrap gap-3">
                            <div class="d-flex align-items-center gap-3">
                                <div class="project-logo-wrapper" style="width: 48px; height: 48px;">
                                    <span style="font-size: 1.5rem;">&#x1F4C2;</span>
                                </div>
                                <div>
                                    <h4 class="mb-0 text-white fw-semibold">${projectName}</h4>
                                    <div class="d-flex align-items-center gap-2 mt-1">
                                        <span class="text-muted small">Running in:</span>
                                        <span class="path-badge py-1 px-2" style="font-size: 0.85rem;">${path}</span>
                                    </div>
                                </div>
                            </div>
                            <button class="btn btn-sm btn-outline-secondary" type="button" data-action="set-custom-name">
                                <span>&#x270F;&#xFE0F;</span> Edit name
                            </button>
                        </div>
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
                fileTree.innerHTML = this.app.renderLocalFileTree();
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

        const menuSlot = root.querySelector('[data-main-menu]');
        if (menuSlot) {
            menuSlot.appendChild(this.renderMainMenu());
            this.bindMainMenu(menuSlot);
        }

        // Terminal section - only show in local context
        const terminalSection = root.querySelector('[data-terminal-section]');
        if (terminalSection && isLocal) {
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

    renderMainMenu() {
        return this.app.cloneTemplate('main-menu-template');
    }

    bindMainMenu(container) {
        if (!container) return;

        this.app.bindActions(container, '[data-action="navigate"]', (element) => {
            const view = element.dataset.view;
            if (view) {
                this.app.navigate(view);
            }
        });

        this.app.bindAction(container, '[data-action="exit-app"]', () => window.close());
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
                const launchLogo = launchButton.querySelector('[data-env-launch-logo]');
                const launchText = launchButton.querySelector('[data-env-launch-text]');

                if (launchLogo && brand.logo) {
                    launchLogo.src = brand.logo;
                    launchLogo.alt = `${brand.label} logo`;
                } else if (launchLogo) {
                    launchLogo.remove();
                }

                if (launchText) {
                    launchText.textContent = `Launch ${env.cli}`;
                }

                launchButton.addEventListener('click', (event) => {
                    event.stopPropagation();
                    this.app.cliLauncher.launchCLI(env.cli, env.name);
                });
            }

            fragment.appendChild(node);
        });

        container.appendChild(fragment);
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
