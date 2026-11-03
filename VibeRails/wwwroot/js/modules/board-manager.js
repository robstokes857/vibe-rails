import { escapeHtml, confirmDialog } from './utils.js';
import { BoardApi } from './board-api.js';
import { TabStatusController, TAB_STATUS } from './terminal-tab-status.js';

/**
 * The board header's AI board manager chip (VIBE-52 follow-up, VIBE-204). Hidden unless the status
 * says enabled: true, so the setting being off, a backend without the manager route, or an answer
 * without the field all show nothing. Otherwise one of four states: no manager saved (Set up board
 * manager), saved but not running (Start manager), running here (Go to manager, Stop, and what it
 * is doing), and running in another VibeRails window on this project (VIBE-262), which this window
 * can neither focus nor stop.
 */
export const boardManagerChipState = status =>
    status?.enabled !== true ? 'hidden'
        : !status.selection ? 'unset'
        : status.active ? 'running'
        : status.elsewhere ? 'elsewhere'
        : 'stopped';

/**
 * While the chip is hidden (the switch off, an older backend, a failed read) the board's ten-second
 * poll asks for the manager status at most this often. A board switch, a launch and a manager save
 * still ask at once through refresh().
 */
export const BOARD_MANAGER_HIDDEN_POLL_MS = 60_000;

const count = (value, one, many = `${one}s`) => `${value} ${value === 1 ? one : many}`;
const flaggedCount = status => Number(status?.flaggedCards) || 0;
const needsYou = flagged => `${flagged} ${flagged === 1 ? 'needs' : 'need'} you`;

/**
 * The one-line summary the chip and its tooltip share, e.g. "3 Agent cards · 1 of 1 worker busy · 1 needs you".
 * The chip itself shows the "needs you" part as its own button, so it asks for the rest with flagged: false.
 */
export function boardManagerSummary(status, { flagged = true } = {}) {
    const parts = [count(Number(status?.managedCards) || 0, 'Agent card')];
    if (status?.active || status?.elsewhere) {
        const max = Math.max(1, Number(status.maxWorkers) || 1);
        parts.push(`${Number(status.runningWorkers) || 0} of ${count(max, 'worker')} busy`);
    }
    const cards = flaggedCount(status);
    if (flagged && cards) parts.push(needsYou(cards));
    return parts.join(' · ');
}

/**
 * The manager tab's status words the chip repeats, exactly as the tab shows them. Anything else
 * (no event seen yet, Connected, Disconnected) reads as Running. The word is display only (owner,
 * 2026-11-03): it is right most of the time, so nothing here decides that the manager is done.
 */
const CHIP_STATUSES = new Set([TAB_STATUS.THINKING, TAB_STATUS.READY, TAB_STATUS.WAITING]);

const FLAGGED_MENU_ID = 'board-ai-manager-flagged-menu';

function renderFlagged(status, menuOpen) {
    const flagged = flaggedCount(status);
    if (!flagged) return '';
    const listed = Array.isArray(status.flagged) ? status.flagged : [];
    const more = flagged - listed.length;
    const items = listed.map(card => `<button type="button" class="board-ai-manager-menu-item" data-board-manager-card="${escapeHtml(card.id)}">
            <span class="board-ai-manager-menu-key">${escapeHtml(card.displayId || card.key || '')}</span>
            <span class="board-ai-manager-menu-title">${escapeHtml(card.title || '')}</span>
            <span class="board-ai-manager-menu-lane">${escapeHtml(card.laneName || '')}</span>
        </button>`).join('');
    return `<span class="board-ai-manager-flagged">
        <button type="button" class="board-ai-manager-needs" data-board-manager-action="flagged" aria-haspopup="true"
            aria-expanded="${menuOpen ? 'true' : 'false'}" aria-controls="${FLAGGED_MENU_ID}" aria-label="${escapeHtml(needsYou(flagged))}"
            title="Agent cards flagged for your attention">
            <i class="fa-solid fa-flag" aria-hidden="true"></i><span class="board-ai-manager-needs-count">${flagged}</span><span class="board-ai-manager-needs-words">${flagged === 1 ? 'needs' : 'need'} you</span></button>
        <div class="board-ai-manager-menu" id="${FLAGGED_MENU_ID}" role="group" aria-label="Flagged Agent cards"${menuOpen ? '' : ' hidden'}>
            ${items}
            ${more > 0 ? `<p class="board-ai-manager-menu-more">${escapeHtml(`+${count(more, 'more flagged card')} on this board`)}</p>` : ''}
        </div>
    </span>`;
}

