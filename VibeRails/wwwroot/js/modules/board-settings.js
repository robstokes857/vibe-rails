import { mountLaunchLane } from './launch-lane-editor.js';
import { escapeHtml } from './utils.js';
import { BoardApi } from './board-api.js';
import { boardTracker } from './board-brand.js';
import { jobLaunchKind } from './launch-kind.js';
import { mountLlmPicker } from './pickers/llm-picker.js';
import { renderBoardLaunchOptions, bindBoardLaunchOptions, readBoardLaunchOptions, waitForBoardLaunchDefaults } from './board-launch-options.js';
import { openProUpgrade, proBadge, requirePro, showProRequired } from './pro-upgrade.js';

// A dedicated board's tracker connection sits under General; a local board offers each as a tab.
const TRACKER_TABS = [['jira', 'Jira Cloud'], ['ado', 'Azure DevOps']];
const SECTION_ORDER = ['general', ...TRACKER_TABS.map(([id]) => id), 'manager', 'context', 'history'];

const navigation = (name, sections) => `
    <nav class="nav nav-pills board-settings-nav gap-2 mb-3" aria-label="${name}" role="tablist">
        ${sections.map(([id, label]) => `
            <button type="button" class="nav-link${id === 'general' ? ' active' : ''}" role="tab"
                id="board-settings-tab-${id}" aria-controls="board-settings-${id}" aria-selected="${id === 'general'}"
                tabindex="${id === 'general' ? 0 : -1}" data-board-settings-tab="${id}">${label}</button>`).join('')}
    </nav>`;

// The Manager tab exists only while the AI board manager setting is on.
export const boardSettingsNavigation = ({ manager = false } = {}) =>
    navigation('Board settings', [['general', 'General'], ...TRACKER_TABS, ...(manager ? [['manager', 'Manager']] : []),
        ['context', 'Context'], ['history', 'History']]);

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
const NUDGE_NOTE = 'Sent when the launch’s Vibe Board Nudge is enabled.';
export const CONTEXT_LEVELS = [
    ['app', 'App context', `Applies across all repos and when no repo is open. ${NUDGE_NOTE}`],
    ['project', 'Project context', 'Sent to every LLM launched from this repo, including launches outside the Board and with the Nudge off.'],
    ['board', 'Board context', `Applies to launches for this board, including launches outside cards. ${NUDGE_NOTE}`],
    ['card', 'Card context', 'Every agent opened from a card on this board, for work or discussion. Always sent.'],
    ['type', 'Card type context', 'Agents opened from a card of that type. Keep the card context, replace it, or send both (card context first). Always sent.']
];

const contextDisclosure = (id, body) => {
    const [, label, reach] = CONTEXT_LEVELS.find(([level]) => level === id);
    return `<details class="mb-3" data-context-level="${id}"${id === 'app' ? ' open' : ''}>
        <summary>${label}</summary><p class="form-text mt-2 mb-2" data-context-reach>${reach}</p>${body}
    </details>`;
};

const contextLoading = '<div data-board-settings-content><p role="status">Loading context…</p></div>';
const boardContextUnits = () => `
    <div data-context-unit="board">${contextDisclosure('board', contextLoading)}</div>
    <div data-context-unit="card">${contextLoading}</div>`;

