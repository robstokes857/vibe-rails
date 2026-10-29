import { escapeHtml } from './utils.js';
import { BoardApi } from './board-api.js';
import { boardTracker } from './board-brand.js';
import { jobLaunchKind } from './launch-kind.js';

// A dedicated board's tracker connection sits under General; a local board offers each as a tab.
const TRACKER_TABS = [['jira', 'Jira Cloud'], ['ado', 'Azure DevOps']];
const SECTION_ORDER = ['general', ...TRACKER_TABS.map(([id]) => id), 'context', 'history'];

const navigation = (name, sections) => `
    <nav class="nav nav-pills board-settings-nav gap-2 mb-3" aria-label="${name}" role="tablist">
        ${sections.map(([id, label]) => `
            <button type="button" class="nav-link${id === 'general' ? ' active' : ''}" role="tab"
                id="board-settings-tab-${id}" aria-controls="board-settings-${id}" aria-selected="${id === 'general'}"
                tabindex="${id === 'general' ? 0 : -1}" data-board-settings-tab="${id}">${label}</button>`).join('')}
    </nav>`;

export const boardSettingsNavigation = () =>
    navigation('Board settings', [['general', 'General'], ...TRACKER_TABS, ['context', 'Context'], ['history', 'History']]);

/** The New board dialog: a local board (the General section), or one connected to a tracker. */
export const newBoardNavigation = () => navigation('Board type', [['general', 'Local'], ...TRACKER_TABS]);

/**
 * Switch sections without replacing their DOM, so connection and context drafts survive.
 * onSelect(tab, tracker) names the tracker whose connection is now visible ('jira', 'ado' or null).
 */
export function mountBoardSettingsNavigation(editor, onSelect = () => {}) {
    const abort = new AbortController();
    const allTabs = [...editor.querySelectorAll('[data-board-settings-tab]')];
    let tabs = allTabs;
    const tabFor = id => allTabs.find(tab => tab.dataset.boardSettingsTab === id);
    const generalTab = tabFor('general');
    const generalPanel = editor.querySelector('[data-board-settings-panel="general"]');
    const trackers = new Map(TRACKER_TABS.map(([id, label]) =>
        [id, { tab: tabFor(id), panel: editor.querySelector(`[data-board-settings-panel="${id}"]`), label }]));
    let combined = null;
    const select = tab => {
        for (const item of allTabs) {
            const active = item === tab;
            item.classList.toggle('active', active);
            item.setAttribute('aria-selected', String(active));
            item.tabIndex = active ? 0 : -1;
        }
        const id = tab.dataset.boardSettingsTab;
        for (const panel of editor.querySelectorAll('[data-board-settings-panel]')) {
            panel.hidden = panel.dataset.boardSettingsPanel !== id;
        }
        onSelect(id, trackers.has(id) ? id : combined && id === 'general' ? combined : null);
    };
    for (const tab of allTabs) {
        tab.addEventListener('click', () => select(tab), { signal: abort.signal });
        tab.addEventListener('keydown', event => {
            const index = tabs.indexOf(tab);
            const target = event.key === 'ArrowRight' ? tabs[(index + 1) % tabs.length]
                : event.key === 'ArrowLeft' ? tabs[(index + tabs.length - 1) % tabs.length]
                    : event.key === 'Home' ? tabs[0] : event.key === 'End' ? tabs.at(-1) : null;
            if (!target) return;
            event.preventDefault();
            select(target);
            target.focus();
        }, { signal: abort.signal });
    }
    // A separated panel goes back before the next section in tab order.
    const place = (id, panel) => {
        const next = SECTION_ORDER.slice(SECTION_ORDER.indexOf(id) + 1)
            .map(item => editor.querySelector(`[data-board-settings-panel="${item}"]`)).find(Boolean);
        if (next) next.before(panel);
        else generalPanel.after(panel);
    };
    return {
        dispose: () => abort.abort(),
        /**
         * Follow the board's provider ('jira', 'ado' or null). A dedicated tracker board shows its
         * connection under General and no tracker tabs; a local board offers each tracker as a tab.
         * The existing controls move, so a delayed status response cannot erase any draft.
         */
        followProvider(provider) {
            const next = trackers.has(provider) ? provider : null;
            if (next === combined || abort.signal.aborted || editor.isConnected === false) return;
            const activeTab = tabs.find(tab => tab.classList.contains('active')) || generalTab;
            const focused = globalThis.document?.activeElement;
            let target = activeTab;
            let restoreFocus = null;
            if (combined) {
                const { tab, panel } = trackers.get(combined);
                if (panel.contains(focused)) restoreFocus = focused;
                panel.dataset.boardSettingsPanel = combined;
                panel.setAttribute('role', 'tabpanel');
                panel.setAttribute('aria-labelledby', tab.id);
                panel.removeAttribute('aria-label');
                panel.classList.remove('board-settings-tracker-combined');
                place(combined, panel);
                // Keep the unlink result visible while retaining every other settings draft.
                if (activeTab === generalTab) target = tab;
            }
            combined = next;
            if (next) {
                const { panel, label } = trackers.get(next);
                if (panel.contains(focused)) restoreFocus = focused;
                panel.removeAttribute('data-board-settings-panel');
                panel.removeAttribute('role');
                panel.removeAttribute('aria-labelledby');
                panel.setAttribute('aria-label', label);
                panel.hidden = false;
                panel.classList.add('board-settings-tracker-combined');
                generalPanel.append(panel);
                if (trackers.has(target.dataset.boardSettingsTab)) target = generalTab;
            }
            for (const { tab } of trackers.values()) {
                tab.hidden = Boolean(next);
                if (next && focused === tab) restoreFocus = generalTab;
            }
            tabs = allTabs.filter(tab => !tab.hidden);
            select(target);
            restoreFocus?.focus({ preventScroll: true });
        }
    };
}

