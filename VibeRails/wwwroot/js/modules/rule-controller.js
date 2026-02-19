export class RuleController {
    constructor(app) {
        this.app = app;
    }

    loadCheckViolations() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('check-violations-template');
        const root = fragment.querySelector('[data-view="check-violations"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            this.app.bindAction(root, '[data-action="run-vca"]', () => this.runVCAValidation());
        }

        content.appendChild(fragment);
    }

    async runVCAValidation() {
        const resultsDiv = document.getElementById('validation-results');
        if (!resultsDiv) return;

        resultsDiv.innerHTML = '<div class="text-center"><div class="spinner-border text-primary"></div><p class="mt-2">Running validation...</p></div>';

        try {
            const response = await this.app.apiCall('/api/v1/hooks/validate', 'POST');
            resultsDiv.innerHTML = this.renderValidationResults(response);

            if (response.passed) {
                this.app.showToast('Validation Passed', response.message, 'success');
            } else {
                this.app.showToast('Validation Failed', response.message, 'error');
            }
        } catch (error) {
            resultsDiv.innerHTML = `<p class="text-danger">Error: ${error.message}</p>`;
            this.app.showError('Validation failed');
        }
    }

    renderValidationResults(response) {
        if (!response || !response.results || response.results.length === 0) {
            return `<p class="text-muted text-center">${response?.message || 'No results'}</p>`;
        }

        const passedClass = response.passed ? 'alert-success' : 'alert-danger';
        const passedIcon = response.passed ? '&#x2705;' : '&#x274C;';

        return `
            <div class="alert ${passedClass} mb-3">
                <strong>${passedIcon} ${response.message}</strong>
            </div>
            <div class="list-group">
                ${response.results.map(result => {
                    const icon = result.passed ? '&#x2705;' : '&#x274C;';
                    const badgeClass = result.enforcement === 'STOP' ? 'bg-danger' :
                                       result.enforcement === 'COMMIT' ? 'bg-warning' : 'bg-secondary';
                    return `
                        <div class="list-group-item">
                            <div class="d-flex justify-content-between align-items-start">
                                <div>
                                    <span class="me-2">${icon}</span>
                                    <strong>${this.app.escapeHtml(result.ruleName)}</strong>
                                    <span class="badge ${badgeClass} ms-2">${result.enforcement}</span>
                                    ${result.message ? `<br><small class="text-muted">${this.app.escapeHtml(result.message)}</small>` : ''}
                                </div>
                            </div>
                            ${result.affectedFiles && result.affectedFiles.length > 0 ? `
                                <div class="mt-2">
                                    <small class="text-muted">Affected files:</small>
                                    <ul class="mb-0 small">
                                        ${result.affectedFiles.slice(0, 5).map(f => `<li><code>${this.app.escapeHtml(f)}</code></li>`).join('')}
                                        ${result.affectedFiles.length > 5 ? `<li class="text-muted">...and ${result.affectedFiles.length - 5} more</li>` : ''}
                                    </ul>
                                </div>
                            ` : ''}
                        </div>
                    `;
                }).join('')}
            </div>
        `;
    }

    loadActiveRules() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('active-rules-template');
        const root = fragment.querySelector('[data-view="active-rules"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            const container = root.querySelector('[data-active-rules]');
            if (container) {
                container.innerHTML = this.renderActiveRulesTree(container);
            }
        }

        content.appendChild(fragment);
    }

    renderActiveRulesTree(container) {
        // Show actual rules from agents
        if (!this.app.data.agents || this.app.data.agents.length === 0) {
            return '<p class="text-muted text-center">No agent files found. Create an AGENTS.md to define rules.</p>';
        }

        const html = this.app.data.agents.map((agent, idx) => {
            const displayName = agent.customName || agent.name;

            return `
            <div class="card mb-3">
                <div class="card-header" style="cursor: pointer;" data-active-rule-agent="${idx}">
                    <strong>${this.app.escapeHtml(displayName)}</strong>
                    <small class="text-muted ms-2">${agent.ruleCount} rule(s)</small>
                    <span class="text-muted ms-2">&rarr;</span>
                </div>
                <div class="card-body">
                    ${agent.rules && agent.rules.length > 0 ? `
                        <ul class="list-unstyled mb-0">
                            ${agent.rules.map(rule => {
                                const badgeClass = rule.enforcement === 'STOP' ? 'bg-danger' :
                                                   rule.enforcement === 'COMMIT' ? 'bg-warning' :
                                                   rule.enforcement === 'WARN' ? 'bg-info' : 'bg-secondary';
                                return `
                                    <li class="mb-2">
                                        <span class="badge ${badgeClass}">${rule.enforcement}</span>
                                        <span class="ms-2">${this.app.escapeHtml(rule.text)}</span>
                                    </li>
                                `;
                            }).join('')}
                        </ul>
                    ` : '<p class="text-muted mb-0">No rules defined</p>'}
                </div>
            </div>
        `;
        }).join('');

        // Bind click handlers after rendering (CSP-safe)
        if (container) {
            setTimeout(() => {
                container.querySelectorAll('[data-active-rule-agent]').forEach(el => {
                    const idx = parseInt(el.dataset.activeRuleAgent);
                    const agent = this.app.data.agents[idx];
                    if (agent) {
                        el.addEventListener('click', () => this.app.navigate('agent-edit', agent));
                    }
                });
            }, 0);
        }

        return html;
    }
}