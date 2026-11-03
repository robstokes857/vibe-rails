import { escapeHtml, confirmDialog } from './utils.js';
import { BoardApi } from './board-api.js';

/**
 * The board header's AI board manager chip (VIBE-52 follow-up, VIBE-204). Hidden unless the status
 * says enabled: true, so the setting being off, a backend without the manager route, or an answer
 * without the field all show nothing. Otherwise one of three states: no manager saved (Set up board manager), saved
 * but not running (Start manager), and running (Go to manager, Stop, and what it is doing).
 */
export const boardManagerChipState = status =>
    status?.enabled !== true ? 'hidden' : !status.selection ? 'unset' : status.active ? 'running' : 'stopped';

const count = (value, one, many = `${one}s`) => `${value} ${value === 1 ? one : many}`;

/** The one-line summary the chip and its tooltip share, e.g. "3 Agent cards · 1 of 1 worker busy · 1 needs you". */
export function boardManagerSummary(status) {
    const parts = [count(Number(status?.managedCards) || 0, 'Agent card')];
    if (status?.active) {
        const max = Math.max(1, Number(status.maxWorkers) || 1);
        parts.push(`${Number(status.runningWorkers) || 0} of ${count(max, 'worker')} busy`);
    }
    const flagged = Number(status?.flaggedCards) || 0;
    if (flagged) parts.push(`${flagged} ${flagged === 1 ? 'needs' : 'need'} you`);
    return parts.join(' · ');
}

/** describe(selection) is the controller's assigneeInfo, so the chip and the lane cards agree on an agent's name. */
export function renderBoardManagerChip(status, describe = () => null) {
    const state = boardManagerChipState(status);
    if (state === 'hidden') return '';
    if (state === 'unset') {
        return `<button type="button" class="board-ai-manager-chip is-unset" data-board-manager-action="setup"
            title="One AI manager agent runs every Agent card on this board: it assigns workers, checks their results and moves the cards">
            <i class="fa-solid fa-diagram-project" aria-hidden="true"></i><span>Set up board manager</span></button>`;
    }
    const label = describe(status.selection)?.label || status.selection;
    const running = state === 'running';
    const summary = boardManagerSummary(status);
    const title = running ? `${label} is managing this board: ${summary}` : `${label} manages ${summary} once started`;
    const flagged = Number(status.flaggedCards) || 0;
    return `<span class="board-ai-manager-chip${running ? ' is-running' : ''}${flagged ? ' has-attention' : ''}" role="group"
            aria-label="Board manager ${escapeHtml(label)}, ${running ? 'running' : 'stopped'}" title="${escapeHtml(title)}">
        <i class="fa-solid fa-diagram-project board-ai-manager-icon" aria-hidden="true"></i>
        <span class="board-ai-manager-text">
            <span class="board-ai-manager-label">${escapeHtml(label)}</span>
            <span class="board-ai-manager-state">${running ? '<span class="board-ai-manager-dot" aria-hidden="true"></span>Running' : 'Stopped'}
                <span class="board-ai-manager-summary">· ${escapeHtml(summary)}</span></span>
        </span>
        ${running
            ? `<button type="button" class="btn btn-sm btn-outline-secondary" data-board-manager-action="focus">Go to manager</button>
               <button type="button" class="board-icon-btn board-ai-manager-stop" data-board-manager-action="stop" aria-label="Stop board manager" title="Stop board manager">
                   <i class="fa-solid fa-stop" aria-hidden="true"></i></button>`
            : '<button type="button" class="btn btn-sm btn-outline-primary" data-board-manager-action="start">Start manager</button>'}
    </span>`;
}

/**
 * Mounts the chip on host and keeps it current: refresh() is called by the controller with the
 * board's ten-second activity poll and on every board switch. Requests carry a generation and an
 * abort so a late reply for another board never repaints this one. A backend without the route
 * (an older binary serving a newer page) hides the chip instead of toasting every ten seconds.
 * confirm is injectable so node tests can answer the Stop dialog.
 */
export function mountBoardManagerChip(app, host, { boardId, describe = () => null, openSettings, focusTab, onStatus = () => {}, confirm = confirmDialog }) {
    if (!host) return { refresh: async () => null, status: () => null, dispose() {} };
    let disposed = false;
    let busy = false;
    let generation = 0;
    let abort = null;
    let status = null;
    const alive = () => !disposed && host.isConnected !== false;
    const render = () => {
        host.innerHTML = renderBoardManagerChip(status, describe);
        host.hidden = boardManagerChipState(status) === 'hidden';
        onStatus(status);
    };
    async function refresh() {
        const id = boardId();
        if (!id) { status = null; if (alive()) render(); return null; }
        abort?.abort();
        const own = abort = new AbortController();
        const token = ++generation;
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
        const button = event.target.closest('[data-board-manager-action]');
        if (!button || busy) return;
        const action = button.dataset.boardManagerAction;
        if (action === 'setup') { openSettings?.('manager'); return; }
        const id = boardId();
        if (!id) return;
        busy = true;
        button.disabled = true;
        try {
            if (action === 'focus' && status?.active) {
                await focusTab?.(status.active.tabId, status.selection);
                return;
            }
            if (action === 'stop') { await stop(id); return; }
            const result = await BoardApi.launchBoardManagerAsync(id);
            if (!alive()) return;
            app.showToast('Board', result.reused
                ? 'The board manager is already running.'
                : 'Board manager started. It picks up this board\'s Agent cards and assigns their work.', 'success');
            await refresh();
            if (result.reused) await focusTab?.(result.tabId, result.selection);
        } catch (error) {
            if (alive()) app.showToast('Board', error?.message || (action === 'stop' ? 'The board manager could not be stopped.' : 'The board manager could not start.'), 'error');
        } finally {
            busy = false;
            if (alive() && button.isConnected !== false) button.disabled = false;
        }
    };
    host.addEventListener('click', click);
    void refresh();
    return {
        refresh,
        status: () => status,
        dispose() { disposed = true; abort?.abort(); host.removeEventListener('click', click); }
    };
}