const TYPES = [
    ['task', 'Task'], ['bug', 'Bug'], ['feature', 'Feature'],
    ['research-spike', 'Research spike'], ['chore', 'Chore / tech debt']
];

// The user's context levels, broadest first (VIBE-229). A launch gets every level that applies to
// it in one labelled block. App and Board context follow the launch's Vibe Board nudge.
const NUDGE_NOTE = 'Sent only when the launch\'s Vibe Board nudge is on in Settings (built-in CLIs or Quick Launches).';
export const CONTEXT_LEVELS = [
    ['app', 'App context', `Every LLM CLI launched from VibeRails: card agents, the Agents page, Automations, continuations and launches with no board, in any project. ${NUDGE_NOTE}`],
    ['board', 'Board context', `Every LLM CLI launched for this board: card agents, Agents, Automations and continuations. Launches outside a card use the currently selected board. ${NUDGE_NOTE}`],
    ['card', 'Card context', 'Every agent opened from a card on this board, for work or discussion. Always sent.'],
    ['type', 'Card type context', 'Agents opened from a card of that type. Keep the card context, replace it, or send both (card context first). Always sent.']
];

const levelHeader = id => {
    const [, label, reach] = CONTEXT_LEVELS.find(([level]) => level === id);
    return `<h6 class="mb-1">${label}</h6><p class="board-editor-muted mb-2" data-context-reach>${reach}</p>`;
};

const levelSection = (id, body) => `<section class="mb-3" data-context-level="${id}">${levelHeader(id)}${body}</section>`;

/**
 * One editor for every context level, broadest first. Settings shows App context alone
 * ({ board: false }); a board's settings show all four, so the whole chain an agent from that board
 * receives is visible in one place. Card and Card type context are one saved record.
 */
export const contextEditorSection = ({ board = true } = {}) => `
    <div data-context-editor>
        <p class="board-editor-muted">A launch gets every level that applies to it, broadest first, in one labelled block. Empty levels send nothing. Each level can contain up to 4,000 characters.</p>
        <div data-context-unit="app">${levelSection('app', '<div data-board-settings-content>Loading context…</div>')}</div>
        ${board ? `<div data-context-unit="board">${levelSection('board', '<div data-board-settings-content>Loading context…</div>')}</div>
        <div data-context-unit="card"><div data-board-settings-content>Loading context…</div></div>` : ''}
    </div>`;

export const laneAutomationSection = () => `
    <section class="mt-3 border-top pt-3" data-lane-automation>
        <h6>Agents and Automations</h6>
        <p class="board-editor-muted">Run the selected Agents and Automations after a card enters this lane and stays for 60 seconds. They run in the order shown, each waiting for the preceding step to pass or be skipped. Moving again restarts the wait. Reordering or editing a card in the same lane does not trigger them.</p>
        <div data-board-settings-content>Loading…</div>
        <p class="board-editor-muted mt-2">Runs while a VibeRails backend is open. Busy entries wait for their turn. Saving cancels pending entries for this lane and applies to future entries; it does not launch anything. The lane's panel shows each entry's purpose, output and reviewer choices. Removing a selection keeps the shared Agent or Automation. You define what each lane, including Done, means.</p>
    </section>`;