/**
 * describe(selection) is the controller's assigneeInfo, so the chip and the lane cards agree on an
 * agent's name. statusWord is the manager tab's { status, text } while it runs here, or null.
 */
export function renderBoardManagerChip(status, describe = () => null, { statusWord = null, menuOpen = false } = {}) {
    const state = boardManagerChipState(status);
    if (state === 'hidden') return '';
    if (state === 'unset') {
        return `<button type="button" class="board-ai-manager-chip is-unset" data-board-manager-action="setup"
            title="One AI manager agent runs every Agent card on this board: it assigns workers, checks their results and moves the cards">
            <i class="fa-solid fa-diagram-project" aria-hidden="true"></i><span>Set up board manager</span></button>`;
    }
    const label = describe(status.selection)?.label || status.selection;
    const running = state === 'running';
    const elsewhere = state === 'elsewhere';
    const summary = boardManagerSummary(status);
    const word = running && statusWord && CHIP_STATUSES.has(statusWord.status) ? statusWord : null;
    const waiting = word?.status === TAB_STATUS.WAITING;
    const title = running ? `${label} is managing this board${word ? ` (${word.text})` : ''}: ${summary}`
        : elsewhere ? `${label} is managing this board from another VibeRails window on this project: ${summary}. Open that window to see or stop it.`
        : `${label} manages ${summary} once started`;
    const stateText = running ? escapeHtml(word?.text || 'Running')
        : elsewhere ? 'Running in another window'
        : 'Stopped';
    const flagged = flaggedCount(status);
    const classes = ['board-ai-manager-chip', running || elsewhere ? 'is-running' : '', elsewhere ? 'is-elsewhere' : '',
        waiting ? 'is-waiting' : '', flagged ? 'has-attention' : ''].filter(Boolean).join(' ');
    const where = running ? 'running' : elsewhere ? 'running in another window' : 'stopped';
    return `<span class="${classes}" role="group"
            aria-label="Board manager ${escapeHtml(label)}, ${where}${word ? `, ${escapeHtml(word.text)}` : ''}" title="${escapeHtml(title)}">
        <i class="fa-solid fa-diagram-project board-ai-manager-icon" aria-hidden="true"></i>
        <span class="board-ai-manager-text">
            <span class="board-ai-manager-label">${escapeHtml(label)}</span>
            <span class="board-ai-manager-state">${running || elsewhere ? '<span class="board-ai-manager-dot" aria-hidden="true"></span>' : ''}<span class="board-ai-manager-word">${stateText}</span>
                <span class="board-ai-manager-summary">· ${escapeHtml(boardManagerSummary(status, { flagged: false }))}</span></span>
        </span>
        ${renderFlagged(status, menuOpen)}
        ${running
            ? `<button type="button" class="btn btn-sm btn-outline-secondary" data-board-manager-action="focus">Go to manager</button>
               <button type="button" class="board-icon-btn board-ai-manager-stop" data-board-manager-action="stop" aria-label="Stop board manager" title="Stop board manager">
                   <i class="fa-solid fa-stop" aria-hidden="true"></i></button>`
            : elsewhere ? ''
            : '<button type="button" class="btn btn-sm btn-outline-primary" data-board-manager-action="start">Start manager</button>'}
    </span>`;
}

/**
 * The manager tab's status, for the chip only (VIBE-262). The Board page does not mount the
 * manager's terminal tab, so it runs the tab's own TabStatusController headless (no DOM, no
 * flashes, toasts or pushes) on the app-wide session events for that one session. A manager this
 * page just started begins at Thinking, as its tab does; one already running when the page loaded
 * shows no word until its first event, and its first output or quiet spell then seeds Thinking,
 * so a 5-second quiet reads Ready. Display only, never a completion signal.
 */
function createManagerStatusTracker(active, onChange) {
    const cli = String(active?.cli || '').toLowerCase();
    const controller = new TabStatusController({ id: active.tabId, label: '' }, {}, {
        getCliKey: () => cli,
        isActiveTab: () => false,
        onStatusChange: () => onChange()
    });
    const seed = () => {
        if (controller.status) return;
        controller.markResumedSession();
        controller.onSocketOpen();
    };
    return {
        sessionId: active.sessionId,
        controller,
        launched() { seed(); },
        word: () => controller.status ? { status: controller.status, text: controller.statusText() } : null,
        input: kind => controller.onSessionInput(kind),
        waiting: () => controller.onWaitingForUserSelection(),
        busy: () => { seed(); controller.onSessionBusy(); },
        idle: () => { seed(); controller.onSessionIdle(); },
        completed: () => controller.onSessionCompleted()
    };
}

