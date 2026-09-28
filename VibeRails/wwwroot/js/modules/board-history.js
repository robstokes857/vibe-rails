import { escapeHtml } from './utils.js';
import { BoardApi } from './board-api.js';

export const historySection = () => `
    <details class="mt-3" data-board-history-view>
        <summary>History</summary>
        <div data-history-entries></div>
        <button type="button" class="btn btn-sm btn-outline-secondary" data-history-more hidden>Load older changes</button>
    </details>`;

// Fetch only after an explicit open. Every editor owns its cancellation and paging state.
export function mountHistory(element, boardId, cardId = null) {
    if (!element) return () => {};
    const abort = new AbortController();
    const host = element.querySelector('[data-history-entries]');
    const more = element.querySelector('[data-history-more]');
    let offset = 0;
    let loaded = false;
    let busy = false;
    let disposed = false;
    // The server pages by offset over a list a sync tick can reorder (acknowledged entries move
    // from the local group to the remote one), so an id already on screen is skipped.
    const rendered = new Set();
    async function load() {
        if (busy || disposed) return;
        busy = true;
        more.disabled = true;
        try {
            const page = await BoardApi.getBoardHistoryAsync(boardId, { card: cardId, offset, signal: abort.signal });
            if (disposed) return;
            if (!loaded) host.replaceChildren();
            const entries = (page.entries || []).filter(entry => !entry.id || !rendered.has(entry.id));
            for (const entry of entries) if (entry.id) rendered.add(entry.id);
            host.insertAdjacentHTML('beforeend', entries.map(entry => `
                <article class="board-log-change">
                    <span class="board-log-change-text">${entry.cardKey ? `<strong>${escapeHtml(entry.cardKey)}</strong> ` : ''}${escapeHtml(entry.body)}</span>
                    <small>${escapeHtml(entry.author)} · ${escapeHtml(new Date(entry.createdUtc).toLocaleString())}</small>
                    ${entry.changes ? `<details><summary>Changed values</summary><pre>${escapeHtml(entry.changes)}</pre></details>` : ''}
                </article>`).join(''));
            if (!loaded && !page.entries.length) host.textContent = 'No changes recorded yet.';
            loaded = true;
            offset = page.nextOffset;
            more.textContent = 'Load older changes';
            more.hidden = !page.hasMore;
        } catch (error) {
            if (!disposed && error?.name !== 'AbortError') {
                if (!loaded) host.textContent = error?.message || 'History could not be loaded.';
                more.textContent = 'Retry';
                more.hidden = false;
            }
        } finally {
            busy = false;
            if (!disposed) more.disabled = false;
        }
    }
    const toggle = () => { if (element.open && !loaded) void load(); };
    element.addEventListener('toggle', toggle);
    more.addEventListener('click', load);
    return () => {
        disposed = true;
        abort.abort();
        element.removeEventListener('toggle', toggle);
        more.removeEventListener('click', load);
    };
}
