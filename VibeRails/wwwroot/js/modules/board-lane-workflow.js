import { escapeHtml } from './utils.js';

const icons = { Passed: 'circle-check', Skipped: 'forward-step', Failed: 'circle-xmark',
    Fixing: 'wrench', Reviewing: 'spinner', Running: 'spinner', Stopping: 'hourglass-half' };
const completed = step => ['Passed', 'Skipped'].includes(step.stepStatus || step.status);

export function laneStepMarkup(id, workflows = []) {
    const entries = (workflows || []).flatMap(flow => (flow.steps || []).filter(step => step.jobId === id)
        .map(step => ({ flow, step })));
    if (!entries.length) return '<span class="board-workflow-state is-idle">Ready</span>';
    return entries.map(({ flow, step }) => {
        const state = step.stepStatus || step.status;
        const style = ['Passed', 'Skipped', 'Failed', 'Fixing', 'Reviewing', 'Running', 'Stopping'].includes(state)
            ? state.toLowerCase() : 'waiting';
        const skip = step.canSkip ? `<button type="button" class="btn btn-sm btn-outline-secondary"
            data-agent-action="skip" data-card-id="${escapeHtml(flow.cardId)}" data-event-key="${escapeHtml(step.eventKey)}"
            title="Skip this step for this card only">${['Queued', 'Running'].includes(step.status) ? 'Stop and skip' : 'Skip'}</button>` : '';
        return `<div class="board-workflow-card">
            <span class="board-workflow-card-label">${escapeHtml(flow.cardLabel)}</span>
            <div class="board-workflow-progress"><span class="board-workflow-state is-${style}" title="${escapeHtml(step.reason)}">
                <i class="fa-solid fa-${icons[state] || 'clock'}" aria-hidden="true"></i> ${escapeHtml(state)}</span>${skip}</div>
            <small class="board-workflow-reason">${escapeHtml(step.reason)}</small>
        </div>`;
    }).join('');
}

export function laneArrowMarkup(id, workflows = []) {
    const entries = (workflows || []).flatMap(flow => (flow.steps || []).filter(step => step.jobId === id));
    const done = entries.length && entries.every(completed);
    return `<span class="board-workflow-arrow${done ? ' is-complete' : ''}" aria-label="Then the next step">
        <i class="fa-solid fa-arrow-down" aria-hidden="true"></i></span>`;
}

export function refreshLaneSteps(content, workflows = []) {
    content.querySelectorAll('[data-lane-step-id]').forEach(host => {
        const active = host.contains(document.activeElement) ? document.activeElement : null;
        const cardId = active?.dataset.cardId;
        const eventKey = active?.dataset.eventKey;
        host.innerHTML = laneStepMarkup(Number(host.dataset.laneStepId), workflows);
        if (cardId && eventKey) [...host.querySelectorAll('[data-agent-action="skip"]')]
            .find(button => button.dataset.cardId === cardId && button.dataset.eventKey === eventKey)?.focus();
    });
    content.querySelectorAll('[data-lane-arrow-id]').forEach(host => {
        host.innerHTML = laneArrowMarkup(Number(host.dataset.laneArrowId), workflows);
    });
}
