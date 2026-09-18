import { escapeHtml } from './utils.js';
import { BoardApi } from './board-api.js';

export function renderCardLinksSection(card) {
    return `<section class="board-side-section" data-board-card-links>
        <h3 class="board-side-label"><i class="fa-solid fa-link" aria-hidden="true"></i>
            Linked cards <span class="board-count" data-board-count="linked-cards">${card?.linkedCards?.length || 0}</span>
        </h3>
        <div class="board-side-list" data-board-linked-cards></div>
        ${card?.id ? `<details class="board-link-picker" data-board-link-picker>
            <summary>Link a card</summary>
            <label class="board-editor-label" for="board-link-search">Find a card</label>
            <input type="search" id="board-link-search" class="form-control form-control-sm"
                placeholder="VB-12 or card title" maxlength="300" autocomplete="off" data-board-link-search>
            <p class="board-editor-muted" data-board-link-status role="status" aria-live="polite"></p>
            <div class="board-side-list" data-board-link-results></div>
        </details>
        <p class="board-editor-muted mt-2">Links save immediately and appear on both cards.</p>`
        : '<p class="board-side-empty">Save the card to link other cards.</p>'}
    </section>`;
}

function cardLabel(card) {
    return `${card.key} · ${card.title}`;
}

function cardText(card) {
    return `<span class="board-side-text">
        <span class="board-side-title">${escapeHtml(cardLabel(card))}</span>
        <span class="board-side-sub">${escapeHtml(card.boardName)} · ${escapeHtml(card.columnName)}</span>
    </span>`;
}

/** Owns only the links rail. It never reloads or saves the surrounding card form. */
export function bindCardLinks(editor, card, { openCard, showError }) {
    const host = editor.querySelector('[data-board-card-links]');
    if (!host) return () => {};
    const list = host.querySelector('[data-board-linked-cards]');
    const picker = host.querySelector('[data-board-link-picker]');
    const search = host.querySelector('[data-board-link-search]');
    const results = host.querySelector('[data-board-link-results]');
    const status = host.querySelector('[data-board-link-status]');
    const lifetime = new AbortController();
    let searchAbort = null;
    let timer = null;
    let generation = 0;
    let busy = false;
    let disposed = false;
    // Other rail operations may fetch fresh card data; keep this rail's current list independent.
    let links = [...(card?.linkedCards || [])];

    function renderLinks() {
        host.querySelector('[data-board-count="linked-cards"]').textContent = String(links.length);
        list.innerHTML = links.length ? links.map(link => `<div class="board-side-row">
            <button type="button" class="board-side-main" data-board-open-linked-card="${escapeHtml(link.id)}"
                title="${escapeHtml(cardLabel(link))}" aria-label="Open ${escapeHtml(cardLabel(link))}">
                ${cardText(link)}
            </button>
            <button type="button" class="board-side-remove" data-board-unlink-card="${escapeHtml(link.id)}"
                title="Unlink ${escapeHtml(link.key)}" aria-label="Unlink ${escapeHtml(link.key)}">
                <i class="fa-solid fa-xmark" aria-hidden="true"></i>
            </button>
        </div>`).join('') : (card?.id ? '<p class="board-side-empty">No cards linked.</p>' : '');
    }

    function cancelSearch() {
        generation++;
        clearTimeout(timer);
        searchAbort?.abort();
    }

    async function loadCandidates() {
        cancelSearch();
        if (disposed || busy || !picker?.open) return;
        const current = generation;
        searchAbort = new AbortController();
        results.innerHTML = '';
        status.textContent = 'Finding cards…';
        try {
            const cards = await BoardApi.getCardLinkCandidatesAsync(card.id, search.value.trim(), { signal: searchAbort.signal });
            if (disposed || current !== generation) return;
            results.innerHTML = cards.map(candidate => `<div class="board-side-row">
                <button type="button" class="board-side-main" data-board-link-card="${escapeHtml(candidate.id)}"
                    title="${escapeHtml(cardLabel(candidate))}" aria-label="Link ${escapeHtml(cardLabel(candidate))}">
                    <i class="fa-solid fa-plus board-side-icon" aria-hidden="true"></i>${cardText(candidate)}
                </button>
            </div>`).join('');
            status.textContent = cards.length
                ? (cards.length === 50 ? 'Showing 50 cards. Refine your search to find more.' : 'Cards from all boards in this project.')
                : 'No matching cards available to link.';
        } catch (error) {
            if (disposed || current !== generation || error?.name === 'AbortError') return;
            status.textContent = error?.message || 'Could not find cards. Try searching again.';
        }
    }

    async function changeLink(targetId, remove) {
        if (busy || disposed) return;
        busy = true;
        cancelSearch();
        host.querySelectorAll('button, input').forEach(control => { control.disabled = true; });
        try {
            if (remove) {
                await BoardApi.unlinkCardAsync(card.id, targetId);
                links = links.filter(link => link.id !== targetId);
            } else {
                const linked = await BoardApi.linkCardAsync(card.id, targetId);
                links = [...links.filter(link => link.id !== linked.id), linked];
            }
            if (disposed) return;
            links.sort((a, b) => a.key.localeCompare(b.key, undefined, { numeric: true }));
            card.linkedCards = links;
            renderLinks();
        } catch (error) {
            if (!disposed) showError(error?.message || (remove ? 'Could not unlink the card.' : 'Could not link the card.'));
        } finally {
            busy = false;
            if (!disposed) {
                host.querySelectorAll('button, input').forEach(control => { control.disabled = false; });
                void loadCandidates();
            }
        }
    }

    host.addEventListener('click', event => {
        const button = event.target.closest('button');
        if (!button || busy || disposed) return;
        if (button.dataset.boardOpenLinkedCard) void openCard(button.dataset.boardOpenLinkedCard);
        if (button.dataset.boardLinkCard) void changeLink(button.dataset.boardLinkCard, false);
        if (button.dataset.boardUnlinkCard) void changeLink(button.dataset.boardUnlinkCard, true);
    }, { signal: lifetime.signal });
    picker?.addEventListener('toggle', () => {
        cancelSearch();
        if (picker.open) {
            search.focus();
            void loadCandidates();
        }
    }, { signal: lifetime.signal });
    search?.addEventListener('input', () => {
        cancelSearch();
        results.innerHTML = '';
        status.textContent = 'Finding cards…';
        timer = setTimeout(() => void loadCandidates(), 200);
    }, { signal: lifetime.signal });
    renderLinks();
    return () => {
        disposed = true;
        cancelSearch();
        lifetime.abort();
    };
}
