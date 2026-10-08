import { laneStepMarkup, laneArrowMarkup } from './board-lane-workflow.js';
import { escapeHtml } from './utils.js';
import { workerIdentity, laneReviewerSummary } from './board-lane-agent-display.js';
import { agentPurposeLabel } from './agent-purpose.js';

export const selectedLaneJobIds = settings => settings.jobIds ?? (settings.jobId ? [settings.jobId] : []);

/** Read-only lane summary. The heading pencil opens the full Automation editor. */
export function laneAgentListMarkup(settings, jobs, environments) {
    const rows = selectedLaneJobIds(settings).map((id, index, ids) => {
        const option = settings.jobs?.find(job => job.id === id);
        const job = jobs.find(job => job.id === id);
        const name = option?.name || `Unavailable Automation (${id})`;
        const worker = workerIdentity(job, environments);
        const environmentId = job?.actions?.find(a => Number(a.kind) === 0)?.environmentId ?? job?.environmentId;
        const environment = environments.find(env => Number(env.id) === Number(environmentId));
        const label = [worker.label, worker.workerName].filter(Boolean).join(' · ');
        const logo = worker.logo ? `<img src="${escapeHtml(worker.logo)}" alt="${escapeHtml(worker.label)}"${worker.logoFilter ? ` style="filter:${escapeHtml(worker.logoFilter)}"` : ''}>`
            : `<i class="fa-solid fa-${worker.icon}" aria-hidden="true"></i>`;
        return `<article class="board-lane-agent" data-agent-id="${Number(id)}">
            <div class="board-lane-agent-heading">
                <span class="board-lane-agent-icon" title="${escapeHtml(label)}">${logo}</span>
                <div class="board-lane-agent-copy"><strong>${escapeHtml(name)}</strong><small>${escapeHtml(label)}</small>
                    ${environment ? `<small>${escapeHtml(agentPurposeLabel(environment.purpose))}</small>` : ''}
                    ${worker.modelSummary ? `<small class="board-lane-agent-model">${escapeHtml(worker.modelSummary)}</small>` : ''}
                    ${option && !option.enabled ? '<small>Disabled · manage in the Automation editor</small>' : ''}</div>
                <button type="button" class="board-lane-agents-action" data-agent-action="edit" aria-label="Edit ${escapeHtml(name)}" title="Edit Automation" ${option ? '' : 'disabled'}><i class="fa-solid fa-pen" aria-hidden="true"></i></button>
                <button type="button" class="board-lane-agents-action" data-agent-action="remove" aria-label="Remove ${escapeHtml(name)} from this lane" title="Remove from this lane"><i class="fa-solid fa-trash-can" aria-hidden="true"></i></button>
            </div>
            <div data-lane-step-id="${Number(id)}" aria-live="polite">${laneStepMarkup(Number(id), settings.workflows || [])}</div>
            ${environment?.purpose === 'code_review' ? `<p class="board-lane-agent-reviewer">${escapeHtml(laneReviewerSummary(environment, environments))}</p>
                <p class="small">Output: configured Checks, a Code review report and a card handoff.</p>
                ${option?.setup ? `<p class="small" role="status">${escapeHtml(option.setup)}</p>` : ''}` : ''}
            <p class="board-lane-agent-description-preview">${escapeHtml(job?.description || 'No description yet.')}</p>
        </article>${index < ids.length - 1 ? `<div data-lane-arrow-id="${Number(id)}">${laneArrowMarkup(Number(id), settings.workflows || [])}</div>` : ''}`;
    }).join('');
    return `${settings.starterSetupPending ? '<p role="status">Starter review setup is pending. Reopen to retry, or save your own lane selection.</p>' : ''}
        <div class="board-lane-agents-list">${rows || '<p class="board-lane-agents-empty">No agents on entry to this lane.</p>'}</div>
        <p class="board-lane-agents-scope">Automations are shared wherever they are used. Remove unlinks one from this lane and cancels pending entries.</p>
        <button type="button" class="btn btn-sm btn-outline-secondary" data-agent-action="add"><i class="fa-solid fa-plus" aria-hidden="true"></i> Add agent</button>`;
}
