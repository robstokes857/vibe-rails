import { SwarmPlanView } from './swarm-plan-view.js';

export class SwarmController {
    constructor(app) {
        this.app = app;
        this.planData = null;
        this.initMessage = '';
        this._root = null;
    }

    get cliOptions() {
        const options = [
            { value: 'base:claude',  label: 'Claude (default)' },
            { value: 'base:codex',   label: 'Codex (default)' },
            { value: 'base:gemini',  label: 'Gemini (default)' },
            { value: 'base:copilot', label: 'Copilot (default)' },
        ];
        (this.app.data.environments || []).forEach((env) => {
            options.push({
                value: `env:${env.id}:${env.cli.toLowerCase()}`,
                label: `${env.name} (${env.cli.toLowerCase()})`
            });
        });
        return options;
    }

    loadSwarm() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('swarm-template');
        const root = fragment.querySelector('[data-view="swarm"]');
        if (!root) return;

        this._root = root;
        this.bindEvents(root);
        content.appendChild(fragment);
    }

    bindEvents(root) {
        const initialForm = root.querySelector('[data-swarm-form]');
        const resubmitForm = root.querySelector('[data-swarm-resubmit-form]');

        initialForm?.addEventListener('submit', (event) => this.handleInitialSubmit(event, root));
        resubmitForm?.addEventListener('submit', (event) => this.handleResubmit(event, root));
        this.bindInitMessageEditors(root);

        root.querySelector('[data-action="swarm-back-to-plan"]')?.addEventListener('click', () => {
            root.querySelector('[data-swarm-terminal-screen]')?.classList.add('d-none');
            root.querySelector('[data-swarm-plan-screen]')?.classList.remove('d-none');
        });
    }

    async handleInitialSubmit(event, root) {
        event.preventDefault();

        const inputEl = root.querySelector('[data-swarm-input]');
        const submitBtn = root.querySelector('[data-action="swarm-submit"]');

        if (!inputEl || !submitBtn) return;

        const message = inputEl.value.trim();
        await this.submitPlan(root, message, submitBtn, 'Submit');
    }

    async handleResubmit(event, root) {
        event.preventDefault();

        const inputEl = root.querySelector('[data-swarm-resubmit-input]');
        const submitBtn = root.querySelector('[data-action="swarm-resubmit"]');

        if (!inputEl || !submitBtn) return;

        const message = inputEl.value.trim();
        await this.submitPlan(root, message, submitBtn, 'Resubmit Plan');
    }

    async submitPlan(root, message, submitBtn, defaultButtonText) {
        if (!submitBtn) return;
        if (!message) {
            this.showError(root, 'Enter a task description before submitting.');
            return;
        }

        this.clearError(root);
        this.showLoadingScreen(root);

        try {
            const response = await this.app.apiCall('/api/v1/swarm/plan', 'POST', { taskDescription: message });
            const plan = this.normalizePlan(response);

            this.planData = plan;
            this.initMessage = this.buildInitMessage(plan);
            this.setInitMessage(root, this.initMessage);
            this.setPromptMessage(root, message);
            this.showPlanScreen(root);
            this.renderPlan(root);
        } catch (error) {
            this.hideLoadingScreen(root);
            this.showError(root, `Failed to generate plan: ${error.message}`);
        }
    }

    showLoadingScreen(root) {
        root.querySelector('[data-swarm-input-screen]')?.classList.add('d-none');
        root.querySelector('[data-swarm-loading-screen]')?.classList.remove('d-none');
    }

    hideLoadingScreen(root) {
        root.querySelector('[data-swarm-loading-screen]')?.classList.add('d-none');
        root.querySelector('[data-swarm-input-screen]')?.classList.remove('d-none');
    }

    showPlanScreen(root) {
        root.querySelector('[data-swarm-input-screen]')?.classList.add('d-none');
        root.querySelector('[data-swarm-loading-screen]')?.classList.add('d-none');
        root.querySelector('[data-swarm-terminal-screen]')?.classList.add('d-none');
        root.querySelector('[data-swarm-plan-screen]')?.classList.remove('d-none');
    }

    renderPlan(root) {
        if (!this.planData) return;

        const planContainer = root.querySelector('[data-swarm-plan-container]');
        if (!planContainer) return;

        new SwarmPlanView(this.planData, planContainer, this.cliOptions, (tasks) => {
            this.launchSwarmTerminals(tasks, root);
        });
    }

    async launchSwarmTerminals(tasks, root) {
        const planScreen = root.querySelector('[data-swarm-plan-screen]');
        const terminalScreen = root.querySelector('[data-swarm-terminal-screen]');
        const terminalContainer = root.querySelector('[data-swarm-terminal-container]');

        if (!terminalContainer) return;

        if (!terminalContainer.hasChildNodes()) {
            terminalContainer.innerHTML = this.app.terminalController.renderTerminalPanel();
        }

        planScreen?.classList.add('d-none');
        terminalScreen?.classList.remove('d-none');

        for (const task of tasks) {
            const { cli, environmentName } = this.parseSelection(task.selected);
            await this.app.terminalController.startTerminalWithOptions(
                { cli, environmentName, title: task.tabTitle || task.groupName || task.name },
                terminalContainer
            );
        }
    }

    parseSelection(selected) {
        if (!selected) return { cli: null, environmentName: null };
        if (selected.startsWith('base:')) return { cli: selected.slice(5), environmentName: null };
        if (selected.startsWith('env:')) {
            const parts = selected.split(':');
            const envId = parseInt(parts[1]);
            const cli = parts[2];
            const env = (this.app.data.environments || []).find(e => e.id === envId);
            return { cli, environmentName: env?.name || null };
        }
        return { cli: selected, environmentName: null };
    }

    bindInitMessageEditors(root) {
        root.querySelectorAll('[data-swarm-init-message]').forEach((editor) => {
            editor.addEventListener('input', (event) => {
                this.initMessage = event.target.value;
                this.setInitMessage(root, this.initMessage, event.target);
            });
        });
    }

    setInitMessage(root, message, sourceEditor = null) {
        root.querySelectorAll('[data-swarm-init-message]').forEach((editor) => {
            if (sourceEditor && editor === sourceEditor) return;
            if (editor.value !== message) {
                editor.value = message;
            }
        });
    }

    setPromptMessage(root, message) {
        const initialInput = root.querySelector('[data-swarm-input]');
        const resubmitInput = root.querySelector('[data-swarm-resubmit-input]');
        if (initialInput && initialInput.value !== message) {
            initialInput.value = message;
        }
        if (resubmitInput && resubmitInput.value !== message) {
            resubmitInput.value = message;
        }
    }

    normalizePlan(response) {
        if (!response || typeof response !== 'object') {
            throw new Error('API returned an invalid plan payload.');
        }

        const steps = Array.isArray(response.steps) ? response.steps : [];
        return {
            name: response.name || 'Swarm Plan',
            description: response.description || '',
            steps: steps.map((step) => ({
                name: step.name || '',
                description: step.description || '',
                completed: !!step.completed,
                selected: typeof step.selected === 'string' ? step.selected : '',
                started: !!step.started
            }))
        };
    }

    buildInitMessage(plan) {
        const lines = [];
        lines.push(plan.name || 'Swarm Plan');

        if (plan.description) {
            lines.push(plan.description);
        }

        lines.push('');
        lines.push('Project Completion');
        lines.push('0%');

        (plan.steps || []).forEach((step, stepIndex) => {
            lines.push(`${stepIndex + 1}`);
            lines.push(`Step ${stepIndex + 1}: ${step.name || ''}`);
            if (step.description) {
                lines.push(step.description);
            }
        });

        return lines.join('\n').trim();
    }

    showError(root, message) {
        root.querySelectorAll('[data-swarm-error]').forEach((errorEl) => {
            errorEl.textContent = message;
            errorEl.classList.remove('d-none');
        });
    }

    clearError(root) {
        root.querySelectorAll('[data-swarm-error]').forEach((errorEl) => {
            errorEl.textContent = '';
            errorEl.classList.add('d-none');
        });
    }
}