/** The Context workspace displays every scope and saves them together. */
export const contextEditorSection = ({ project = false } = {}) => `
    <div data-context-editor>
        <p class="form-text mt-0">Launches receive the applicable instructions from broadest to most specific. Each message can contain up to 4,000 characters.</p>
        ${contextDisclosure('app', `<div data-context-unit="app">${contextLoading}</div><div data-context-unit="nudge">${contextLoading}</div>`)}
        ${contextDisclosure('project', project ? `<div data-context-unit="project">${contextLoading}</div>`
            : '<p class="form-text">Open a Git repository to edit Project context.</p>')}
        <div class="border-top pt-3 mb-3">
            <label class="form-label" for="context-board-picker">Board to edit</label>
            <select class="form-select form-select-sm" id="context-board-picker" aria-describedby="context-board-help" data-context-board-picker disabled>
                <option value="">Loading boards…</option>
            </select>
            <p class="form-text" id="context-board-help">Launches outside a card use the currently selected board in the Board view.</p>
            <p class="form-text mb-0" data-context-boards-status aria-live="polite"></p>
            <button type="button" class="btn btn-sm btn-outline-secondary mt-2" data-context-boards-retry hidden>Retry</button>
        </div>
        <div data-context-board-units></div>
        <button type="button" class="btn btn-sm btn-primary" data-context-save disabled>Save context</button>
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
        // The hosted server paused this board's sync for Vibe Rails Pro. Only the account that pays
        // is offered the upgrade; an invited member waits for the Board's owner.
        const paused = status.pausedCode !== 'pro_required' ? '' : `<p class="board-editor-muted mb-2" data-board-sync-paused>${
            status.pausedPayer === 'board_owner'
                ? 'Sync is paused until this Board\'s owner has Vibe Rails Pro. You do not need to upgrade, and nothing was deleted.'
                : 'Sync is paused because remote Boards need Vibe Rails Pro. This board stays on this computer, and nothing was deleted.'}
            ${status.pausedPayer === 'account' ? '<button type="button" class="btn btn-sm btn-outline-primary ms-2" data-board-sync-upgrade>Upgrade</button>' : ''}</p>`;
        content.innerHTML = `
            <p class="board-editor-muted mb-2">${!status.configured
                ? 'Sign in to your account in Settings to start syncing automatically.'
                : status.enabled
                    ? 'This board syncs automatically while VibeRails is open.'
                    : 'Sync is paused for this board. Use Sync now to resume.'} ${proBadge(app, 'remote_boards')}</p>
            ${status.lastError && !paused ? `<p class="text-warning" role="alert">${escapeHtml(status.lastError)}</p>` : ''}
            ${paused}
            ${status.remoteUrl ? `<a href="${escapeHtml(status.remoteUrl)}" target="_blank" rel="noopener noreferrer">Open hosted board</a>` : ''}
            ${status.configured ? '<button type="button" class="btn btn-sm btn-outline-secondary ms-2" data-board-sync-now>Sync now</button>' : ''}`;
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
    const click = async event => {
        // The prompt opens only from this button, never from a status read.
        if (event.target.closest('[data-board-sync-upgrade]')) { openProUpgrade(app, 'remote_boards'); return; }
        if (!event.target.closest('[data-board-sync-now]') || busy || !requirePro(app, 'remote_boards')) return;
        busy = true;
        event.target.disabled = true;
        try {
            const status = await BoardApi.syncBoardNowAsync(boardId);
            if (alive()) { render(status); app.showToast('Board', status.lastError || 'Board synced.', status.lastError ? 'warning' : 'success'); }
        } catch (error) {
            if (alive() && !showProRequired(app, 'remote_boards', error)) app.showToast('Board', error?.message || 'Sync failed.', 'error');
            // Resuming may save the state before publication fails.
            await reload();
        } finally { busy = false; if (alive()) event.target.disabled = false; }
    };
    element.addEventListener('click', click);
    void reload();
    return () => { disposed = true; abort.abort(); element.removeEventListener('click', click); };
}

// Each mount owns its request and DOM. Replacing/closing a modal cannot populate a newer one.
// afterRender(element, result) may mount widgets on the fresh markup and return their disposer;
// clear(element) is the payload for a [data-settings-clear] button; reloadAfterSave re-renders
// from the saved result so widgets show what was stored.
// saveGroup coordinates one footer across independently revisioned records. Its snapshot is
// the submitted draft, so edits made during a save remain dirty for the next one.
function mountSettings(app, element, { load, render, read, save, savedMessage, onSaved, afterRender, beforeSave, clear, reloadAfterSave = false, toastTitle = 'Board', saveGroup }) {
    if (!element) return () => {};
    const abort = new AbortController();
    let disposed = false;
    let revision = null;
    let busy = false;
    let savedDraft = null;
    let disposeWidgets = () => {};
    const content = element.querySelector('[data-board-settings-content]');
    const alive = () => !disposed && element.isConnected !== false;
    const show = result => {
        disposeWidgets();
        disposeWidgets = () => {};
        content.innerHTML = render(result);
        element.querySelectorAll('[data-context-mode]').forEach(select => {
            const message = element.querySelector(`[data-context-message="${select.dataset.contextMode}"]`);
            const sync = () => { message.hidden = select.value === 'default'; };
            select.addEventListener('change', sync);
            sync();
        });
        disposeWidgets = afterRender?.(element, result) || (() => {});
        if (saveGroup) {
            savedDraft = JSON.stringify(read(element));
        }
        revision = result.revision;
        saveGroup?.refresh();
    };
    async function reload() {
        try {
            const result = await load({ signal: abort.signal });
            if (!alive()) return;
            show(result);
        } catch (error) {
            if (!alive() || error?.name === 'AbortError') return;
            content.innerHTML = `<p role="alert">${escapeHtml(error?.message || 'Settings could not be loaded.')}</p><button type="button" class="btn btn-sm btn-outline-secondary" data-settings-retry>Retry</button>`;
        }
    }
    const persist = async (clearing = false) => {
        if (!alive() || busy || revision === null) return false;
        busy = true;
        try {
            const waiting = !clearing && beforeSave?.(element);
            if (waiting) await waiting;
            if (!alive()) return false;
            const draft = clearing ? clear(element) : read(element);
            const serialized = JSON.stringify(draft);
            if (saveGroup && serialized === savedDraft) return true;
            const result = await save({ ...draft, expectedRevision: revision });
            if (!alive()) return false;
            if (reloadAfterSave) show(result);
            else revision = result.revision;
            savedDraft = serialized;
            onSaved?.(result);
            if (!saveGroup) app.showToast(toastTitle, savedMessage, 'success');
            return true;
        } catch (error) {
            if (alive()) app.showToast(toastTitle, error?.message || 'Settings could not be saved.', 'error');
            return false;
        } finally {
            busy = false;
        }
    };
    const click = async event => {
        if (event.target.closest('[data-settings-retry]')) { void reload(); return; }
        const clearing = clear && event.target.closest('[data-settings-clear]');
        const button = clearing || event.target.closest('[data-settings-save]');
        if (!button || busy || revision === null) return;
        button.disabled = true;
        try { await persist(Boolean(clearing)); }
        finally {
            if (alive() && button.isConnected !== false) button.disabled = false;
        }
    };
    const unregister = saveGroup?.register({
        ready: () => alive() && revision !== null,
        dirty: () => alive() && revision !== null && JSON.stringify(read(element)) !== savedDraft,
        save: () => persist()
    }, element.dataset?.contextUnit);
    element.addEventListener('click', click);
    void reload();
    return () => { disposed = true; unregister?.(); abort.abort(); disposeWidgets(); element.removeEventListener('click', click); };
}

export const boardManagerSection = () => `
    <section class="mt-3 border-top pt-3" data-board-manager-settings>
        <h6>Board manager</h6>
        <p class="board-editor-muted">One AI manager agent for the whole board. It runs every card whose workflow is Agent and that has no manager of its own: it assigns each card's work to a worker session, waits for board events instead of polling, checks handoffs and reviews, and moves cards through your lanes. Start, open or stop it from the board header. Cards you give their own manager keep it.</p>
        <div data-board-settings-content>Loading manager…</div>
    </section>`;

/** The most standing-order characters; the server enforces the same limit. */
export const MANAGER_INSTRUCTIONS_LIMIT = 4000;
/** The most implementation workers a board manager may run at once. */
export const MANAGER_WORKERS_LIMIT = 4;

const managerStatusLine = status => {
    const cards = status.managedCards === 1 ? '1 Agent card' : `${status.managedCards || 0} Agent cards`;
    const own = status.ownManagerCards ? ` ${status.ownManagerCards} ${status.ownManagerCards === 1 ? 'card keeps its' : 'cards keep their'} own manager.` : '';
    if (!status.selection) return `No board manager yet. Pick one below; it will manage ${cards} on this board.${own}`;
    return `${status.active ? `Running since ${new Date(status.active.startedAt).toLocaleString()}` : 'Not running'} · manages ${cards}.${own}`;
};

/**
 * The Manager tab: the shared LLM picker and base launch options, how many implementation workers
 * may run at once, and the manager's standing instructions, saved together under the board's
 * manager revision. picker is injectable so node tests can stand in for the Tom Select mount.
 */
export function mountBoardManager(app, element, boardId, { onSaved, picker = mountLlmPicker } = {}) {
    return mountSettings(app, element, {
        load: extra => BoardApi.getBoardManagerAsync(boardId, extra),
        render: status => {
            const workers = Math.min(MANAGER_WORKERS_LIMIT, Math.max(1, Number(status.maxWorkers) || 1));
            return `
            <p class="board-editor-muted mb-2" data-board-manager-status>${escapeHtml(managerStatusLine(status))}</p>
            <label class="board-editor-label" for="board-manager-selection">Manager agent</label>
            <select class="form-select form-select-sm" id="board-manager-selection" aria-label="Board manager agent"></select>
            <div class="mt-2" data-board-manager-options></div>
            <label class="board-editor-label mt-3" for="board-manager-workers">Workers at a time</label>
            <select class="form-select form-select-sm" id="board-manager-workers" data-board-manager-workers aria-describedby="board-manager-workers-help">
                ${Array.from({ length: MANAGER_WORKERS_LIMIT }, (_, index) => index + 1).map(value =>
                    `<option value="${value}"${value === workers ? ' selected' : ''}>${value === 1 ? '1 (recommended for one checkout)' : value}</option>`).join('')}
            </select>
            <p class="board-editor-muted mt-1" id="board-manager-workers-help">Implementation workers share this checkout unless their environment uses a clone sandbox. VibeRails refuses a worker past this limit; reviews and planning workers do not count.</p>
            <label class="board-editor-label mt-3" for="board-manager-instructions">Manager instructions</label>
            <textarea class="form-control form-control-sm" id="board-manager-instructions" data-board-manager-instructions rows="5" maxlength="${MANAGER_INSTRUCTIONS_LIMIT}" spellcheck="true"
                placeholder="Standing orders for this board's manager, e.g. Bugs before features. Run the tests before Review. Never move a card to Done without a passing code review."
                aria-describedby="board-manager-instructions-help">${escapeHtml(status.instructions || '')}</textarea>
            <p class="board-editor-muted mt-1" id="board-manager-instructions-help">The manager follows these on every card it runs, within its role. Card and Card type context still reach each worker through its card.</p>
            <div class="d-flex flex-wrap gap-2 mt-3">
                <button type="button" class="btn btn-sm btn-outline-primary" data-settings-save>Save manager</button>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-settings-clear ${status.selection ? '' : 'disabled'}>Remove manager</button>
            </div>
            <p class="board-editor-muted mt-2">Choose a work agent or environment, not a code review one. A running manager keeps the settings it started with; stop and start it to apply changes.</p>`;
        },
        afterRender: (root, status) => {
            const select = root.querySelector('#board-manager-selection');
            const host = root.querySelector('[data-board-manager-options]');
            if (!select || !host) return () => {};
            const disposePicker = picker(app, select, {
                context: 'assignee', includeHidden: true,
                placeholder: 'Choose a manager agent…',
                selectedValue: status.selection || ''
            }) || (() => {});
            let disposeOptions = () => {};
            const renderOptions = (options, selection = select.value) => {
                disposeOptions();
                host.innerHTML = renderBoardLaunchOptions(selection, options, { idPrefix: 'board-manager-settings' });
                disposeOptions = bindBoardLaunchOptions(host, selection, { app, useDefaults: options == null }) || (() => {});
            };
            // The stored null also represents a saved all-default/YOLO-off selection.
            renderOptions(status.baseLlmOptions || {}, status.selection || '');
            const change = () => renderOptions();
            select.addEventListener('change', change);
            return () => { select.removeEventListener('change', change); disposeOptions(); disposePicker(); };
        },
        read: root => {
            const selection = root.querySelector('#board-manager-selection')?.value || '';
            const workers = Number(root.querySelector('[data-board-manager-workers]')?.value) || 1;
            return {
                selection,
                baseLlmOptions: selection ? readBoardLaunchOptions(root.querySelector('[data-board-manager-options]') || root, selection) : null,
                instructions: root.querySelector('[data-board-manager-instructions]')?.value ?? '',
                maxWorkers: Math.min(MANAGER_WORKERS_LIMIT, Math.max(1, workers))
            };
        },
        clear: () => ({ selection: '', baseLlmOptions: null }),
        save: payload => BoardApi.saveBoardManagerAsync(boardId, payload),
        beforeSave: waitForBoardLaunchDefaults,
        savedMessage: 'Board manager saved.',
        reloadAfterSave: true,
        onSaved
    });
}

// Labels need ids that stay unique when Settings and a board's settings are both in the document.
let contextEditorIds = 0;

/** App context: above every board and project, so it takes no board id. */
export function mountAppContext(app, element, _boardId = null, { toastTitle = 'Board', saveGroup } = {}) {
    const id = `app-context-message-${++contextEditorIds}`;
    return mountSettings(app, element, {
        load: extra => BoardApi.getAppContextAsync(extra),
        render: ({ message }) => `
            <label class="visually-hidden" for="${id}">App context</label>
            <textarea id="${id}" class="form-control form-control-sm mb-2" rows="5" maxlength="4000" placeholder="Context for every LLM launched from VibeRails…" spellcheck="true" data-context-message="app">${escapeHtml(message || '')}</textarea>`,
        read: root => ({ message: root.querySelector('[data-context-message="app"]').value }),
        save: payload => BoardApi.saveAppContextAsync(payload),
        savedMessage: 'App context saved for every launch.',
        toastTitle,
        saveGroup
    });
}

function mountProjectContext(app, element, saveGroup) {
    return mountSettings(app, element, {
        load: BoardApi.getProjectContextAsync,
        render: ({ message }) => `
            <label class="visually-hidden" for="project-context-message">Project context</label>
            <textarea id="project-context-message" class="form-control form-control-sm mb-2" rows="5" maxlength="4000" spellcheck="true" data-context-message="project">${escapeHtml(message || '')}</textarea>`,
        read: root => ({ message: root.querySelector('[data-context-message="project"]').value }),
        save: BoardApi.saveProjectContextAsync,
        toastTitle: 'Context',
        saveGroup
    });
}

function mountContextNudge(app, element, saveGroup) {
    const keys = ['createVibeStoryTracking', 'createVibeStoryTrackingCustomEnvs'];
    const values = settings => ({
        createVibeStoryTracking: settings.createVibeStoryTracking !== false,
        createVibeStoryTrackingCustomEnvs: settings.createVibeStoryTrackingCustomEnvs === true
    });
    let saved;
    return mountSettings(app, element, {
        load: async extra => {
            saved = values(await app.apiCall('/api/v1/settings', 'GET', null, { showLoading: false, ...extra }));
            return { ...saved, revision: 0 };
        },
        render: settings => `
            <fieldset class="border-top pt-3 mt-3">
                <legend class="form-label fs-6 mb-1">Vibe Board Nudge</legend>
                <p class="form-text mt-0">Includes App and Board context. When a launch has no starting message, it also asks the agent to consider tracking the work as a Vibe Story.</p>
                ${['Built-in CLIs and Agent presets', 'Legacy custom environments'].map((label, index) => `
                    <div class="form-check form-switch mb-2">
                        <input class="form-check-input" type="checkbox" id="context-nudge-${index}" ${settings[keys[index]] ? 'checked' : ''}>
                        <label class="form-check-label" for="context-nudge-${index}">${label}</label>
                    </div>`).join('')}
            </fieldset>`,
        read: root => Object.fromEntries(keys.map((key, index) => [key, root.querySelector(`#context-nudge-${index}`).checked])),
        save: async draft => {
            const changes = Object.fromEntries(keys.filter(key => draft[key] !== saved[key]).map(key => [key, draft[key]]));
            const result = await app.apiCall('/api/v1/settings/context-nudge', 'PUT', changes,
                { showLoading: false, preferErrorResponseMessage: true });
            saved = { ...saved, ...changes };
            app.setAppSettings(values(result));
            return { revision: 0 };
        },
        toastTitle: 'Context',
        saveGroup
    });
}

