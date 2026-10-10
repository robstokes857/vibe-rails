import { refreshLaneSteps } from './board-lane-workflow.js';
import { BoardApi } from './board-api.js';
import { escapeHtml, confirmDialog } from './utils.js';
import { laneAgentListMarkup, selectedLaneJobIds } from './board-lane-agent-list.js';
import { laneAgentAddMarkup, prepareLaneAutomation } from './board-lane-agent-add.js';

/** Owns requests, drafts and CRUD inside one disposable lane dialog. */
export function mountLaneAgentBody({ app, panel, column, signal, position, updateCount, close, openRunningAgent }) {
    const content = panel.querySelector('[data-lane-agents-content]');
    const alive = () => !signal.aborted && panel.isConnected;
    const projectPath = app.data.configs?.rootPath || '';
    const options = { showLoading: false, preferErrorResponseMessage: true, signal };
    const api = (path, method = 'GET', body = null) => app.apiCall(path, method, body, options);
    let settings, jobs = [], environments = [], scripts;
    let adding = false, busy = false, polling = false, scriptRequest = 0, activityGeneration = 0;
    let draft = { kind: '', jobId: '', name: '', path: '', arguments: '' };
    const renderRunning = () => {
        const host = panel.querySelector('[data-lane-running]');
        host.hidden = adding;
        host.innerHTML = '<h3 class="h6">Running agents</h3>' + ((settings?.runningAgents || []).map(agent =>
            `<button type="button" class="board-side-main mb-2" data-lane-running-id="${escapeHtml(agent.runId)}"><i class="fa-solid fa-robot" aria-hidden="true"></i><span><strong>${escapeHtml(agent.name)}</strong><small class="d-block">${escapeHtml(agent.cardLabel)}</small></span></button>`).join('')
            || '<p class="board-lane-agents-empty">No agents running from this lane.</p>');
    };
    const render = () => {
        if (!alive() || !settings) return;
        panel.classList.toggle('is-adding', adding);
        panel.querySelector('.board-lane-agents-help').hidden = adding;
        renderRunning();
        content.removeAttribute('role');
        content.innerHTML = adding ? laneAgentAddMarkup(draft, settings, jobs, environments, scripts)
            : laneAgentListMarkup(settings, jobs, environments) + '<div data-agent-error role="alert"></div>';
        if (busy) content.querySelectorAll('button, input, select, textarea').forEach(control => { control.disabled = true; });
        position();
    };
    const load = async () => {
        const [lane, catalog, profiles] = await Promise.all([
            BoardApi.getLaneAutomationAsync(column.id, { signal }),
            api(`/api/v1/jobs?projectPath=${encodeURIComponent(projectPath)}`), api('/api/v1/environments')
        ]);
        if (!alive()) return;
        settings = lane; jobs = catalog?.jobs || []; environments = profiles?.environments || [];
        updateCount(settings);
    };
    const showError = (error, retryAction = 'retry') => {
        if (!alive()) return;
        const host = content.querySelector('[data-agent-error]');
        if (host) {
            host.textContent = error?.message || 'Could not update lane agents.';
            const retry = document.createElement('button');
            retry.type = 'button'; retry.className = 'btn btn-sm btn-link';
            retry.dataset.agentAction = retryAction; retry.textContent = 'Reload'; host.append(retry);
        }
    };
    const run = async (operation, focus) => {
        if (busy) return;
        busy = true;
        ++activityGeneration;
        content.querySelectorAll('button, select, textarea, input').forEach(control => { control.disabled = true; });
        let failure;
        try { await operation(); } catch (error) { failure = error; }
        finally {
            busy = false;
            if (alive()) {
                render();
                if (failure) showError(failure);
                content.querySelector(typeof focus === 'function' ? focus() : focus)?.focus();
                position();
            }
        }
    };
    const reload = async () => {
        await run(load, '[data-agent-action="add"]');
        if (alive() && !settings) content.innerHTML = '<p role="alert">Could not load lane agents.</p><button type="button" class="btn btn-outline-secondary" data-agent-action="retry">Retry</button>';
    };
    const renderScriptResults = () => {
        if (!adding || draft.kind !== 'script' || busy) return;
        const active = document.activeElement;
        const field = content.contains(active) ? active.dataset.laneDraft : null;
        const start = active?.selectionStart, end = active?.selectionEnd;
        render();
        const restored = [...content.querySelectorAll('[data-lane-draft]')].find(el => el.dataset.laneDraft === field);
        restored?.focus();
        if (start != null && end != null) restored?.setSelectionRange?.(start, end);
    };
    const loadScripts = async () => {
        const request = ++scriptRequest;
        const query = (draft.scriptQuery || '').trim();
        scripts = null;
        renderScriptResults();
        try {
            const catalog = await api(`/api/v1/jobs/scripts${query ? `?q=${encodeURIComponent(query)}` : ''}`);
            if (!alive() || request !== scriptRequest) return;
            scripts = catalog;
            // Script discovery must not disturb a form the user has moved on to.
            renderScriptResults();
        } catch (error) { if (alive() && request === scriptRequest && adding && draft.kind === 'script') showError(error, 'reload-scripts'); }
    };
    const saveSelection = async ids => {
        const saved = await BoardApi.saveLaneAutomationAsync(column.id, { jobIds: ids, expectedRevision: settings.revision });
        if (!alive()) return;
        settings = { ...settings, ...saved, jobIds: ids };
        updateCount(settings);
        // The mutation succeeded. Return to the list even if a catalog refresh fails.
        adding = false;
        draft = { kind: '', jobId: '', name: '', path: '', arguments: '' };
        scriptRequest++;
        scripts = null;
        await load();
    };
    const updateJob = async (id, changes) => {
        const job = await api(`/api/v1/jobs/${id}`);
        if (!alive()) return null;
        const { name, projectPath, llm, environmentId, prompt, timeoutMinutes, triggers, launchMinimized, enabled, description } = job;
        return api(`/api/v1/jobs/${id}`, 'PUT', { name, projectPath, llm, environmentId, prompt, timeoutMinutes, triggers, launchMinimized, enabled, description, ...changes });
    };
    panel.addEventListener('click', async event => {
        const runningId = event.target.closest('[data-lane-running-id]')?.dataset.laneRunningId;
        if (runningId) {
            const agent = settings?.runningAgents?.find(item => item.runId === runningId);
            if (agent && openRunningAgent) { close(false); void openRunningAgent(agent); }
            return;
        }
        const action = event.target.closest('[data-agent-action]')?.dataset.agentAction;
        if (action === 'close') { close(); return; }
        if (!action || busy) return;
        const id = Number(event.target.closest('[data-agent-id]')?.dataset.agentId);
        if (action === 'retry') { void reload(); return; }
        if (action === 'reload-scripts') { void loadScripts(); return; }
        if (action === 'add' || action === 'back') {
            adding = action === 'add'; render();
            panel.scrollTop = 0;
            content.querySelector(adding ? '#board-lane-agent-choice' : '[data-agent-action="add"]')?.focus();
            return;
        }
        if (action === 'edit' || action === 'create') {
            close(false);
            app.navigate('jobs', action === 'edit' ? { editJobId: id } : { newJob: true, triggerKind: 3 });
            return;
        }
        if (action === 'skip' || action === 'rerun') {
            const button = event.target.closest('[data-agent-action]');
            void run(async () => {
                if (action === 'rerun') {
                    const result = await BoardApi.rerunCardAutomationAsync(button.dataset.cardId, id, button.dataset.eventKey);
                    if (!alive()) return;
                    const step = settings.workflows?.find(flow => flow.cardId === button.dataset.cardId)?.steps
                        ?.find(step => step.jobId === id && step.eventKey === button.dataset.eventKey);
                    if (step) Object.assign(step, { status: 'Queued', stepStatus: 'Queued', canRerun: false,
                        runId: result.runId, reason: 'Re-run queued.' });
                } else await BoardApi.skipCardAutomationAsync(button.dataset.cardId, id, button.dataset.eventKey);
                if (!alive()) return;
                const fresh = await BoardApi.getLaneRunningAgentsAsync(column.id, { signal });
                if (alive()) { settings.runningAgents = fresh.runningAgents; settings.workflows = fresh.workflows; }
            }, '[data-agent-action="add"]');
            return;
        }
        if (action === 'remove') {
            const name = settings.jobs?.find(job => job.id === id)?.name || `Automation ${id}`;
            const retained = selectedLaneJobIds(settings).filter(value => value !== id && settings.jobs?.some(job => job.id === value && job.enabled));
            const alsoRemoved = selectedLaneJobIds(settings).filter(value => value !== id && !retained.includes(value));
            const extra = alsoRemoved.length ? ` Also clear disabled or unavailable selections: ${alsoRemoved.map(value => settings.jobs?.find(job => job.id === value)?.name || value).join(', ')}.` : '';
            void run(async () => {
                const confirmed = await confirmDialog({ title: `Remove “${name}” from ${column.name}?`, message: `The Automation and recordings are kept. Saving cancels pending triggers for this lane.${extra}`, confirmLabel: 'Remove', danger: true });
                if (alive() && confirmed) await saveSelection(retained);
            }, '[data-agent-action="add"]');
        }
    }, { signal });
    panel.addEventListener('input', event => {
        const key = event.target.dataset.laneDraft;
        if (key && !busy) draft[key] = event.target.value;
    }, { signal });
    panel.addEventListener('keydown', event => {
        if (event.key === 'Enter' && event.target.dataset.laneDraft === 'scriptQuery') {
            event.preventDefault();
            if (!busy) void loadScripts();
        }
    }, { signal });
    panel.addEventListener('change', event => {
        const key = event.target.dataset.laneDraft;
        if (!key || busy) return;
        draft[key] = event.target.value;
        if (key === 'kind' || key === 'jobId') {
            delete draft.createdId;
            delete draft.purpose;
            render();
            content.querySelector(key === 'kind' ? '#board-lane-agent-choice' : '#board-lane-automation')?.focus();
        }
        if (key === 'kind' && draft.kind === 'script' && !scripts) void loadScripts();
    }, { signal });
    panel.addEventListener('submit', event => {
        if (!event.target.matches('.board-lane-agents-add')) return;
        event.preventDefault();
        let id;
        void run(async () => {
            id = await prepareLaneAutomation({ draft, settings, jobs, environments, scripts, column, projectPath, api, updateJob, alive });
            if (!alive() || !id) return;
            if (!selectedLaneJobIds(settings).includes(id)) await saveSelection([...selectedLaneJobIds(settings), id]);
            else adding = false;
        }, () => !adding && id ? `[data-agent-id="${id}"] [data-agent-action="edit"]` : '#board-lane-agent-choice');
    }, { signal });
    const timer = setInterval(async () => {
        if (!alive() || document.hidden || polling || busy || adding) return;
        polling = true;
        const generation = activityGeneration;
        try {
            const fresh = await BoardApi.getLaneRunningAgentsAsync(column.id, { signal });
            if (alive() && settings && !busy && !adding && generation === activityGeneration) { settings.runningAgents = fresh.runningAgents; settings.workflows = fresh.workflows; renderRunning(); refreshLaneSteps(content, fresh.workflows); position(); }
        } catch { /* Preserve the last known running list. */ }
        finally { polling = false; }
    }, 10000);
    void reload();
    return () => { clearInterval(timer); scriptRequest++; };
}
