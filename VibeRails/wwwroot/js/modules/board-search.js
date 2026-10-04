import { BoardApi } from './board-api.js';
import { cardLabel } from './board-card-label.js';
import { escapeHtml } from './utils.js';

/** Shared ownership labels for search results and both sides of a card link. */
export function cardLocationHtml(card, currentBoardId = null) {
    const otherProject = card.isCurrentProject === false;
    const otherBoard = currentBoardId && card.boardId !== currentBoardId;
    return `<span class="board-side-sub">${escapeHtml(card.boardName || '')} · ${escapeHtml(card.columnName || '')}</span>
        ${otherProject ? `<span class="board-search-warning"><i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i> Another project · ${escapeHtml(card.projectPath || '')}</span>`
            : otherBoard ? '<span class="board-side-sub">Another board in this project</span>' : ''}`;
}

export function searchResultsHtml(cards, currentBoardId) {
    return cards.map(card => `<button type="button" class="board-search-result" data-board-search-card="${escapeHtml(card.id)}">
        <span class="board-side-title">${escapeHtml(cardLabel(card))}</span>
        ${cardLocationHtml(card, currentBoardId)}
        ${card.snippet ? `<span class="board-search-snippet">${escapeHtml(card.snippet)}</span>` : ''}
    </button>`).join('');
}

/** Abort and generation guards protect typing, navigation and late failed searches. */
export class BoardSearch {
    constructor(root, { currentBoardId, openCard, search = BoardApi.searchBoardCardsAsync }) {
        this.root = root;
        this.host = root.querySelector('[data-board-search-results]');
        this.status = this.host?.querySelector('[data-board-search-status]');
        this.results = this.host?.querySelector('[data-board-search-list]');
        this.currentBoardId = currentBoardId;
        this.openCard = openCard;
        this.search = search;
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
        if (this.disposed || !this.host || (!force && query === this.query && boardId === this.boardId)) return;
        this.query = query;
        this.boardId = boardId;
        clearTimeout(this.timer);
        this.abort?.abort();
        const generation = ++this.generation;
        const active = Boolean(query);
        this.host.hidden = !active;
        this.root.classList.toggle('is-searching', active);
        this.root.querySelectorAll('[data-board-filter-assignee], [data-board-filter-type], [data-board-filter-priority], [data-board-filter-origin]')
            .forEach(control => {
                control.disabled = active;
                if (active) control.title = 'Lane filters apply when search is cleared.';
                else control.removeAttribute('title');
            });
        this.cards = [];
        this.results.innerHTML = '';
        this.status.textContent = active ? 'Searching all local boards…' : '';
        if (!active) return;
        if (immediate) return this.run(query, generation);
        this.timer = setTimeout(() => void this.run(query, generation), 200);
    }

    async run(query, generation) {
        const abort = this.abort = new AbortController();
        const current = () => !this.disposed && !abort.signal.aborted && generation === this.generation;
        try {
            const cards = await this.search(query, { signal: abort.signal });
            if (!current()) return;
            this.cards = cards;
            this.results.innerHTML = searchResultsHtml(cards, this.currentBoardId());
            this.status.textContent = cards.length
                ? `${cards.length} result${cards.length === 1 ? '' : 's'} across all local boards. Relevant cards in this project are preferred.${cards.length >= 50 ? ' Refine your search to find more.' : ''}`
                : 'No matching cards across local boards.';
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