export const boardSyncSection = () => `
    <section class="mt-3 border-top pt-3" data-board-sync hidden>
        <h6>viberails.ai</h6>
        <p class="board-editor-muted">Save the board name above to use it as the hosted name. The hosted Boards list also shows this repository and computer.</p>
        <div data-board-sync-content>Loading sync status…</div>
    </section>`;

export function mountBoardSync(app, element, boardId, onStatus = () => {}) {
    if (!element) return () => {};
    const abort = new AbortController();
    const content = element.querySelector('[data-board-sync-content]');
    let busy = false;
    let disposed = false;
    const alive = () => !disposed && element.isConnected !== false;
    const render = status => {
        onStatus(status);
        // A dedicated Jira or Azure DevOps board syncs with its tracker, never with viberails.ai.
        const tracker = boardTracker(status);
        element.hidden = Boolean(tracker);
        if (tracker) {
            content.innerHTML = '';
            return;
        }
        // A copy of another account's shared board never syncs (VIBE-235); its data stays here.
        if (status.shared) {
            content.innerHTML = `
                <p class="board-editor-muted mb-2" data-board-sync-shared>This board was shared with you and is read-only. It no longer syncs, and changes here are not sent. View the current board on viberails.ai.</p>
                ${status.remoteUrl ? `<a href="${escapeHtml(status.remoteUrl)}" target="_blank" rel="noopener noreferrer">Open on viberails.ai</a>` : ''}`;
            return;
        }
        content.innerHTML = `
            <label class="form-check mb-2">
                <input class="form-check-input" type="checkbox" data-board-sync-enabled ${status.enabled ? 'checked' : ''}>
                <span class="form-check-label">Sync this board with viberails.ai</span>
            </label>
            <p class="board-editor-muted mb-2">${status.enabled
                ? status.configured ? 'This board syncs while a VibeRails backend is open.' : 'Sign in to your account in Settings to start syncing.'
                : 'This board stays local. A previously hosted copy remains on viberails.ai until you delete it there.'}</p>
            ${status.lastError ? `<p class="text-warning" role="alert">${escapeHtml(status.lastError)}</p>` : ''}
            ${status.remoteUrl ? `<a href="${escapeHtml(status.remoteUrl)}" target="_blank" rel="noopener noreferrer">Open hosted board</a>` : ''}
            ${status.enabled && status.configured ? '<button type="button" class="btn btn-sm btn-outline-secondary ms-2" data-board-sync-now>Sync now</button>' : ''}`;
    };
    const reload = async () => {
        try {
            const status = await BoardApi.getBoardSyncAsync(boardId, { signal: abort.signal });
            if (alive()) render(status);
        } catch (error) {
            if (alive() && error?.name !== 'AbortError') {
                element.hidden = false;
                content.textContent = error?.message || 'Sync status could not be loaded.';
            }
        }
    };
    const change = async event => {
        if (!event.target.matches('[data-board-sync-enabled]') || busy) return;
        busy = true;
        const enabled = event.target.checked;
        event.target.disabled = true;
        try {
            const status = await BoardApi.setBoardSyncAsync(boardId, enabled);
            if (alive()) { render(status); app.showToast('Board', enabled ? 'Board sync enabled.' : 'Board sync disabled.', 'success'); }
        } catch (error) {
            if (alive()) app.showToast('Board', error?.message || 'Sync setting could not be saved.', 'error');
            // Publication can fail after the choice was saved. Read the stored setting again
            // instead of assuming the request rolled it back.
            await reload();
        } finally {
            busy = false;
            if (alive()) event.target.disabled = false;
        }
    };
    const click = async event => {
        if (!event.target.closest('[data-board-sync-now]') || busy) return;
        busy = true;
        event.target.disabled = true;
        try {
            const status = await BoardApi.syncBoardNowAsync(boardId);
            if (alive()) { render(status); app.showToast('Board', status.lastError || 'Board synced.', status.lastError ? 'warning' : 'success'); }
        } catch (error) {
            if (alive()) app.showToast('Board', error?.message || 'Sync failed.', 'error');
        } finally { busy = false; if (alive()) event.target.disabled = false; }
    };
    element.addEventListener('change', change);
    element.addEventListener('click', click);
    void reload();
    return () => { disposed = true; abort.abort(); element.removeEventListener('change', change); element.removeEventListener('click', click); };
}

