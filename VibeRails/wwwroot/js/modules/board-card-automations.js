import { BoardApi } from './board-api.js';
import { escapeHtml } from './utils.js';

export function cardAutomationControls(saved) {
    if (!saved) return '<p class="board-side-empty">Save the card to run an Automation.</p>';
    return `<div data-board-automation-controls>
        <div class="board-automation-launch">
            <select class="form-select form-select-sm" aria-label="Automation to run" data-board-automation-choice disabled>
                <option value="">Loading Automations…</option>
            </select>
            <button type="button" class="btn btn-sm btn-outline-primary" data-board-run-automation disabled>
                <i class="fa-solid fa-play" aria-hidden="true"></i> Run automation
            </button>
        </div>
        <p class="board-side-empty">Uses the saved card. The run and its recording stay linked here.</p>
        <p class="board-side-empty" data-board-automation-message role="status" aria-live="polite"></p>
        <button type="button" class="btn btn-sm btn-outline-secondary" data-board-reload-automations hidden>Reload automations</button>
        <div class="board-side-list" data-board-automation-runs></div>
    </div>`;
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
    const current = () => !disposed && host.isConnected;
    const updateButton = () => { button.disabled = running || !loaded || !select.value; };

    async function refresh() {
        if (!current() || running) return;
        const version = ++generation;
        request?.abort();
        const abort = request = new AbortController();
        try {
            const result = await BoardApi.getCardAutomationsAsync(card.id, { signal: abort.signal });
            if (!current() || generation !== version) return;
            const selected = select.value;
            const jobs = result?.jobs || [];
            hasEnabledJobs = jobs.some(job => job.enabled);
            select.innerHTML = `<option value="">${hasEnabledJobs ? 'Choose an Automation…' : 'No enabled Automations'}</option>`
                + jobs.map(job => `<option value="${escapeHtml(String(job.id))}"${job.enabled ? '' : ' disabled'}>${escapeHtml(job.name)}${job.enabled ? '' : ' (disabled)'}</option>`).join('');
            if (jobs.some(job => String(job.id) === selected && job.enabled)) select.value = selected;
            select.disabled = !hasEnabledJobs;
            runs.innerHTML = (result?.runs || []).map(run => `<div class="board-automation-run" data-board-run-id="${escapeHtml(run.id)}">
                <span class="board-side-title">${escapeHtml(run.name)}</span>
                <span class="board-side-sub">${escapeHtml(STATUSES[run.status] || 'Unknown')}</span>
                ${run.errorMessage ? `<span class="board-side-sub text-danger">${escapeHtml(run.errorMessage)}</span>` : ''}
            </div>`).join('');
            const count = editor.querySelector('[data-board-count="automations"]');
            if (count) count.textContent = String((result?.runs?.length || 0)
                + editor.querySelectorAll('[data-board-automations] [data-session-id]').length);
            loaded = true;
            message.textContent = jobs.length ? '' : 'Create an Automation for this project on the Automations page.';
            retry.hidden = true;
        } catch (error) {
            if (!current() || generation !== version || abort.signal.aborted) return;
            message.textContent = error?.message || 'Could not load Automations.';
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
        message.textContent = 'Queuing Automation…';
        try {
            // Do not abort a submitted launch on close: it may already have committed.
            await BoardApi.runCardAutomationAsync(card.id, jobId);
            if (!current()) return;
            app.showToast('Board', 'Automation queued and linked to this card.', 'success');
            message.textContent = 'Automation queued.';
            running = false;
            await refresh();
            if (current()) onQueued?.();
        } catch (error) {
            if (current()) {
                message.textContent = error?.message || 'Could not queue the Automation.';
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
    button.addEventListener('click', run);
    retry.addEventListener('click', refresh);
    void refresh();
    return { refresh, dispose() {
        disposed = true;
        ++generation;
        request?.abort();
        select.removeEventListener('change', updateButton);
        button.removeEventListener('click', run);
        retry.removeEventListener('click', refresh);
    } };
}
