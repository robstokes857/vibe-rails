import { BoardApi } from './board-api.js';
import { cardDisplayId } from './board-card-label.js';
import { escapeHtml } from './utils.js';

/** Shared ownership labels for search results and both sides of a card link. */
export function cardLocationHtml(card, currentBoardId = null) {
    const otherProject = card.isCurrentProject === false;
    const otherBoard = currentBoardId && card.boardId !== currentBoardId;
    return `<span class="board-side-sub">${escapeHtml(card.boardName || '')} · ${escapeHtml(card.columnName || '')}</span>
        ${otherProject ? `<span class="board-search-warning"><i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i> Another project · ${escapeHtml(card.projectPath || '')}</span>`
            : otherBoard ? '<span class="board-side-sub">Another board in this project</span>' : ''}`;
}

/** Highlight literal query terms without allowing card text or the query to become markup. */
export function highlightSearchText(value, query = '') {
    const text = String(value || '');
    const terms = [...new Set(query.trim().split(/\s+/).filter(term => term.length > 1))]
        .slice(0, 12).sort((a, b) => b.length - a.length);
    if (!terms.length) return escapeHtml(text);
    const pattern = new RegExp(terms.map(term => term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|'), 'giu');
    let html = '', offset = 0;
    for (const match of text.matchAll(pattern)) {
        html += escapeHtml(text.slice(offset, match.index)) + `<mark>${escapeHtml(match[0])}</mark>`;
        offset = match.index + match[0].length;
    }
    return html + escapeHtml(text.slice(offset));
}

export function searchResultsHtml(cards, currentBoardId, query = '') {
    return cards.map(card => `<button type="button" class="board-search-result" data-board-search-card="${escapeHtml(card.id)}">
        <span class="board-search-result-heading">
            <span class="board-search-result-key">${highlightSearchText(cardDisplayId(card), query)}</span>
            <span class="board-search-result-title">${highlightSearchText(card.title, query)}</span>
            ${card.type ? `<span class="board-search-result-type">${escapeHtml(card.type.replaceAll('-', ' '))}</span>` : ''}
            <i class="fa-solid fa-chevron-right board-search-result-arrow" aria-hidden="true"></i>
        </span>
        <span class="board-search-result-location">${cardLocationHtml(card, currentBoardId)}</span>
        ${card.snippet ? `<span class="board-search-snippet">${highlightSearchText(card.snippet, query)}</span>` : ''}
    </button>`).join('');
}

/** Abort and generation guards protect typing, navigation and late failed searches. */
export class BoardSearch {
    constructor(root, { currentBoardId, openCard, filters = () => ({}), onResults = () => {}, search = BoardApi.searchBoardCardsAsync }) {
        this.root = root;
        this.host = root.querySelector('[data-board-search-results]');
        this.status = this.host?.querySelector('[data-board-search-status]');
        this.results = this.host?.querySelector('[data-board-search-list]');
        this.currentBoardId = currentBoardId;
        this.openCard = openCard;
        this.search = search;
        this.getFilters = filters;
        this.onResults = onResults;
        this.query = '';
        this.cards = [];
        this.generation = 0;
        this.lifetime = new AbortController();
        this.host?.addEventListener('click', event => {
            const id = event.target.closest('[data-board-search-card]')?.dataset.boardSearchCard;
            const card = this.cards.find(item => item.id === id);
            if (card) void this.openCard(card);
        }, { signal: this.lifetime.signal });
    }

    setQuery(value, { force = false, immediate = false } = {}) {
        const query = String(value || '').trim();
        const boardId = this.currentBoardId();
        const currentFilters = this.getFilters();
        const filters = Object.fromEntries(['assignee', 'type', 'priority', 'origin', 'ghost']
            .map(key => [key, currentFilters[key] || '']));
        const filterKey = JSON.stringify(filters);
        if (this.disposed || !this.host || (!force && query === this.query && boardId === this.boardId && filterKey === this.filterKey)) return;
        this.query = query;
        this.boardId = boardId;
        this.filterKey = filterKey;
        clearTimeout(this.timer);
        this.abort?.abort();
        const generation = ++this.generation;
        const active = Boolean(query);
        this.host.hidden = !active;
        this.root.classList.toggle('is-searching', active);
        this.cards = [];
        this.results.innerHTML = '';
        this.status.textContent = active ? 'Searching all local boards…' : '';
        if (!active) return;
        if (immediate) return this.run(query, generation, filters);
        this.timer = setTimeout(() => void this.run(query, generation, filters), 200);
    }

    async run(query, generation, filters) {
        const abort = this.abort = new AbortController();
        const current = () => !this.disposed && !abort.signal.aborted && generation === this.generation;
        try {
            const cards = await this.search(query, { signal: abort.signal, filters });
            if (!current()) return;
            this.cards = cards;
            this.results.innerHTML = searchResultsHtml(cards, this.currentBoardId(), query);
            this.status.textContent = cards.length
                ? `${cards.length} match${cards.length === 1 ? '' : 'es'}${cards.length >= 50 ? ' · Refine your search for more' : ''}`
                : Object.values(filters).some(Boolean)
                    ? 'No cards match this search and the selected filters. Try changing a filter.'
                    : 'No matching cards across local boards.';
            this.onResults();
        } catch (error) {
            if (current()) this.status.textContent = error?.message || 'Could not search cards. Try again.';
        }
    }

    dispose() {
        this.disposed = true;
        clearTimeout(this.timer);
        this.abort?.abort();
        this.lifetime.abort();
    }
}