// Each mount owns its request and DOM. Replacing/closing a modal cannot populate a newer one.
function mountSettings(app, element, { load, render, read, save, savedMessage, onSaved, toastTitle = 'Board' }) {
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
            onSaved?.(result);
            app.showToast(toastTitle, savedMessage, 'success');
        } catch (error) {
            if (alive()) app.showToast(toastTitle, error?.message || 'Settings could not be saved.', 'error');
        } finally {
            busy = false;
            if (alive()) button.disabled = false;
        }
    };
    element.addEventListener('click', click);
    void reload();
    return () => { disposed = true; abort.abort(); element.removeEventListener('click', click); };
}

// Labels need ids that stay unique when Settings and a board's settings are both in the document.
let contextEditorIds = 0;

/** App context: above every board and project, so it takes no board id. */
export function mountAppContext(app, element, _boardId = null, { toastTitle = 'Board' } = {}) {
    const id = `app-context-message-${++contextEditorIds}`;
    return mountSettings(app, element, {
        load: extra => BoardApi.getAppContextAsync(extra),
        render: ({ message }) => `
            <label class="visually-hidden" for="${id}">App context</label>
            <textarea id="${id}" class="form-control form-control-sm mb-2" rows="5" maxlength="4000" placeholder="Context for every LLM launched from VibeRails…" spellcheck="true" data-context-message="app">${escapeHtml(message || '')}</textarea>
            <button type="button" class="btn btn-sm btn-outline-primary" data-settings-save>Save App context</button>`,
        read: root => ({ message: root.querySelector('[data-context-message="app"]').value }),
        save: payload => BoardApi.saveAppContextAsync(payload),
        savedMessage: 'App context saved for every launch.',
        toastTitle
    });
}

/** Board context, stored as the board's Every Context message. */
export function mountBoardLevelContext(app, element, boardId) {
    const id = `board-level-context-message-${++contextEditorIds}`;
    return mountSettings(app, element, {
        load: extra => BoardApi.getBoardEveryContextAsync(boardId, extra),
        render: ({ message }) => `
            <label class="visually-hidden" for="${id}">Board context</label>
            <textarea id="${id}" class="form-control form-control-sm mb-2" rows="5" maxlength="4000" placeholder="Context for every LLM launched for this board…" spellcheck="true" data-context-message="board">${escapeHtml(message || '')}</textarea>
            <button type="button" class="btn btn-sm btn-outline-primary" data-settings-save>Save Board context</button>`,
        read: root => ({ message: root.querySelector('[data-context-message="board"]').value }),
        save: payload => BoardApi.saveBoardEveryContextAsync(boardId, payload),
        savedMessage: 'Board context saved for this board.'
    });
}

/** Card context and Card type context: the board's Agent context record, saved together. */
export function mountCardContext(app, element, boardId) {
    const id = `card-context-message-${++contextEditorIds}`;
    return mountSettings(app, element, {
        load: extra => BoardApi.getBoardContextAsync(boardId, extra),
        render: ({ context }) => `
            ${levelSection('card', `
                <label class="visually-hidden" for="${id}">Card context</label>
                <textarea id="${id}" class="form-control form-control-sm" rows="4" maxlength="4000" placeholder="Context for every card agent on this board…" spellcheck="true" data-context-card>${escapeHtml(context.defaultMessage)}</textarea>`)}
            ${levelSection('type', TYPES.map(([type, label]) => {
                const override = context.typeOverrides.find(item => item.type === type);
                const mode = override?.mode || 'default';
                return `<details class="mb-2"><summary>${label}</summary>
                    <label class="board-editor-label mt-2" for="${id}-mode-${type}">${label} cards</label>
                    <select id="${id}-mode-${type}" class="form-select form-select-sm mb-2" data-context-mode="${type}">
                        ${[['default', 'Card context only'], ['replace', 'Type-specific only'], ['append', 'Card context + type-specific']].map(([value, text]) => `<option value="${value}" ${value === mode ? 'selected' : ''}>${text}</option>`).join('')}
                    </select>
                    <textarea class="form-control form-control-sm" rows="3" maxlength="4000" data-context-message="${type}" aria-label="${label} card type context" spellcheck="true">${escapeHtml(override?.message || '')}</textarea>
                </details>`;
            }).join('') + '<p class="board-editor-muted">An empty type-specific-only message sends no Card or Card type context.</p>')}
            <button type="button" class="btn btn-sm btn-outline-primary" data-settings-save>Save Card and Card type context</button>`,
        read: root => ({ context: {
            defaultMessage: root.querySelector('[data-context-card]').value,
            typeOverrides: TYPES.map(([type]) => ({ type,
                mode: root.querySelector(`[data-context-mode="${type}"]`).value,
                message: root.querySelector(`[data-context-message="${type}"]`).value
            }))
        } }),
        save: payload => BoardApi.saveBoardContextAsync(boardId, payload),
        savedMessage: 'Card and Card type context saved.'
    });
}

