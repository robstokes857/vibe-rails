function escapeHtml(value) {
    if (value === null || value === undefined) return '';
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

const STEP_COLORS = [
    '#3b82f6', '#10b981', '#f59e0b', '#ef4444',
    '#8b5cf6', '#06b6d4', '#f97316', '#ec4899'
];

const COLOR_EMOJIS = {
    '#3b82f6': '🔵', '#10b981': '🟢', '#f59e0b': '🟡', '#ef4444': '🔴',
    '#8b5cf6': '🟣', '#06b6d4': '🔵', '#f97316': '🟠', '#ec4899': '🩷'
};

const ICON_PLUS = `<svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor"><path d="M8 2a.5.5 0 0 1 .5.5v5h5a.5.5 0 0 1 0 1h-5v5a.5.5 0 0 1-1 0v-5h-5a.5.5 0 0 1 0-1h5v-5A.5.5 0 0 1 8 2z"/></svg>`;
const ICON_TRASH = `<svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor"><path d="M5.5 5.5A.5.5 0 0 1 6 6v6a.5.5 0 0 1-1 0V6a.5.5 0 0 1 .5-.5zm2.5 0a.5.5 0 0 1 .5.5v6a.5.5 0 0 1-1 0V6a.5.5 0 0 1 .5-.5zm3 .5a.5.5 0 0 0-1 0v6a.5.5 0 0 0 1 0V6z"/><path fill-rule="evenodd" d="M14.5 3a1 1 0 0 1-1 1H13v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V4h-.5a1 1 0 0 1-1-1V2a1 1 0 0 1 1-1H6a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1h3.5a1 1 0 0 1 1 1v1zM4.118 4 4 4.059V13a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1V4.059L11.882 4H4.118zM2.5 3V2h11v1h-11z"/></svg>`;

export class SwarmPlanView {
    constructor(data, container, cliOptions = [], onLaunch = null) {
        this.planData = data;
        this.container = container;
        this.cliOptions = cliOptions;
        this.onLaunch = onLaunch;
        this.render();
    }

    getOverallProgress() {
        const steps = this.planData?.steps || [];
        const total = steps.length;
        const done = steps.filter((s) => s.completed).length;
        return total === 0 ? 100 : Math.round((done / total) * 100);
    }

    render() {
        const steps = this.planData?.steps || [];
        const percent = this.getOverallProgress();
        const allDone = steps.length > 0 && steps.every((s) => s.completed);

        let html = `
            <div class="swarm-plan-container">
                <div class="swarm-progress-card">
                    <div class="swarm-progress-header">
                        <span class="swarm-progress-label">Project Completion</span>
                        <span class="swarm-progress-value">${percent}%</span>
                    </div>
                    <div class="swarm-progress-track">
                        <div class="swarm-progress-fill" style="width: ${percent}%"></div>
                    </div>
                </div>
                <div class="swarm-steps-container">
        `;

        steps.forEach((step, stepIndex) => {
            const color = STEP_COLORS[stepIndex % STEP_COLORS.length];
            const selected = typeof step.selected === 'string' ? step.selected : '';
            const marker = step.completed
                ? '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>'
                : `${stepIndex + 1}`;

            html += `
                <div class="swarm-step ${step.completed ? 'completed' : ''}" data-step-index="${stepIndex}">
                    <div class="swarm-step-marker">${marker}</div>
                    <div class="swarm-step-card">
                        <div class="swarm-step-header">
                            <div class="swarm-step-title-wrap">
                                <div class="swarm-step-title d-flex align-items-center gap-2">
                                    <div class="swarm-group-dot" style="background:${color}; box-shadow: 0 0 10px ${color}66; width:10px; height:10px; border-radius:50%; flex-shrink:0;"></div>
                                    ${escapeHtml(step.name || '')}
                                </div>
                            </div>
                            <div class="swarm-step-header-actions">
                                <button class="swarm-icon-btn swarm-icon-btn-delete" title="Delete this step"
                                    data-action="swarm-delete-step" data-step-index="${stepIndex}">
                                    ${ICON_TRASH}
                                </button>
                            </div>
                        </div>
                        <div class="swarm-step-body">
                            <textarea class="swarm-task-desc form-control mb-3"
                                data-step-index="${stepIndex}"
                                rows="5">${escapeHtml(step.description || '')}</textarea>
                            <div class="swarm-task-controls">
                                <select class="swarm-task-select" data-step-index="${stepIndex}">
                                    <option value="">Select CLI / Environment</option>
            `;

            this.cliOptions.forEach(({ value, label }) => {
                const sel = selected === value ? 'selected' : '';
                html += `<option value="${escapeHtml(value)}" ${sel}>${escapeHtml(label)}</option>`;
            });

            html += `
                                </select>
                                <button class="swarm-task-start swarm-step-action-btn ${step.started ? 'active' : ''}"
                                    data-action="swarm-start-step"
                                    data-step-index="${stepIndex}">
                                    ${step.started ? 'Open Terminal' : 'Start Terminal'}
                                </button>
                                <button class="swarm-btn swarm-btn-next swarm-step-action-btn" data-action="swarm-toggle-complete" data-step-index="${stepIndex}">
                                    ${step.completed ? 'Undo Complete' : 'Mark Complete'}
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
            `;
        });

        html += `
                </div>
                <div class="swarm-add-step-row">
                    <button class="swarm-icon-btn swarm-icon-btn-add swarm-add-step-btn" data-action="swarm-add-step">
                        ${ICON_PLUS} Add Step
                    </button>
                </div>
                <div class="swarm-complete-banner ${allDone ? 'show' : ''}">
                    <div class="swarm-success-content">
                        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>
                        <span>Plan completed successfully</span>
                    </div>
                </div>
            </div>
        `;

        this.container.innerHTML = html;
        this.attachEvents();
    }

    attachEvents() {
        this.container.querySelectorAll('.swarm-task-desc').forEach((textarea) => {
            textarea.addEventListener('input', (event) => {
                const stepIndex = Number(event.target.dataset.stepIndex);
                const step = this.planData.steps?.[stepIndex];
                if (!step) return;
                step.description = event.target.value;
            });
        });

        this.container.querySelectorAll('.swarm-task-select').forEach((selectEl) => {
            selectEl.addEventListener('change', (event) => {
                const stepIndex = Number(event.target.dataset.stepIndex);
                const step = this.planData.steps?.[stepIndex];
                if (!step) return;
                step.selected = event.target.value;
            });
        });

        this.container.querySelectorAll('[data-action="swarm-start-step"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                const step = this.planData.steps?.[stepIndex];
                if (!step || !step.selected) return;
                const color = STEP_COLORS[stepIndex % STEP_COLORS.length];
                const emoji = COLOR_EMOJIS[color] || '🔵';
                step.started = true;
                this.render();
                if (this.onLaunch) this.onLaunch([{ ...step, tabTitle: `${emoji} ${step.name}` }]);
            });
        });

        this.container.querySelectorAll('[data-action="swarm-toggle-complete"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                const step = this.planData.steps?.[stepIndex];
                if (!step) return;
                step.completed = !step.completed;
                this.render();
            });
        });

        this.container.querySelector('[data-action="swarm-add-step"]')?.addEventListener('click', () => {
            if (!this.planData.steps) this.planData.steps = [];
            this.planData.steps.push({ name: 'New Step', description: '', completed: false, selected: '', started: false });
            this.render();
        });

        this.container.querySelectorAll('[data-action="swarm-delete-step"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                this.planData.steps.splice(stepIndex, 1);
                this.render();
            });
        });

        this.attachSortable();
    }

    attachSortable() {
        const Sortable = window.Sortable;
        if (!Sortable) throw new Error('SortableJS is not loaded.');

        const stepsContainer = this.container.querySelector('.swarm-steps-container');
        if (!stepsContainer) return;

        Sortable.create(stepsContainer, {
            animation: 150,
            handle: '.swarm-step-marker',
            ghostClass: 'swarm-drag-ghost',
            chosenClass: 'swarm-drag-chosen',
            dragClass: 'swarm-dragging',
            onEnd: (evt) => {
                const oldIndex = evt.oldDraggableIndex;
                const newIndex = evt.newDraggableIndex;
                if (oldIndex === newIndex) return;
                const [moved] = this.planData.steps.splice(oldIndex, 1);
                this.planData.steps.splice(newIndex, 0, moved);
                this.render();
            }
        });
    }

    getData() {
        return this.planData;
    }
}
