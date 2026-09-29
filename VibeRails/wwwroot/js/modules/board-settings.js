import { escapeHtml } from './utils.js';
import { BoardApi } from './board-api.js';

const TYPES = [
    ['task', 'Task'], ['bug', 'Bug'], ['feature', 'Feature'],
    ['research-spike', 'Research spike'], ['chore', 'Chore / tech debt']
];

export const boardContextSection = () => `
    <section class="mt-3 border-top pt-3" data-board-context>
        <h6>Agent context</h6>
        <p class="board-editor-muted">Sent to every agent opened from a card on this board, for work or discussion. Each message can contain up to 4,000 characters.</p>
        <div data-board-settings-content>Loading context…</div>
    </section>`;

export const boardSyncSection = () => `
    <section class="mt-3 border-top pt-3" data-board-sync>
        <h6>viberails.ai</h6>
        <p class="board-editor-muted">Boards sync automatically to your viberails.ai account while an API key is configured. Cards, discussion, linked sessions, saved commit code and attachments refresh while VibeRails is open. Large files may be available only on the desktop. Lane moves can run your configured local Automations.</p>
        <div data-board-settings-content>Loading sync status…</div>
    </section>`;

export const laneAutomationSection = () => `
    <section class="mt-3 border-top pt-3" data-lane-automation>
        <h6>Automations</h6>
        <p class="board-editor-muted">Run selected Automations after a card enters this lane and stays for 60 seconds. Each runs independently. Moving again restarts the wait. Reordering or editing a card in the same lane does not trigger them.</p>
        <div data-board-settings-content>Loading Automations…</div>
        <p class="board-editor-muted mt-2">Runs while a VibeRails backend is open. Disabled Automations and overlapping runs are skipped. Saving this setting cancels pending triggers for this lane and applies to future entries.</p>
    </section>`;

// Each mount owns its request and DOM. Replacing/closing a modal cannot populate a newer one.
function mountSettings(app, element, { load, render, read, save, savedMessage }) {
    if (!element) return () => {};
    const abort = new AbortController();
    let disposed = false;
    let revision = null;
    let busy = false;
    const content = element.querySelector('[data-board-settings-content]');
    const alive = () => !disposed && element.isConnected !== false;
    async function reload() {
        try {
            const result = await load({ signal: abort.signal });
            if (!alive()) return;
            revision = result.revision;
            content.innerHTML = render(result);
            element.querySelectorAll('[data-context-mode]').forEach(select => {
                const message = element.querySelector(`[data-context-message="${select.dataset.contextMode}"]`);
                const sync = () => { message.hidden = select.value === 'default'; };
                select.addEventListener('change', sync);
                sync();
            });
        } catch (error) {
            if (!alive() || error?.name === 'AbortError') return;
            content.innerHTML = `<p role="alert">${escapeHtml(error?.message || 'Settings could not be loaded.')}</p><button type="button" class="btn btn-sm btn-outline-secondary" data-settings-retry>Retry</button>`;
        }
    }
    const click = async event => {
        if (event.target.closest('[data-settings-retry]')) { void reload(); return; }
        const button = event.target.closest('[data-settings-save]');
        if (!button || busy || revision === null) return;
        busy = true;
        button.disabled = true;
        try {
            const result = await save({ ...read(element), expectedRevision: revision });
            if (!alive()) return;
            revision = result.revision;
            app.showToast('Board', savedMessage, 'success');
        } catch (error) {
            if (alive()) app.showToast('Board', error?.message || 'Settings could not be saved.', 'error');
        } finally {
            busy = false;
            if (alive()) button.disabled = false;
        }
    };
    element.addEventListener('click', click);
    void reload();
    return () => { disposed = true; abort.abort(); element.removeEventListener('click', click); };
}

