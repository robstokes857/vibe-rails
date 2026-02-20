// ============================================ 
// VibeControl Application
// Single Page Application for Agent Management
// ============================================ 

import { AgentController } from './js/modules/agent-controller.js';
import { DashboardController } from './js/modules/dashboard-controller.js';
import { SessionController } from './js/modules/session-controller.js';
import { EnvironmentController } from './js/modules/environment-controller.js';
import { ConfigController } from './js/modules/config-controller.js';
import { RuleController } from './js/modules/rule-controller.js';
import { CliLauncher } from './js/modules/cli-launcher.js';
import { TerminalController } from './js/modules/terminal-controller.js';
import { SandboxController } from './js/modules/sandbox-controller.js';
import { SettingsController } from './js/modules/settings-controller.js';
import { getLlmName, getProjectNameFromPath, formatRelativeTime, getCliBrand, escapeHtml } from './js/modules/utils.js';

export class VibeControlApp {
    constructor() {
        this.currentView = 'dashboard';
        this.navigationStack = ['dashboard'];
        this.data = {
            agents: [],
            environments: [],
            sandboxes: [],
            availableRulesWithDescriptions: [],
            isLocal: false,
            configs: null
        };
        this.hostUnreachableToastShown = false;
        
        // Initialize Controllers
        this.agentController = new AgentController(this);
        this.dashboardController = new DashboardController(this);
        this.sessionController = new SessionController(this);
        this.environmentController = new EnvironmentController(this);
        this.configController = new ConfigController(this);
        this.ruleController = new RuleController(this);
        this.cliLauncher = new CliLauncher(this);
        this.terminalController = new TerminalController(this);
        this.sandboxController = new SandboxController(this);
        this.settingsController = new SettingsController(this);

        this.init();
    }

    async init() {
        await this.fetchConfigs();
        this.loadView('dashboard'); // Start with dashboard
        this.bindGlobalActions();
        this.setupKeyboardShortcuts();
        this.setupVSCodeIntegration();
    }

    setupVSCodeIntegration() {
        const exitBtn = document.getElementById('exit-btn');
        if (exitBtn) {
            exitBtn.addEventListener('click', () => {
                if (window.__viberails_VSCODE__ && window.__viberails_close__) {
                    window.__viberails_close__();
                } else {
                    this.apiCall('/api/v1/shutdown', 'POST').catch(() => {});
                    window.close();
                }
            });
        }

        // Apply VS Code-specific UI adjustments when in webview
        if (window.__viberails_VSCODE__) {
            // Hide "Edit in VS Code" button since we're already in VS Code
            const editVsCodeCard = document.querySelector('[data-agent-action="edit-vscode"]');
            if (editVsCodeCard) {
                editVsCodeCard.closest('.col-md-4')?.remove();
            }
        }
    }

    async fetchConfigs() {
        try {
            const configs = await this.apiCall('/api/v1/IsLocal', 'GET');
            this.data.configs = configs;
            this.data.isLocal = configs.isLocalContext;
        } catch (error) {
            console.error('Failed to fetch configs:', error);
            this.data.isLocal = false;
        }
    }

    // ============================================ 
    // Core Infrastructure
    // ============================================ 

    cloneTemplate(id) {
        const template = document.getElementById(id);
        if (!template) {
            console.warn(`Template not found: ${id}`);
            return document.createDocumentFragment();
        }
        return template.content.cloneNode(true);
    }

    bindAction(container, selector, handler) {
        const element = container.querySelector(selector);
        if (element) {
            element.addEventListener('click', handler);
        }
    }

    bindGlobalActions() {
        document.addEventListener('click', (e) => {
            const goBack = e.target.closest('[data-action="go-back"]');
            if (goBack) {
                this.goBack();
            }

            const goHome = e.target.closest('[data-action="navigate-home"]');
            if (goHome) {
                e.preventDefault();
                this.navigationStack = ['dashboard'];
                this.currentView = 'dashboard';
                this.updateBreadcrumb();
                this.loadView('dashboard');
            }

            const goSettings = e.target.closest('[data-action="navigate-settings"]');
            if (goSettings) {
                e.preventDefault();
                this.navigate('settings');
            }
        });
    }

    bindActions(container, selector, handler) {
        container.querySelectorAll(selector).forEach((element) => {
            element.addEventListener('click', () => handler(element));
        });
    }

    // ============================================ 
    // Utils Wrappers
    // ============================================ 

    getLlmName(llmEnum) { return getLlmName(llmEnum); }
    getProjectNameFromPath(path) { return getProjectNameFromPath(path); }
    formatRelativeTime(dateString) { return formatRelativeTime(dateString); }
    getCliBrand(cli) { return getCliBrand(cli); }
    escapeHtml(text) { return escapeHtml(text); }

    // ============================================
    // Navigation & Routing
    // ============================================ 

