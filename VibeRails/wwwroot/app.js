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
import { getLlmName, getProjectNameFromPath, formatRelativeTime, getCliBrand, escapeHtml } from './js/modules/utils.js';

export class VibeControlApp {
    constructor() {
        this.currentView = 'dashboard';
        this.navigationStack = ['dashboard'];
        this.data = {
            agents: [],
            environments: [],
            rules: this.getAvailableRules(),
            availableRules: [],
            availableRulesWithDescriptions: [],
            isLocal: false,
            configs: null
        };
        
        // Initialize Controllers
        this.agentController = new AgentController(this);
        this.dashboardController = new DashboardController(this);
        this.sessionController = new SessionController(this);
        this.environmentController = new EnvironmentController(this);
        this.configController = new ConfigController(this);
        this.ruleController = new RuleController(this);
        this.cliLauncher = new CliLauncher(this);
        this.terminalController = new TerminalController(this);
        
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
        // Show exit button if running inside VS Code webview
        if (window.__viberails_VSCODE__) {
            const exitBtn = document.getElementById('vscode-exit-btn');
            if (exitBtn) {
                exitBtn.style.display = 'block';

                // Add click event listener (CSP-compliant, no inline onclick)
                exitBtn.addEventListener('click', () => {
                    if (window.__viberails_close__) {
                        window.__viberails_close__();
                    }
                });
            }

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
        const crumbs = this.navigationStack.map((item, index) => {
            const viewName = typeof item === 'string' ? item : item.view;
            const displayName = this.getViewDisplayName(viewName);
            const isLast = index === this.navigationStack.length - 1;

            return `
                <li class="breadcrumb-item ${isLast ? 'active' : ''}">
                    ${isLast ? displayName : `<a href="#" onclick="app.navigateToIndex(${index}); return false;">${displayName}</a>`}
                </li>
            `;
        }).join('');

        breadcrumb.innerHTML = `<ol class="breadcrumb mb-0">${crumbs}</ol>`;
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
            'config': 'Configuration'
        };
        return names[view] || view;
    }

    loadView(view, data = {}) {
        const views = {
            'dashboard': () => this.dashboardController.loadDashboard(),
            'launch-cli': () => this.cliLauncher.loadLaunchCLI(),
            'agents': () => this.agentController.loadAgents(),
            'agent-edit': () => this.agentController.loadAgentEdit(data),
            'agent-create': () => this.agentController.loadAgentCreate(),
            'check-violations': () => this.ruleController.loadCheckViolations(),
            'active-rules': () => this.ruleController.loadActiveRules(),
            'environments': () => this.environmentController.loadEnvironments(),
            'config': () => this.configController.loadConfiguration(),
            'sessions': () => this.sessionController.loadSessions()
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

    renderLocalFileTree() {
        if (this.data.agents.length === 0) {
            return `
                <div class="agent-files-empty">
                    <div class="agent-files-empty-icon">&#x1F4C4;</div>
                    <p>No agent files found in this project.</p>
                </div>
            `;
        }

        return `
            <div class="agent-files-tree">
                ${this.data.agents.map(agent => {
                    const parts = agent.path.split(/[\\/]/);
                    const fileName = parts.pop();
                    const dirPath = parts.length > 0 ? parts.join('/') + '/' : '';
                    const displayName = agent.customName || fileName;

                    return `
                    <div class="agent-file-tree-item" onclick="app.navigate('agent-edit', ${JSON.stringify(agent).replace(/"/g, '&quot;')})">
                        <div class="agent-file-tree-icon">&#x1F4DD;</div>
                        <div class="agent-file-tree-info">
                            <span class="agent-file-tree-name">${displayName}</span>
                            <span class="agent-file-tree-path">${dirPath}${fileName}</span>
                        </div>
                        <span class="agent-file-tree-badge">${agent.ruleCount || 0} rules</span>
                    </div>
                `}).join('')}
            </div>
        `;
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

    getAvailableRules() {
        return [
            { name: 'Log all file changes', description: 'Track every file modification', category: 'logging' },
            { name: 'Log file changes > 5 lines', description: 'Only log significant changes', category: 'logging' },
            { name: 'Log file changes > 10 lines', description: 'Log moderate to large changes', category: 'logging' },
            { name: 'Check cyclomatic complexity < 10', description: 'Enforce simple code structure', category: 'complexity' },
            { name: 'Check cyclomatic complexity < 15', description: 'Allow moderate complexity', category: 'complexity' },
            { name: 'Check cyclomatic complexity < 20', description: 'Allow higher complexity', category: 'complexity' },
            { name: 'Cyclomatic complexity disabled', description: 'Skip complexity checks', category: 'complexity' },
            { name: 'Require test coverage', description: 'Tests must exist', category: 'testing' },
            { name: 'Require test coverage minimum 50%', description: 'Half of code must be tested', category: 'testing' },
            { name: 'Require test coverage minimum 70%', description: 'High test coverage required', category: 'testing' },
            { name: 'Require test coverage minimum 80%', description: 'Very high test coverage', category: 'testing' },
            { name: 'Skip test coverage', description: 'Disable coverage checks', category: 'testing' }
        ];
    }

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
                    <button type="button" class="btn btn-secondary" onclick="app.closeModal()">Cancel</button>
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
        modalContainer.innerHTML = `
            <div class="modal fade show d-block" tabindex="-1">
                <div class="modal-dialog modal-lg modal-dialog-scrollable">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">${title}</h5>
                            <button type="button" class="btn-close" onclick="app.closeModal()"></button>
                        </div>
                        <div class="modal-body">
                            ${content}
                        </div>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show"></div>
        `;
    }

    closeModal() {
        const modalContainer = document.getElementById('modal-container');
        modalContainer.innerHTML = '';
    }

    showToast(title, message, type = 'info') {
        const toastContainer = document.getElementById('toast-container');
        const toastId = 'toast-' + Date.now();
        const toast = document.createElement('div');
        toast.id = toastId;
        toast.className = `toast ${type} show`;
        toast.innerHTML = `
            <div class="toast-header">
                <strong class="me-auto">${title}</strong>
                <button type="button" class="btn-close" onclick="document.getElementById('${toastId}').remove()"></button>
            </div>
            <div class="toast-body">${message}</div>
        `;
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

    // ============================================ 
    // API Calls
    // ============================================ 

    async apiCall(endpoint, method = 'GET', data = null) {
        this.showLoading(true);
        try {
            const options = {
                method,
                headers: { 'Content-Type': 'application/json' }
            };
            if (data) options.body = JSON.stringify(data);
            const baseUrl = window.__viberails_API_BASE__ || '';
            const response = await fetch(baseUrl + endpoint, options);
            if (!response.ok) throw new Error(`API call failed: ${response.statusText}`);
            return await response.json();
        } catch (error) {
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
                    this.data.availableRules = this.data.availableRulesWithDescriptions.map(r => r.name);
                } catch (error) {
                    console.error('Failed to fetch available rules:', error);
                    this.data.availableRules = [];
                    this.data.availableRulesWithDescriptions = [];
                }
            } else {
                this.data.agents = [];
                this.data.availableRules = [];
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
