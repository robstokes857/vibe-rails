import { escapeHtml, confirmDialog } from './utils.js';
import { cardLabel } from './board-card-label.js';
import { BoardApi } from './board-api.js';

// Single-card and multi-card deletion share this consequence text. Keep it in sync with
// the store's permanent-delete contract.
export function cardDeletionConfirmation(cards) {
    const count = cards.length;
    return {
        title: count === 1 ? 'Delete card?' : `Delete ${count} cards?`,
        message: count === 1
            ? `Permanently delete ${cardLabel(cards[0]) || 'this card'} and its discussion, attachments, and saved commit data? Session recordings are kept. This cannot be undone.`
            : `Permanently delete the ${count} selected cards and their discussion, attachments, and saved commit data? Session recordings are kept. This cannot be undone.`,
        confirmLabel: count === 1 ? 'Delete card' : `Delete ${count} cards`,
        danger: true
    };
}

/** Explicit, view-local selection. Never infer selected IDs from filters or lane totals. */
export class BoardCardSelection {
    constructor(board, { confirm = confirmDialog, remove = id => BoardApi.deleteBoardCardAsync(id) } = {}) {
        this.board = board;
        this.confirm = confirm;
        this.remove = remove;
        this.selected = new Map();
        this.active = false;
        this.busy = false;
        this.generation = 0;
        this.message = '';
        this.failures = new Map();
    }

    reset(message = '') {
        this.generation++;
        this.active = false;
        this.busy = false;
        this.selected.clear();
        this.failures.clear();
        this.message = message;
    }

    available() {
        return Boolean(this.board.currentBoard()) && !this.board.currentBoard().shared
            && !this.board.state.filters.q.trim();
    }

    toggle(cardId, checked) {
        if (!this.active || this.busy || !this.available()) return;
        if (checked && this.board._cardPageGeneration !== this.board._refreshGeneration) return;
        const card = this.board.filteredCards().find(item => item.id === cardId);
        if (checked && card && !this.board.collapsedLaneIds.has(card.columnId)) this.selected.set(cardId, { ...card });
        if (!checked) { this.selected.delete(cardId); this.failures.delete(cardId); }
        this.message = '';
        this.render();
    }

    handleClick(event) {
        const trigger = event.target.closest('[data-board-selection-action]');
        if (trigger) {
            const action = trigger.dataset.boardSelectionAction;
            if (this.busy) return true;
            if (action === 'start' && this.available() && this.board._cardPageGeneration === this.board._refreshGeneration) {
                this.reset();
                this.active = true;
                this.board.cardAssign.close();
                this.board.laneAgents.close(false);
                this.board.renderLanes();
                const first = this.board.queryAll('[data-board-card-select]').find(input => !input.closest('[hidden]'));
                (first || this.board.query('[data-board-selection-action="cancel"]'))?.focus();
            } else if (action === 'cancel') {
                this.reset();
                this.board.renderLanes();
                this.board.query('[data-board-selection-action="start"]')?.focus();
            } else if (action === 'remove') {
                this.toggle(trigger.dataset.cardId, false);
                this.board.query('[data-board-selection-list] summary')?.focus();
            } else if (action === 'delete') void this.deleteSelected();
            this.render();
            return true;
        }
        if (this.active && event.target.closest('.board-card')) {
            // Leave the native labelled checkbox alone, but suppress editor/launch actions.
            return true;
        }
        return false;
    }

    cardControl(card) {
        if (!this.active) return '';
        return `<label class="board-card-select"><input type="checkbox" data-board-card-select="${escapeHtml(card.id)}"
            ${this.selected.has(card.id) ? 'checked' : ''} ${this.busy ? 'disabled' : ''}
            aria-label="Select ${escapeHtml(cardLabel(card))}"><span>Select card</span></label>`;
    }

