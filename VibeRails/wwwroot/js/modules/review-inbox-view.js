// "Reviews requested of you" (VB-50 slice D): wording and escape-first markup for the inbox page.
// Pure functions over the GET /api/v1/review-inbox view, node-tested in review-inbox-view.test.mjs.
// Every string that came from viberails.ai (repository, requester, card key) is the requester's,
// possibly an agent's, and is escaped before it touches the DOM.
import { escapeHtml, formatRelativeTime } from './utils.js';

const esc = escapeHtml;

export const STATUS_WORDING = Object.freeze({
    open: 'Waiting for your decision',
    approved: 'Approved',
    rejected: 'Rejected',
    cancelled: 'Cancelled by the requester',
    expired: 'Expired'
});

export const SIGNING_WORDING = Object.freeze({
    signing: 'Signing popup open on this computer',
    approved: 'Approved from this computer',
    not_approved: 'The signing popup closed without approving',
    popup_unavailable: 'The signing popup could not open on this computer'
});

/** A request can be answered while it is open, unanswered by this account, and a key is saved. */
export function canAct(item, view) {
    return Boolean(view?.hasApiKey) && item?.status === 'open' && !item?.decided && item?.signing?.outcome !== 'signing';
}

export function describeStatus(item) {
    if (item?.decided && item?.status === 'open') return 'You answered; waiting for others';
    return STATUS_WORDING[item?.status] || String(item?.status || 'unknown');
}

export function statusTone(item) {
    if (item?.status === 'open') return item?.decided ? 'info' : 'warning';
    if (item?.status === 'approved') return 'success';
    if (item?.status === 'rejected') return 'danger';
    return 'muted';
}

export function describeSigning(signing) {
    if (!signing?.outcome) return '';
    return SIGNING_WORDING[signing.outcome] || '';
}

/** "Checked 3 minutes ago · next automatic check in 15 min", or why there is nothing to show yet. */
export function describeRefresh(view, now = Date.now()) {
    if (!view?.hasApiKey) return 'Sign in to viberails.ai in Settings to see requests addressed to your account.';
    if (!view.fetchedUtc) return view.lastError ? `Not checked yet: ${view.lastError}` : 'Not checked yet.';
    const checked = `Checked ${formatRelativeTime(view.fetchedUtc)}`;
    const dueMs = view.nextDueUtc ? Date.parse(view.nextDueUtc) - now : NaN;
    const next = Number.isFinite(dueMs) && dueMs > 60_000
        ? ` · next automatic check in ${Math.round(dueMs / 60_000)} min`
        : Number.isFinite(dueMs) ? ' · next automatic check within a minute' : '';
    return checked + next;
}

export function emptyMessage(view) {
    if (!view?.hasApiKey) return 'Sign in to viberails.ai in Settings to see requests addressed to your account.';
    if (view.lastError && !view.fetchedUtc) return `viberails.ai could not be read: ${view.lastError}`;
    if (view.fetchedUtc && !view.emailVerified) return 'Your viberails.ai account has no verified email, so no review request can be addressed to it.';
    if (view.fetchedUtc) return 'Nobody has asked you to review a change yet.';
    return 'Checking viberails.ai for requests addressed to you…';
}

export function summarizeInbox(view) {
    const requests = Array.isArray(view?.requests) ? view.requests : [];
    const open = requests.filter(item => item.status === 'open' && !item.decided);
    const answered = requests.filter(item => item.decided);
    return { total: requests.length, open: open.length, answered: answered.length, more: Boolean(view?.more) };
}

export function badge(text, tone) {
    return `<span class="ri-badge" data-tone="${esc(tone)}">${esc(text)}</span>`;
}

/** One request row. The reference is the only identity the buttons carry; the row never embeds a URL from the server except as a vetted link. */
export function requestRow(item, view) {
    const tone = statusTone(item);
    const signing = describeSigning(item.signing);
    const acting = canAct(item, view);
    const counts = `${item.fileCount} file${item.fileCount === 1 ? '' : 's'} · ${item.sessionCount} session${item.sessionCount === 1 ? '' : 's'}`;
    // In the VS Code webview target=_blank cannot open; the controller hands the link to the host (data-ri-external).
    const readLink = isReviewPageUrl(item.readUrl)
        ? `<a class="btn btn-sm btn-outline-secondary" href="${esc(item.readUrl)}" target="_blank" rel="noopener noreferrer" data-ri-external>Read on viberails.ai</a>`
        : '';
    return `<article class="ri-row" data-ri-row="${esc(item.id)}" data-tone="${esc(tone)}">
        <div class="ri-row-main">
            <div class="ri-row-title">${esc(item.repository)}${item.cardKey ? ` <span class="ri-card">${esc(item.cardKey)}</span>` : ''}</div>
            <div class="ri-row-meta">Requested by ${esc(item.requestedBy)} · ${esc(formatRelativeTime(item.createdUtc))} · ${esc(counts)}
                · expires ${esc(formatRelativeTime(item.expiresUtc))}</div>
            <div class="ri-row-badges">${badge(describeStatus(item), tone)}${signing ? badge(signing, item.signing.outcome === 'approved' ? 'success' : item.signing.outcome === 'signing' ? 'info' : 'warning') : ''}</div>
            <div class="ri-reject-form" data-ri-reject-form hidden>
                <label class="form-label small mb-1" for="ri-comment-${esc(item.id)}">Why are you rejecting it? The requester sees this comment.</label>
                <textarea class="form-control form-control-sm" id="ri-comment-${esc(item.id)}" rows="3" maxlength="4000" data-ri-comment></textarea>
                <div class="ri-reject-actions">
                    <button type="button" class="btn btn-sm btn-danger" data-ri-action="send-reject">Send rejection</button>
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-ri-action="cancel-reject">Cancel</button>
                </div>
            </div>
        </div>
        <div class="ri-row-actions">
            ${readLink}
            <button type="button" class="btn btn-sm btn-success" data-ri-action="approve" ${acting && view.popupAvailable ? '' : 'disabled'}
                title="${esc(view.popupAvailable ? 'Opens the signing popup: it rebuilds the tree from your repository and asks for your key password' : 'The signing popup opens only on Windows with an interactive desktop')}">Approve…</button>
            <button type="button" class="btn btn-sm btn-outline-danger" data-ri-action="reject" ${acting ? '' : 'disabled'}>Reject…</button>
        </div>
    </article>`;
}

/** Only the hosted reviewer page for a well-formed reference is linked; anything else is not rendered as a link. */
export function isReviewPageUrl(url) {
    return typeof url === 'string' && /^https:\/\/viberails\.ai\/Reviews\/requests\/rvw_[0-9a-f]{32}$/.test(url);
}

export function emptyState(message) {
    return `<div class="ri-empty">${esc(message)}</div>`;
}
