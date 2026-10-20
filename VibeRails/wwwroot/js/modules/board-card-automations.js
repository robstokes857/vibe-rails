import { BoardApi } from './board-api.js';
import { escapeHtml } from './utils.js';
import { jobLaunchKind } from './launch-kind.js';

export function cardAutomationControls(saved) {
    if (!saved) return '<p class="board-side-empty">Save the card to run an Agent or Automation on it.</p>';
    return `<div data-board-automation-controls>
        <div class="board-automation-launch">
            <select class="form-select form-select-sm" aria-label="Agent or Automation to run" data-board-automation-choice disabled>
                <option value="">Loading…</option>
            </select>
            <button type="button" class="btn btn-sm btn-outline-primary" data-board-run-automation disabled>
                <i class="fa-solid fa-play" aria-hidden="true"></i> Run
            </button>
        </div>
        <p class="board-side-empty">Uses the saved card. The run and its recording stay linked here.</p>
        <p class="board-side-empty" data-board-automation-message role="status" aria-live="polite"></p>
        <button type="button" class="btn btn-sm btn-outline-secondary" data-board-reload-automations hidden>Reload</button>
        <div class="board-side-list" data-board-automation-runs></div>
    </div>`;
}

/** Agents first, then Automations, each group only when it has an entry. */
function groupedJobOptions(jobs, kinds) {
    const option = job => `<option value="${escapeHtml(String(job.id))}"${job.enabled ? '' : ' disabled'}>${escapeHtml(job.name)}${job.enabled ? '' : ' (disabled)'}</option>`;
    const groups = [['Agents', jobs.filter(job => kinds.get(job.id) === 'agent')],
        ['Automations', jobs.filter(job => kinds.get(job.id) !== 'agent')]];
    return groups.filter(([, items]) => items.length)
        .map(([label, items]) => `<optgroup label="${label}">${items.map(option).join('')}</optgroup>`).join('');
}

