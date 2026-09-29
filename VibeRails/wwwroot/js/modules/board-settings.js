import { escapeHtml, confirmDialog } from './utils.js';
import { BoardApi } from './board-api.js';

// Shown before publishing switches on: what leaves the machine (SYNC.md "What leaves the machine").
const PUBLISH_CONSENT = 'Publishing uploads this board to your viberails.ai account now and about every minute '
    + 'while VibeRails is open: the board name and key prefix; lane names, colours and order; every card\'s '
    + 'title, description, type, priority, points, tags, flags, lane and assignee; and its comments, agent '
    + 'notes and change history. Attachments, linked commits, terminal sessions, Automation settings and '
    + 'environment definitions stay on this machine. File references travel as text, without file contents.';

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
        <p class="board-editor-muted">Publish this board to your viberails.ai account. Cards, comments, agent notes and history sync about every minute while VibeRails is open. The last field change received by the server wins; earlier changes remain in History. Lane names, colours and order come from this machine. Attachments, linked commits, sessions and Automation settings stay local. File references travel as text, without file contents. Web card creation and lane moves run your local lane Automations. Pausing sync keeps the published copy on your account.</p>
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

// The Publish switch and "Sync now" (VB-51). Not mountSettings: there is no revision to save, each
// action returns the fresh status, and the switch itself is the action. `confirm` is the consent
// dialog before switching publishing on; tests inject a stand-in for the DOM overlay.
export function mountBoardSync(app, element, boardId, { confirm = confirmDialog } = {}) {
    if (!element) return () => {};
    const abort = new AbortController();
    let disposed = false;
    let busy = false;
    const content = element.querySelector('[data-board-settings-content]');
    const alive = () => !disposed && element.isConnected !== false;
    const render = status => {
        const stamp = status.lastSyncUtc ? new Date(status.lastSyncUtc).toLocaleString() : 'never';
        const label = status.enabled ? 'Published · syncs every 60 seconds'
            : status.published ? 'Sync paused' : 'Publish to viberails.ai';
        content.innerHTML = `
            ${status.configured || status.published ? '' : '<p class="board-editor-muted" data-board-sync-unconfigured>Add your viberails.ai API key in Settings to publish this board.</p>'}
            <div class="form-check form-switch mb-2">
                <input class="form-check-input" type="checkbox" role="switch" id="board-sync-enabled"
                    data-board-sync-action="${status.enabled ? 'unpublish' : 'publish'}" ${status.enabled ? 'checked' : ''} ${status.configured || status.enabled ? '' : 'disabled'}>
                <label class="form-check-label" for="board-sync-enabled">${label}</label>
            </div>
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
                so later changes keep syncing. They stay on viberails.ai; a newer VibeRails may be able to apply them.</p>
                <ul class="mb-1">${(status.skippedEntries || []).map(entry => `<li>${escapeHtml(entry.cardKey)} · ${escapeHtml(entry.kind)} · ${escapeHtml(entry.reason)} · <code>${escapeHtml(entry.entryId)}</code></li>`).join('')}</ul>
                ${status.skipped > (status.skippedEntries || []).length ? '<p class="mb-0">Showing the latest 50 skipped changes.</p>' : ''}
                </div>` : ''}
            ${status.enabled ? '<button type="button" class="btn btn-sm btn-outline-secondary" data-board-sync-action="now">Sync now</button>' : ''}`;
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
        // A click while an action is in flight must not flip the switch away from the server state.
        if (busy) { event.preventDefault(); return; }
        const action = control.dataset.boardSyncAction;
        // Switching on uploads the board at once, so the switch stays off until the dialog is
        // confirmed; the status render after success paints it on. Pausing asks nothing.
        if (action === 'publish') event.preventDefault();
        busy = true;
        control.disabled = true;
        try {
            if (action === 'publish' && !await confirm({
                title: 'Publish this board to viberails.ai?',
                message: PUBLISH_CONSENT,
                confirmLabel: 'Publish'
            })) {
                control.disabled = false;
                return;
            }
            const status = action === 'now'
                ? await BoardApi.syncBoardNowAsync(boardId)
                : await BoardApi.setBoardSyncAsync(boardId, action === 'publish');
            if (!alive()) return;
            render(status);
            const message = action === 'publish' ? 'Board published to viberails.ai.'
                : action === 'now' ? (status.lastError || status.rejected ? 'Sync finished with entries needing attention.' : 'Board synced.')
                : 'Sync switched off. Switch it back on to resume where it stopped.';
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
