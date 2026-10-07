// Retained for consumers of the current Board view selection. It does not choose the startup board.
export const BOARD_SELECTION_STORAGE_KEY = 'viberails.board.selected.v1';

/** The first board in the saved order is the startup board. */
export function pickSelectedBoard(boards) {
    const sorted = (boards || []).slice().sort((a, b) => a.position - b.position);
    return sorted[0] || null;
}