export function mountBoardContext(app, element, boardId) {
    return mountSettings(app, element, {
        load: extra => BoardApi.getBoardContextAsync(boardId, extra),
        render: ({ context }) => `
            <label class="board-editor-label" for="board-context-default">Default message</label>
            <textarea id="board-context-default" class="form-control form-control-sm mb-3" rows="4" maxlength="4000" placeholder="Context for every card…">${escapeHtml(context.defaultMessage)}</textarea>
            <p class="board-editor-muted">For each card type, use the default, replace it, or send both (default first). An empty type-only message sends no board context.</p>
            ${TYPES.map(([type, label]) => {
                const override = context.typeOverrides.find(item => item.type === type);
                const mode = override?.mode || 'default';
                return `<details class="mb-2"><summary>${label}</summary>
                    <label class="board-editor-label mt-2" for="board-context-mode-${type}">${label} context</label>
                    <select id="board-context-mode-${type}" class="form-select form-select-sm mb-2" data-context-mode="${type}">
                        ${[['default', 'Default only'], ['replace', 'Type-specific only'], ['append', 'Default + type-specific']].map(([value, text]) => `<option value="${value}" ${value === mode ? 'selected' : ''}>${text}</option>`).join('')}
                    </select>
                    <textarea class="form-control form-control-sm" rows="3" maxlength="4000" data-context-message="${type}" aria-label="${label} message">${escapeHtml(override?.message || '')}</textarea>
                </details>`;
            }).join('')}
            <button type="button" class="btn btn-sm btn-outline-primary mt-2" data-settings-save>Save context</button>`,
        read: root => ({ context: {
            defaultMessage: root.querySelector('#board-context-default').value,
            typeOverrides: TYPES.map(([type]) => ({ type,
                mode: root.querySelector(`[data-context-mode="${type}"]`).value,
                message: root.querySelector(`[data-context-message="${type}"]`).value
            }))
        } }),
        save: payload => BoardApi.saveBoardContextAsync(boardId, payload),
        savedMessage: 'Board context saved.'
    });
}

// Automatic account sync status and an immediate retry.
export function mountBoardSync(app, element, boardId) {
    if (!element) return () => {};
    const abort = new AbortController();
    let disposed = false;
    let busy = false;
    const content = element.querySelector('[data-board-settings-content]');
    const alive = () => !disposed && element.isConnected !== false;
    const render = status => {
        const stamp = status.lastSyncUtc ? new Date(status.lastSyncUtc).toLocaleString() : 'never';
        content.innerHTML = `
            <p class="board-editor-muted" data-board-sync-policy>${status.configured ? 'Automatic sync is on · cards, sessions and code' : 'Sign in from the navigation or add an API key in Settings to sync your boards.'}</p>
            ${status.published ? `<p class="board-editor-muted mb-2" data-board-sync-status>${status.remoteUrl ? `<a href="${escapeHtml(status.remoteUrl)}" target="_blank" rel="noopener">Open on viberails.ai</a> · ` : ''}Last sync: ${escapeHtml(stamp)}${status.unsent ? ` · ${Number(status.unsent)} waiting to send` : ''}</p>` : ''}
            ${status.lastError ? `<p class="text-danger small mb-2" role="alert" data-board-sync-error>${escapeHtml(status.lastError)}</p>` : ''}
            ${status.rejected ? `<div class="text-warning small mb-2" role="alert" data-board-sync-rejected>
                <p class="mb-1">${Number(status.rejected)} rejected entries are kept on this machine. Other entries continue syncing.
                Inspect the affected cards' History, Comments, or Agent notes. Edit rejected fields again to send a correction;
                rejected values stay protected until that correction syncs. A card with a rejected creation keeps later edits local;
                create a replacement card to publish its corrected state. These records remain here after corrections sync.</p>
                <ul class="mb-1">${(status.rejectedEntries || []).map(entry => `<li>${escapeHtml(entry.cardKey)} · ${escapeHtml(entry.kind)} · <code>${escapeHtml(entry.entryId)}</code></li>`).join('')}</ul>
                ${status.rejected > (status.rejectedEntries || []).length ? '<p class="mb-0">Showing the latest 50 rejected entries.</p>' : ''}
                </div>` : ''}
            ${status.skipped ? `<div class="text-warning small mb-2" role="alert" data-board-sync-skipped>
                <p class="mb-1">${Number(status.skipped)} changes from viberails.ai could not be applied on this machine and were passed over,
                so later changes keep syncing. They stay on viberails.ai, and the first sync after a VibeRails update tries them again.</p>
                <ul class="mb-1">${(status.skippedEntries || []).map(entry => `<li>${escapeHtml(entry.cardKey)} · ${escapeHtml(entry.kind)} · ${escapeHtml(entry.reason)} · <code>${escapeHtml(entry.entryId)}</code></li>`).join('')}</ul>
                ${status.skipped > (status.skippedEntries || []).length ? '<p class="mb-0">Showing the latest 50 skipped changes.</p>' : ''}
                </div>` : ''}
            ${status.configured ? '<button type="button" class="btn btn-sm btn-outline-secondary" data-board-sync-action="now">Sync now</button>' : ''}`;
    };
    async function reload() {
        try {
            const status = await BoardApi.getBoardSyncAsync(boardId, { signal: abort.signal });
            if (!alive()) return;
            render(status);
        } catch (error) {
            if (!alive() || error?.name === 'AbortError') return;
            content.innerHTML = `<p role="alert">${escapeHtml(error?.message || 'Sync status could not be loaded.')}</p><button type="button" class="btn btn-sm btn-outline-secondary" data-settings-retry>Retry</button>`;
        }
    }
    const click = async event => {
        if (event.target.closest('[data-settings-retry]')) { void reload(); return; }
        const control = event.target.closest('[data-board-sync-action]');
        if (!control) return;
        // Keep manual retries from overlapping.
        if (busy) { event.preventDefault(); return; }
        const action = control.dataset.boardSyncAction;
        if (action !== 'now') return;
        busy = true;
        control.disabled = true;
        try {
            const status = await BoardApi.syncBoardNowAsync(boardId);
            if (!alive()) return;
            render(status);
            const message = status.lastError || status.rejected ? 'Sync finished with entries needing attention.' : 'Board synced.';
            app.showToast('Board', message, status.lastError || status.rejected ? 'warning' : 'success');
        } catch (error) {
            if (!alive()) return;
            app.showToast('Board', error?.message || 'Sync change failed.', 'error');
            void reload();
        } finally {
            busy = false;
        }
    };
    element.addEventListener('click', click);
    void reload();
    return () => {
        disposed = true;
        abort.abort();
        element.removeEventListener('click', click);
    };
}

