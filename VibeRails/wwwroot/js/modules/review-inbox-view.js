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

/**
 * viberails.ai keeps taking decisions after the first one (a rule may name several approvers, and an
 * approval after someone else's rejection is still this reviewer's to give), so only a cancelled or
 * expired request is closed to this account.
 */
export const DECIDABLE_STATUSES = Object.freeze(['open', 'approved', 'rejected']);

/** A request can be answered while viberails.ai still takes decisions, this account has not answered, and a key is saved. */
export function canAct(item, view) {
    return Boolean(view?.hasApiKey) && DECIDABLE_STATUSES.includes(item?.status) && !item?.decided && item?.signing?.outcome !== 'signing';
}

export function describeStatus(item) {
    if (item?.decided && item?.status === 'open') return 'You answered; waiting for others';
    if (!item?.decided && item?.status === 'approved') return 'Approved by another reviewer; your decision is still taken';
    if (!item?.decided && item?.status === 'rejected') return 'Rejected by another reviewer; your decision is still taken';
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

/** A path's last folder, for either separator; the counterpart of the requester's repository name. */
export function folderName(path) {
    const parts = String(path || '').split(/[\\/]+/).filter(Boolean);
    return parts.length ? parts[parts.length - 1] : '';
}

/** The clones Approve may rebuild in (this window's project first); an older backend sends only `project`. */
export function clonesOf(view) {
    if (Array.isArray(view?.clones) && view.clones.length) return view.clones;
    return view?.project ? [{ path: view.project, name: folderName(view.project), current: true }] : [];
}

/** The requester's repository is their clone's folder name, so a different name is a warning, never a refusal. */
export function sameRepository(item, clone) {
    return String(item?.repository || '').toLowerCase() === String(clone?.name || '').toLowerCase();
}

/** The reviewer's pick if it is still open, else a clone named like the requester's (this window's first), else this window's. */
export function chosenClone(item, view, selectedPath) {
    const clones = clonesOf(view);
    return clones.find(clone => clone.path === selectedPath)
        || clones.find(clone => clone.current && sameRepository(item, clone))
        || clones.find(clone => sameRepository(item, clone))
        || clones.find(clone => clone.current) || clones[0] || null;
}

/** Shown before the launch when the names differ (VIBE-213 item 7); the reviewer can still approve. */
export function cloneWarning(item, clone) {
    if (!clone || sameRepository(item, clone)) return '';
    return `This request was made in a folder named "${item.repository}". Approve rebuilds it in your "${clone.name}" folder (${clone.path}), `
        + 'so continue only if that is a clone of the same repository.';
}

export function cloneNote(item, clone) {
    const warning = cloneWarning(item, clone);
    return warning ? `<div class="ri-alert" data-tone="warning" role="note">${esc(warning)}</div>` : '';
}

/** The clone picker (only when more than one root is open, beside the requester's name) and the mismatch note. */
export function cloneChoice(item, view, selectedPath) {
    const clones = clonesOf(view);
    const chosen = chosenClone(item, view, selectedPath);
    const picker = clones.length > 1
        ? `<label class="ri-clone"><span class="ri-muted">Rebuild in</span>
            <select class="form-select form-select-sm" data-ri-clone>${clones.map(clone =>
                `<option value="${esc(clone.path)}" title="${esc(clone.path)}" ${clone.path === chosen?.path ? 'selected' : ''}>${esc(clone.name)}${clone.current ? ' (this window)' : ''}</option>`).join('')}</select>
            <span class="ri-muted">requested from ${esc(item.repository)}</span></label>`
        : '';
    return `<div class="ri-clone-choice">${picker}<div data-ri-clone-note>${cloneNote(item, chosen)}</div></div>`;
}

/** One request row. The reference is the only identity the buttons carry; the row never embeds a URL from the server except as a vetted link. */
export function requestRow(item, view, selectedPath) {
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
            ${acting && view.popupAvailable ? cloneChoice(item, view, selectedPath) : ''}
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