/** Board context, stored as the board's Every Context message. */
export function mountBoardLevelContext(app, element, boardId, { saveGroup, toastTitle = 'Board' } = {}) {
    const id = `board-level-context-message-${++contextEditorIds}`;
    return mountSettings(app, element, {
        load: extra => BoardApi.getBoardEveryContextAsync(boardId, extra),
        render: ({ message }) => `
            <label class="visually-hidden" for="${id}">Board context</label>
            <textarea id="${id}" class="form-control form-control-sm mb-2" rows="5" maxlength="4000" placeholder="Context for every LLM launched for this board…" spellcheck="true" data-context-message="board">${escapeHtml(message || '')}</textarea>`,
        read: root => ({ message: root.querySelector('[data-context-message="board"]').value }),
        save: payload => BoardApi.saveBoardEveryContextAsync(boardId, payload),
        savedMessage: 'Board context saved for this board.',
        toastTitle,
        saveGroup
    });
}

/** Card context and Card type context: the board's Agent context record, saved together. */
export function mountCardContext(app, element, boardId, { saveGroup, toastTitle = 'Board' } = {}) {
    const id = `card-context-message-${++contextEditorIds}`;
    return mountSettings(app, element, {
        load: extra => BoardApi.getBoardContextAsync(boardId, extra),
        render: ({ context }) => `
            ${contextDisclosure('card', `
                <label class="visually-hidden" for="${id}">Card context</label>
                <textarea id="${id}" class="form-control form-control-sm" rows="4" maxlength="4000" placeholder="Context for every card agent on this board…" spellcheck="true" data-context-card>${escapeHtml(context.defaultMessage)}</textarea>`)}
            ${contextDisclosure('type', TYPES.map(([type, label]) => {
                const override = context.typeOverrides.find(item => item.type === type);
                const mode = override?.mode || 'default';
                return `<details class="mb-2"><summary>${label}</summary>
                    <label class="board-editor-label mt-2" for="${id}-mode-${type}">${label} cards</label>
                    <select id="${id}-mode-${type}" class="form-select form-select-sm mb-2" data-context-mode="${type}">
                        ${[['default', 'Card context only'], ['replace', 'Type-specific only'], ['append', 'Card context + type-specific']].map(([value, text]) => `<option value="${value}" ${value === mode ? 'selected' : ''}>${text}</option>`).join('')}
                    </select>
                    <textarea class="form-control form-control-sm" rows="3" maxlength="4000" data-context-message="${type}" aria-label="${label} card type context" spellcheck="true">${escapeHtml(override?.message || '')}</textarea>
                </details>`;
            }).join('') + '<p class="form-text">A blank type-specific message in replace mode skips both card levels.</p>')}`,
        read: root => ({ context: {
            defaultMessage: root.querySelector('[data-context-card]').value,
            typeOverrides: TYPES.map(([type]) => ({ type,
                mode: root.querySelector(`[data-context-mode="${type}"]`).value,
                message: root.querySelector(`[data-context-message="${type}"]`).value
            }))
        } }),
        save: payload => BoardApi.saveBoardContextAsync(boardId, payload),
        savedMessage: 'Card and Card type context saved.',
        toastTitle,
        saveGroup
    });
}

