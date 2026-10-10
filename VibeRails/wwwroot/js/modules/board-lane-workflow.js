import { escapeHtml } from './utils.js';
import { BoardStepStatus as Status } from './board-step-status.js';

const icons = { [Status.Passed]: 'circle-check', [Status.Skipped]: 'forward-step', [Status.Failed]: 'circle-xmark',
    [Status.Fixing]: 'wrench', [Status.Reviewing]: 'spinner', [Status.Running]: 'spinner', [Status.Stopping]: 'hourglass-half' };
const completed = step => [Status.Passed, Status.Skipped].includes(step.stepStatus || step.status);

export function laneStepMarkup(id, workflows = []) {
    const entries = (workflows || []).flatMap(flow => (flow.steps || []).filter(step => step.jobId === id)
        .map(step => ({ flow, step })));
    if (!entries.length) return '<span class="board-workflow-state is-idle">Ready</span>';
    return entries.map(({ flow, step }) => {
        const state = step.stepStatus || step.status;
        const style = [Status.Passed, Status.Skipped, Status.Failed, Status.Fixing, Status.Reviewing, Status.Running, Status.Stopping].includes(state)
            ? state.toLowerCase() : 'waiting';
        const skip = step.canSkip ? `<button type="button" class="btn btn-sm btn-outline-secondary"
            data-agent-action="skip" data-card-id="${escapeHtml(flow.cardId)}" data-event-key="${escapeHtml(step.eventKey)}"
            title="Skip this step for this card only">${[Status.Queued, Status.Running].includes(step.status) ? 'Stop and skip' : 'Skip'}</button>` : '';
        const rerun = step.canRerun ? `<button type="button" class="btn btn-sm btn-outline-primary"
            data-agent-action="rerun" data-card-id="${escapeHtml(flow.cardId)}" data-event-key="${escapeHtml(step.eventKey)}"
            title="Re-run this failed step for this card">Re-run</button>` : '';
        return `<div class="board-workflow-card">
            <span class="board-workflow-card-label">${escapeHtml(flow.cardLabel)}</span>
            <div class="board-workflow-progress"><span class="board-workflow-state is-${style}" title="${escapeHtml(step.reason)}">
                <i class="fa-solid fa-${icons[state] || 'clock'}" aria-hidden="true"></i> ${escapeHtml(state)}</span>
                <span class="board-workflow-actions">${rerun}${skip}</span></div>
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
        const action = active?.dataset.agentAction;
        host.innerHTML = laneStepMarkup(Number(host.dataset.laneStepId), workflows);
        if (cardId && eventKey) [...host.querySelectorAll('[data-agent-action]')]
            .find(button => button.dataset.cardId === cardId && button.dataset.eventKey === eventKey
                && button.dataset.agentAction === action)?.focus();
    });
    content.querySelectorAll('[data-lane-arrow-id]').forEach(host => {
        host.innerHTML = laneArrowMarkup(Number(host.dataset.laneArrowId), workflows);
    });
}