export function mountLaneAutomation(app, element, columnId) {
    return mountSettings(app, element, {
        load: extra => BoardApi.getLaneAutomationAsync(columnId, extra),
        render: ({ jobs, jobIds, jobId }) => {
            const selected = new Set(jobIds ?? (jobId ? [jobId] : []));
            const choices = [...jobs, ...[...selected].filter(id => !jobs.some(job => job.id === id))
                .map(id => ({ id, name: `Unavailable Automation (${id})`, enabled: false }))];
            return `<fieldset class="mb-2">
                <legend class="board-editor-label">Automations on entry</legend>
                <p class="board-editor-muted">Select any number, or clear all to turn off Automations for this lane. Remove unavailable or disabled selections before saving.</p>
                ${choices.map(job => `<label class="d-flex align-items-start gap-2 mb-2">
                    <input type="checkbox" class="form-check-input flex-shrink-0" data-lane-automation-job value="${Number(job.id)}" ${selected.has(job.id) ? 'checked' : ''} ${!job.enabled && !selected.has(job.id) ? 'disabled' : ''}>
                    <span class="text-break">${escapeHtml(job.name)}${job.enabled ? '' : ' (disabled)'}</span>
                </label>`).join('')}
            </fieldset>
            ${jobs.length ? '' : '<p class="board-editor-muted">Create an Automation for this project on the Automations page first.</p>'}
            <button type="button" class="btn btn-sm btn-outline-primary" data-settings-save>Save automations</button>`;
        },
        read: root => ({ jobIds: [...root.querySelectorAll('[data-lane-automation-job]:checked')].map(input => Number(input.value)) }),
        save: payload => BoardApi.saveLaneAutomationAsync(columnId, payload),
        savedMessage: 'Lane automations saved.'
    });
}
