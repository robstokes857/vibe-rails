import { buildLlmSelectionOptionsMarkup } from './utils.js';

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
const ICON_TERMINAL = `<svg width="13" height="13" viewBox="0 0 16 16" fill="currentColor"><path d="M.146 2.146a.5.5 0 0 1 .708 0L4.707 6 .854 9.854a.5.5 0 1 1-.708-.708L3.293 6 .146 2.854a.5.5 0 0 1 0-.708"/><path d="M5.5 9.5a.5.5 0 0 1 .5-.5H15a.5.5 0 0 1 0 1H6a.5.5 0 0 1-.5-.5"/></svg>`;
const ICON_CHECK = `<svg width="13" height="13" viewBox="0 0 16 16" fill="currentColor"><path d="M13.485 1.929a.75.75 0 0 1 .086 1.056l-7.25 8.5a.75.75 0 0 1-1.102.04l-3.25-3.25a.75.75 0 1 1 1.06-1.06l2.677 2.677 6.72-7.877a.75.75 0 0 1 1.059-.086"/></svg>`;
const ICON_UNDO = `<svg width="13" height="13" viewBox="0 0 16 16" fill="currentColor"><path d="M8 3a5 5 0 1 0 4.546 2.914.5.5 0 1 1 .908-.417A6 6 0 1 1 8 2v1z"/><path d="M8 4.466V.534a.25.25 0 0 1 .41-.192l2.36 1.966a.25.25 0 0 1 0 .384L8.41 4.658A.25.25 0 0 1 8 4.466"/></svg>`;
const ICON_DRAG = `<svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor"><path d="M6 2a1 1 0 1 1-2 0 1 1 0 0 1 2 0M6 8a1 1 0 1 1-2 0 1 1 0 0 1 2 0m-1 6a1 1 0 1 0 0-2 1 1 0 0 0 0 2m7-12a1 1 0 1 1-2 0 1 1 0 0 1 2 0M11 9a1 1 0 1 0 0-2 1 1 0 0 0 0 2m1 5a1 1 0 1 1-2 0 1 1 0 0 1 2 0"/></svg>`;