/**
 * Mounts every unit {@link contextEditorSection} rendered. Each level keeps its own revision, so
 * saving one never overwrites another's draft. Returns one dispose for all of them.
 */
export function mountContextEditor(app, root, { boardId = null, toastTitle = 'Board' } = {}) {
    const unit = name => root?.querySelector(`[data-context-unit="${name}"]`);
    const disposers = [mountAppContext(app, unit('app'), null, { toastTitle })];
    if (boardId) disposers.push(mountBoardLevelContext(app, unit('board'), boardId), mountCardContext(app, unit('card'), boardId));
    return () => disposers.forEach(dispose => dispose());
}

// Automatic account sync status and an immediate retry.
export function mountLaneAutomation(app, element, columnId, onSaved) {
    return mountSettings(app, element, {
        // The lane settings carry id/name/enabled; the project catalog says which entries are
        // Agents. Losing the catalog only loses the grouping.
        load: async extra => {
            const projectPath = app.data?.configs?.rootPath || '';
            const [lane, catalog] = await Promise.all([
                BoardApi.getLaneAutomationAsync(columnId, extra),
                projectPath
                    ? app.apiCall(`/api/v1/jobs?projectPath=${encodeURIComponent(projectPath)}`, 'GET', null, { showLoading: false, ...extra }).catch(() => null)
                    : Promise.resolve(null)
            ]);
            return { ...lane, kinds: new Map((catalog?.jobs || []).map(job => [job.id, jobLaunchKind(job)])) };
        },
        render: ({ jobs, jobIds, jobId, starterSetupPending, kinds }) => {
            const selected = new Set(jobIds ?? (jobId ? [jobId] : []));
            const choices = [...[...selected].map(id => jobs.find(job => job.id === id)
                || { id, name: `Unavailable entry (${id})`, enabled: false }), ...jobs.filter(job => !selected.has(job.id))];
            const row = job => `<label class="d-flex align-items-start gap-2 mb-2">
                    <input type="checkbox" class="form-check-input flex-shrink-0" data-lane-automation-job value="${Number(job.id)}" ${selected.has(job.id) ? 'checked' : ''} ${!job.enabled && !selected.has(job.id) ? 'disabled' : ''}>
                    <span class="text-break">${escapeHtml(job.name)}${job.enabled ? '' : ' (disabled)'}</span>
                </label>`;
            const agents = choices.filter(job => kinds?.get(job.id) === 'agent');
            const automations = choices.filter(job => kinds?.get(job.id) !== 'agent');
            return `${starterSetupPending ? '<p role="status">Starter review setup is pending. Reopen to retry, or save your lane selection to cancel the default.</p>' : ''}
            <p class="board-editor-muted">Select any number, or clear all so nothing runs on entry. Remove unavailable or disabled selections before saving.</p>
            ${agents.length ? `<fieldset class="mb-2"><legend class="board-editor-label">Agents on entry</legend>${agents.map(row).join('')}</fieldset>` : ''}
            ${automations.length ? `<fieldset class="mb-2"><legend class="board-editor-label">Automations on entry</legend>${automations.map(row).join('')}</fieldset>` : ''}
            ${jobs.length ? '' : '<p class="board-editor-muted">Create an Agent or Automation for this project first.</p>'}
            <button type="button" class="btn btn-sm btn-outline-primary" data-settings-save>Save lane settings</button>`;
        },
        read: root => ({ jobIds: [...root.querySelectorAll('[data-lane-automation-job]:checked')].map(input => Number(input.value)) }),
        save: payload => BoardApi.saveLaneAutomationAsync(columnId, payload),
        savedMessage: 'Lane settings saved.',
        onSaved
    });
}
