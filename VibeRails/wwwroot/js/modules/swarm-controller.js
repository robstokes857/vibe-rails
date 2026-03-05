import { SwarmPlanView } from './swarm-plan-view.js';
import { SwarmStateStore } from './swarm-state-store.js';

const STEP_COLORS = [
    '#3b82f6', '#10b981', '#f59e0b', '#ef4444',
    '#8b5cf6', '#06b6d4', '#f97316', '#ec4899'
];
const MAX_PLAN_HISTORY = 20;

function createStepId() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') {
        return window.crypto.randomUUID();
    }
    return `swarm-step-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

function createRequestId() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') {
        return window.crypto.randomUUID();
    }
    return `swarm-request-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

export class SwarmController {
    constructor(app) {
        this.app = app;
        this.stateStore = new SwarmStateStore(app);
        this.planData = null;
        this.initMessage = '';
        this.pendingPlan = null;
        this.hasSpawnedTerminals = false;
        this.planHistory = [];
        this._root = null;
    }

    get cliOptions() {
        const options = [
            { value: 'base:claude', label: 'Claude (default)' },
            { value: 'base:codex', label: 'Codex (default)' },
            { value: 'base:gemini', label: 'Gemini (default)' },
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
        this.restoreState(root);
    }

    bindEvents(root) {
        const initialForm = root.querySelector('[data-swarm-form]');
        const resubmitForm = root.querySelector('[data-swarm-resubmit-form]');
        const newPlanButtons = root.querySelectorAll('[data-action="swarm-new-plan"]');
        const historySelectors = root.querySelectorAll('[data-swarm-plan-history]');

        initialForm?.addEventListener('submit', (event) => this.handleInitialSubmit(event, root));
        resubmitForm?.addEventListener('submit', (event) => this.handleResubmit(event, root));
        newPlanButtons.forEach((button) => {
            button.addEventListener('click', () => this.startNewPlan(root));
        });
        historySelectors.forEach((selector) => {
            selector.addEventListener('change', (event) => this.handleHistorySelection(event, root));
        });
        this.bindInitMessageEditors(root);
    }

    handleInitialSubmit(event, root) {
        event.preventDefault();

        const inputEl = root.querySelector('[data-swarm-input]');
        const submitBtn = root.querySelector('[data-action="swarm-submit"]');

        if (!inputEl || !submitBtn) return;
        this.submitPlan(root, inputEl.value.trim(), submitBtn);
    }

    handleResubmit(event, root) {
        event.preventDefault();

        const inputEl = root.querySelector('[data-swarm-resubmit-input]');
        const submitBtn = root.querySelector('[data-action="swarm-resubmit"]');

        if (!inputEl || !submitBtn) return;
        this.submitPlan(root, inputEl.value.trim(), submitBtn);
    }

    submitPlan(root, message, submitBtn) {
        if (!submitBtn) return;

        if (!message) {
            this.showError(root, 'Enter a task description before submitting.');
            return;
        }

        this.clearHistorySelection(root);
        if (this.pendingPlan) {
            this.showLoadingScreen(root, this.pendingPlan.message, true);
            this.app.showToast('Plan Already Running', 'A Swarm plan is already generating. You can safely navigate away and come back.', 'info');
            return;
        }

        this.clearError(root);
        this.setPromptMessage(root, message);
        this.showLoadingScreen(root, message, true);
        this.app.showToast('Plan Requested', 'Plan generation can take a while. You can safely navigate away and come back.', 'info');

        const requestId = createRequestId();
        this.pendingPlan = {
            requestId,
            message,
            startedUtc: new Date().toISOString()
        };
        this.persistState(root);

        void this.generatePlanInBackground(requestId, message);
    }

    async generatePlanInBackground(requestId, message) {
        try {
            const response = await this.app.apiCall(
                '/api/v1/swarm/plan',
                'POST',
                { taskDescription: message },
                { showLoading: false }
            );

            if (!this.pendingPlan || this.pendingPlan.requestId !== requestId) {
                return;
            }

            const plan = this.normalizePlan(response);
            this.planData = plan;
            this.initMessage = this.buildInitMessage(plan);
            this.hasSpawnedTerminals = false;
            this.pendingPlan = null;
            this.addPlanToHistory(plan, message, this.initMessage);

            const activeRoot = this.getActiveRoot();
            if (activeRoot) {
                this.setInitMessage(activeRoot, this.initMessage);
                this.setPromptMessage(activeRoot, message);
                this.hideLoadingScreen(activeRoot);
                this.showPlanScreen(activeRoot);
                this.renderPlan(activeRoot);
            }

            this.persistState(activeRoot || this._root);
            this.app.showToast('Swarm Plan Ready', 'Your Swarm plan is ready. Open the Swarm tab to review it.', 'success');
        } catch (error) {
            if (!this.pendingPlan || this.pendingPlan.requestId !== requestId) {
                return;
            }

            this.pendingPlan = null;
            const activeRoot = this.getActiveRoot();
            if (activeRoot) {
                this.hideLoadingScreen(activeRoot);
                this.showError(activeRoot, `Failed to generate plan: ${error.message}`);
            }

            this.persistState(activeRoot || this._root);
            this.app.showToast('Swarm Plan Failed', `Failed to generate plan: ${error.message}`, 'error');
        }
    }

    showLoadingScreen(root, taskMessage = '', preservePlanScreen = false) {
        this.setLoadingMessage(root, taskMessage);
        root.querySelector('[data-swarm-loading-screen]')?.classList.remove('d-none');

        if (preservePlanScreen && this.planData) {
            root.querySelector('[data-swarm-input-screen]')?.classList.add('d-none');
            root.querySelector('[data-swarm-plan-screen]')?.classList.remove('d-none');
            this.updateWorkspaceLayout(root);
        } else {
            root.querySelector('[data-swarm-input-screen]')?.classList.add('d-none');
            root.querySelector('[data-swarm-plan-screen]')?.classList.add('d-none');
        }

        this.updateSubmitBusyState(root, true);
    }

    hideLoadingScreen(root) {
        root.querySelector('[data-swarm-loading-screen]')?.classList.add('d-none');
        this.updateSubmitBusyState(root, this.pendingPlan !== null);

        if (this.pendingPlan) {
            return;
        }

        if (this.planData) {
            this.showPlanScreen(root);
            return;
        }

        root.querySelector('[data-swarm-input-screen]')?.classList.remove('d-none');
    }

    setLoadingMessage(root, taskMessage = '') {
        const loadingMessage = root.querySelector('[data-swarm-loading-message]');
        if (!loadingMessage) return;

        const safeTask = (taskMessage || '').trim();
        if (!safeTask) {
            loadingMessage.textContent = 'Generating plan. This can take a while. You can safely navigate away and come back.';
            return;
        }

        const shortened = safeTask.length > 84 ? `${safeTask.slice(0, 81)}...` : safeTask;
        loadingMessage.textContent = `Generating plan for "${shortened}". This can take a while. You can safely navigate away and come back.`;
    }

    updateSubmitBusyState(root, isBusy) {
        root.querySelectorAll('[data-action="swarm-submit"], [data-action="swarm-resubmit"]').forEach((button) => {
            button.disabled = !!isBusy;
        });
    }

    showPlanScreen(root) {
        root.querySelector('[data-swarm-input-screen]')?.classList.add('d-none');
        root.querySelector('[data-swarm-loading-screen]')?.classList.add('d-none');
        root.querySelector('[data-swarm-plan-screen]')?.classList.remove('d-none');
        this.updateWorkspaceLayout(root);
    }

    renderPlan(root) {
        if (!this.planData) return;

        const planContainer = root.querySelector('[data-swarm-plan-container]');
        if (!planContainer) return;

        new SwarmPlanView(
            this.planData,
            planContainer,
            this.cliOptions,
            (tasks) => this.launchSwarmTerminals(tasks, root),
            () => this.persistState(root)
        );
    }

    startNewPlan(root = this._root) {
        const activeRoot = root && document.body.contains(root) ? root : this.getActiveRoot();
        if (!activeRoot) return;

        this.planData = null;
        this.pendingPlan = null;
        this.initMessage = '';
        this.hasSpawnedTerminals = false;

        this.setPromptMessage(activeRoot, '');
        this.setInitMessage(activeRoot, '');
        const input = activeRoot.querySelector('[data-swarm-input]');
        if (input) {
            input.value = '';
        }
        const resubmitInput = activeRoot.querySelector('[data-swarm-resubmit-input]');
        if (resubmitInput) {
            resubmitInput.value = '';
        }

        const planContainer = activeRoot.querySelector('[data-swarm-plan-container]');
        planContainer && (planContainer.innerHTML = '');

        const terminalScreen = activeRoot.querySelector('[data-swarm-terminal-screen]');
        terminalScreen?.classList.add('d-none');

        const terminalContainer = activeRoot.querySelector('[data-swarm-terminal-container]');
        if (terminalContainer) {
            terminalContainer.innerHTML = '';
        }

        this.showInputScreen(activeRoot);
        this.clearError(activeRoot);
        this.persistState(activeRoot);
        this.renderPlanHistoryOptions(activeRoot);
        this.app.showToast('Swarm Reset', 'Started a fresh Swarm plan.', 'info');
    }

    handleHistorySelection(event, root) {
        const target = event?.target;
        if (!target || target.tagName !== 'SELECT') {
            return;
        }

        const index = Number.parseInt(target.value, 10);
        if (!Number.isInteger(index) || index < 0 || index >= this.planHistory.length) {
            return;
        }

        const entry = this.planHistory[index];
        if (!entry) return;

        target.value = '';
        this.loadPlanFromHistory(entry, root);
    }

    loadPlanFromHistory(entry, root) {
        if (!entry || typeof entry !== 'object' || !entry.planData) {
            return;
        }

        const loadedPlan = this.normalizePlan(entry.planData);
        this.planData = loadedPlan;
        this.pendingPlan = null;
        this.hasSpawnedTerminals = false;
        this.initMessage = typeof entry.initMessage === 'string' && entry.initMessage
            ? entry.initMessage
            : this.buildInitMessage(loadedPlan);

        const promptMessage = typeof entry.promptMessage === 'string' ? entry.promptMessage : '';
        this.setPromptMessage(root, promptMessage);
        this.setInitMessage(root, this.initMessage);

        const terminalScreen = root.querySelector('[data-swarm-terminal-screen]');
        const terminalContainer = root.querySelector('[data-swarm-terminal-container]');
        terminalScreen?.classList.add('d-none');
        if (terminalContainer) {
            terminalContainer.innerHTML = '';
        }

        const planContainer = root.querySelector('[data-swarm-plan-container]');
        if (planContainer) {
            planContainer.innerHTML = '';
        }

        this.showPlanScreen(root);
        this.renderPlan(root);
        this.renderPlanHistoryOptions(root);
        this.clearError(root);
        this.persistState(root);
    }

    showInputScreen(root) {
        root.querySelector('[data-swarm-loading-screen]')?.classList.add('d-none');
        root.querySelector('[data-swarm-plan-screen]')?.classList.add('d-none');
        root.querySelector('[data-swarm-input-screen]')?.classList.remove('d-none');
        this.updateSubmitBusyState(root, this.pendingPlan !== null);
        this.updateWorkspaceLayout(root);
    }

    async launchSwarmTerminals(tasks, root) {
        const terminalScreen = root.querySelector('[data-swarm-terminal-screen]');
        const terminalContainer = root.querySelector('[data-swarm-terminal-container]');

        if (!terminalContainer || !Array.isArray(tasks) || tasks.length === 0) return;

        await this.ensureTerminalPanel(root);

        this.hasSpawnedTerminals = true;
        root.querySelector('[data-swarm-plan-screen]')?.classList.remove('d-none');
        terminalScreen?.classList.remove('d-none');
        this.updateWorkspaceLayout(root);

        for (const task of tasks) {
            const stepRef = this.planData?.steps?.find((step) => step.id === task.id) || null;
            const { cli, environmentName } = this.parseSelection(task.selected);
            if (!cli) continue;

            const result = await this.app.terminalController.startTerminalWithOptions(
                {
                    cli,
                    environmentName,
                    title: task.name || task.tabTitle || task.groupName || 'Swarm Task',
                    tabLabel: task.name || task.groupName || 'Swarm Task',
                    color: task.color || stepRef?.color || null,
                    taskKey: task.id || stepRef?.id || null,
                    reuseTabId: stepRef?.terminalTabId || null
                },
                terminalContainer
            );

            if (stepRef) {
                stepRef.started = true;
                if (task.color) {
                    stepRef.color = task.color;
                }
                if (result?.tabId) {
                    stepRef.terminalTabId = result.tabId;
                }
            }
        }

        this.renderPlan(root);
        this.persistState(root);
    }

    parseSelection(selected) {
        if (!selected) return { cli: null, environmentName: null };
        if (selected.startsWith('base:')) return { cli: selected.slice(5), environmentName: null };
        if (selected.startsWith('env:')) {
            const parts = selected.split(':');
            const envId = parseInt(parts[1], 10);
            const cli = parts[2];
            const env = (this.app.data.environments || []).find((item) => item.id === envId);
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
            steps: steps.map((step, stepIndex) => ({
                id: typeof step.id === 'string' && step.id ? step.id : createStepId(),
                name: step.name || '',
                description: step.description || '',
                completed: !!step.completed,
                collapsed: !!step.collapsed || !!step.completed,
                selected: typeof step.selected === 'string' ? step.selected : '',
                started: !!step.started,
                color: typeof step.color === 'string' && step.color ? step.color : STEP_COLORS[stepIndex % STEP_COLORS.length],
                terminalTabId: typeof step.terminalTabId === 'string' && step.terminalTabId ? step.terminalTabId : null
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

    updateWorkspaceLayout(root) {
        const resubmitCard = root.querySelector('[data-swarm-resubmit-card]');
        const terminalScreen = root.querySelector('[data-swarm-terminal-screen]');

        if (this.hasSpawnedTerminals) {
            root.classList.add('swarm-terminals-active');
            resubmitCard?.classList.add('d-none');
            terminalScreen?.classList.remove('d-none');
            return;
        }

        root.classList.remove('swarm-terminals-active');
        resubmitCard?.classList.remove('d-none');
        terminalScreen?.classList.add('d-none');
    }

    addPlanToHistory(plan, promptMessage, initMessage) {
        const normalized = this.normalizePlan(plan);
        const sanitizedPlan = {
            ...normalized,
            steps: normalized.steps.map((step) => ({
                ...step,
                started: false,
                terminalTabId: null
            }))
        };

        const entry = {
            id: createRequestId(),
            createdUtc: new Date().toISOString(),
            promptMessage: typeof promptMessage === 'string' ? promptMessage : '',
            initMessage: typeof initMessage === 'string' ? initMessage : '',
            planData: sanitizedPlan
        };

        this.planHistory = this.planHistory.filter((item) => item.id !== entry.id);
        this.planHistory.unshift(entry);
        this.planHistory = this.planHistory.slice(0, MAX_PLAN_HISTORY);
        this.renderPlanHistoryOptions(this.getActiveRoot() || this._root);
        return entry;
    }

    normalizePlanHistory(rawHistory) {
        if (!Array.isArray(rawHistory)) {
            return [];
        }

        return rawHistory
            .filter((entry) => entry && typeof entry === 'object')
            .slice(0, MAX_PLAN_HISTORY)
            .map((entry) => ({
                id: entry.id,
                createdUtc: typeof entry.createdUtc === 'string' ? entry.createdUtc : new Date().toISOString(),
                promptMessage: typeof entry.promptMessage === 'string' ? entry.promptMessage : '',
                initMessage: typeof entry.initMessage === 'string' ? entry.initMessage : '',
                planData: entry.planData
            }))
            .filter((entry) => entry.planData && typeof entry.planData === 'object');
    }

    formatHistoryLabel(entry, index) {
        const name = (entry?.planData?.name && String(entry.planData.name).trim()) || 'Swarm Plan';
        const created = typeof entry?.createdUtc === 'string' ? new Date(entry.createdUtc) : null;
        const dateText = created && !Number.isNaN(created.getTime())
            ? created.toLocaleString([], { dateStyle: 'short', timeStyle: 'short' })
            : `Item ${index + 1}`;
        const promptText = typeof entry?.promptMessage === 'string' && entry.promptMessage.trim()
            ? entry.promptMessage.trim()
            : 'No prompt';
        const shortPrompt = promptText.length > 48 ? `${promptText.slice(0, 48)}...` : promptText;
        return `${name} · ${dateText} · ${shortPrompt}`;
    }

    renderPlanHistoryOptions(root) {
        if (!root) return;
        const cards = Array.from(root.querySelectorAll('[data-swarm-history-card]'));
        const hasHistory = Array.isArray(this.planHistory) && this.planHistory.length > 0;

        cards.forEach((card) => {
            if (hasHistory) {
                card.classList.remove('d-none');
            } else {
                card.classList.add('d-none');
                return;
            }

            const selector = card.querySelector('[data-swarm-plan-history]');
            if (!selector) return;

            const existingValue = selector.value;
            selector.innerHTML = '';
            const placeholder = document.createElement('option');
            placeholder.value = '';
            placeholder.textContent = 'Load a previous Swarm plan...';
            selector.appendChild(placeholder);

            this.planHistory.forEach((entry, index) => {
                const option = document.createElement('option');
                option.value = String(index);
                option.textContent = this.formatHistoryLabel(entry, index);
                selector.appendChild(option);
            });

            if (existingValue && Number.parseInt(existingValue, 10) < this.planHistory.length) {
                selector.value = existingValue;
            } else {
                selector.value = '';
            }
        });
    }

    clearHistorySelection(root) {
        if (!root) return;
        root.querySelectorAll('[data-swarm-plan-history]').forEach((selector) => {
            selector.value = '';
        });
    }

    async ensureTerminalPanel(root) {
        const terminalContainer = root.querySelector('[data-swarm-terminal-container]');
        if (!terminalContainer) return;

        if (!terminalContainer.hasChildNodes()) {
            terminalContainer.innerHTML = this.app.terminalController.renderTerminalPanel();
            await this.app.terminalController.bindTerminalActions(terminalContainer);
        }
    }

    getActiveRoot() {
        if (!this._root) return null;
        return document.body.contains(this._root) ? this._root : null;
    }

    persistState(root = this._root) {
        const activeRoot = root && document.body.contains(root) ? root : this.getActiveRoot();
        const promptInput = activeRoot?.querySelector('[data-swarm-resubmit-input]') || activeRoot?.querySelector('[data-swarm-input]');
        const promptMessage = promptInput?.value?.trim() || this.pendingPlan?.message || '';
        const pendingPlan = this.pendingPlan
                ? {
                requestId: this.pendingPlan.requestId,
                message: this.pendingPlan.message,
                startedUtc: this.pendingPlan.startedUtc
            }
            : null;

        const hasHistory = Array.isArray(this.planHistory) && this.planHistory.length > 0;

        if (!this.planData && !pendingPlan && !promptMessage && !hasHistory) {
            this.stateStore.clear();
            return;
        }

        this.stateStore.save({
            promptMessage,
            initMessage: this.initMessage,
            hasSpawnedTerminals: this.hasSpawnedTerminals,
            pendingPlan,
            planData: this.planData,
            planHistory: this.planHistory
        });
    }

    restoreState(root) {
        const saved = this.stateStore.load();
        this.planHistory = this.normalizePlanHistory(saved?.planHistory);

        this.renderPlanHistoryOptions(root);

        if (!saved) {
            this.hideLoadingScreen(root);
            return;
        }

        const promptMessage = typeof saved.promptMessage === 'string' ? saved.promptMessage : '';
        this.setPromptMessage(root, promptMessage);

        if (saved.planData) {
            try {
                this.planData = this.normalizePlan(saved.planData);
                this.initMessage = typeof saved.initMessage === 'string'
                    ? saved.initMessage
                    : this.buildInitMessage(this.planData);
                this.hasSpawnedTerminals = saved.hasSpawnedTerminals === true;
                this.showPlanScreen(root);
                this.renderPlan(root);
                if (this.hasSpawnedTerminals) {
                    void this.ensureTerminalPanel(root);
                }
            } catch {
                this.stateStore.clear();
                this.planData = null;
                this.initMessage = '';
                this.hasSpawnedTerminals = false;
            }
        }

        const savedPending = saved.pendingPlan && typeof saved.pendingPlan === 'object'
            ? {
                requestId: typeof saved.pendingPlan.requestId === 'string' ? saved.pendingPlan.requestId : null,
                message: typeof saved.pendingPlan.message === 'string' ? saved.pendingPlan.message : '',
                startedUtc: typeof saved.pendingPlan.startedUtc === 'string' ? saved.pendingPlan.startedUtc : null
            }
            : null;

        let droppedStalePending = false;
        if (savedPending?.requestId && this.pendingPlan && this.pendingPlan.requestId === savedPending.requestId) {
            this.pendingPlan.message = savedPending.message;
            this.pendingPlan.startedUtc = savedPending.startedUtc;
        } else if (savedPending?.requestId && !this.pendingPlan) {
            droppedStalePending = true;
        }

        if (this.pendingPlan) {
            this.showLoadingScreen(root, this.pendingPlan.message, true);
        } else {
            this.hideLoadingScreen(root);
        }

        this.renderPlanHistoryOptions(root);

        if (droppedStalePending) {
            this.persistState(root);
        }
    }
}
