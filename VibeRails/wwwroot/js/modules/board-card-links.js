import { escapeHtml } from './utils.js';
import { BoardApi } from './board-api.js';
import { cardLabel } from './board-card-label.js';
import { CardSearchPicker, cardPickerText } from './board-card-search-picker.js';

export function renderCardLinksSection(card) {
    return `<section class="board-side-section" data-board-card-links>
        <h3 class="board-side-label"><i class="fa-solid fa-link" aria-hidden="true"></i>
            Linked cards <span class="board-count" data-board-count="linked-cards">${card?.linkedCards?.length || 0}</span>
        </h3>
        <div class="board-side-list" data-board-linked-cards></div>
        <details class="board-link-picker" data-board-link-picker>
            <summary>Link a card</summary>
            <label class="board-editor-label" for="board-link-search">Find a card</label>
            <input type="search" id="board-link-search" class="form-control form-control-sm"
                placeholder="Search all local boards" maxlength="300" autocomplete="off" data-board-link-search>
            <p class="board-editor-muted" data-board-link-status role="status" aria-live="polite"></p>
            <div class="board-side-list" data-board-link-results></div>
        </details>
        <p class="board-editor-muted mt-2">${card?.id ? 'Links save immediately and appear on both cards.' : 'Links will be saved when you create this card.'}</p>
    </section>`;
}

/** Owns only the links rail. It never reloads or saves the surrounding card form. */
export function bindCardLinks(editor, card, { openCard, showError, onChanged, api = BoardApi }) {
    const host = editor.querySelector('[data-board-card-links]');
    if (!host) return () => {};
    const list = host.querySelector('[data-board-linked-cards]');
    const picker = host.querySelector('[data-board-link-picker]');
    const search = host.querySelector('[data-board-link-search]');
    const lifetime = new AbortController();
    let busy = false;
    let disposed = false;
    // Other rail operations may fetch fresh card data; keep this rail's current list independent.
    let links = [...(card?.linkedCards || [])];
    const cardSearch = new CardSearchPicker(host, {
        search: async (query, options) => (await api.getCardLinkCandidatesAsync(card.id, query, options))
            .filter(candidate => !links.some(link => link.id === candidate.id)),
        onSelect: candidate => changeLink(candidate.id, false),
        isActive: () => !busy && picker.open,
        currentBoardId: card.boardId,
        action: 'Link',
        emptyText: 'No matching cards available to link.'
    });

    function renderLinks() {
        host.querySelector('[data-board-count="linked-cards"]').textContent = String(links.length);
        list.innerHTML = links.length ? links.map(link => `<div class="board-side-row">
            <button type="button" class="board-side-main" data-board-open-linked-card="${escapeHtml(link.id)}"
                title="${escapeHtml(cardLabel(link))}" aria-label="Open ${escapeHtml(cardLabel(link))}">
                ${cardPickerText(link, card.boardId)}
            </button>
            <button type="button" class="board-side-remove" data-board-unlink-card="${escapeHtml(link.id)}"
                title="Unlink ${escapeHtml(link.displayId || link.key)}" aria-label="Unlink ${escapeHtml(link.displayId || link.key)}">
                <i class="fa-solid fa-xmark" aria-hidden="true"></i>
            </button>
        </div>`).join('') : (card?.id ? '<p class="board-side-empty">No cards linked.</p>' : '');
    }

    async function changeLink(targetId, remove) {
        if (busy || disposed) return;
        busy = true;
        cardSearch.cancel();
        host.querySelectorAll('button, input').forEach(control => { control.disabled = true; });
        try {
            if (!card.id) {
                if (remove) links = links.filter(link => link.id !== targetId);
                else {
                    const target = cardSearch.candidates.find(candidate => candidate.id === targetId);
                    if (!target || links.some(link => link.id === targetId)) return;
                    if (links.length >= 50) throw new Error('A new card can link up to 50 cards.');
                    links = [...links, target];
                }
                editor._boardHasEdits = true;
            } else if (remove) {
                await api.unlinkCardAsync(card.id, targetId);
                links = links.filter(link => link.id !== targetId);
            } else {
                const linked = await api.linkCardAsync(card.id, targetId);
                links = [...links.filter(link => link.id !== linked.id), linked];
            }
            if (disposed) return;
            links.sort((a, b) => (a.displayId || a.key).localeCompare(b.displayId || b.key, undefined, { numeric: true }));
            card.linkedCards = links;
            renderLinks();
            onChanged?.();
        } catch (error) {
            if (!disposed) showError(error?.message || (remove ? 'Could not unlink the card.' : 'Could not link the card.'));
        } finally {
            busy = false;
            if (!disposed) {
                host.querySelectorAll('button, input').forEach(control => { control.disabled = false; });
                void cardSearch.load();
            }
        }
    }

    host.addEventListener('click', event => {
        const button = event.target.closest('button');
        if (!button || busy || disposed) return;
        if (button.dataset.boardOpenLinkedCard) void openCard(button.dataset.boardOpenLinkedCard);
        if (button.dataset.boardUnlinkCard) void changeLink(button.dataset.boardUnlinkCard, true);
    }, { signal: lifetime.signal });
    picker?.addEventListener('toggle', () => {
        cardSearch.cancel();
        if (picker.open) {
            search.focus();
            void cardSearch.load();
        }
    }, { signal: lifetime.signal });
    renderLinks();
    return () => {
        disposed = true;
        cardSearch.dispose();
        lifetime.abort();
    };
}
