import { escapeHtml } from './utils.js';
import { agentPurposeOptions } from './agent-purpose.js';
import { selectedLaneJobIds } from './board-lane-agent-list.js';

/** Preserve argv boundaries; the server validates containment, runtime and approval. */
export function laneScriptAction(path, argumentsText = '') {
    const scriptPath = path.trim();
    const extension = scriptPath.toLowerCase().match(/\.[^.\\/]+$/)?.[0];
    const scriptRuntime = { '.py': 0, '.ps1': 1, '.sh': 2 }[extension];
    if (scriptRuntime === undefined) throw new Error('Choose a repository Python (.py), PowerShell (.ps1), or Bash (.sh) script.');
    return { kind: 1, scriptPath, scriptRuntime, arguments: argumentsText === '' ? [] : argumentsText.split(/\r?\n/) };
}

export function findCheckAutomation(jobs, { name, kind, scope }) {
    const job = (jobs || []).find(item => String(item.name ?? '').trim().toLowerCase() === name.trim().toLowerCase());
    if (!job) return null;
    const actions = Array.isArray(job.actions) ? job.actions : [];
    return { job, reuse: actions.length === 1 && Number(actions[0].kind) === kind && actions[0].arguments?.[0] === scope };
}

export const laneWorker = (job, environments) => {
    const id = job?.actions?.find(a => Number(a.kind) === 0)?.environmentId ?? job?.environmentId;
    return environments.find(env => Number(env.id) === Number(id));
};

/** Creation has its own body and drafts; no existing rows sit above the form. */
export function laneAgentAddMarkup(draft, settings, jobs, environments, scripts) {
    const ids = selectedLaneJobIds(settings);
    const available = (settings.jobs || []).filter(job => !ids.includes(job.id));
    const worker = laneWorker(jobs.find(job => job.id === Number(draft.jobId)), environments);
    const scriptOptions = scripts?.scripts || [];
    return `<form class="board-lane-agents-add">
        <button type="button" class="btn btn-sm btn-outline-secondary mb-3" data-agent-action="back">← Lane agents</button>
        <h3>Add to lane</h3>
        <label for="board-lane-agent-choice">What would you like to add?</label>
        <select id="board-lane-agent-choice" class="form-select" data-lane-draft="kind" required>
            <option value="">Choose a type…</option>
            ${[['automation', 'Existing Automation'], ['new', 'New Automation'], ['check:2', 'Code quality'], ['check:3', 'VCA'], ['script', 'Repository script']].map(([value, label]) =>
                `<option value="${value}"${draft.kind === value ? ' selected' : ''}>${label}</option>`).join('')}
        </select>
        ${draft.kind === 'automation' ? `<label for="board-lane-automation">Automation</label>
            <select id="board-lane-automation" class="form-select" data-lane-draft="jobId" required>
                <option value="">Choose an Automation…</option>
                ${available.map(job => `<option value="${Number(job.id)}"${String(job.id) === draft.jobId ? ' selected' : ''} ${job.enabled ? '' : 'disabled'}>${escapeHtml(job.name)}${job.enabled ? '' : ' (disabled)'}</option>`).join('')}
            </select>${!available.some(job => job.enabled) ? '<p class="board-lane-agents-help">No enabled Automations available. Create one or enable it in the Automation editor.</p>' : ''}
            ${worker ? `<label for="board-lane-purpose">What kind of work? <span class="text-muted">(optional)</span></label>
                <select id="board-lane-purpose" class="form-select" data-lane-draft="purpose">${agentPurposeOptions(draft.purpose ?? worker.purpose)}</select>
                <small>Saved on Worker “${escapeHtml(worker.name)}” wherever it is used. Future agent comments can be filtered by this purpose.</small>` : ''}` : ''}
        ${draft.kind.startsWith('check:') ? '<p class="board-lane-check-summary">Checks working changes, including staged and unstaged edits. Committed code is covered by Git Guard.</p>' : ''}
        ${draft.kind === 'script' ? `<label for="board-lane-script-search">Find script</label>
            <input id="board-lane-script-search" type="search" class="form-control" data-lane-draft="scriptQuery" maxlength="256" value="${escapeHtml(draft.scriptQuery || '')}" placeholder="Search by file name or path">
            <button type="button" class="btn btn-sm btn-link" data-agent-action="reload-scripts">Search scripts</button>
            <label for="board-lane-script">Script file</label>
            <select id="board-lane-script" class="form-select" data-lane-draft="path" required ${scripts ? '' : 'disabled'}>
                <option value="">${scripts ? 'Choose a repository script…' : 'Loading scripts…'}</option>
                ${scriptOptions.map(script => `<option value="${escapeHtml(script.path)}"${draft.path === script.path ? ' selected' : ''} ${script.unavailableReason ? 'disabled' : ''}>${escapeHtml(script.path)} — ${escapeHtml(script.unavailableReason || (script.approved ? 'Approved' : 'Needs approval'))}</option>`).join('')}
            </select>
            <small role="status">${scripts ? `${scriptOptions.filter(s => !s.unavailableReason).length} valid scripts · ${scriptOptions.filter(s => s.approved && !s.unavailableReason).length} approved${scripts.hasMore ? ' · More matches exist. Narrow your search to find them.' : ''}` : 'Discovering Python, PowerShell and Bash scripts…'}</small>
            <label for="board-lane-script-name">Automation name</label>
            <input id="board-lane-script-name" class="form-control" data-lane-draft="name" maxlength="100" value="${escapeHtml(draft.name)}" required placeholder="Run lane script">
            <label for="board-lane-script-arguments">Arguments — one per line</label>
            <textarea id="board-lane-script-arguments" class="form-control" data-lane-draft="arguments" rows="4">${escapeHtml(draft.arguments)}</textarea>
            <small>Each line is one argument. Adding approves the current file contents. Later file changes require approval in the Automation editor.</small>` : ''}
        ${draft.kind === 'new' ? '<p>Create a workflow with a Worker, scripts or checks in the full Automation editor, then return here to add it to the lane.</p>' : ''}
        <div data-agent-error role="alert"></div>
        <footer class="board-lane-agents-footer">
            ${draft.kind === 'new' ? '<button type="button" class="btn btn-outline-primary" data-agent-action="create">Open Automation editor</button>' : `<button type="submit" class="btn btn-outline-primary" ${!draft.kind || draft.kind === 'script' && !scripts ? 'disabled' : ''}>Add to lane</button>`}
            <button type="button" class="btn btn-outline-secondary" data-agent-action="back">Cancel</button>
        </footer>
    </form>`;
}

