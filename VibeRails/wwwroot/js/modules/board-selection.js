// The last board the user looked at. Board ids are unique across projects, so a stale id from
// another workspace simply fails to match and the first board is used. The Board view writes it;
// Settings → Integrations reads it so a Jira save/test/pull targets the board the user has open.
export const BOARD_SELECTION_STORAGE_KEY = 'viberails.board.selected.v1';

/** The remembered board when it still exists, else the first board by position. */
export function pickSelectedBoard(boards) {
    let stored = null;
    try {
        stored = localStorage.getItem(BOARD_SELECTION_STORAGE_KEY) || null;
    } catch {
        // Storage blocked: fall back to the first board.
    }
    const sorted = (boards || []).slice().sort((a, b) => a.position - b.position);
    return (stored && sorted.find(board => board.id === stored)) || sorted[0] || null;
}
