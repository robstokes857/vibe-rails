import { SwarmPlanView } from './swarm-plan-view.js';

export class SwarmController {
    constructor(app) {
        this.app = app;
        this.planData = null;
        this.cliOptions = ['Claude', 'Codex', 'Gemini'];
        this.initMessage = '';
    }

    loadSwarm() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('swarm-template');
        const root = fragment.querySelector('[data-view="swarm"]');
        if (!root) return;

        this.bindEvents(root);
        content.appendChild(fragment);
    }

    bindEvents(root) {
        const initialForm = root.querySelector('[data-swarm-form]');
        const resubmitForm = root.querySelector('[data-swarm-resubmit-form]');

        initialForm?.addEventListener('submit', (event) => this.handleInitialSubmit(event, root));
        resubmitForm?.addEventListener('submit', (event) => this.handleResubmit(event, root));
        this.bindInitMessageEditors(root);
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
        submitBtn.disabled = true;
        submitBtn.dataset.originalHtml = submitBtn.innerHTML;
        submitBtn.textContent = 'Submitting...';

        try {
            const response = await this.app.apiCall('/api/v1/swarm/plan', 'POST', { message });
            const plan = this.normalizePlan(response);

            this.planData = plan;
            this.initMessage = this.buildInitMessage(plan);
            this.setInitMessage(root, this.initMessage);
            this.setPromptMessage(root, message);
            this.showPlanScreen(root);
            this.renderPlan(root);
        } catch (error) {
            this.showError(root, `Failed to generate plan: ${error.message}`);
        } finally {
            submitBtn.disabled = false;
            submitBtn.innerHTML = submitBtn.dataset.originalHtml || defaultButtonText;
        }
    }

    showPlanScreen(root) {
        const inputScreen = root.querySelector('[data-swarm-input-screen]');
        const planScreen = root.querySelector('[data-swarm-plan-screen]');
        if (!inputScreen || !planScreen) return;

        inputScreen.classList.add('d-none');
        planScreen.classList.remove('d-none');
    }

    renderPlan(root) {
        if (!this.planData) return;

        const planContainer = root.querySelector('[data-swarm-plan-container]');
        if (!planContainer) return;

        new SwarmPlanView(this.planData, planContainer, this.cliOptions);
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
        const normalizedSteps = steps.map((step) => ({
            name: step.name || '',
            description: step.description || '',
            completed: !!step.completed,
            groups: (Array.isArray(step.groups) ? step.groups : []).map((group) => ({
                name: group.name || '',
                description: group.description || '',
                color: group.color || '#3b82f6',
                tasks: (Array.isArray(group.tasks) ? group.tasks : []).map((task) => ({
                    name: task.name || '',
                    description: task.description || '',
                    selected: typeof task.selected === 'string' ? task.selected : '',
                    started: !!task.started
                }))
            }))
        }));

        return {
            name: response.name || 'Swarm Plan',
            description: response.description || '',
            steps: normalizedSteps
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
