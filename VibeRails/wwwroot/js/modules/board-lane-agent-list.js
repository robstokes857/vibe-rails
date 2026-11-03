import { laneStepMarkup, laneArrowMarkup } from './board-lane-workflow.js';
import { escapeHtml } from './utils.js';
import { workerIdentity } from './board-lane-agent-display.js';
import { agentPurposeLabel } from './agent-purpose.js';

export const selectedLaneJobIds = settings => settings.jobIds ?? (settings.jobId ? [settings.jobId] : []);

const actionIcon = path => `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${path}</svg>`;
const upIcon = actionIcon('<path d="m5 12 7-7 7 7M12 5v14"/>');
const downIcon = actionIcon('<path d="m5 12 7 7 7-7M12 5v14"/>');
const editIcon = actionIcon('<path d="m16 3 5 5-13 13H3v-5Z M14 5l5 5"/>');
const removeIcon = actionIcon('<path d="M3 6h18M9 6V4h6v2M5 6l1 14h12l1-14M10 10v6M14 10v6"/>');

/** Ordered lane summary: one row per Agent or Automation, with immediate move controls. */
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
                    ${option && !option.enabled ? '<small>Disabled · open Edit to enable it</small>' : ''}</div>
            </div>
            <div class="board-lane-agent-controls" role="group" aria-label="Actions for ${escapeHtml(name)}">
                <button type="button" class="btn btn-sm btn-outline-secondary" data-agent-action="move-up" aria-label="Move ${escapeHtml(name)} up" title="Move up" ${index === 0 ? 'disabled' : ''}>${upIcon}</button>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-agent-action="move-down" aria-label="Move ${escapeHtml(name)} down" title="Move down" ${index === ids.length - 1 ? 'disabled' : ''}>${downIcon}</button>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-agent-action="edit" aria-label="Edit ${escapeHtml(name)}" title="Edit" ${option ? '' : 'disabled'}>${editIcon}</button>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-agent-action="remove" aria-label="Remove ${escapeHtml(name)} from this lane" title="Remove from this lane">${removeIcon}</button>
            </div>
            <div data-lane-step-id="${Number(id)}" aria-live="polite">${laneStepMarkup(Number(id), settings.workflows || [])}</div>
        </article>${index < ids.length - 1 ? `<div data-lane-arrow-id="${Number(id)}">${laneArrowMarkup(Number(id), settings.workflows || [])}</div>` : ''}`;
    }).join('');
    return `${settings.starterSetupPending ? '<p role="status">Starter review setup is pending. Reopen to retry, or save your own lane selection.</p>' : ''}
        <div class="board-lane-agents-list">${rows || '<p class="board-lane-agents-empty">Nothing runs when a card enters this lane yet.</p>'}</div>
        <p class="board-lane-agents-scope">Move up or down to change the run order. Changes save immediately and cancel pending entries. Edits apply wherever the Agent or Automation is used; Remove only unlinks it from this lane.</p>
        <button type="button" class="btn btn-sm btn-outline-secondary" data-agent-action="add"><i class="fa-solid fa-plus" aria-hidden="true"></i> Add agent or automation</button>`;
}