    render() {
        const board = this.board;
        if (!this.available() && this.active) this.reset();
        board.root?.classList.toggle('is-selecting', this.active);
        const start = board.query('[data-board-selection-action="start"]');
        if (start) { start.hidden = !this.available() || this.active;
            start.disabled = this.busy || board._cardPageGeneration !== board._refreshGeneration; }
        const panel = board.query('[data-board-selection]');
        if (!panel) return;
        panel.hidden = !this.active && !this.message;
        const count = this.selected.size;
        const loaded = new Set(board.filteredCards().filter(card => !board.collapsedLaneIds.has(card.columnId)).map(card => card.id));
        const hidden = [...this.selected.keys()].filter(id => !loaded.has(id)).length;
        const status = board.query('[data-board-selection-status]');
        const statusText = this.message || `${count} selected${hidden ? ` · ${hidden} outside the visible lanes (listed below)` : ''}`;
        if (status.textContent !== statusText) status.textContent = statusText;
        const actions = board.query('[data-board-selection-controls]');
        actions.hidden = !this.active;
        const deleteButton = board.query('[data-board-selection-action="delete"]');
        deleteButton.disabled = !count || this.busy;
        deleteButton.textContent = this.busy ? 'Deleting…' : 'Delete selected';
        board.query('[data-board-selection-action="cancel"]').disabled = this.busy;
        const list = board.query('[data-board-selection-list]');
        list.hidden = !count;
        const items = [...this.selected.values()].map(card => `<li><span>${escapeHtml(cardLabel(card))}${this.failures.has(card.id)
            ? `<span class="board-selection-error" role="alert">${escapeHtml(this.failures.get(card.id))}</span>` : ''}</span>
            <button type="button" class="btn btn-sm btn-outline-secondary" data-board-selection-action="remove"
                data-card-id="${escapeHtml(card.id)}" aria-label="Deselect ${escapeHtml(cardLabel(card))}" ${this.busy ? 'disabled' : ''}>Deselect</button></li>`).join('');
        const itemsHost = list.querySelector('ul');
        if (itemsHost.innerHTML !== items) itemsHost.innerHTML = items;
        if (hidden || this.failures.size) list.open = true;
        board.queryAll('[data-board-card-select]').forEach(input => {
            input.checked = this.selected.has(input.dataset.boardCardSelect);
            input.disabled = this.busy;
            input.closest('.board-card')?.classList.toggle('is-selected', input.checked);
        });
    }

    async deleteSelected() {
        if (!this.active || this.busy || !this.selected.size || !this.available()) return;
        const cards = [...this.selected.values()];
        const generation = this.generation;
        const root = this.board.root;
        const boardId = this.board.state.boardId;
        const current = () => generation === this.generation && root === this.board.root
            && root?.isConnected && boardId === this.board.state.boardId && this.available();
        this.busy = true; // Includes the confirmation wait: rapid clicks cannot open another batch.
        this.render();
        let deleted = 0;
        try {
            if (!await this.confirm(cardDeletionConfirmation(cards)) || !current()) return;
            this.failures.clear();
            this.message = `Deleting ${cards.length} selected ${cards.length === 1 ? 'card' : 'cards'}…`;
            this.render();
            // A bounded, sequential batch uses exactly the same authorized endpoint as Delete.
            // On navigation/filter changes, finish the in-flight request but do not send another.
            for (const card of cards) {
                if (!current()) return;
                try {
                    await this.remove(card.id);
                    if (!current()) return;
                    deleted++;
                    this.selected.delete(card.id);
                    this.board.state.cards = this.board.state.cards.filter(item => item.id !== card.id);
                } catch (error) {
                    if (!current()) return;
                    this.failures.set(card.id, error?.message || 'Could not delete this card. Try again.');
                }
            }
            this.message = `${deleted} of ${cards.length} cards deleted.${this.failures.size
                ? ` ${this.failures.size} could not be deleted and remain selected. Review the errors below, then retry.` : ''}`;
            this.board.renderLanes();
            await this.board.refresh();
        } catch (error) {
            if (current()) this.message = error?.message || 'Could not delete the selected cards. Try again.';
        } finally {
            if (current()) {
                this.busy = false;
                this.render();
                // confirmDialog cannot restore focus to its temporarily disabled opener.
                // Restore only if focus has fallen to the page, not after the user moved it.
                if (globalThis.document && document.activeElement === document.body)
                    this.board.query(`[data-board-selection-action="${this.selected.size ? 'delete' : 'cancel'}"]`)?.focus();
            }
        }
    }
}
