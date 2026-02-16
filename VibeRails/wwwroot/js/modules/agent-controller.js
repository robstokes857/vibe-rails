import { getFileTypeVisual } from './file-type-icons.js';

export class AgentController {
    constructor(app) {
        this.app = app;
        this.currentAgent = null;
        this.selectedRuleIndex = null;

        // Wizard state for agent creation
        this.wizardState = {
            currentStep: 1,
            totalSteps: 4,
            directory: '',
            selectedRules: [],  // Array of { text: string, enforcement: string }
            fileReferences: []
        };
    }

    // ============================================
    // Agent Files & Rules View
    // ============================================

    loadAgents() {
        const content = document.getElementById('app-content');
        if (!content) {
            return;
        }

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('agents-template');
        const root = fragment.querySelector('[data-view="agents"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            this.app.bindActions(root, '[data-action="navigate"]', (element) => {
                const view = element.dataset.view;
                if (view) {
                    this.app.navigate(view);
                }
            });
            this.app.bindAction(root, '[data-action="select-agent"]', () => this.showAgentSelector());
            this.app.bindAction(root, '[data-action="available-rules"]', () => this.showAvailableRules());

            const fileTree = root.querySelector('[data-agent-file-tree]');
            if (fileTree) {
                // Ensure we have data to render
                if (this.app.data.agents && this.app.data.agents.length > 0) {
                    fileTree.innerHTML = this.app.renderLocalFileTree();
                } else if (this.app.data.isLocal) {
                    fileTree.innerHTML = '<p class="text-muted text-center">No agent files found in this project.</p>';
                } else {
                    fileTree.innerHTML = '<p class="text-muted text-center">Agent files are only available in local project context.</p>';
                }
            }
        }

        content.appendChild(fragment);
    }

    showAgentSelector() {
        const agents = this.app.data.agents.map(agent => `
            <div class="list-group-item" onclick="app.navigate('agent-edit', ${JSON.stringify(agent).replace(/"/g, '&quot;')})">
                <div class="d-flex justify-content-between align-items-center">
                    <div>
                        <strong>${agent.name}</strong>
                        <br>
                        <small class="text-muted">${agent.path}</small>
                    </div>
                    <span class="badge badge-primary">${agent.ruleCount || 0} rules</span>
                </div>
            </div>
        `).join('');

        this.app.showModal('Select Agent File', `
            <div class="list-group">
                ${agents || '<p class="text-muted text-center">No agents found</p>'}
            </div>
        `);
    }

    showAvailableRules() {
        // Use API-fetched rules with descriptions if available
        const rulesWithDescriptions = this.app.data.availableRulesWithDescriptions || [];

        const rules = rulesWithDescriptions.map(rule => `
            <div class="list-group-item">
                <div class="mb-1">
                    <strong>${this.app.escapeHtml(rule.name)}</strong>
                </div>
                <small class="text-muted">${this.app.escapeHtml(rule.description)}</small>
            </div>
        `).join('');

        this.app.showModal('Available VCA Rules', `
            <div class="list-group">
                ${rules || '<p class="text-muted text-center">No rules available</p>'}
            </div>
        `);
    }

    // ============================================
    // Agent Edit View
    // ============================================

    loadAgentEdit(agent) {
        this.currentAgent = agent;
        this.selectedRuleIndex = null;

        const content = document.getElementById('app-content');
        if (!content) {
            return;
        }

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('agent-edit-template');
        const root = fragment.querySelector('[data-view="agent-edit"]');

        if (root) {
            const displayName = root.querySelector('[data-agent-display-name]');
            if (displayName) {
                displayName.textContent = agent.customName || agent.name;
            }

            const path = root.querySelector('[data-agent-path]');
            if (path) {
                path.textContent = agent.path;
            }

            const rules = root.querySelector('[data-agent-rules]');
            if (rules) {
                rules.innerHTML = this.renderAgentRules(agent);
            }

            const fullContent = root.querySelector('[data-agent-full-content]');
            const contentToggle = root.querySelector('[data-agent-content-toggle]');
            const contentContainer = root.querySelector('[data-agent-content-container]');

            if (fullContent && contentToggle && contentContainer) {
                let isLoaded = false;

                contentToggle.addEventListener('click', () => {
                    const isHidden = contentContainer.style.display === 'none';
                    contentContainer.style.display = isHidden ? 'block' : 'none';

                    const icon = contentToggle.querySelector('.toggle-icon');
                    if (icon) {
                        icon.classList.toggle('rotated', isHidden);
                    }

                    if (isHidden && !isLoaded) {
                        this.loadAgentFileContent(agent.path, fullContent);
                        isLoaded = true;
                    }
                });
            }

            // Files affected - load immediately (always visible)
            const filesList = root.querySelector('[data-agent-files-list]');
            if (filesList) {
                this.loadAgentFiles(agent.path, filesList, root.querySelector('[data-agent-file-count]'));
            }

            const actions = {
                'add-rule': () => this.addRule(agent),
                'edit-rule': () => this.editRule(),
                'remove-rule': () => this.removeRule(),
                'validate-agent': () => this.validateAgent(agent),
                'edit-vscode': () => this.editInVSCode(agent),
                'show-available-rules': () => this.showAvailableRules()
            };

            root.querySelectorAll('[data-agent-action]').forEach((element) => {
                const action = element.dataset.agentAction;
                const handler = actions[action];
                if (handler) {
                    element.addEventListener('click', handler);
                }

                // Disable edit/remove buttons initially
                if (action === 'edit-rule' || action === 'remove-rule') {
                    element.closest('.card').classList.add('disabled-card');
                    element.style.pointerEvents = 'none';
                    element.style.opacity = '0.5';
                }

                // Hide "Edit in VS Code" button when running inside VS Code
                if (action === 'edit-vscode' && window.__viberails_VSCODE__) {
                    element.closest('.col-md-4')?.remove();
                }
            });

            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            this.app.bindAction(root, '[data-action="set-custom-agent-name"]', () => this.showAgentCustomNameModal(agent));
        }

        content.appendChild(fragment);
    }

    renderAgentRules(agent) {
        if (!agent.rules || agent.rules.length === 0) {
            return '<div class="alert alert-secondary border-0"><i class="me-2">ℹ️</i>No rules configured for this agent.</div>';
        }

        const getEnforcementBadge = (level) => {
            const className = level ? `badge-${level.toLowerCase()}` : 'bg-secondary';
            return `<span class="badge ${className} px-3 py-2">${level}</span>`;
        };

        return `
            <div class="d-flex flex-column gap-3 mb-4">
                ${agent.rules.map((rule, index) => `
                    <div class="card rule-card border-0 shadow-sm" onclick="app.agentController.selectRule(this, ${index})" data-rule-index="${index}">
                        <div class="card-body p-3 d-flex justify-content-between align-items-center">
                            <div class="pe-3 d-flex align-items-center flex-grow-1">
                                <span class="rule-icon me-3 text-muted">📜</span>
                                <span class="fw-medium text-light" style="font-size: 1.05rem;">${rule.text}</span>
                            </div>
                            <div class="d-flex align-items-center gap-3">
                                <button class="btn btn-sm btn-primary d-flex align-items-center gap-2">
                                    <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                        <path d="M12.736 3.97a.733.733 0 0 1 1.047 0c.286.289.29.756.01 1.05L7.88 12.01a.733.733 0 0 1-1.065.02L3.217 8.384a.757.757 0 0 1 0-1.06.733.733 0 0 1 1.047 0l3.052 3.093 5.42-6.447z"/>
                                    </svg>
                                    Select
                                </button>
                                <div class="flex-shrink-0">
                                    ${getEnforcementBadge(rule.enforcement)}
                                </div>
                            </div>
                        </div>
                    </div>
                `).join('')}
            </div>
        `;
    }

    async loadAgentFileContent(path, element) {
        try {
            element.textContent = 'Loading...';
            const response = await this.app.apiCall(`/api/v1/agents/content?path=${encodeURIComponent(path)}`, 'GET');
            if (response && response.content) {
                element.textContent = response.content;
            } else {
                element.textContent = 'No content available';
            }
        } catch (error) {
            console.error('Failed to load agent file content:', error);
            element.textContent = `Error loading file content: ${error.message || 'Unknown error'}`;
        }
    }

    async loadAgentFiles(path, listElement, countBadge) {
        try {
            const response = await this.app.apiCall(`/api/v1/agents/files?path=${encodeURIComponent(path)}`, 'GET');
            if (response && response.files && response.files.length > 0) {
                const totalCount = response.totalCount || response.files.length;
                if (countBadge) {
                    countBadge.textContent = `${totalCount} files`;
                }

                // Build summary of top-level directories with file counts
                const summary = this.buildDirectorySummary(response.files);
                const summaryHtml = this.renderDirectorySummary(summary, totalCount);

                listElement.innerHTML = summaryHtml;
            } else {
                if (countBadge) {
                    countBadge.textContent = '0 files';
                }
                listElement.innerHTML = '<p class="text-muted mb-0">No files found in this agent\'s scope.</p>';
            }
        } catch (error) {
            console.error('Failed to load agent files:', error);
            const errorMsg = this.app.escapeHtml(error.message || 'Unknown error');
            listElement.innerHTML = `<p class="text-danger mb-0">Error loading files: ${errorMsg}</p>`;
        }
    }

    buildDirectorySummary(files) {
        const summary = { dirs: {}, rootFiles: [] };

        files.forEach(filePath => {
            const parts = filePath.split(/[\\/]/);

            // Skip hidden files/directories (starting with .)
            if (parts[0].startsWith('.')) return;

            if (parts.length === 1) {
                // Root level file
                summary.rootFiles.push(parts[0]);
            } else {
                // File in a directory
                const topDir = parts[0];
                if (!summary.dirs[topDir]) {
                    summary.dirs[topDir] = { count: 0, subdirs: new Set() };
                }
                summary.dirs[topDir].count++;
                if (parts.length > 2) {
                    summary.dirs[topDir].subdirs.add(parts[1]);
                }
            }
        });

        return summary;
    }

    renderDirectorySummary(summary, totalCount) {
        const maxItems = 12;
        let html = '<div class="file-summary-grid">';
        let itemCount = 0;

        // Sort directories by file count (descending)
        const sortedDirs = Object.entries(summary.dirs)
            .sort((a, b) => b[1].count - a[1].count);

        // Render directories
        for (const [dirName, data] of sortedDirs) {
            if (itemCount >= maxItems) break;

            const subdirCount = data.subdirs.size;
            const subdirText = subdirCount > 0 ? `${subdirCount} subdirs` : '';

            html += `
                <div class="file-summary-item dir-item">
                    <div class="file-summary-icon dir-icon"></div>
                    <div class="file-summary-info">
                        <span class="file-summary-name">${this.app.escapeHtml(dirName)}</span>
                        <span class="file-summary-meta">${data.count} files${subdirText ? ' · ' + subdirText : ''}</span>
                    </div>
                </div>
            `;
            itemCount++;
        }

        // Render root files (if any and space remaining)
        for (const fileName of summary.rootFiles) {
            if (itemCount >= maxItems) break;
            const fileType = getFileTypeVisual(fileName);

            html += `
                <div class="file-summary-item file-item">
                    <div class="file-summary-icon file-icon" title="${this.app.escapeHtml(fileType.name)}">
                        <img src="${this.app.escapeHtml(fileType.iconPath)}" alt="${this.app.escapeHtml(fileType.name)} icon" loading="lazy">
                    </div>
                    <div class="file-summary-info">
                        <span class="file-summary-name">${this.app.escapeHtml(fileName)}</span>
                        <span class="file-summary-meta">${this.app.escapeHtml(fileType.name)}</span>
                    </div>
                </div>
            `;
            itemCount++;
        }

        html += '</div>';

        // Add overflow indicator if needed
        const totalItems = Object.keys(summary.dirs).length + summary.rootFiles.length;
        if (totalItems > maxItems) {
            html += `<div class="file-summary-overflow">+ ${totalItems - maxItems} more directories/files</div>`;
        }

        return html;
    }

    async validateAgent(agent) {
        const root = document.querySelector('[data-view="agent-edit"]');
        if (!root) return;

        const section = root.querySelector('[data-agent-validation-section]');
        const resultsContainer = root.querySelector('[data-agent-validation-results]');
        if (!section || !resultsContainer) return;

        section.style.display = 'block';
        resultsContainer.innerHTML = '<div class="text-center"><div class="spinner-border text-primary"></div><p class="mt-2">Running validation...</p></div>';

        try {
            const response = await this.app.apiCall(
                `/api/v1/agents/validate?path=${encodeURIComponent(agent.path)}`, 'POST'
            );

            resultsContainer.innerHTML = this.app.ruleController.renderValidationResults(response);

            if (response.passed) {
                this.app.showToast('Validation Passed', response.message, 'success');
            } else {
                this.app.showToast('Validation Failed', response.message, 'error');
            }
        } catch (error) {
            console.error('Failed to validate agent:', error);
            const errorMsg = this.app.escapeHtml(error.message || 'Unknown error');
            resultsContainer.innerHTML = `<p class="text-danger">Error: ${errorMsg}</p>`;
            this.app.showError('Validation failed');
        }
    }

    selectRule(element, index) {
        // Remove active class from all items
        const container = element.parentElement;
        container.querySelectorAll('.rule-card').forEach(item => {
            item.classList.remove('active');
        });

        // Add active class to clicked item
        element.classList.add('active');
        this.selectedRuleIndex = index;

        // Enable buttons
        const root = document.querySelector('[data-view="agent-edit"]');
        if (root) {
            root.querySelectorAll('[data-agent-action="edit-rule"], [data-agent-action="remove-rule"]').forEach(btn => {
                const card = btn.closest('.card');
                if (card) {
                    card.classList.remove('disabled-card');
                    card.style.pointerEvents = 'auto';
                    card.style.opacity = '1';
                }
            });
        }
    }

    async addRule(agent) {
        // Get available rules with descriptions from API data
        const rulesWithDescriptions = this.app.data.availableRulesWithDescriptions || [];

        // Filter out rules already in use
        const existingRuleTexts = agent.rules.map(r => r.text);
        const unusedRulesWithDescriptions = rulesWithDescriptions.filter(r => !existingRuleTexts.includes(r.name));

        if (unusedRulesWithDescriptions.length === 0) {
            this.app.showToast('Add Rule', 'All available rules are already added', 'info');
            return;
        }

        const ruleOptions = unusedRulesWithDescriptions.map(rule => `
            <div class="list-group-item list-group-item-action" data-rule="${this.app.escapeHtml(rule.name)}" style="cursor: pointer;">
                <div class="mb-1"><strong>${this.app.escapeHtml(rule.name)}</strong></div>
                <small class="text-muted">${this.app.escapeHtml(rule.description)}</small>
            </div>
        `).join('');

        this.app.showModal('Add Rule', `
            <p class="text-muted mb-3">Select a rule to add to this agent file:</p>
            <div class="list-group">
                ${ruleOptions}
            </div>
        `);

        // Bind click handlers - show enforcement picker after selecting rule
        document.querySelectorAll('[data-rule]').forEach(el => {
            el.addEventListener('click', () => {
                const ruleText = el.dataset.rule;
                this.showEnforcementPicker(agent, ruleText);
            });
        });
    }

    showEnforcementPicker(agent, ruleText, isEdit = false) {
        const currentEnforcement = isEdit ? agent.rules.find(r => r.text === ruleText)?.enforcement : null;

        this.app.showModal('Select Enforcement Level', `
            <p class="text-muted mb-3">How should this rule be enforced?</p>
            <p class="mb-4"><strong>${ruleText}</strong></p>
            <div class="d-flex flex-column gap-3">
                <div class="card enforcement-option ${currentEnforcement === 'WARN' ? 'border-warning' : ''}" data-enforcement="WARN" style="cursor: pointer;">
                    <div class="card-body d-flex align-items-center gap-3">
                        <span class="fs-3">⚠️</span>
                        <div>
                            <h6 class="mb-1">WARN</h6>
                            <small class="text-muted">Warn the user about the violation but allow the action to proceed.</small>
                        </div>
                    </div>
                </div>
                <div class="card enforcement-option ${currentEnforcement === 'COMMIT' ? 'border-info' : ''}" data-enforcement="COMMIT" style="cursor: pointer;">
                    <div class="card-body d-flex align-items-center gap-3">
                        <span class="fs-3">💬</span>
                        <div>
                            <h6 class="mb-1">COMMIT</h6>
                            <small class="text-muted">Require an explanation in the commit or PR message about why the rule was broken.</small>
                        </div>
                    </div>
                </div>
                <div class="card enforcement-option ${currentEnforcement === 'STOP' ? 'border-danger' : ''}" data-enforcement="STOP" style="cursor: pointer;">
                    <div class="card-body d-flex align-items-center gap-3">
                        <span class="fs-3">🛑</span>
                        <div>
                            <h6 class="mb-1">STOP</h6>
                            <small class="text-muted">Block the commit or PR entirely until the violation is fixed.</small>
                        </div>
                    </div>
                </div>
            </div>
        `);

        // Bind click handlers for enforcement options
        document.querySelectorAll('[data-enforcement]').forEach(el => {
            el.addEventListener('click', async () => {
                const enforcement = el.dataset.enforcement;
                try {
                    if (isEdit) {
                        // Update existing rule's enforcement
                        await this.app.apiCall('/api/v1/agents/rules/enforcement', 'PUT', {
                            path: agent.path,
                            ruleText: ruleText,
                            enforcement: enforcement
                        });
                    } else {
                        // Add new rule with enforcement
                        await this.app.apiCall('/api/v1/agents/rules', 'POST', {
                            path: agent.path,
                            ruleText: ruleText,
                            enforcement: enforcement
                        });
                    }
                    this.app.closeModal();
                    this.app.showToast('Success', isEdit ? 'Enforcement level updated' : 'Rule added successfully', 'success');
                    await this.app.refreshDashboardData();
                    // Reload the agent edit view with updated data
                    const updatedAgent = this.app.data.agents.find(a => a.path === agent.path);
                    if (updatedAgent) {
                        this.loadAgentEdit(updatedAgent);
                    }
                } catch (error) {
                    this.app.showError(isEdit ? 'Failed to update enforcement' : 'Failed to add rule');
                }
            });
        });
    }

    editRule() {
        if (this.selectedRuleIndex === null) {
            this.app.showToast('Edit Rule', 'Please select a rule first', 'warning');
            return;
        }
        const rule = this.currentAgent.rules[this.selectedRuleIndex];
        // Show enforcement picker in edit mode
        this.showEnforcementPicker(this.currentAgent, rule.text, true);
    }

    async removeRule() {
        if (this.selectedRuleIndex === null) {
            this.app.showToast('Remove Rule', 'Please select a rule first', 'warning');
            return;
        }

        const rule = this.currentAgent.rules[this.selectedRuleIndex];

        this.app.showModal('Remove Rule', `
            <div class="text-center py-3">
                <div class="mb-3 text-danger">
                    <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" fill="currentColor" viewBox="0 0 16 16">
                        <path d="M11 1.5v1h3.5a.5.5 0 0 1 0 1h-.538l-.853 10.66A2 2 0 0 1 11.115 16h-6.23a2 2 0 0 1-1.994-1.84L2.038 3.5H1.5a.5.5 0 0 1 0-1H5v-1A1.5 1.5 0 0 1 6.5 0h3A1.5 1.5 0 0 1 11 1.5m-5 0v1h4v-1a.5.5 0 0 0-.5-.5h-3a.5.5 0 0 0-.5.5M4.5 5.029l.5 8.5a.5.5 0 1 0 .998-.06l-.5-8.5a.5.5 0 1 0-.998.06m6.53-.06a.5.5 0 0 0-.515.479l-.5 8.5a.5.5 0 1 0 .998.06l.5-8.5a.5.5 0 0 0-.484-.539M8 5.5a.5.5 0 0 0-.5.5v8.5a.5.5 0 0 0 1 0V6a.5.5 0 0 0-.5-.5"/>
                    </svg>
                </div>
                <h5>Remove rule from agent?</h5>
                <p>Are you sure you want to remove <strong>"${this.app.escapeHtml(rule.text)}"</strong>?</p>
                <p class="text-muted small px-4">This rule will be removed from the agent file. You can add it back later if needed.</p>
            </div>
            <div class="d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" onclick="app.closeModal()">Cancel</button>
                <button type="button" class="btn btn-danger" id="confirm-delete-rule-btn">Remove Rule</button>
            </div>
        `);

        document.getElementById('confirm-delete-rule-btn').onclick = async () => {
            this.app.closeModal();
            try {
                await this.app.apiCall('/api/v1/agents/rules', 'DELETE', {
                    path: this.currentAgent.path,
                    rules: [rule.text]
                });
                this.app.showToast('Success', 'Rule removed', 'success');
                await this.app.refreshDashboardData();
                // Reload the agent edit view with updated data
                const updatedAgent = this.app.data.agents.find(a => a.path === this.currentAgent.path);
                if (updatedAgent) {
                    this.loadAgentEdit(updatedAgent);
                } else {
                    this.app.goBack();
                }
            } catch (error) {
                this.app.showError('Failed to remove rule');
            }
        };
    }

    async editInVSCode(agent) {
        try {
            await this.app.apiCall('/api/v1/cli/launch/vscode', 'POST', { path: agent.path });
            this.app.showToast('VS Code', `Opened ${agent.name} in VS Code`, 'success');
        } catch (error) {
            this.app.showError(`Failed to open ${agent.name} in VS Code`);
        }
    }

    showAgentCustomNameModal(agent) {
        const currentName = agent.customName || agent.name;

        this.app.showModal('Set Custom Agent Name', `
            <form id="agent-custom-name-form">
                <div class="mb-3">
                    <label class="form-label">Custom Name</label>
                    <input type="text" class="form-control" id="agent-custom-name" placeholder="${currentName}" required>
                    <small class="form-text text-muted">Enter a friendly name to identify this agent file.</small>
                </div>
                <div class="d-flex gap-2 justify-content-end">
                    <button type="button" class="btn btn-secondary" onclick="app.closeModal()">Cancel</button>
                    <button type="submit" class="btn btn-primary d-flex align-items-center gap-2">
                        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                            <path d="M11 2H9v3h2z"/>
                            <path d="M1.5 0h11.586a1.5 1.5 0 0 1 1.06.44l1.415 1.414A1.5 1.5 0 0 1 16 2.914V14.5a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 0 14.5v-13A1.5 1.5 0 0 1 1.5 0M1 1.5v13a.5.5 0 0 0 .5.5H2v-4.5A1.5 1.5 0 0 1 3.5 9h9a1.5 1.5 0 0 1 1.5 1.5V15h.5a.5.5 0 0 0 .5-.5V2.914a.5.5 0 0 0-.146-.353l-1.415-1.415A.5.5 0 0 0 13.086 1H13v4.5A1.5 1.5 0 0 1 11.5 7h-7A1.5 1.5 0 0 1 3 5.5V1H1.5a.5.5 0 0 0-.5.5m3 4a.5.5 0 0 0 .5.5h7a.5.5 0 0 0 .5-.5V1H4zM3 15h10v-4.5a.5.5 0 0 0-.5-.5h-9a.5.5 0 0 0-.5.5z"/>
                        </svg>
                        Save Custom Name
                    </button>
                </div>
            </form>
        `);

        document.getElementById('agent-custom-name-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            const newName = document.getElementById('agent-custom-name').value;
            try {
                await this.app.apiCall('/api/v1/agents/name', 'PUT', {
                    path: agent.path,
                    customName: newName
                });
                this.app.showToast('Success', `Agent name updated to "${newName}"`, 'success');
                this.app.closeModal();
                // Refresh data and reload the view
                await this.app.refreshDashboardData();
                const updatedAgent = this.app.data.agents.find(a => a.path === agent.path);
                if (updatedAgent) {
                    this.loadAgentEdit(updatedAgent);
                }
            } catch (error) {
                this.app.showError('Failed to update agent name');
            }
        });
    }

    // ============================================
    // Agent Create Wizard
    // ============================================

    resetWizardState() {
        this.wizardState = {
            currentStep: 1,
            totalSteps: 4,
            directory: '',
            selectedRules: [],
            fileReferences: []
        };
    }

    loadAgentCreate() {
        this.resetWizardState();

        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('agent-create-template');
        const root = fragment.querySelector('[data-view="agent-create"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
        }

        content.appendChild(fragment);
        this.renderWizardStep();
    }

    renderWizardStep() {
        const wizardContent = document.getElementById('wizard-content');
        if (!wizardContent) return;

        // Update step indicators
        this.updateStepIndicators();

        switch (this.wizardState.currentStep) {
            case 1:
                this.renderStep1Directory(wizardContent);
                break;
            case 2:
                this.renderStep2Rules(wizardContent);
                break;
            case 3:
                this.renderStep3Enforcement(wizardContent);
                break;
            case 4:
                this.renderStep4Review(wizardContent);
                break;
        }
    }

    updateStepIndicators() {
        const steps = document.querySelectorAll('.wizard-step');
        steps.forEach((step, index) => {
            const stepNum = index + 1;
            step.classList.remove('active', 'completed');
            if (stepNum < this.wizardState.currentStep) {
                step.classList.add('completed');
            } else if (stepNum === this.wizardState.currentStep) {
                step.classList.add('active');
            }
        });
    }

    renderStep1Directory(container) {
        const rootPath = this.app.data.configs?.rootPath || '';
        const defaultPath = rootPath || '';

        // Escape user data to prevent XSS
        const escapedDirectory = this.app.escapeHtml(this.wizardState.directory || defaultPath);
        const escapedRootPath = this.app.escapeHtml(rootPath);

        container.innerHTML = `
            <h5 class="mb-4">Step 1: Select Directory</h5>
            <div class="mb-4">
                <label class="form-label">Directory for AGENTS.md</label>
                <input type="text" class="form-control" id="agent-directory"
                       placeholder="/path/to/directory"
                       value="${escapedDirectory}">
                <small class="form-text text-muted">
                    Enter the directory where <code>AGENTS.md</code> will be created.
                </small>
            </div>
            ${rootPath ? `
                <div class="alert alert-info">
                    <strong>Current Project:</strong> ${escapedRootPath}
                </div>
            ` : ''}
            <div class="d-flex justify-content-between mt-4">
                <button class="btn btn-outline-secondary" type="button" data-action="go-back">Cancel</button>
                <button class="btn btn-primary d-flex align-items-center gap-2" type="button" id="wizard-next-btn">
                    Next
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M1 8a.5.5 0 0 1 .5-.5h11.793l-3.147-3.146a.5.5 0 0 1 .708-.708l4 4a.5.5 0 0 1 0 .708l-4 4a.5.5 0 0 1-.708-.708L13.293 8.5H1.5A.5.5 0 0 1 1 8"/>
                    </svg>
                </button>
            </div>
        `;

        document.getElementById('wizard-next-btn').addEventListener('click', () => {
            let directory = document.getElementById('agent-directory').value.trim();
            if (!directory) {
                this.app.showToast('Validation Error', 'Please enter a directory path', 'warning');
                return;
            }
            // Remove trailing slash if present and append AGENTS.md
            directory = directory.replace(/[\\/]+$/, '');
            this.wizardState.directory = `${directory}/AGENTS.md`;
            this.wizardState.currentStep = 2;
            this.renderWizardStep();
        });
    }

    renderStep2Rules(container) {
        // Use rules with descriptions if available
        const rulesWithDescriptions = this.app.data.availableRulesWithDescriptions || [];

        const ruleCheckboxes = rulesWithDescriptions.map((rule, index) => {
            const isChecked = this.wizardState.selectedRules.some(r => r.text === rule.name);
            return `
                <div class="form-check mb-3 pb-2 border-bottom">
                    <input class="form-check-input" type="checkbox" id="rule-${index}"
                           data-rule="${this.app.escapeHtml(rule.name)}" ${isChecked ? 'checked' : ''}>
                    <label class="form-check-label" for="rule-${index}">
                        <strong>${this.app.escapeHtml(rule.name)}</strong>
                        <br><small class="text-muted">${this.app.escapeHtml(rule.description)}</small>
                    </label>
                </div>
            `;
        }).join('');

        container.innerHTML = `
            <h5 class="mb-4">Step 2: Choose Rules</h5>
            <p class="text-muted mb-3">Select the rules you want to include in this agent file:</p>
            <div class="card mb-4">
                <div class="card-body" style="max-height: 400px; overflow-y: auto;">
                    ${ruleCheckboxes || '<p class="text-muted">No rules available</p>'}
                </div>
            </div>
            <div class="d-flex justify-content-between">
                <button class="btn btn-outline-secondary d-flex align-items-center gap-2" type="button" id="wizard-prev-btn">
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M15 8a.5.5 0 0 0-.5-.5H2.707l3.147-3.146a.5.5 0 1 0-.708-.708l-4 4a.5.5 0 0 0 0 .708l4 4a.5.5 0 0 0 .708-.708L2.707 8.5H14.5A.5.5 0 0 0 15 8"/>
                    </svg>
                    Back
                </button>
                <button class="btn btn-primary d-flex align-items-center gap-2" type="button" id="wizard-next-btn">
                    Next ${this.wizardState.selectedRules.length > 0 ? `(${this.wizardState.selectedRules.length} selected)` : ''}
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M1 8a.5.5 0 0 1 .5-.5h11.793l-3.147-3.146a.5.5 0 0 1 .708-.708l4 4a.5.5 0 0 1 0 .708l-4 4a.5.5 0 0 1-.708-.708L13.293 8.5H1.5A.5.5 0 0 1 1 8"/>
                    </svg>
                </button>
            </div>
        `;

        document.getElementById('wizard-prev-btn').addEventListener('click', () => {
            this.wizardState.currentStep = 1;
            this.renderWizardStep();
        });

        document.getElementById('wizard-next-btn').addEventListener('click', () => {
            // Collect selected rules (preserving any existing enforcement levels)
            const checkboxes = container.querySelectorAll('input[type="checkbox"]:checked');
            const newSelectedRules = [];

            checkboxes.forEach(cb => {
                const ruleText = cb.dataset.rule;
                const existingRule = this.wizardState.selectedRules.find(r => r.text === ruleText);
                newSelectedRules.push({
                    text: ruleText,
                    enforcement: existingRule?.enforcement || 'WARN'
                });
            });

            this.wizardState.selectedRules = newSelectedRules;

            if (this.wizardState.selectedRules.length === 0) {
                this.app.showToast('Validation', 'Please select at least one rule', 'warning');
                return;
            }

            this.wizardState.currentStep = 3;
            this.renderWizardStep();
        });

        // Update button text dynamically as checkboxes change
        container.querySelectorAll('input[type="checkbox"]').forEach(cb => {
            cb.addEventListener('change', () => {
                const checkedCount = container.querySelectorAll('input[type="checkbox"]:checked').length;
                const btn = document.getElementById('wizard-next-btn');
                btn.textContent = checkedCount > 0 ? `Next (${checkedCount} selected)` : 'Next';
            });
        });
    }

    renderStep3Enforcement(container) {
        const enforcementOptions = ['WARN', 'COMMIT', 'STOP'];
        const enforcementDescriptions = {
            'WARN': 'Warn the user but allow the action to proceed',
            'COMMIT': 'Require an explanation in the commit/PR message',
            'STOP': 'Block the commit/PR until the violation is fixed'
        };
        const enforcementIcons = {
            'WARN': '⚠️',
            'COMMIT': '💬',
            'STOP': '🛑'
        };

        const ruleCards = this.wizardState.selectedRules.map((rule, index) => {
            const options = enforcementOptions.map(opt => `
                <option value="${opt}" ${rule.enforcement === opt ? 'selected' : ''}>
                    ${enforcementIcons[opt]} ${opt}
                </option>
            `).join('');

            return `
                <div class="card mb-3">
                    <div class="card-body">
                        <div class="d-flex justify-content-between align-items-start">
                            <div class="flex-grow-1 me-3">
                                <p class="mb-2 fw-medium">${this.app.escapeHtml(rule.text)}</p>
                            </div>
                            <select class="form-select" style="width: auto; min-width: 150px;"
                                    data-rule-index="${index}">
                                ${options}
                            </select>
                        </div>
                    </div>
                </div>
            `;
        }).join('');

        container.innerHTML = `
            <h5 class="mb-4">Step 3: Set Enforcement Levels</h5>
            <p class="text-muted mb-3">Choose how each rule should be enforced:</p>
            <div class="mb-3">
                <div class="d-flex gap-4 mb-3">
                    ${enforcementOptions.map(opt => `
                        <small class="text-muted">
                            <span class="me-1">${enforcementIcons[opt]}</span>
                            <strong>${opt}:</strong> ${enforcementDescriptions[opt]}
                        </small>
                    `).join('')}
                </div>
            </div>
            <div style="max-height: 400px; overflow-y: auto;">
                ${ruleCards}
            </div>
            <div class="d-flex justify-content-between mt-4">
                <button class="btn btn-outline-secondary d-flex align-items-center gap-2" type="button" id="wizard-prev-btn">
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M15 8a.5.5 0 0 0-.5-.5H2.707l3.147-3.146a.5.5 0 1 0-.708-.708l-4 4a.5.5 0 0 0 0 .708l4 4a.5.5 0 0 0 .708-.708L2.707 8.5H14.5A.5.5 0 0 0 15 8"/>
                    </svg>
                    Back
                </button>
                <button class="btn btn-primary d-flex align-items-center gap-2" type="button" id="wizard-next-btn">
                    Review
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M1 8a.5.5 0 0 1 .5-.5h11.793l-3.147-3.146a.5.5 0 0 1 .708-.708l4 4a.5.5 0 0 1 0 .708l-4 4a.5.5 0 0 1-.708-.708L13.293 8.5H1.5A.5.5 0 0 1 1 8"/>
                    </svg>
                </button>
            </div>
        `;

        // Handle enforcement changes
        container.querySelectorAll('select[data-rule-index]').forEach(select => {
            select.addEventListener('change', (e) => {
                const index = parseInt(e.target.dataset.ruleIndex);
                this.wizardState.selectedRules[index].enforcement = e.target.value;
            });
        });

        document.getElementById('wizard-prev-btn').addEventListener('click', () => {
            this.wizardState.currentStep = 2;
            this.renderWizardStep();
        });

        document.getElementById('wizard-next-btn').addEventListener('click', () => {
            this.wizardState.currentStep = 4;
            this.renderWizardStep();
        });
    }

    renderStep4Review(container) {
        const enforcementIcons = {
            'WARN': '⚠️',
            'COMMIT': '💬',
            'STOP': '🛑'
        };

        const ruleSummary = this.wizardState.selectedRules.map(rule => `
            <div class="d-flex justify-content-between align-items-center py-2 border-bottom">
                <span>${this.app.escapeHtml(rule.text)}</span>
                <span class="badge badge-${rule.enforcement.toLowerCase()}">
                    ${enforcementIcons[rule.enforcement]} ${rule.enforcement}
                </span>
            </div>
        `).join('');

        container.innerHTML = `
            <h5 class="mb-4">Step 4: Review & Create</h5>
            <div class="card mb-4">
                <div class="card-header">Agent File Details</div>
                <div class="card-body">
                    <dl class="row mb-0">
                        <dt class="col-sm-3">File Path</dt>
                        <dd class="col-sm-9"><code>${this.app.escapeHtml(this.wizardState.directory)}</code></dd>
                        <dt class="col-sm-3">Rules Count</dt>
                        <dd class="col-sm-9">${this.wizardState.selectedRules.length} rules</dd>
                    </dl>
                </div>
            </div>
            <div class="card mb-4">
                <div class="card-header">Selected Rules</div>
                <div class="card-body" style="max-height: 300px; overflow-y: auto;">
                    ${ruleSummary || '<p class="text-muted">No rules selected</p>'}
                </div>
            </div>
            <div class="d-flex justify-content-between">
                <button class="btn btn-outline-secondary d-flex align-items-center gap-2" type="button" id="wizard-prev-btn">
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M15 8a.5.5 0 0 0-.5-.5H2.707l3.147-3.146a.5.5 0 1 0-.708-.708l-4 4a.5.5 0 0 0 0 .708l4 4a.5.5 0 0 0 .708-.708L2.707 8.5H14.5A.5.5 0 0 0 15 8"/>
                    </svg>
                    Back
                </button>
                <button class="btn btn-success btn-lg d-flex align-items-center gap-2" type="button" id="wizard-create-btn">
                    <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" fill="currentColor" viewBox="0 0 16 16">
                        <path d="M16 8A8 8 0 1 1 0 8a8 8 0 0 1 16 0m-3.97-3.03a.75.75 0 0 0-1.08.022L7.477 9.417 5.384 7.323a.75.75 0 0 0-1.06 1.06L6.97 11.03a.75.75 0 0 0 1.079-.02l3.992-4.99a.75.75 0 0 0-.01-1.05z"/>
                    </svg>
                    Create Agent File
                </button>
            </div>
        `;

        document.getElementById('wizard-prev-btn').addEventListener('click', () => {
            this.wizardState.currentStep = 3;
            this.renderWizardStep();
        });

        document.getElementById('wizard-create-btn').addEventListener('click', () => {
            this.createAgent();
        });
    }

    async createAgent() {
        try {
            // First create the agent file with just the rules (text only for the initial creation)
            const ruleTexts = this.wizardState.selectedRules.map(r => r.text);

            await this.app.apiCall('/api/v1/agents', 'POST', {
                path: this.wizardState.directory,
                rules: ruleTexts
            });

            // Now update enforcement levels for each rule
            for (const rule of this.wizardState.selectedRules) {
                if (rule.enforcement !== 'WARN') { // WARN is the default
                    try {
                        await this.app.apiCall('/api/v1/agents/rules/enforcement', 'PUT', {
                            path: this.wizardState.directory,
                            ruleText: rule.text,
                            enforcement: rule.enforcement
                        });
                    } catch (enfError) {
                        console.warn(`Failed to set enforcement for rule: ${rule.text}`, enfError);
                    }
                }
            }

            this.app.showToast('Success', 'Agent file created successfully!', 'success');

            // Refresh data and navigate to the new agent
            await this.app.refreshDashboardData();

            const newAgent = this.app.data.agents.find(a => a.path === this.wizardState.directory);
            if (newAgent) {
                this.app.navigate('agent-edit', newAgent);
            } else {
                this.app.navigate('agents');
            }
        } catch (error) {
            console.error('Failed to create agent:', error);
            this.app.showError('Failed to create agent file. ' + (error.message || ''));
        }
    }
}