function createStepId() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') {
        return window.crypto.randomUUID();
    }
    return `swarm-step-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

export class SwarmPlanView {
    constructor(data, container, cliOptions = [], onLaunch = null, onChange = null) {
        this.planData = data;
        this.container = container;
        this.cliOptions = cliOptions;
        this.onLaunch = onLaunch;
        this.onChange = onChange;
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
            const color = step.color || STEP_COLORS[stepIndex % STEP_COLORS.length];
            if (!step.color) {
                step.color = color;
            }
            const selected = typeof step.selected === 'string' ? step.selected : '';
            const marker = step.completed
                ? '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>'
                : `${stepIndex + 1}`;

            html += `
                <div class="swarm-step ${step.completed ? 'completed' : ''} ${step.collapsed ? 'collapsed' : ''}" data-step-index="${stepIndex}" data-step-id="${escapeHtml(step.id || '')}">
                    <div class="swarm-step-marker">${marker}</div>
                    <div class="swarm-step-card">
                        <div class="swarm-step-header">
                            <div class="swarm-step-title-wrap">
                                <div class="swarm-step-title d-flex align-items-center gap-2">
                                    <div class="swarm-group-dot" style="background:${color}; box-shadow: 0 0 10px ${color}66; width:10px; height:10px; border-radius:50%; flex-shrink:0;"></div>
                                    <span class="swarm-step-drag-handle" title="Drag to reorder">${ICON_DRAG}</span>
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
                                    ${buildLlmSelectionOptionsMarkup(this.cliOptions, {
                                        placeholder: 'Select CLI / Environment',
                                        selectedValue: selected
                                    })}
                                </select>
                                <button class="swarm-task-start swarm-step-action-btn ${step.started ? 'active' : ''}"
                                    data-action="swarm-start-step"
                                    data-step-index="${stepIndex}">
                                    ${ICON_TERMINAL}
                                    <span>${step.started ? 'Open Terminal' : 'Start Terminal'}</span>
                                </button>
                                <button class="swarm-btn swarm-btn-next swarm-step-action-btn" data-action="swarm-toggle-complete" data-step-index="${stepIndex}">
                                    ${step.completed ? ICON_UNDO : ICON_CHECK}
                                    <span>${step.completed ? 'Undo Complete' : 'Mark Complete'}</span>
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
                this.emitChange();
            });
        });

        this.container.querySelectorAll('.swarm-task-select').forEach((selectEl) => {
            selectEl.addEventListener('change', (event) => {
                const stepIndex = Number(event.target.dataset.stepIndex);
                const step = this.planData.steps?.[stepIndex];
                if (!step) return;
                const nextSelection = event.target.value;
                if (step.selected !== nextSelection) {
                    step.selected = nextSelection;
                    step.terminalTabId = null;
                    step.started = false;
                }
                this.emitChange();
            });
        });

        this.container.querySelectorAll('[data-action="swarm-start-step"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                const step = this.planData.steps?.[stepIndex];
                if (!step) return;
                if (!step.selected) {
                    this.showSelectionRequiredFeedback(event.currentTarget);
                    return;
                }
                const color = step.color || STEP_COLORS[stepIndex % STEP_COLORS.length];
                const emoji = COLOR_EMOJIS[color] || '🔵';
                if (this.onLaunch) {
                    this.onLaunch([{
                        ...step,
                        color: step.color || color,
                        tabTitle: `${emoji} ${step.name}`
                    }]);
                }
            });
        });

        this.container.querySelectorAll('[data-action="swarm-toggle-complete"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                const step = this.planData.steps?.[stepIndex];
                if (!step) return;
                const markComplete = !step.completed;
                step.completed = markComplete;
                step.collapsed = markComplete;
                this.emitChange();
                this.render();
            });
        });

        this.container.querySelector('[data-action="swarm-add-step"]')?.addEventListener('click', () => {
            if (!this.planData.steps) this.planData.steps = [];
            const nextIndex = this.planData.steps.length;
            this.planData.steps.push({
                id: createStepId(),
                name: 'New Step',
                description: '',
                completed: false,
                collapsed: false,
                selected: '',
                started: false,
                color: STEP_COLORS[nextIndex % STEP_COLORS.length],
                terminalTabId: null
            });
            this.emitChange();
            this.render();
        });

        this.container.querySelectorAll('[data-action="swarm-delete-step"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                this.planData.steps.splice(stepIndex, 1);
                this.emitChange();
                this.render();
            });
        });

        this.attachSortable();
    }

    attachSortable() {
        const Sortable = window.Sortable;
        if (!Sortable) return;

        const stepsContainer = this.container.querySelector('.swarm-steps-container');
        if (!stepsContainer) return;

        Sortable.create(stepsContainer, {
            animation: 150,
            handle: '.swarm-step-marker, .swarm-step-drag-handle',
            ghostClass: 'swarm-drag-ghost',
            chosenClass: 'swarm-drag-chosen',
            dragClass: 'swarm-dragging',
            onEnd: (evt) => {
                const oldIndex = evt.oldDraggableIndex;
                const newIndex = evt.newDraggableIndex;
                if (oldIndex === newIndex) return;
                const [moved] = this.planData.steps.splice(oldIndex, 1);
                this.planData.steps.splice(newIndex, 0, moved);
                this.emitChange();
                this.render();
            }
        });
    }

    emitChange() {
        if (typeof this.onChange === 'function') {
            this.onChange(this.planData);
        }
    }

    showSelectionRequiredFeedback(startButton) {
        const controls = startButton?.closest('.swarm-task-controls');
        const selectEl = controls?.querySelector('.swarm-task-select');
        if (!selectEl) return;

        [startButton, selectEl].forEach((el) => {
            el.classList.remove('swarm-shake');
            // Force reflow so repeated clicks replay animation.
            void el.offsetWidth;
            el.classList.add('swarm-shake');
        });

        selectEl.focus();
        if (typeof selectEl.showPicker === 'function') {
            try {
                selectEl.showPicker();
                return;
            } catch {
                // Fallback below.
            }
        }

        try {
            selectEl.click();
            selectEl.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
        } catch {
            // no-op
        }
    }

    getData() {
        return this.planData;
    }
}