/**
 * Mounts the chip on host and keeps it current: refresh() is called by the controller with the
 * board's ten-second activity poll and on every board switch. Requests carry a generation and an
 * abort so a late reply for another board never repaints this one. A backend without the route
 * (an older binary serving a newer page) hides the chip instead of toasting every ten seconds.
 * The flagged-cards list opens from "N need you" and closes on Escape, an outside click or focus
 * leaving it; while it is open a refresh waits, so keyboard focus stays put. confirm is injectable
 * so node tests can answer the Stop dialog, and events (app.appEventClient) so they can drive the
 * status word.
 */
export function mountBoardManagerChip(app, host, {
    boardId, describe = () => null, openSettings, focusTab, openCard, onStatus = () => {}, confirm = confirmDialog,
    events = app?.appEventClient, now = () => Date.now()
}) {
    if (!host) return { refresh: async () => null, poll: async () => null, status: () => null, dispose() {} };
    let disposed = false;
    let busy = false;
    let generation = 0;
    let abort = null;
    let status = null;
    let requestedAt = null;
    let tracker = null;
    let launchedSession = null;
    let menuOpen = false;
    const alive = () => !disposed && host.isConnected !== false;
    const paint = () => {
        if (!alive()) return;
        // An open list is left alone; closing it repaints with whatever arrived meanwhile.
        if (menuOpen) return;
        host.innerHTML = renderBoardManagerChip(status, describe, { statusWord: tracker?.word() ?? null, menuOpen });
        host.hidden = boardManagerChipState(status) === 'hidden';
    };
    const track = () => {
        const active = status?.enabled === true ? status.active : null;
        const sessionId = active?.sessionId || null;
        if ((tracker?.sessionId || null) === sessionId) return;
        tracker = sessionId ? createManagerStatusTracker(active, paint) : null;
        if (tracker && sessionId === launchedSession) tracker.launched();
    };
    const render = () => {
        track();
        if (!flaggedCount(status) && menuOpen) menuOpen = false;
        paint();
        onStatus(status);
    };
    const forManager = handler => payload => {
        if (!alive() || !tracker || !payload?.sessionId || payload.sessionId !== tracker.sessionId) return;
        handler(payload);
    };
    const unsubscribe = typeof events?.on === 'function' ? [
        events.on('session_input', forManager(payload => tracker.input(payload?.kind))),
        events.on('session_waiting_for_user', forManager(() => tracker.waiting())),
        events.on('session_busy', forManager(() => tracker.busy())),
        events.on('session_idle', forManager(() => tracker.idle())),
        events.on('session_completed', forManager(() => tracker.completed()))
    ].filter(fn => typeof fn === 'function') : [];

    const trigger = () => host.querySelector?.('[data-board-manager-action="flagged"]');
    // Right-aligned to its button, flipped to left-aligned near the window's left edge, and kept
    // 8px inside the window either way (the Menu panel standard: align to the trigger, flip at edges).
    function placeMenu() {
        const menu = host.querySelector?.(`#${FLAGGED_MENU_ID}`);
        const button = trigger();
        const frame = menu?.parentElement;
        if (!menu?.getBoundingClientRect || !button?.getBoundingClientRect || !frame?.getBoundingClientRect) return;
        const gutter = 8;
        const viewport = globalThis.document?.documentElement?.clientWidth || globalThis.innerWidth || 0;
        const anchor = button.getBoundingClientRect();
        const width = menu.getBoundingClientRect().width;
        let left = anchor.right - width;
        if (left < gutter) left = anchor.left;
        if (viewport) left = Math.min(left, viewport - width - gutter);
        left = Math.max(gutter, left);
        menu.style.right = 'auto';
        menu.style.left = `${Math.round(left - frame.getBoundingClientRect().left)}px`;
    }
    function setMenu(open, { focusTrigger = false } = {}) {
        if (menuOpen === open || !alive()) return;
        menuOpen = open;
        host.innerHTML = renderBoardManagerChip(status, describe, { statusWord: tracker?.word() ?? null, menuOpen });
        if (open) {
            placeMenu();
            host.querySelector?.('[data-board-manager-card]')?.focus?.();
        } else if (focusTrigger) trigger()?.focus?.();
    }

    async function refresh() {
        const id = boardId();
        if (!id) { status = null; if (alive()) render(); return null; }
        abort?.abort();
        const own = abort = new AbortController();
        const token = ++generation;
        requestedAt = now();
        try {
            const next = await BoardApi.getBoardManagerAsync(id, { signal: own.signal });
            if (!alive() || token !== generation || id !== boardId()) return status;
            status = next;
            render();
        } catch (error) {
            if (error?.name === 'AbortError' || !alive() || token !== generation) return status;
            status = null;
            render();
        }
        return status;
    }
    /** The board's ten-second poll: every tick while the chip shows, once a minute while it is hidden. */
    function poll() {
        if (boardManagerChipState(status) === 'hidden' && requestedAt !== null && now() - requestedAt < BOARD_MANAGER_HIDDEN_POLL_MS)
            return Promise.resolve(status);
        return refresh();
    }
    async function stop(id) {
        const label = describe(status?.selection)?.label || 'The board manager';
        const confirmed = await confirm({
            title: 'Stop board manager',
            message: `Stop ${label}? Its terminal closes; workers it started keep running and their cards keep their handoffs. Start it again any time and it picks up from the cards.`,
            confirmLabel: 'Stop manager',
            danger: true
        });
        if (!confirmed || !alive()) return;
        const result = await BoardApi.stopBoardManagerAsync(id);
        if (!alive()) return;
        app.showToast('Board', result?.stopped ? 'Board manager stopped.' : 'The board manager was not running.', result?.stopped ? 'success' : 'info');
        await refresh();
    }
    const click = async event => {
        const target = event.target?.closest?.('[data-board-manager-card], [data-board-manager-action]');
        if (!target) return;
        const cardId = target.dataset?.boardManagerCard;
        if (cardId) {
            setMenu(false);
            await openCard?.(cardId);
            return;
        }
        const action = target.dataset?.boardManagerAction;
        if (action === 'flagged') { setMenu(!menuOpen); return; }
        if (busy) return;
        if (action === 'setup') { openSettings?.('manager'); return; }
        const id = boardId();
        if (!id) return;
        busy = true;
        target.disabled = true;
        try {
            if (action === 'focus' && status?.active) {
                await focusTab?.(status.active.tabId, status.selection);
                return;
            }
            if (action === 'stop') { await stop(id); return; }
            if (action !== 'start') return;
            const result = await BoardApi.launchBoardManagerAsync(id);
            if (!alive()) return;
            if (!result.reused) launchedSession = result.sessionId || null;
            app.showToast('Board', result.reused
                ? 'The board manager is already running.'
                : 'Board manager started. It picks up this board\'s Agent cards and assigns their work.', 'success');
            await refresh();
            if (result.reused) await focusTab?.(result.tabId, result.selection);
        } catch (error) {
            if (alive()) app.showToast('Board', error?.message || (action === 'stop' ? 'The board manager could not be stopped.' : 'The board manager could not start.'), 'error');
        } finally {
            busy = false;
            if (alive() && target.isConnected !== false) target.disabled = false;
        }
    };
    const keydown = event => {
        if (event.key === 'Escape' && menuOpen) {
            event.preventDefault?.();
            setMenu(false, { focusTrigger: true });
        }
    };
    const focusout = event => {
        if (menuOpen && event.relatedTarget && !host.contains?.(event.relatedTarget)) setMenu(false);
    };
    const outside = event => {
        if (menuOpen && !host.contains?.(event.target)) setMenu(false);
    };
    host.addEventListener('click', click);
    host.addEventListener('keydown', keydown);
    host.addEventListener('focusout', focusout);
    globalThis.document?.addEventListener?.('pointerdown', outside, true);
    void refresh();
    return {
        refresh,
        poll,
        status: () => status,
        statusWord: () => tracker?.word() ?? null,
        dispose() {
            disposed = true;
            abort?.abort();
            host.removeEventListener('click', click);
            host.removeEventListener('keydown', keydown);
            host.removeEventListener('focusout', focusout);
            globalThis.document?.removeEventListener?.('pointerdown', outside, true);
            for (const off of unsubscribe) off();
            tracker = null;
        }
    };
}
