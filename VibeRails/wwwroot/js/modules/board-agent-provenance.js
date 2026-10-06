// Who made an agent-made card (VIBE-96): the agent's name, the VibeRails session it worked in and
// when. create_board_card stores the name and session once, beside the agentMade mark; the time is
// the card's own createdAt. Cards an agent made before VIBE-96 carry only the mark and read
// "Made by an agent". Every value is untrusted card data, so all of it is escaped here.
import { escapeHtml } from './utils.js';

/** The agent's name as shown, or the generic "an agent". */
export function agentMakerName(card) {
    return card?.agentMadeBy || 'an agent';
}

/** Date and time with the year: a card can be read long after it was made. Empty for a bad value. */
export function formatMadeAt(iso) {
    const date = new Date(iso);
    if (!iso || Number.isNaN(date.getTime())) return '';
    return date.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
}

/** One line for a tooltip or an accessible name: "Made by Claude Opus 5.5 · Oct 5, 2026, 2:31 PM". */
export function agentMadeSummary(card) {
    const when = formatMadeAt(card?.createdAt);
    return `Made by ${agentMakerName(card)}${when ? ` · ${when}` : ''}`;
}

/**
 * The card editor's provenance block; empty for a human-made card. The session button replays the
 * agent's terminal seeked to the moment the card was made, like a comment's "in session" link.
 */
export function agentProvenanceHtml(card) {
    if (!card?.agentMade) return '';
    const when = formatMadeAt(card.createdAt);
    const session = String(card.agentMadeSessionId || '');
    const sessionButton = session
        ? `<button type="button" class="board-comment-jump" data-board-agent-session="${escapeHtml(session)}"
            data-board-agent-session-at="${escapeHtml(card.createdAt || '')}"
            title="Replay session ${escapeHtml(session)} at the moment this card was made"
            aria-label="Replay session ${escapeHtml(session)} at the moment this card was made">
            <i class="fa-solid fa-clock-rotate-left" aria-hidden="true"></i> Session ${escapeHtml(session.slice(0, 8))}</button>`
        : '';
    return `<div class="board-editor-muted board-agent-provenance" data-board-agent-provenance>
            <div><i class="fa-solid fa-robot board-agent-mark" aria-hidden="true"></i> Made by <strong>${escapeHtml(agentMakerName(card))}</strong></div>
            ${when || sessionButton ? `<div>${when ? `<time datetime="${escapeHtml(card.createdAt)}">${escapeHtml(when)}</time>` : ''}${when && sessionButton ? ' · ' : ''}${sessionButton}</div>` : ''}
        </div>`;
}

/** Wires the session button inside `root`; `openReplay(sessionId, seekToUtc)` opens the replay. */
export function bindAgentProvenance(root, openReplay) {
    root?.querySelectorAll('[data-board-agent-session]').forEach(button => {
        button.addEventListener('click', event => {
            event.stopPropagation();
            openReplay(button.dataset.boardAgentSession, button.dataset.boardAgentSessionAt || null);
        });
    });
}