const STATUSES = ['Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'Timed out', 'Interrupted'];

export function bindCardAutomations(editor, card, { app, onQueued }) {
    const host = editor.querySelector('[data-board-automation-controls]');
    if (!host || !card?.id) return null;
    const select = host.querySelector('[data-board-automation-choice]');
    const button = host.querySelector('[data-board-run-automation]');
    const message = host.querySelector('[data-board-automation-message]');
    const retry = host.querySelector('[data-board-reload-automations]');
    const runs = host.querySelector('[data-board-automation-runs]');
    let disposed = false;
    let running = false;
    let loaded = false;
    let hasEnabledJobs = false;
    let generation = 0;
    let request;
    let catalog;
    let renderedOptions = '';
    const catalogRequest = new AbortController();
    const current = () => !disposed && host.isConnected;
    const updateButton = () => { button.disabled = running || !loaded || !select.value; };

    // The project catalog only says which jobs are Agents, so it is fetched once per editor
    // open rather than on every 10 s refresh. Its failure only loses the grouping, never the
    // list, and the next refresh asks again.
    function loadCatalog() {
        const projectPath = app.data?.configs?.rootPath || '';
        if (!projectPath) return Promise.resolve(null);
        catalog ??= app.apiCall(`/api/v1/jobs?projectPath=${encodeURIComponent(projectPath)}`, 'GET', null,
            { showLoading: false, signal: catalogRequest.signal })
            .catch(() => { catalog = undefined; return null; });
        return catalog;
    }

    async function refresh() {
        if (!current() || running) return;
        const version = ++generation;
        request?.abort();
        const abort = request = new AbortController();
        try {
            // The card endpoint lists id/name/enabled; the project catalog says which are Agents.
            const [result, jobCatalog] = await Promise.all([
                BoardApi.getCardAutomationsAsync(card.id, { signal: abort.signal }),
                loadCatalog()
            ]);
            if (!current() || generation !== version) return;
            const selected = select.value;
            const jobs = result?.jobs || [];
            const kinds = new Map((jobCatalog?.jobs || []).map(job => [job.id, jobLaunchKind(job)]));
            hasEnabledJobs = jobs.some(job => job.enabled);
            const options = `<option value="">${hasEnabledJobs ? 'Choose…' : 'Nothing enabled yet'}</option>`
                + groupedJobOptions(jobs, kinds);
            // Rewriting the options closes an open dropdown, so a periodic refresh with the
            // same list leaves the element and its selection alone.
            if (options !== renderedOptions) {
                select.innerHTML = renderedOptions = options;
                if (jobs.some(job => String(job.id) === selected && job.enabled)) select.value = selected;
            }
            select.disabled = !hasEnabledJobs;
            const entries = result?.laneEntries || [];
            runs.innerHTML = entries.map(entry => `<div class="board-automation-run">
                <span class="board-side-title">${escapeHtml(entry.name)}</span>
                <span class="board-side-sub">${escapeHtml(entry.stepStatus || entry.status)}</span>
                <span class="board-side-sub">${escapeHtml(entry.reason)}</span>
                ${(entry.canSkip || entry.status === 'Waiting' && !entry.runId) ? `<p class="board-side-empty">Steps run in order. The next step waits for this one to pass or be skipped.</p>
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-board-skip-automation
                        data-job-id="${Number(entry.jobId)}" data-event-key="${escapeHtml(entry.eventKey)}">${['Queued', 'Running'].includes(entry.status) ? 'Stop and skip' : 'Skip this step'}</button>` : ''}
            </div>`).join('') + (result?.runs || []).map(run => `<div class="board-automation-run" data-board-run-id="${escapeHtml(run.id)}">
                <span class="board-side-title">${escapeHtml(run.name)}</span>
                <span class="board-side-sub">${escapeHtml(STATUSES[run.status] || 'Unknown')}</span>
                ${run.errorMessage ? `<span class="board-side-sub text-danger">${escapeHtml(run.errorMessage)}</span>` : ''}
            </div>`).join('');
            const count = editor.querySelector('[data-board-count="automations"]');
            if (count) count.textContent = String(entries.length + (result?.runs?.length || 0)
                + editor.querySelectorAll('[data-board-automations] [data-session-id]').length);
            loaded = true;
            message.textContent = jobs.length ? '' : 'Create an Agent or Automation for this project first.';
            retry.hidden = true;
        } catch (error) {
            if (!current() || generation !== version || abort.signal.aborted) return;
            message.textContent = error?.message || 'Could not load the list.';
            retry.hidden = false;
        } finally {
            if (current() && generation === version) updateButton();
        }
    }

    async function run() {
        if (!current() || button.disabled || running) return;
        if (editor._boardSaving || editor._boardUploading || editor._boardStarting) {
            app.showToast('Board', 'Wait for the current card action to finish.', 'info');
            return;
        }
        const jobId = Number(select.value);
        if (!Number.isSafeInteger(jobId) || jobId <= 0) return;
        running = true;
        ++generation;
        request?.abort();
        select.disabled = true;
        updateButton();
        message.textContent = 'Queuing…';
        try {
            // Do not abort a submitted launch on close: it may already have committed.
            await BoardApi.runCardAutomationAsync(card.id, jobId);
            if (!current()) return;
            app.showToast('Board', 'Run queued and linked to this card.', 'success');
            message.textContent = 'Queued.';
            running = false;
            await refresh();
            if (current()) onQueued?.();
        } catch (error) {
            if (current()) {
                message.textContent = error?.message || 'Could not queue the run.';
                app.showToast('Board', message.textContent, 'error');
            }
        } finally {
            running = false;
            if (current()) {
                select.disabled = !loaded || !hasEnabledJobs;
                updateButton();
            }
        }
    }

    select.addEventListener('change', updateButton);
    async function skip(event) {
        const target = event.target.closest('[data-board-skip-automation]');
        if (!target || !current() || running) return;
        running = true;
        ++generation;
        request?.abort();
        target.disabled = true;
        updateButton();
        try {
            const result = await BoardApi.skipCardAutomationAsync(card.id, Number(target.dataset.jobId), target.dataset.eventKey);
            if (!current()) return;
            const entry = result?.laneEntries?.find(item => item.eventKey === target.dataset.eventKey && item.jobId === Number(target.dataset.jobId));
            const state = entry?.stepStatus || entry?.status;
            app.showToast('Board', state === 'Skipped' ? 'Continuing without this step.'
                : state === 'Stopping' ? 'Stopping this step before continuing.'
                : 'The entry changed while you were skipping it. Its current status is shown below.', 'info');
            running = false;
            await refresh();
            if (current()) onQueued?.();
        } catch (error) {
            if (current()) {
                running = false;
                await refresh();
                if (current()) message.textContent = error?.message || 'Could not skip this step.';
            }
        } finally {
            running = false;
            if (current()) { target.disabled = false; updateButton(); }
        }
    }
    runs.addEventListener('click', skip);
    button.addEventListener('click', run);
    retry.addEventListener('click', refresh);
    void refresh();
    return { refresh, dispose() {
        disposed = true;
        ++generation;
        request?.abort();
        catalogRequest.abort();
        select.removeEventListener('change', updateButton);
        button.removeEventListener('click', run);
        retry.removeEventListener('click', refresh);
        runs.removeEventListener('click', skip);
    } };
}
