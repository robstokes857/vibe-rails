import { BoardApi } from './board-api.js';
import { boardBrandLogo } from './board-brand.js';
import { enhanceSelectWithTomSelect, escapeHtml } from './utils.js';

/** Owns the Board select without replacing a user's open search on each activity poll. */
export class BoardPicker {
    constructor(select, onManage) {
        this.select = select;
        this.onManage = onManage;
        this.signature = null;
    }

    update(boards, selectedId) {
        const ordered = boards.slice().sort((a, b) => a.position - b.position);
        const signature = JSON.stringify(ordered.map(board => [board.id, board.name, Boolean(board.isJiraBoard)]));
        const select = this.select;
        if (signature !== this.signature) {
            const search = select.tomselect?.control_input?.value || '';
            const wasOpen = Boolean(select.tomselect?.isOpen);
            select.tomselect?.destroy();
            select.innerHTML = ordered.map(board =>
                `<option value="${escapeHtml(board.id)}">${escapeHtml(board.name)}</option>`).join('');
            select.value = selectedId || '';
            const jiraBoards = new Set(ordered.filter(board => board.isJiraBoard).map(board => board.id));
            const renderBoard = (data, escape) => `<div><span class="board-picker-label">${boardBrandLogo(jiraBoards.has(data.value))}<span class="board-picker-name">${escape(data.text)}</span></span></div>`;
            const ts = enhanceSelectWithTomSelect(select, {
                placeholder: 'Select a board',
                searchPlaceholder: 'Search boards...',
                emptyMessage: 'No matching boards.',
                customizeLabel: 'Manage boards',
                onCustomize: this.onManage,
                render: { option: renderBoard, item: renderBoard }
            });
            if (ts) {
                ts.wrapper.classList.add('board-picker-control');
                ts.dropdown.classList.add('board-picker-dropdown');
                ts.control.setAttribute('aria-label', 'Board');
                if (search) { ts.setTextboxValue(search); ts.refreshOptions(false); }
                if (wasOpen) ts.open();
            }
            this.signature = signature;
        }
        if (select.tomselect) {
            if (select.tomselect.getValue() !== (selectedId || '')) select.tomselect.setValue(selectedId || '', true);
        } else select.value = selectedId || '';
        select.title = ordered.find(board => board.id === selectedId)?.name || 'Switch board';
    }

    dispose() {
        this.select.tomselect?.destroy();
    }
}

/** A saved, project-wide order; closing the dialog leaves an unsaved order untouched. */
export function openBoardOrderManager(app, onSaved) {
    const abort = new AbortController();
    app.showModal('Manage boards', `
        <section data-board-order>
            <p class="board-editor-muted">Move boards up or down. The first board opens by default when you open Vibe Board.</p>
            <p role="status" data-board-order-status>Loading boards…</p>
            <fieldset data-board-order-fields disabled>
                <div data-board-order-list></div>
                <div class="d-flex justify-content-end gap-2 mt-3">
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-board-order-reload>Reload boards</button>
                    <button type="button" class="btn btn-sm btn-primary" data-board-order-save disabled>Save order</button>
                </div>
            </fieldset>
        </section>`, { onClose: () => abort.abort() });
    const root = document.querySelector('#modal-container [data-board-order]');
    if (!root) return () => abort.abort();
    const active = () => !abort.signal.aborted && root.isConnected;
    const fields = root.querySelector('[data-board-order-fields]');
    const list = root.querySelector('[data-board-order-list]');
    const status = root.querySelector('[data-board-order-status]');
    let boards = [];
    let busy = false;
    const render = () => {
        list.innerHTML = boards.map((board, index) => `
            <div class="board-order-row" data-board-order-id="${escapeHtml(board.id)}">
                <span class="board-order-name">${escapeHtml(board.name)}${index === 0 ? ' <span class="badge text-bg-secondary">Default</span>' : ''}</span>
                <div class="btn-group btn-group-sm" role="group" aria-label="Order ${escapeHtml(board.name)}">
                    <button type="button" class="btn btn-outline-secondary" data-board-order-move="up"
                        aria-label="Move ${escapeHtml(board.name)} up" ${index === 0 ? 'disabled' : ''}><i class="fa-solid fa-arrow-up" aria-hidden="true"></i></button>
                    <button type="button" class="btn btn-outline-secondary" data-board-order-move="down"
                        aria-label="Move ${escapeHtml(board.name)} down" ${index === boards.length - 1 ? 'disabled' : ''}><i class="fa-solid fa-arrow-down" aria-hidden="true"></i></button>
                </div>
            </div>`).join('');
        root.querySelector('[data-board-order-save]').disabled = !boards.length;
    };
    const load = async () => {
        if (busy) return;
        busy = true;
        fields.disabled = true;
        status.textContent = 'Loading boards…';
        try {
            const result = await BoardApi.getBoardsAsync({ signal: abort.signal });
            if (!active()) return;
            boards = result;
            render();
            status.textContent = '';
        } catch (error) {
            if (active()) status.textContent = error?.message || 'Could not load boards. Try Reload boards.';
        } finally {
            busy = false;
            if (active()) fields.disabled = false;
        }
    };
    root.addEventListener('click', async event => {
        if (busy) return;
        const move = event.target.closest('[data-board-order-move]');
        if (move) {
            const id = move.closest('[data-board-order-id]').dataset.boardOrderId;
            const index = boards.findIndex(board => board.id === id);
            const target = index + (move.dataset.boardOrderMove === 'up' ? -1 : 1);
            if (index < 0 || target < 0 || target >= boards.length) return;
            [boards[index], boards[target]] = [boards[target], boards[index]];
            render();
            const row = [...list.children].find(item => item.dataset.boardOrderId === id);
            (row.querySelector(`[data-board-order-move="${move.dataset.boardOrderMove}"]:not(:disabled)`)
                || row.querySelector('button:not(:disabled)'))?.focus();
            status.textContent = 'Order changed. Save order to apply.';
            return;
        }
        if (event.target.closest('[data-board-order-reload]')) { void load(); return; }
        if (!event.target.closest('[data-board-order-save]')) return;
        busy = true;
        fields.disabled = true;
        status.textContent = 'Saving order…';
        try {
            const saved = await BoardApi.reorderBoardsAsync(boards.map(board => board.id), { signal: abort.signal });
            if (!active()) return;
            app.closeModal();
            onSaved(saved);
            app.showToast('Board', 'Board order saved.', 'success');
        } catch (error) {
            if (active()) status.textContent = error?.message || 'Could not save the order. Try again or reload the boards.';
        } finally {
            busy = false;
            if (active()) fields.disabled = false;
        }
    }, { signal: abort.signal });
    void load();
    return () => abort.abort();
}