    navigate(view, data = {}) {
        this.closeModal();

        const currentStackItem = this.navigationStack[this.navigationStack.length - 1];
        const currentViewName = typeof currentStackItem === 'string' ? currentStackItem : currentStackItem.view;

        if (currentViewName === view) {
             this.navigationStack[this.navigationStack.length - 1] = { view, data };
        } else {
             this.navigationStack.push({ view, data });
        }

        this.currentView = view;
        this.updateBreadcrumb();
        this.loadView(view, data);
    }

    goBack() {
        if (this.navigationStack.length > 1) {
            this.navigationStack.pop();
            const previous = this.navigationStack[this.navigationStack.length - 1];
            this.currentView = previous.view || previous;
            this.updateBreadcrumb();
            this.loadView(this.currentView, previous.data);
        }
    }

    updateBreadcrumb() {
        const breadcrumb = document.getElementById('breadcrumb');

        // Use DOM methods instead of innerHTML to prevent XSS
        const breadcrumbList = document.createElement('ol');
        breadcrumbList.className = 'breadcrumb mb-0';

        this.navigationStack.forEach((item, index) => {
            const viewName = typeof item === 'string' ? item : item.view;
            const displayName = this.getViewDisplayName(viewName);
            const isLast = index === this.navigationStack.length - 1;

            const li = document.createElement('li');
            li.className = `breadcrumb-item${isLast ? ' active' : ''}`;

            if (isLast) {
                li.textContent = displayName;
            } else {
                const link = document.createElement('a');
                link.href = '#';
                link.textContent = displayName;
                link.addEventListener('click', (e) => {
                    e.preventDefault();
                    this.navigateToIndex(index);
                });
                li.appendChild(link);
            }

            breadcrumbList.appendChild(li);
        });

        breadcrumb.innerHTML = ''; // Clear existing content
        breadcrumb.appendChild(breadcrumbList);
    }

    navigateToIndex(index) {
        this.navigationStack = this.navigationStack.slice(0, index + 1);
        const current = this.navigationStack[index];
        this.currentView = typeof current === 'string' ? current : current.view;
        this.updateBreadcrumb();
        this.loadView(this.currentView, current.data);
    }

    getViewDisplayName(view) {
        const names = {
            'dashboard': 'Dashboard',
            'launch-cli': 'Launch CLI',
            'agents': 'Agent Files & Rules',
            'agent-edit': 'Edit Agent',
            'agent-create': 'Create Agent',
            'check-violations': 'Check Violations',
            'active-rules': 'Active Rules',
            'environments': 'Environments',
            'config': 'Configuration',
            'settings': 'Settings'
        };
        return names[view] || view;
    }

    loadView(view, data = {}) {
        window.scrollTo(0, 0);
        const views = {
            'dashboard': () => this.dashboardController.loadDashboard(data),
            'launch-cli': () => this.cliLauncher.loadLaunchCLI(),
            'agents': () => this.agentController.loadAgents(),
            'agent-edit': () => this.agentController.loadAgentEdit(data),
            'agent-create': () => this.agentController.loadAgentCreate(),
            'check-violations': () => this.ruleController.loadCheckViolations(),
            'active-rules': () => this.ruleController.loadActiveRules(),
            'environments': () => this.environmentController.loadEnvironments(),
            'config': () => this.configController.loadConfiguration(),
            'sessions': () => this.sessionController.loadSessions(),
            'settings': () => this.settingsController.loadSettings()
        };

        const loadFunc = views[view];
        if (loadFunc) {
            loadFunc();
        } else {
            this.showError('View not found: ' + view);
        }
    }

    // ============================================ 
    // Shared Logic (Used by multiple controllers)
    // ============================================ 

    renderLocalFileTree(container) {
        if (this.data.agents.length === 0) {
            return `
                <div class="agent-files-empty">
                    <div class="agent-files-empty-icon">&#x1F4C4;</div>
                    <p>No agent files found in this project.</p>
                </div>
            `;
        }

        const html = `
            <div class="agent-files-tree">
                ${this.data.agents.map((agent, idx) => {
                    const parts = agent.path.split(/[\\/]/);
                    const fileName = parts.pop();
                    const dirPath = parts.length > 0 ? parts.join('/') + '/' : '';

                    const infoHtml = agent.customName
                        ? `<span class="agent-file-tree-name">${agent.customName}</span>
                           <span class="agent-file-tree-path">${dirPath}${fileName}</span>`
                        : `<span class="agent-file-tree-name agent-file-tree-name--path">${dirPath}${fileName}</span>`;

                    return `
                    <div class="agent-file-tree-item" data-agent-tree-index="${idx}" style="cursor:pointer;">
                        <div class="agent-file-tree-icon">&#x1F4DD;</div>
                        <div class="agent-file-tree-info">
                            ${infoHtml}
                        </div>
                        <span class="agent-file-tree-badge">${agent.ruleCount || 0} rules</span>
                    </div>
                `}).join('')}
            </div>
        `;

        // If a container is provided, bind click handlers after setting innerHTML
        if (container) {
            setTimeout(() => {
                container.querySelectorAll('[data-agent-tree-index]').forEach(el => {
                    const idx = parseInt(el.dataset.agentTreeIndex);
                    const agent = this.data.agents[idx];
                    if (agent) {
                        el.addEventListener('click', () => this.navigate('agent-edit', agent));
                    }
                });
            }, 0);
        }

        return html;
    }

