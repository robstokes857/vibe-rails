import { laneStepMarkup, laneArrowMarkup } from './board-lane-workflow.js';
import { escapeHtml } from './utils.js';
import { workerIdentity } from './board-lane-agent-display.js';
import { agentPurposeLabel } from './agent-purpose.js';

export const selectedLaneJobIds = settings => settings.jobIds ?? (settings.jobId ? [settings.jobId] : []);

/** Read-only lane summary: one row per Agent or Automation. The pencil opens its editor. */
export function laneAgentListMarkup(settings, jobs, environments) {
    const rows = selectedLaneJobIds(settings).map((id, index, ids) => {
        const option = settings.jobs?.find(job => job.id === id);
        const job = jobs.find(job => job.id === id);
        const name = option?.name || `Unavailable entry (${id})`;
        const worker = workerIdentity(job, environments);
        const isAgent = worker.kindLabel === 'Agent';
        const environmentId = job?.actions?.find(a => Number(a.kind) === 0)?.environmentId ?? job?.environmentId;
        const environment = environments.find(env => Number(env.id) === Number(environmentId));
        const label = isAgent
            ? [worker.label, worker.workerName && worker.workerName !== name ? worker.workerName : ''].filter(Boolean).join(' · ')
            : worker.label;
        const logo = worker.logo ? `<img src="${escapeHtml(worker.logo)}" alt="${escapeHtml(worker.label)}"${worker.logoFilter ? ` style="filter:${escapeHtml(worker.logoFilter)}"` : ''}>`
            : `<i class="fa-solid fa-${worker.icon}" aria-hidden="true"></i>`;
        return `<article class="board-lane-agent" data-agent-id="${Number(id)}" data-launch-kind="${isAgent ? 'agent' : 'automation'}">
            <div class="board-lane-agent-heading">
                <span class="board-lane-agent-icon" title="${escapeHtml(label)}">${logo}</span>
                <div class="board-lane-agent-copy"><strong>${escapeHtml(name)}</strong>
                    <small><span class="board-lane-agent-kind">${escapeHtml(worker.kindLabel)}</span>${label ? ` · ${escapeHtml(label)}` : ''}</small>
                    ${environment ? `<small>${escapeHtml(agentPurposeLabel(environment.purpose))}</small>` : ''}
                    ${worker.modelSummary ? `<small class="board-lane-agent-model">${escapeHtml(worker.modelSummary)}</small>` : ''}
                    ${option && !option.enabled ? `<small>Disabled · enable it on the ${isAgent ? 'Agents' : 'Automations'} page</small>` : ''}</div>
                <button type="button" class="board-lane-agents-action" data-agent-action="edit" aria-label="Edit ${escapeHtml(name)}" title="Edit" ${option ? '' : 'disabled'}><i class="fa-solid fa-pen" aria-hidden="true"></i></button>
                <button type="button" class="board-lane-agents-action" data-agent-action="remove" aria-label="Remove ${escapeHtml(name)} from this lane" title="Remove from this lane"><i class="fa-solid fa-trash-can" aria-hidden="true"></i></button>
            </div>
            <div data-lane-step-id="${Number(id)}" aria-live="polite">${laneStepMarkup(Number(id), settings.workflows || [])}</div>
        </article>${index < ids.length - 1 ? `<div data-lane-arrow-id="${Number(id)}">${laneArrowMarkup(Number(id), settings.workflows || [])}</div>` : ''}`;
    }).join('');
    return `${settings.starterSetupPending ? '<p role="status">Starter review setup is pending. Reopen to retry, or save your own lane selection.</p>' : ''}
        <div class="board-lane-agents-list">${rows || '<p class="board-lane-agents-empty">Nothing runs when a card enters this lane yet.</p>'}</div>
        <p class="board-lane-agents-scope">Agents and Automations are shared wherever they are used. Remove unlinks one from this lane and cancels pending entries.</p>
        <button type="button" class="btn btn-sm btn-outline-secondary" data-agent-action="add"><i class="fa-solid fa-plus" aria-hidden="true"></i> Add agent or automation</button>`;
}
