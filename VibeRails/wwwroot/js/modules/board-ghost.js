// The post-processing ghost (VIBE-185). Once a card settles in its board's last lane, the server's
// run-once pass sets `ghostReason` when an agent session worked on it but no code reached the
// default branch: `no-code` (no commit or GitHub link at all) or `not-landed` (linked commits, none
// on the default branch). Recheck code verifies one card; Advanced offers explicit dismissal.
// Opening an editor never triggers a check. Nothing in the browser ever sets a ghost.
import { escapeHtml } from './utils.js';
import { cardDisplayId } from './board-card-label.js';
import { agentProvenanceHtml } from './board-agent-provenance.js';

/** Saved identity stays visible above the card's sidebar fields. */
export function cardIdentityHtml(card) {
    if (!card?.id) return '';
    const summary = ghostSummary(card);
    return `<section class="board-card-identity" aria-label="Card identity and code status">
        <label class="board-editor-label" for="board-card-display-id">Display ID</label>
        <input class="form-control form-control-sm" id="board-card-display-id" maxlength="32" spellcheck="false" value="${escapeHtml(cardDisplayId(card))}">
        <div class="board-editor-muted board-permanent-id">
            <span>Permanent ID: <code>${escapeHtml(card.key)}</code></span>
            <button type="button" class="btn btn-sm btn-outline-secondary board-permanent-id-copy" data-board-copy-permanent-id title="Copy permanent ID" aria-label="Copy permanent ID">
                <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><g data-copy-icon><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></g><path data-copy-check d="m5 12 4 4L19 6"/></svg>
            </button>
            <span class="visually-hidden" data-board-permanent-id-status role="status"></span>
        </div>
        <div class="board-card-identity-actions">
            ${agentProvenanceHtml(card)}
            <button type="button" class="btn btn-sm btn-outline-secondary board-ghost-recheck" data-board-recheck-ghost${summary ? '' : ' hidden'} title="Recheck this card's code landing" aria-label="Recheck this card's code landing">
                <i class="fa-solid fa-rotate-right" aria-hidden="true"></i>
                <span>Recheck code</span>
            </button>
        </div>
        <p class="board-editor-muted board-ghost-status" data-board-ghost-status role="status">${escapeHtml(summary)}</p>
    </section>`;
}

/** Copies the immutable key without reading the editable display ID. */
export function bindPermanentIdCopy(editor, card) {
    const button = editor.querySelector('[data-board-copy-permanent-id]');
    const status = editor.querySelector('[data-board-permanent-id-status]');
    if (!button || !status || !card?.key) return () => {};
    let timer;
    let disposed = false;
    const current = () => !disposed && editor.isConnected !== false;
    const reset = () => {
        delete button.dataset.copyState;
        button.title = 'Copy permanent ID';
        status.textContent = '';
        status.classList.add('visually-hidden');
    };
    const copy = async () => {
        if (button.disabled || !current()) return;
        clearTimeout(timer);
        reset();
        button.disabled = true;
        try {
            await navigator.clipboard.writeText(card.key);
            if (!current()) return;
            button.dataset.copyState = 'success';
            button.title = 'Copied';
            status.textContent = 'Permanent ID copied.';
            timer = setTimeout(reset, 1600);
        } catch {
            if (!current()) return;
            status.classList.remove('visually-hidden');
            status.textContent = 'Could not copy. Select the permanent ID and copy it manually.';
        } finally {
            if (current()) button.disabled = false;
        }
    };
    button.addEventListener('click', copy);
    return () => {
        disposed = true;
        clearTimeout(timer);
        button.removeEventListener('click', copy);
    };
}

/** One readable sentence for a tooltip, accessible name or the editor note; empty without a ghost. */
export function ghostSummary(card) {
    switch (card?.ghostReason) {
        case undefined:
        case null:
        case '':
            return '';
        case 'not-landed':
            return 'Ghost: an agent worked on this card, but its linked code has not been verified on the default branch';
        default:
            return 'Ghost: an agent worked on this card, but no commit or GitHub link was linked';
    }
}

/**
 * The tile's key row. A permanent ghost marks the card while a larger ghost occasionally runs
 * across the key and robot mark in a separate, clipped stage (CSS `board-ghost-*`).
 */
export function ghostKeyHtml(labelHtml, card) {
    if (!ghostSummary(card)) return labelHtml;
    return `<i class="fa-solid fa-ghost board-ghost-mark" aria-hidden="true"></i><span class="board-ghost-stage"><span class="board-key-label">${labelHtml}</span><span class="board-ghost-runner" aria-hidden="true"><i class="fa-solid fa-ghost"></i></span></span>`;
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