    async launchCliForProject(projectPath, cliName) {
        const cliType = cliName.toLowerCase();
        try {
            const requestBody = {
                workingDirectory: projectPath,
                environmentName: null,
                args: []
            };

            const response = await this.apiCall(`/api/v1/cli/launch/${cliType}`, 'POST', requestBody);

            if (response.success) {
                this.showToast('CLI Launched', `${cliName} launched for ${this.getProjectNameFromPath(projectPath)}`, 'success');
            } else {
                this.showToast('CLI Error', response.message, 'error');
            }
        } catch (error) {
            this.showError(`Failed to launch ${cliName} CLI`);
        }
    }

    // ============================================
    // Remaining View Logic (To be moved if AgentController expands)
    // ============================================

    loadAgentCreate() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.cloneTemplate('agent-create-template');
        const root = fragment.querySelector('[data-view="agent-create"]');

        if (root) {
            this.bindAction(root, '[data-action="go-back"]', () => this.goBack());
            this.bindAction(root, '[data-action="wizard-next"]', () => this.wizardNext());
        }

        content.appendChild(fragment);
    }

    wizardNext() {
        alert('Fix me: Agent Wizard navigation not implemented');
    }

    // ============================================
    // Helper Methods & Data
    // ============================================

    showCustomNameModal() {
        const path = this.data.configs?.rootPath || '';
        const currentName = this.getProjectNameFromPath(path);

        this.showModal('Set Custom Project Name', `
            <form id="custom-name-form">
                <div class="mb-3">
                    <label class="form-label">Custom Name</label>
                    <input type="text" class="form-control" id="project-custom-name" placeholder="${currentName}" required>
                    <small class="form-text text-muted">Enter a friendly name to identify this project in your history.</small>
                </div>
                <div class="d-flex gap-2 justify-content-end">
                    <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                    <button type="submit" class="btn btn-primary">Save Custom Name</button>
                </div>
            </form>
        `);

        document.getElementById('custom-name-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            const newName = document.getElementById('project-custom-name').value;
            const path = this.data.configs?.rootPath;

            if (!path) {
                this.showError('Cannot determine project path');
                return;
            }

            try {
                await this.apiCall('/api/v1/projects/name', 'PUT', {
                    path: path,
                    customName: newName
                });

                this.showToast('Success', `Project name updated to "${newName}"`, 'success');
                this.closeModal();
                await this.dashboardController.loadDashboard();
            } catch (error) {
                this.showError('Failed to update project name: ' + error.message);
            }
        });
    }

    // ============================================
    // UI Helpers
    // ============================================

    showModal(title, content) {
        const modalContainer = document.getElementById('modal-container');
        // Escape title to prevent XSS, content is trusted HTML template
        const escapedTitle = this.escapeHtml(title);
        modalContainer.innerHTML = `
            <div class="modal fade show d-block" tabindex="-1">
                <div class="modal-dialog modal-lg modal-dialog-scrollable">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">${escapedTitle}</h5>
                            <button type="button" class="btn-close" data-action="close-modal"></button>
                        </div>
                        <div class="modal-body">
                            ${content}
                        </div>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show"></div>
        `;
        // Bind all close buttons without inline handlers (CSP-safe)
        modalContainer.querySelectorAll('[data-action="close-modal"]')
            .forEach(btn => btn.addEventListener('click', () => this.closeModal()));
    }

    closeModal() {
        const modalContainer = document.getElementById('modal-container');
        modalContainer.innerHTML = '';
    }

    showToast(title, message, type = 'info') {
        const toastContainer = document.getElementById('toast-container');
        const toast = document.createElement('div');
        toast.className = `toast ${type} show`;
        // Escape title and message to prevent XSS
        const escapedTitle = this.escapeHtml(title);
        const escapedMessage = this.escapeHtml(message);
        toast.innerHTML = `
            <div class="toast-header">
                <strong class="me-auto">${escapedTitle}</strong>
                <button type="button" class="btn-close" data-action="dismiss-toast"></button>
            </div>
            <div class="toast-body">${escapedMessage}</div>
        `;
        toast.querySelector('[data-action="dismiss-toast"]')
            .addEventListener('click', () => toast.remove());
        toastContainer.appendChild(toast);
        setTimeout(() => toast.remove(), 5000);
    }

    showError(message) {
        this.showToast('Error', message, 'error');
    }

    showLoading(show = true) {
        const overlay = document.getElementById('loading-overlay');
        if (show) {
            overlay.classList.remove('d-none');
        } else {
            overlay.classList.add('d-none');
        }
    }

    getHostUnreachableMessage() {
        return 'Cannot reach the VibeRails host. It may have stopped. Relaunch VibeRails, then refresh this page.';
    }

    isHostUnreachableError(error) {
        if (!error) return false;
        if (error.name === 'HostUnreachableError') return true;
        if (!(error instanceof TypeError)) return false;

        const message = (error.message || '').toLowerCase();
        return message.includes('failed to fetch')
            || message.includes('load failed')
            || message.includes('networkerror')
            || message.includes('network error');
    }

    notifyHostUnreachable() {
        if (this.hostUnreachableToastShown) return;
        this.hostUnreachableToastShown = true;
        this.showToast('Host Unreachable', this.getHostUnreachableMessage(), 'error');
    }

    // ============================================ 
    // API Calls
    // ============================================ 

    async apiCall(endpoint, method = 'GET', data = null) {
        this.showLoading(true);
        try {
            const options = {
                method,
                headers: { 'Content-Type': 'application/json' },
                credentials: 'include'  // Send cookies with requests
            };
            if (data) options.body = JSON.stringify(data);
            const baseUrl = window.__viberails_API_BASE__ || '';
            const response = await fetch(baseUrl + endpoint, options);
            this.hostUnreachableToastShown = false;

            if (response.status === 401) {
                if (window.__viberails_VSCODE__) {
                    // In VS Code webview, can't redirect - show error
                    throw new Error('Session expired. Close and reopen the VibeRails panel to re-authenticate.');
                }
                window.location.href = (baseUrl || '') + '/auth/bootstrap';
                throw new Error('Unauthorized');
            }

            if (!response.ok) throw new Error(`API call failed: ${response.statusText}`);
            return await response.json();
        } catch (error) {
            if (this.isHostUnreachableError(error)) {
                this.notifyHostUnreachable();
                const hostError = new Error(this.getHostUnreachableMessage());
                hostError.name = 'HostUnreachableError';
                throw hostError;
            }
            console.error('API Error:', error);
            throw error;
        } finally {
            this.showLoading(false);
        }
    }

    // ============================================ 
    // Data Refresh
    // ============================================ 

    async refreshDashboardData() {
        try {
            // Fetch environments (global, always available)
            try {
                const envResponse = await this.apiCall('/api/v1/environments', 'GET');
                this.data.environments = (envResponse.environments || []).map(env => ({
                    id: env.id,
                    name: env.name,
                    cli: env.cli,
                    customArgs: env.customArgs,
                    customPrompt: env.customPrompt,
                    defaultPrompt: env.defaultPrompt,
                    lastUsed: this.formatRelativeTime(env.lastUsedUTC)
                }));
            } catch (error) {
                console.error('Failed to fetch environments:', error);
                this.data.environments = [];
            }

            if (this.data.isLocal) {
                try {
                    const agentsResponse = await this.apiCall('/api/v1/agents', 'GET');
                    this.data.agents = (agentsResponse.agents || []).map((agent, index) => ({
                        id: index + 1,
                        name: agent.name,
                        customName: agent.customName || null,
                        path: agent.path,
                        ruleCount: agent.ruleCount,
                        rules: agent.rules.map(rule => ({
                            text: rule.text,
                            enforcement: rule.enforcement || 'WARN'
                        }))
                    }));
                } catch (error) {
                    console.error('Failed to fetch agents:', error);
                    this.data.agents = [];
                }

                try {
                    const rulesResponse = await this.apiCall('/api/v1/rules/details', 'GET');
                    this.data.availableRulesWithDescriptions = rulesResponse.rules || [];
                } catch (error) {
                    console.error('Failed to fetch available rules:', error);
                    this.data.availableRulesWithDescriptions = [];
                }

                // Fetch sandboxes (local context only)
                try {
                    await this.sandboxController.refreshSandboxes();
                } catch (error) {
                    console.error('Failed to fetch sandboxes:', error);
                    this.data.sandboxes = [];
                }
            } else {
                this.data.agents = [];
                this.data.sandboxes = [];
            }

        } catch (error) {
            console.error('Failed to refresh dashboard data:', error);
        }
    }

    setupKeyboardShortcuts() {
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                this.closeModal();
                if (this.navigationStack.length > 1) {
                    this.goBack();
                }
            }
        });
    }
}

// Initialize the app when DOM is loaded
document.addEventListener('DOMContentLoaded', () => {
    window.app = new VibeControlApp();
});
