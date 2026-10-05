import { escapeHtml } from './utils.js';
import { cardLabel } from './board-card-label.js';
import { cardLocationHtml } from './board-search.js';

export function cardPickerText(card, currentBoardId) {
    return `<span class="board-side-text">
        <span class="board-side-title">${escapeHtml(cardLabel(card))}</span>
        ${cardLocationHtml(card, currentBoardId)}
    </span>`;
}

/** Shared Find a card search, result rendering and cancellation for links and history. */
export class CardSearchPicker {
    constructor(host, { search, onSelect, onResults, isActive = () => true, currentBoardId, action = 'Select', emptyText = 'No matching cards.' }) {
        this.input = host.querySelector('[data-board-link-search]');
        this.results = host.querySelector('[data-board-link-results]');
        this.status = host.querySelector('[data-board-link-status]');
        this.search = search;
        this.isActive = isActive;
        this.currentBoardId = currentBoardId;
        this.action = action;
        this.emptyText = emptyText;
        this.onResults = onResults;
        this.candidates = [];
        this.generation = 0;
        this.lifetime = new AbortController();
        this.input.addEventListener('input', () => {
            this.cancel();
            this.candidates = [];
            this.results.innerHTML = '';
            this.status.textContent = 'Finding cards…';
            this.timer = setTimeout(() => void this.load(), 200);
        }, { signal: this.lifetime.signal });
        this.results.addEventListener('click', event => {
            const id = event.target.closest('[data-board-link-card]')?.dataset.boardLinkCard;
            const card = this.candidates.find(candidate => candidate.id === id);
            if (card && !this.disposed && this.isActive()) void onSelect(card);
        }, { signal: this.lifetime.signal });
        host.addEventListener('keydown', event => {
            // Other controls in the host (Unlink, Open) keep their own arrow-key behaviour.
            if (event.target !== this.input && !this.results.contains(event.target)) return;
            const buttons = [...this.results.querySelectorAll('[data-board-link-card]')];
            if (!buttons.length || !this.isActive() || !['ArrowDown', 'ArrowUp'].includes(event.key)) return;
            event.preventDefault();
            const index = buttons.indexOf(event.target);
            const next = event.key === 'ArrowDown' ? index + 1 : index < 0 ? buttons.length - 1 : index - 1;
            if (next < 0) this.input.focus();
            else buttons[Math.min(next, buttons.length - 1)].focus();
        }, { signal: this.lifetime.signal });
    }

    cancel() {
        this.generation++;
        clearTimeout(this.timer);
        this.abort?.abort();
    }

    async load() {
        this.cancel();
        if (this.disposed || !this.isActive()) return;
        const generation = this.generation;
        const abort = this.abort = new AbortController();
        const current = () => !this.disposed && !abort.signal.aborted && generation === this.generation && this.isActive();
        this.candidates = [];
        this.results.innerHTML = '';
        this.status.textContent = 'Finding cards…';
        try {
            const cards = await this.search(this.input.value.trim(), { signal: abort.signal });
            if (!current()) return;
            this.candidates = cards;
            this.results.innerHTML = cards.map(card => `<div class="board-side-row">
                <button type="button" class="board-side-main" data-board-link-card="${escapeHtml(card.id)}"
                    title="${escapeHtml(cardLabel(card))}" aria-label="${escapeHtml(this.action)} ${escapeHtml(cardLabel(card))}">
                    <i class="fa-solid fa-${this.action === 'Link' ? 'plus' : 'filter'} board-side-icon" aria-hidden="true"></i>${cardPickerText(card, this.currentBoardId)}
                </button>
            </div>`).join('');
            this.status.textContent = cards.length
                ? (cards.length === 50 ? 'Showing 50 cards. Refine your search to find more.' : 'Cards from all local boards. This project appears first.')
                : this.emptyText;
            this.onResults?.();
        } catch (error) {
            if (current() && error?.name !== 'AbortError') {
                this.status.textContent = error?.message || 'Could not find cards. Try searching again.';
                this.onResults?.();
            }
        }
    }

    dispose() {
        this.disposed = true;
        this.cancel();
        this.lifetime.abort();
    }
}