/** Prepare an ordinary Automation. Keep its id after creation so lane-save retries never duplicate it. */
export async function prepareLaneAutomation(context) {
    const { draft, settings, jobs, environments, scripts, column, projectPath, api, updateJob, alive } = context;
    if (selectedLaneJobIds(settings).some(id => !settings.jobs?.some(job => job.id === id && job.enabled)))
        throw new Error('Enable or remove disabled/unavailable selections before adding another Automation.');
    if (draft.createdId) return draft.createdId;
    if (draft.kind === 'automation') {
        const id = Number(draft.jobId);
        if (!settings.jobs?.some(job => job.id === id && job.enabled)) throw new Error('Choose an enabled Automation.');
        const worker = laneWorker(jobs.find(job => job.id === id), environments);
        if (worker && draft.purpose != null && draft.purpose !== worker.purpose) {
            await api(`/api/v1/environments/${encodeURIComponent(worker.name)}`, 'PUT', { purpose: draft.purpose });
            if (!alive()) return null;
        }
        return id;
    }
    let action, name, description, existing;
    if (draft.kind === 'script') {
        name = draft.name.trim();
        if (!name || name.length > 100) throw new Error('Give this script Automation a name of up to 100 characters.');
        if (!scripts?.scripts.some(script => script.path === draft.path && !script.unavailableReason)) throw new Error('Choose a valid repository script.');
        action = laneScriptAction(draft.path, draft.arguments);
        description = `Runs ${action.scriptPath} when a card enters ${column.name}.`;
    } else if (['check:2', 'check:3'].includes(draft.kind)) {
        const kind = Number(draft.kind.slice(6));
        name = `${kind === 2 ? 'Code quality' : 'VCA'} · ${column.name} · working-tree`;
        action = { kind, arguments: ['working-tree'] };
        description = `${kind === 2 ? 'Code quality' : 'VCA'} checks working changes when a card enters ${column.name}.`;
        existing = findCheckAutomation(jobs, { name, kind, scope: 'working-tree' });
        if (existing && !existing.reuse) throw new Error(`An Automation named “${existing.job.name}” already exists with a different workflow. Rename or edit it in the Automation editor.`);
    } else throw new Error('Choose what to add.');
    let saved = existing?.job;
    if (!saved) saved = await api('/api/v1/jobs', 'POST', {
        name, projectPath, llm: 0, prompt: '', environmentId: null, timeoutMinutes: null,
        enabled: true, triggers: [], actions: [action], description
    });
    else if (!saved.enabled) saved = await updateJob(saved.id, { enabled: true });
    if (!alive() || !saved) return null;
    draft.createdId = saved.id;
    if (!jobs.some(job => job.id === saved.id)) jobs.push(saved);
    const option = settings.jobs?.find(job => job.id === saved.id);
    if (option) option.enabled = saved.enabled;
    else (settings.jobs ??= []).push(saved);
    draft.kind = 'automation';
    draft.jobId = String(saved.id);
    return saved.id;
}
