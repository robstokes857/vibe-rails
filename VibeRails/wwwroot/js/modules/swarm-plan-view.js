function escapeHtml(value) {
    if (value === null || value === undefined) return '';
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

const ICON_PLUS = `<svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor"><path d="M8 2a.5.5 0 0 1 .5.5v5h5a.5.5 0 0 1 0 1h-5v5a.5.5 0 0 1-1 0v-5h-5a.5.5 0 0 1 0-1h5v-5A.5.5 0 0 1 8 2z"/></svg>`;
const ICON_TRASH = `<svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor"><path d="M5.5 5.5A.5.5 0 0 1 6 6v6a.5.5 0 0 1-1 0V6a.5.5 0 0 1 .5-.5zm2.5 0a.5.5 0 0 1 .5.5v6a.5.5 0 0 1-1 0V6a.5.5 0 0 1 .5-.5zm3 .5a.5.5 0 0 0-1 0v6a.5.5 0 0 0 1 0V6z"/><path fill-rule="evenodd" d="M14.5 3a1 1 0 0 1-1 1H13v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V4h-.5a1 1 0 0 1-1-1V2a1 1 0 0 1 1-1H6a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1h3.5a1 1 0 0 1 1 1v1zM4.118 4 4 4.059V13a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1V4.059L11.882 4H4.118zM2.5 3V2h11v1h-11z"/></svg>`;

export class SwarmPlanView {
    constructor(data, container, cliOptions = []) {
        this.planData = data;
        this.container = container;
        this.cliOptions = cliOptions;
        this.uid = `swarm-${Math.random().toString(36).slice(2, 8)}`;
        this.render();
    }

    getOverallProgress() {
        const steps = this.planData?.steps || [];
        const total = steps.length;
        const done = steps.filter((s) => s.completed).length;
        return total === 0 ? 100 : Math.round((done / total) * 100);
    }

    getActiveStepIndex() {
        const steps = this.planData?.steps || [];
        const i = steps.findIndex((s) => !s.completed);
        return i === -1 ? steps.length : i;
    }

    render() {
        const planName = escapeHtml(this.planData?.name || 'Swarm Plan');
        const planDescription = escapeHtml(this.planData?.description || '');
        const steps = this.planData?.steps || [];
        const percent = this.getOverallProgress();
        const activeStepIndex = this.getActiveStepIndex();
        const allDone = steps.length > 0 && steps.every((s) => s.completed);

        let html = `
            <div class="swarm-plan-container">
                <h3 class="swarm-plan-title">${planName}</h3>
                <p class="swarm-plan-description">${planDescription}</p>

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
            const isActive = stepIndex === activeStepIndex;
            const groups = Array.isArray(step.groups) ? step.groups : [];
            const marker = step.completed
                ? '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>'
                : `${stepIndex + 1}`;

            html += `
                <div class="swarm-step ${step.completed ? 'completed' : ''}" data-step="${stepIndex}">
                    <div class="swarm-step-marker">${marker}</div>
                    <div class="swarm-step-card">
                        <div class="swarm-step-header">
                            <div class="swarm-step-title-wrap">
                                <div class="swarm-step-title">Step ${stepIndex + 1}: ${escapeHtml(step.name || '')}</div>
                                <div class="swarm-step-desc">${escapeHtml(step.description || '')}</div>
                            </div>
                            <div class="swarm-step-header-actions">
                                <button class="swarm-icon-btn swarm-icon-btn-add" title="Add group to this step"
                                    data-action="swarm-add-group" data-step-index="${stepIndex}">
                                    ${ICON_PLUS} Group
                                </button>
                                <button class="swarm-icon-btn swarm-icon-btn-delete" title="Delete this step"
                                    data-action="swarm-delete-step" data-step-index="${stepIndex}">
                                    ${ICON_TRASH}
                                </button>
                            </div>
                        </div>
                        <div class="swarm-step-body">
                            <div class="swarm-step-body-inner">
                                <div class="swarm-groups-grid" data-step-index="${stepIndex}">
            `;

            groups.forEach((group, groupIndex) => {
                const tasks = Array.isArray(group.tasks) ? group.tasks : [];
                const color = /^#[0-9a-fA-F]{6}$/.test(group.color || '') ? group.color : '#3b82f6';

                html += `
                    <div class="swarm-group">
                        <div class="swarm-group-header">
                            <span class="swarm-drag-handle-group" title="Drag to move group">⠿</span>
                            <div class="swarm-group-dot" style="background:${color}; box-shadow: 0 0 10px ${color}66;"></div>
                            <span class="swarm-group-name">${escapeHtml(group.name || '')}</span>
                            <div class="swarm-group-actions">
                                <button class="swarm-icon-btn swarm-icon-btn-delete" title="Delete this group"
                                    data-action="swarm-delete-group"
                                    data-step-index="${stepIndex}"
                                    data-group-index="${groupIndex}">
                                    ${ICON_TRASH}
                                </button>
                            </div>
                        </div>
                        <div class="swarm-group-desc">${escapeHtml(group.description || '')}</div>
                        <div class="swarm-task-list" data-step-index="${stepIndex}" data-group-index="${groupIndex}">
                `;

                tasks.forEach((task, taskIndex) => {
                    const isStarted = !!task.started;
                    const selected = typeof task.selected === 'string' ? task.selected : '';

                    html += `
                        <div class="swarm-task ${isStarted ? 'started' : ''}">
                            <span class="swarm-drag-handle" title="Drag to move task">⠿</span>
                            <div class="swarm-task-info">
                                <div class="swarm-task-name">${escapeHtml(task.name || '')}</div>
                                <textarea class="swarm-task-desc"
                                    data-step-index="${stepIndex}"
                                    data-group-index="${groupIndex}"
                                    data-task-index="${taskIndex}"
                                    rows="2">${escapeHtml(task.description || '')}</textarea>
                            </div>
                            <div class="swarm-task-controls">
                                <select class="swarm-task-select"
                                    data-step-index="${stepIndex}"
                                    data-group-index="${groupIndex}"
                                    data-task-index="${taskIndex}">
                                    <option value="">-- Select CLI --</option>
                    `;

                    this.cliOptions.forEach((item) => {
                        const escaped = escapeHtml(item);
                        const selectedAttr = selected === item ? 'selected' : '';
                        html += `<option value="${escaped}" ${selectedAttr}>${escaped}</option>`;
                    });

                    html += `
                                </select>
                                <button class="swarm-task-start ${isStarted ? 'active' : ''}"
                                    data-step-index="${stepIndex}"
                                    data-group-index="${groupIndex}"
                                    data-task-index="${taskIndex}">
                                    ${isStarted ? 'Started' : 'Start'}
                                </button>
                                <button class="swarm-icon-btn swarm-icon-btn-delete"
                                    data-action="swarm-delete-task"
                                    data-step-index="${stepIndex}"
                                    data-group-index="${groupIndex}"
                                    data-task-index="${taskIndex}"
                                    title="Delete task">
                                    ${ICON_TRASH}
                                </button>
                            </div>
                        </div>
                    `;
                });

                html += `
                        </div>
                        <button class="swarm-icon-btn swarm-icon-btn-add swarm-add-task-btn" title="Add task to this group"
                            data-action="swarm-add-task"
                            data-step-index="${stepIndex}"
                            data-group-index="${groupIndex}">
                            ${ICON_PLUS} Add Task
                        </button>
                    </div>
                `;
            });

            html += `
                                </div>
                            </div>
                            ${isActive ? `
                            <div class="swarm-step-actions">
                                <button class="swarm-btn swarm-btn-start-all" data-action="swarm-start-all" data-step-index="${stepIndex}">Start All</button>
                                <button class="swarm-btn swarm-btn-next" data-action="swarm-toggle-complete" data-step-index="${stepIndex}">Mark Complete</button>
                            </div>` : ''}
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
                const groupIndex = Number(event.target.dataset.groupIndex);
                const taskIndex = Number(event.target.dataset.taskIndex);
                const task = this.planData.steps?.[stepIndex]?.groups?.[groupIndex]?.tasks?.[taskIndex];
                if (!task) return;
                task.description = event.target.value;
            });
        });

        this.container.querySelectorAll('.swarm-task-select').forEach((selectEl) => {
            selectEl.addEventListener('change', (event) => {
                const stepIndex = Number(event.target.dataset.stepIndex);
                const groupIndex = Number(event.target.dataset.groupIndex);
                const taskIndex = Number(event.target.dataset.taskIndex);
                const task = this.planData.steps?.[stepIndex]?.groups?.[groupIndex]?.tasks?.[taskIndex];
                if (!task) return;
                task.selected = event.target.value;
            });
        });

        this.container.querySelectorAll('.swarm-task-start').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.target.dataset.stepIndex);
                const groupIndex = Number(event.target.dataset.groupIndex);
                const taskIndex = Number(event.target.dataset.taskIndex);
                const task = this.planData.steps?.[stepIndex]?.groups?.[groupIndex]?.tasks?.[taskIndex];
                if (!task) return;
                if (!task.started && !task.selected) {
                    window.alert('Please select a CLI before starting this task.');
                    return;
                }
                task.started = !task.started;
                this.render();
            });
        });

        this.container.querySelectorAll('[data-action="swarm-start-all"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.target.dataset.stepIndex);
                const step = this.planData.steps?.[stepIndex];
                if (!step) return;

                const missingSelections = [];
                (step.groups || []).forEach((group) => {
                    (group.tasks || []).forEach((task) => {
                        if (!task.selected) missingSelections.push(task.name || 'Unnamed task');
                    });
                });

                if (missingSelections.length > 0) {
                    window.alert(`These tasks need a CLI selected:\n\n- ${missingSelections.join('\n- ')}`);
                    return;
                }

                (step.groups || []).forEach((group) => {
                    (group.tasks || []).forEach((task) => { task.started = true; });
                });
                this.render();
            });
        });

        this.container.querySelectorAll('[data-action="swarm-toggle-complete"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.target.dataset.stepIndex);
                const step = this.planData.steps?.[stepIndex];
                if (!step) return;
                step.completed = !step.completed;
                this.render();
            });
        });

        // Add step
        this.container.querySelector('[data-action="swarm-add-step"]')?.addEventListener('click', () => {
            if (!this.planData.steps) this.planData.steps = [];
            this.planData.steps.push({ name: 'New Step', description: '', groups: [], completed: false });
            this.render();
        });

        // Delete step
        this.container.querySelectorAll('[data-action="swarm-delete-step"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                if (!window.confirm('Delete this step?')) return;
                this.planData.steps.splice(stepIndex, 1);
                this.render();
            });
        });

        // Add group
        this.container.querySelectorAll('[data-action="swarm-add-group"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                const step = this.planData.steps?.[stepIndex];
                if (!step) return;
                if (!step.groups) step.groups = [];
                step.groups.push({ name: 'New Group', description: '', color: '#3b82f6', tasks: [] });
                this.render();
            });
        });

        // Delete task
        this.container.querySelectorAll('[data-action="swarm-delete-task"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                const groupIndex = Number(event.currentTarget.dataset.groupIndex);
                const taskIndex = Number(event.currentTarget.dataset.taskIndex);
                const group = this.planData.steps?.[stepIndex]?.groups?.[groupIndex];
                if (!group) return;
                group.tasks.splice(taskIndex, 1);
                if (group.tasks.length === 0) {
                    this.planData.steps[stepIndex].groups.splice(groupIndex, 1);
                }
                this.planData.steps = this.planData.steps.filter((s) => (s.groups || []).length > 0);
                this.render();
            });
        });

        // Add task
        this.container.querySelectorAll('[data-action="swarm-add-task"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                const groupIndex = Number(event.currentTarget.dataset.groupIndex);
                const group = this.planData.steps?.[stepIndex]?.groups?.[groupIndex];
                if (!group) return;
                if (!group.tasks) group.tasks = [];
                group.tasks.push({ name: 'New Task', description: '', selected: '', started: false });
                this.render();
            });
        });

        // Delete group
        this.container.querySelectorAll('[data-action="swarm-delete-group"]').forEach((button) => {
            button.addEventListener('click', (event) => {
                const stepIndex = Number(event.currentTarget.dataset.stepIndex);
                const groupIndex = Number(event.currentTarget.dataset.groupIndex);
                if (!window.confirm('Delete this group?')) return;
                this.planData.steps?.[stepIndex]?.groups?.splice(groupIndex, 1);
                this.planData.steps = this.planData.steps.filter((s) => (s.groups || []).length > 0);
                this.render();
            });
        });

        this.attachSortable();
    }

    attachSortable() {
        const Sortable = window.Sortable;
        if (!Sortable) throw new Error('SortableJS is not loaded. Ensure sortable.min.js is included before Monaco loader.');

        const steps = this.planData?.steps || [];

        // Make task lists sortable (tasks drag between groups/steps)
        this.container.querySelectorAll('.swarm-task-list').forEach((list) => {
            Sortable.create(list, {
                group: 'swarm-tasks',
                animation: 150,
                handle: '.swarm-drag-handle',
                filter: 'textarea, input, select, button',
                preventOnFilter: false,
                ghostClass: 'swarm-drag-ghost',
                chosenClass: 'swarm-drag-chosen',
                dragClass: 'swarm-dragging',
                onEnd: (evt) => {
                    const fromStepIndex = Number(evt.from.dataset.stepIndex);
                    const fromGroupIndex = Number(evt.from.dataset.groupIndex);
                    const toStepIndex = Number(evt.to.dataset.stepIndex);
                    const toGroupIndex = Number(evt.to.dataset.groupIndex);
                    const oldIndex = evt.oldDraggableIndex;
                    const newIndex = evt.newDraggableIndex;

                    if (fromStepIndex === toStepIndex && fromGroupIndex === toGroupIndex && oldIndex === newIndex) return;

                    const fromTasks = this.planData.steps[fromStepIndex]?.groups?.[fromGroupIndex]?.tasks;
                    const toTasks = this.planData.steps[toStepIndex]?.groups?.[toGroupIndex]?.tasks;
                    if (!fromTasks || !toTasks) return;

                    const [moved] = fromTasks.splice(oldIndex, 1);
                    toTasks.splice(newIndex, 0, moved);

                    // Remove groups that are now empty, then steps that are now empty
                    this.planData.steps.forEach((step) => {
                        step.groups = (step.groups || []).filter((g) => (g.tasks || []).length > 0);
                    });
                    this.planData.steps = this.planData.steps.filter((s) => (s.groups || []).length > 0);

                    this.render();
                },
            });
        });

        // Make group grids sortable (groups drag between steps)
        this.container.querySelectorAll('.swarm-groups-grid').forEach((grid) => {
            Sortable.create(grid, {
                group: 'swarm-groups',
                animation: 150,
                handle: '.swarm-drag-handle-group',
                filter: 'textarea, input, select, button',
                preventOnFilter: false,
                ghostClass: 'swarm-drag-ghost',
                chosenClass: 'swarm-drag-chosen',
                dragClass: 'swarm-dragging',
                onEnd: (evt) => {
                    const fromStepIndex = Number(evt.from.dataset.stepIndex);
                    const toStepIndex = Number(evt.to.dataset.stepIndex);
                    const oldIndex = evt.oldDraggableIndex;
                    const newIndex = evt.newDraggableIndex;

                    if (fromStepIndex === toStepIndex && oldIndex === newIndex) return;

                    const fromGroups = this.planData.steps[fromStepIndex]?.groups;
                    const toGroups = this.planData.steps[toStepIndex]?.groups;
                    if (!fromGroups || !toGroups) return;

                    const [moved] = fromGroups.splice(oldIndex, 1);
                    toGroups.splice(newIndex, 0, moved);
                    this.render();
                },
            });
        });
    }

    getData() {
        return this.planData;
    }
}
