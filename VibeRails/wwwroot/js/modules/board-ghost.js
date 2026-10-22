// The post-processing ghost (VIBE-185). Once a card settles in its board's last lane, the server's
// run-once pass sets `ghostReason` when an agent session worked on it but no code reached the
// default branch: `no-code` (no commit or GitHub link at all) or `not-landed` (linked commits, none
// on the default branch). The lane tile animates a ghost across the key row; the editor's Advanced
// section offers the only way to clear it. Nothing in the browser ever sets one.
import { escapeHtml } from './utils.js';

/** One readable sentence for a tooltip, accessible name or the editor note; empty without a ghost. */
export function ghostSummary(card) {
    switch (card?.ghostReason) {
        case undefined:
        case null:
        case '':
            return '';
        case 'not-landed':
            return 'Ghost: an agent worked on this card, but none of its linked commits is on the default branch';
        default:
            return 'Ghost: an agent worked on this card, but no commit or GitHub link was linked';
    }
}

/**
 * The tile's key row. Without a ghost it is the label unchanged. With one, the key and robot mark
 * fade out, the ghost runs across the row, then the key comes back (CSS `board-ghost-*`).
 */
export function ghostKeyHtml(labelHtml, card) {
    if (!ghostSummary(card)) return labelHtml;
    return `<span class="board-key-label">${labelHtml}</span><span class="board-ghost-runner" aria-hidden="true"><i class="fa-solid fa-ghost"></i></span>`;
}

/** The Advanced section's checkbox; unchecking it and saving clears the ghost. Empty without one. */
export function ghostSettingHtml(card) {
    const summary = ghostSummary(card);
    if (!summary) return '';
    return `<div class="form-check mt-2 board-ghost-setting" data-board-ghost-setting>
            <input class="form-check-input" type="checkbox" id="board-card-ghost" data-board-ghost checked>
            <label class="form-check-label" for="board-card-ghost"><i class="fa-solid fa-ghost" aria-hidden="true"></i> Ghost</label>
            <p class="board-editor-muted mb-0">${escapeHtml(summary)}. Uncheck and save to clear it.</p>
        </div>`;
}

/** The save payload's ghost field: only present while the editor shows the checkbox. */
export function readGhostField(editor) {
    const box = editor?.querySelector('[data-board-ghost]');
    return box ? { clearGhost: !box.checked } : {};
}
