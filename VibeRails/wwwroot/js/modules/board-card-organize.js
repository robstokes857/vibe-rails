import { BoardApi } from './board-api.js';
import { escapeHtml, confirmDialog } from './utils.js';
import { cardLabel } from './board-card-label.js';

export const cardOrganizeSection = () => `
    <section class="board-side-section" data-board-organize>
        <details><summary class="board-side-label">Move or merge</summary>
            <label class="board-editor-label mt-2">Move to board
                <select class="form-select form-select-sm" data-move-board aria-label="Destination board"></select>
            </label>
            <select class="form-select form-select-sm mt-2" data-move-lane aria-label="Destination lane"></select>
            <button class="btn btn-sm btn-outline-secondary mt-2" type="button" data-move-card>Move card</button>
            <hr>
            <label class="board-editor-label">Merge this card into
                <input class="form-control form-control-sm" data-merge-search placeholder="Find a card by ID or title" autocomplete="off">
            </label>
            <select class="form-select form-select-sm mt-2" data-merge-target aria-label="Merge destination"></select>
            <p class="board-editor-muted mt-2">Keep the destination's title and settings. Combine descriptions, comments and linked activity, then delete this card.</p>
            <button class="btn btn-sm btn-outline-secondary" type="button" data-merge-card>Merge cards</button>
            <p class="board-editor-muted mt-2" data-organize-status role="status"></p>
        </details>
    </section>`;

export function bindCardOrganization(editor, card, { hasDraft, onChanged }) {
    const host = editor.querySelector('[data-board-organize]');
    if (!host || !card?.id) return () => {};
    const board = host.querySelector('[data-move-board]');
    const lane = host.querySelector('[data-move-lane]');
    const search = host.querySelector('[data-merge-search]');
    const target = host.querySelector('[data-merge-target]');
    const status = host.querySelector('[data-organize-status]');
    const abort = new AbortController();
    let disposed = false, busy = false, boards = [], timer, generation = 0;
    const alive = () => !disposed && editor.isConnected !== false;
    function lanes() {
        lane.innerHTML = (boards.find(item => item.id === board.value)?.columns || [])
            .map(item => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)}</option>`).join('');
    }
    async function find() {
        const current = ++generation;
        try {
            const cards = await BoardApi.getCardLinkCandidatesAsync(null, search.value.trim(), { signal: abort.signal });
            if (!alive() || generation !== current) return;
            target.innerHTML = '<option value="">Choose a card</option>' + cards.filter(item => item.id !== card.id)
                .map(item => `<option value="${escapeHtml(item.id)}">${escapeHtml(cardLabel(item))} · ${escapeHtml(item.boardName)}</option>`).join('');
        } catch (error) { if (alive() && generation === current) status.textContent = error.message || 'Card search failed.'; }
    }
    async function run(kind) {
        if (busy || !alive()) return;
        if (hasDraft()) { status.textContent = 'Save your card edits and post or clear your comment before moving or merging.'; return; }
        const destination = kind === 'move' ? lane.value : target.value;
        if (!destination) { status.textContent = 'Choose a destination first.'; return; }
        busy = true;
        editor._boardOrganizing = true;
        let lockedControls = [];
        host.querySelectorAll('button, select, input').forEach(button => { button.disabled = true; });
        try {
            let message;
            if (kind === 'move') {
                const automation = await BoardApi.getLaneAutomationAsync(destination, { signal: abort.signal });
                if (!alive()) return;
                const jobs = automation?.jobIds || (automation?.jobId ? [automation.jobId] : []);
                message = `Move this card to ${board.selectedOptions[0]?.textContent} / ${lane.selectedOptions[0]?.textContent}?`
                    + (jobs.length ? ` Entering this lane can run ${jobs.length} configured Automation${jobs.length === 1 ? '' : 's'}.` : '');
            } else {
                message = `Merge this card into ${target.selectedOptions[0]?.textContent}? Its description, comments, files, sessions, commits and links will be copied. The destination's settings are kept, and this card is deleted.`;
            }
            if (!await confirmDialog({ title: kind === 'move' ? 'Move card' : 'Merge cards', message, confirmLabel: kind === 'move' ? 'Move' : 'Merge', danger: kind === 'merge' })) return;
            if (!alive()) return;
            if (hasDraft()) { status.textContent = 'Save your edits before moving or merging.'; return; }
            // The successful response replaces this editor. Prevent a new draft while the
            // mutation is in flight, after the final draft check and before sending it.
            lockedControls = [...editor.querySelectorAll('button, select, input, textarea')]
                .filter(control => !control.disabled);
            lockedControls.forEach(control => { control.disabled = true; });
            const result = kind === 'move'
                ? await BoardApi.moveBoardCardAsync(card.id, { columnId: destination })
                : await BoardApi.mergeBoardCardsAsync(card.id, destination);
            if (alive()) await onChanged(result);
        } catch (error) { if (alive()) status.textContent = error.message || 'The card could not be changed.'; }
        finally {
            busy = false;
            editor._boardOrganizing = false;
            lockedControls.forEach(control => { control.disabled = false; });
            if (alive()) host.querySelectorAll('button, select, input').forEach(button => { button.disabled = false; });
        }
    }
    board.addEventListener('change', lanes);
    search.addEventListener('input', () => { ++generation; clearTimeout(timer); timer = setTimeout(find, 200); });
    host.querySelector('[data-move-card]').addEventListener('click', () => void run('move'));
    host.querySelector('[data-merge-card]').addEventListener('click', () => void run('merge'));
    void (async () => {
        try {
            boards = await BoardApi.getBoardsAsync();
            if (!alive()) return;
            board.innerHTML = boards.map(item => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)}</option>`).join('');
            board.value = card.boardId;
            lanes(); lane.value = card.columnId;
            await find();
        } catch (error) { if (alive()) status.textContent = error.message || 'Boards could not be loaded.'; }
    })();
    return () => { disposed = true; ++generation; abort.abort(); clearTimeout(timer); };
}