/**
 * Mounts every unit {@link contextEditorSection} rendered. Each level keeps its own revision, so
 * one Save context action saves changed units without replacing any draft.
 * Returns one dispose for all of them.
 */
export function mountContextEditor(app, root) {
    const button = root.querySelector('[data-context-save]');
    const controls = new Map();
    const boardUnits = root.querySelector('[data-context-board-units]');
    let disposed = false;
    let saving = false;
    let boardDisposers = [];
    const anyDirty = () => Array.from(controls.values()).some(control => control.dirty());
    const ready = () => Array.from(controls.values()).every(control => control.ready());
    const saveGroup = {
        register: (control, unit) => {
            controls.set(unit, control);
            return () => { controls.delete(unit); saveGroup.refresh(); };
        },
        refresh: () => {
            if (!disposed) button.disabled = saving || !ready() || !anyDirty();
        }
    };
    const unit = name => root.querySelector(`[data-context-unit="${name}"]`);
    const options = { toastTitle: 'Context', saveGroup };
    const disposers = [mountAppContext(app, unit('app'), null, options), mountContextNudge(app, unit('nudge'), saveGroup)];
    if (unit('project')) disposers.push(mountProjectContext(app, unit('project'), saveGroup));
    const setBoard = boardId => {
        boardDisposers.forEach(dispose => dispose());
        boardUnits.innerHTML = boardId ? boardContextUnits() : '';
        boardDisposers = boardId ? [
            mountBoardLevelContext(app, unit('board'), boardId, options),
            mountCardContext(app, unit('card'), boardId, options)
        ] : [];
        saveGroup.refresh();
    };
    const save = async () => {
        if (disposed || saving || !ready() || !anyDirty()) return;
        saving = true;
        button.textContent = 'Saving…';
        saveGroup.refresh();
        try {
            const results = await Promise.all(Array.from(controls.values(), control => control.save()));
            if (!disposed && results.every(Boolean)) app.showToast('Context', 'Context saved.', 'success');
        } finally {
            saving = false;
            if (!disposed) { button.textContent = 'Save context'; saveGroup.refresh(); }
        }
    };
    button.addEventListener('click', save);
    root.addEventListener('input', saveGroup.refresh);
    root.addEventListener('change', saveGroup.refresh);
    return {
        setBoard,
        anyDirty,
        boardDirty: () => controls.get('board')?.dirty() || controls.get('card')?.dirty(),
        isSaving: () => saving,
        dispose() {
            disposed = true;
            button.removeEventListener('click', save);
            root.removeEventListener('input', saveGroup.refresh);
            root.removeEventListener('change', saveGroup.refresh);
            [...disposers, ...boardDisposers].forEach(dispose => dispose());
        }
    };
}

// Automatic account sync status and an immediate retry.
export function mountLaneAutomation(app, element, columnId, onSaved, options) {
    return element ? mountLaunchLane(app, element, columnId, onSaved, options) : () => {};
}
