// Request review from a card (VB-50 slice E). A details section beside Share card: what the staged
// index would send (files with +/− counts and omissions, the tree id, the sharing disclosure),
// reviewers prefilled from the Signed Approval rules the staged paths touch, the card's Sessions rail
// as a picker, an optional note, and Create. Below it, the project's requests with their status, the
// approval trailer (Copy), Send (after the disclosure is confirmed), Cancel and Delete, and under a
// decision the reviewer's line comments grouped by file (slice F). Nothing here signs or decides;
// Send is the only step that transfers anything, and it asks first.
//
// Routes: /api/v1/review-requests (list, create), /staged (preview), /{id}/disclosure, /{id}/send,
// /{id}/cancel, DELETE /{id}, /commit-approvals?sha=… (which linked commits carry an approved tree).
// Every string from the server is escaped before it reaches markup.
import { escapeHtml, confirmDialog } from './utils.js';
import { cardDisplayId, shortCardKey } from './board-card-label.js';

const API = '/api/v1/review-requests';
export const REVIEWER_LIMIT = 10;
export const SESSION_LIMIT = 10;
export const NOTE_LIMIT = 2000;
/** The commit rail shows at most 30 commits; the route takes 50. */
const COMMIT_LOOKUP_LIMIT = 50;

export function parseReviewers(text) {
    return [...new Set(String(text || '').split(/[\s,;]+/).map(part => part.trim()).filter(Boolean))];
}

/** Every name a card answers to: its key, display id and the key's short form, lowercased. */
export function cardKeySet(card) {
    return new Set([card?.key, cardDisplayId(card), shortCardKey(card?.key)].filter(Boolean).map(value => String(value).toLowerCase()));
}

export function isForCard(request, card) {
    const key = String(request?.cardKey || '').toLowerCase();
    return Boolean(key) && cardKeySet(card).has(key);
}

/** This card's requests first, each group newest first. */
export function orderRequests(requests, card) {
    const list = Array.isArray(requests) ? [...requests] : [];
    const time = request => String(request?.createdUtc || '');
    return list.sort((a, b) => (isForCard(b, card) - isForCard(a, card)) || time(b).localeCompare(time(a)));
}

/** The card's own sessions, as the Sessions rail lists them (never Automation or review recordings). */
export function pickableSessions(card) {
    return (card?.sessions || []).filter(session => session && !session.isAutomation && !session.isReview && session.origin !== 'code_review');
}

function decisions(request, kind) {
    return (Array.isArray(request?.decisions) ? request.decisions : []).filter(decision => decision?.decision === kind);
}

/** The newest verified approval's commit-message line, ready to paste. */
export function approvalTrailer(request) {
    const approved = decisions(request, 'approved').filter(decision => typeof decision.trailer === 'string' && decision.trailer);
    return approved.length ? approved[approved.length - 1].trailer : null;
}

/** Line comments grouped by file in path order, each file's comments by side (before, then after) and line. */
export function groupLineComments(comments) {
    const groups = new Map();
    for (const comment of Array.isArray(comments) ? comments : []) {
        if (!comment || typeof comment.path !== 'string' || !comment.path) continue;
        if (!groups.has(comment.path)) groups.set(comment.path, []);
        groups.get(comment.path).push(comment);
    }
    const sideOrder = side => side === 'before' ? 0 : 1;
    return [...groups.entries()].sort((a, b) => a[0].localeCompare(b[0])).map(([path, list]) => ({
        path,
        comments: list.sort((a, b) => (sideOrder(a.side) - sideOrder(b.side)) || ((Number(a.line) || 0) - (Number(b.line) || 0)))
    }));
}

/** A decision's line comments grouped by file, every string escaped; empty when there are none. */
export function lineCommentsHtml(decision) {
    const groups = groupLineComments(decision?.lineComments);
    if (!groups.length) return '';
    return `<ul class="board-review-line-comments" data-card-review-line-comments>${groups.map(group => `<li>
        <span class="board-review-path" title="${escapeHtml(group.path)}">${escapeHtml(group.path)}</span>
        <ul>${group.comments.map(comment => `<li><code title="${comment.side === 'before' ? 'Base text' : 'Staged text'}, line ${escapeHtml(String(comment.line))}">${comment.side === 'before' ? '−' : '+'}L${escapeHtml(String(comment.line))}</code> <span class="board-review-line-comment-text">${escapeHtml(comment.text)}</span></li>`).join('')}</ul>
    </li>`).join('')}</ul>`;
}

export function describeRequestStatus(request) {
    const status = String(request?.status || '');
    if (status === 'cancelled') return 'Cancelled';
    if (status === 'approved') {
        const people = [...new Set(decisions(request, 'approved').map(decision => decision.reviewer).filter(Boolean))];
        return people.length ? `Approved by ${people.join(', ')}` : 'Approved';
    }
    if (status === 'rejected') {
        const rejected = decisions(request, 'rejected');
        const last = rejected[rejected.length - 1];
        if (!last) return 'Rejected';
        return `Rejected by ${last.reviewer}${last.comment ? `: ${last.comment}` : ''}`;
    }
    return request?.remote ? 'Sent, awaiting decisions' : 'Open, not sent yet';
}

export function requestTone(request) {
    const status = String(request?.status || '');
    if (status === 'approved') return 'success';
    if (status === 'rejected') return 'danger';
    if (status === 'cancelled') return 'secondary';
    return request?.remote ? 'info' : 'warning';
}

/**
 * "+3 −1", "±4" when only the snapshot's count is known, with the omission reason when the text is not
 * sent and whether the file's exact bytes are sent instead (VIBE-211), because a file without either
 * cannot be approved.
 */
export function formatFileChange(file) {
    const parts = [];
    if (Number.isInteger(file?.added) && Number.isInteger(file?.removed)) parts.push(`+${file.added} −${file.removed}`);
    else if (Number.isInteger(file?.changedLines)) parts.push(`±${file.changedLines}`);
    if (file?.textIncluded === false) parts.push(`no text (${file.omitted || 'omitted'})${file.bytesIncluded ? ', bytes sent' : ', not approvable'}`);
    return parts.join(' · ');
}

function kib(bytes) {
    const value = Number(bytes) || 0;
    return value < 1024 ? `${value} B` : `${(value / 1024).toFixed(value < 10240 ? 1 : 0)} KiB`;
}

export function describeCounts(preview) {
    const files = Array.isArray(preview?.files) ? preview.files.length : 0;
    return `${files} staged file${files === 1 ? '' : 's'}: ${preview?.filesWithText ?? 0} with text (${kib(preview?.textBytes)}), ${describeWithout(preview)}`;
/** "N without" grows into "N without text, M of them as bytes (x KiB)" when bytes travel for files listed without text. */
function describeWithout(counts) {
    const without = counts?.filesWithoutText ?? 0;
    const withBytes = counts?.filesWithBytes ?? 0;
    if (!without || !withBytes) return `${without} without`;
    return `${without} without text, ${withBytes} of ${withBytes === 1 ? 'them' : 'those'} as exact bytes (${kib(counts?.carriedBytes)})`;
}

}

/** The header badge for this card: its newest request's state, or nothing. */
export function reviewStatusBadge(requests, card) {
    const own = orderRequests(requests, card).filter(request => isForCard(request, card) && request.status !== 'cancelled');
    if (!own.length) return '';
    const request = own[0];
    const label = request.status === 'open' ? (request.remote ? 'Review requested' : 'Review request not sent') : `Review ${describeRequestStatus(request).replace(/^./, c => c.toLowerCase())}`;
    return `<span class="badge text-bg-${requestTone(request)} board-review-status-badge" data-board-review-status title="${escapeHtml(describeRequestStatus(request))}">${escapeHtml(label)}</span>`;
}

/** The commit-rail badge for one linked commit: who approved its tree, or nothing. */
export function commitApprovalBadge(approvals) {
    const people = [...new Set((Array.isArray(approvals) ? approvals : []).map(approval => approval?.reviewer).filter(Boolean))];
    if (!people.length) return '';
    return `<span class="badge text-bg-success board-review-approved" title="A reviewer signed this commit's tree through a review request">Approved by ${escapeHtml(people.join(', '))}</span>`;
}

export function previewHtml(preview) {
    const files = Array.isArray(preview?.files) ? preview.files : [];
    const rules = Array.isArray(preview?.rules) ? preview.rules : [];
    return `<p class="board-side-sub mb-1" data-card-review-counts>${escapeHtml(describeCounts(preview))} · tree <code title="${escapeHtml(preview.treeId || '')}">${escapeHtml(String(preview.treeId || '').slice(0, 12))}</code></p>
        <ul class="board-review-files" data-card-review-files>${files.map(file => `<li>
            <span class="board-review-path" title="${escapeHtml(file.path)}">${escapeHtml(file.change)} ${escapeHtml(file.path)}${file.previousPath ? ` <span class="board-side-sub">(from ${escapeHtml(file.previousPath)})</span>` : ''}</span>
            <span class="board-review-counts${file.textIncluded === false ? ' is-omitted' : ''}">${escapeHtml(formatFileChange(file))}</span>
        </li>`).join('')}</ul>
        ${rules.length ? `<p class="board-side-empty mb-1" data-card-review-rules>Signed Approval ${rules.length === 1 ? 'rule' : 'rules'} covering these paths: ${rules.map(rule => `<code>${escapeHtml(rule.path)}</code> (${escapeHtml(rule.approvers.join(', '))}, ${escapeHtml(rule.sourcePath)})`).join('; ')}. Those approvers are filled in below.</p>`
        : '<p class="board-side-empty mb-1" data-card-review-rules>No Signed Approval rule covers these paths; name the reviewers yourself.</p>'}`;
}

function sessionsHtml(card) {
    const sessions = pickableSessions(card);
    if (!sessions.length) return '<p class="board-side-empty mb-0">No sessions are linked to this card. A request can still be created from the staged changes alone.</p>';
    return sessions.map((session, index) => `<label class="form-check-label d-block text-break">
        <input type="checkbox" class="form-check-input me-1" data-card-review-session value="${escapeHtml(session.id)}"${index < SESSION_LIMIT ? ' checked' : ''}>
        ${escapeHtml(session.displayName || session.id)}${session.cli ? ` <span class="board-side-sub">· ${escapeHtml(session.cli)}</span>` : ''}${session.active ? ' <span class="board-side-sub">· still running: uploads after it ends</span>' : ''}
    </label>`).join('') + `<p class="board-side-empty mb-0">Up to ${SESSION_LIMIT} sessions. Each uploads to your viberails.ai account only after Send, and only after it ends.</p>`;
}

export function requestReviewSection(saved) {
    if (!saved) return '<section class="board-side-section"><h3 class="board-side-label">Request review</h3><p class="board-side-empty">Save the card to request a review of the staged changes.</p></section>';
    return `<section class="board-side-section"><details data-card-review-request>
        <summary class="board-side-label"><i class="fa-solid fa-user-check" aria-hidden="true"></i> Request review</summary>
        <p class="board-side-empty mt-2">Ask named people to review the staged changes, and the sessions that produced them, before you commit. They read on viberails.ai and approve by signing the staged tree on their own computer; that approval is the vibe-signature line Git Guard applies at commit. Any change to the staged content after that needs a new approval.</p>
        <div data-card-review-preview><p class="board-side-empty">Reading the staged changes…</p></div>
        <label class="board-editor-label">Reviewers <textarea class="form-control form-control-sm" rows="2" spellcheck="false" data-card-review-reviewers placeholder="name@example.com, one per line or comma-separated"></textarea></label>
        <p class="board-side-empty mb-0">Up to ${REVIEWER_LIMIT} people. Each signs in to viberails.ai with that verified email address; nobody is notified or looked up.</p>
        <fieldset class="mt-2" data-card-review-sessions-wrap><legend class="board-editor-label">Sessions to include</legend><div data-card-review-sessions></div></fieldset>
        <label class="board-editor-label mt-2">Note for the reviewers (optional) <textarea class="form-control form-control-sm" rows="2" maxlength="${NOTE_LIMIT}" data-card-review-note placeholder="What changed and what to look at"></textarea></label>
        <p class="board-side-empty mt-2" data-card-review-disclosure>Creating a request keeps it on this computer. Send, below, is what transfers it: the files above with their text, the tree and base ids, the note and the reviewers to viberails.ai, readable only by the listed reviewers for one month, and each included session's terminal output, prompts and saved code changes after it ends.</p>
        <div class="d-flex flex-wrap gap-2 mt-2">
            <button type="button" class="btn btn-sm btn-outline-primary" data-review-action="create">Create request</button>
            <button type="button" class="btn btn-sm btn-outline-secondary" data-review-action="reload">Reload</button>
        </div>
        <p class="board-side-empty mt-2" role="status" aria-live="polite" data-card-review-message></p>
        <h4 class="board-editor-label mt-2">Requests in this project</h4>
        <div class="board-side-list" data-card-review-requests></div>
    </details></section>`;
}

export function requestRowHtml(request, card) {
    const status = describeRequestStatus(request);
    const trailer = approvalTrailer(request);
    const rejections = decisions(request, 'rejected');
    const commented = (Array.isArray(request.decisions) ? request.decisions : []).filter(decision => Array.isArray(decision?.lineComments) && decision.lineComments.length);
    const created = request.createdUtc ? new Date(request.createdUtc).toLocaleString() : '';
    const meta = [created, `${Number(request.fileCount) || 0} file${request.fileCount === 1 ? '' : 's'}`, `${Number(request.sessionCount) || 0} session${request.sessionCount === 1 ? '' : 's'}`,
        request.cardKey ? (isForCard(request, card) ? 'this card' : `card ${request.cardKey}`) : 'no card',
        request.remote?.expiresUtc ? `reviewers' access ends ${new Date(request.remote.expiresUtc).toLocaleDateString()}` : ''].filter(Boolean);
    const open = request.status === 'open';
    return `<div class="board-card-review-request" data-card-review-id="${escapeHtml(request.id)}" data-tone="${requestTone(request)}">
        <div class="d-flex flex-wrap align-items-center gap-1">
            <span class="badge text-bg-${requestTone(request)}" data-card-review-status>${escapeHtml(status)}</span>
            <span class="board-side-sub text-break">${escapeHtml(meta.join(' · '))}</span>
        </div>
        <p class="board-side-empty text-break mb-1">Reviewers: ${escapeHtml((request.reviewers || []).join(', '))} · tree <code title="${escapeHtml(request.treeId || '')}">${escapeHtml(String(request.treeId || '').slice(0, 12))}</code></p>
        ${rejections.length && request.status !== 'rejected' ? `<p class="board-side-empty text-break mb-1">${rejections.map(decision => `Rejected by ${escapeHtml(decision.reviewer)}${decision.comment ? `: ${escapeHtml(decision.comment)}` : ''}`).join('; ')}</p>` : ''}
        ${commented.map(decision => `<div class="board-review-decision-comments"><span class="board-side-sub">${decision.decision === 'approved' ? 'Approved' : 'Rejected'} by ${escapeHtml(decision.reviewer)} · ${decision.lineComments.length} line comment${decision.lineComments.length === 1 ? '' : 's'}</span>${lineCommentsHtml(decision)}</div>`).join('')}
        ${trailer ? `<input class="form-control form-control-sm mt-1" aria-label="Approval trailer" readonly value="${escapeHtml(trailer)}" data-card-review-trailer>` : ''}
        <div class="d-flex flex-wrap gap-2 mt-2">
            ${trailer ? '<button type="button" class="btn btn-sm btn-outline-secondary" data-review-action="copy">Copy approval</button>' : ''}
            ${open && !request.remote ? '<button type="button" class="btn btn-sm btn-outline-primary" data-review-action="send">Send…</button>' : ''}
            ${open ? '<button type="button" class="btn btn-sm btn-outline-secondary" data-review-action="cancel">Cancel request</button>' : ''}
            <button type="button" class="btn btn-sm btn-outline-danger" data-review-action="delete">Delete</button>
        </div>
    </div>`;
}

/** The text a person confirms before anything leaves the computer. Plain text: confirmDialog renders it as text. */
export function describeDisclosure(disclosure) {
    const files = Array.isArray(disclosure?.files) ? disclosure.files : [];
    const sessions = Array.isArray(disclosure?.sessions) ? disclosure.sessions : [];
    const omitted = files.filter(file => file.textIncluded === false);
    const lines = [
        `To ${disclosure.destination}, readable only by ${(disclosure.reviewers || []).join(', ')} after signing in with that verified email, until ${new Date(disclosure.expiresUtc).toLocaleDateString()}.`,
        `${files.length} file${files.length === 1 ? '' : 's'} of tree ${String(disclosure.treeId || '').slice(0, 12)} with their text (${kib(disclosure.textBytes)})${omitted.length ? `; ${omitted.length} listed without text (${[...new Set(omitted.map(file => file.omitted))].join(', ')})${bytesNote}` : ''}: ${files.map(file => file.path).join(', ')}.`,
    const asBytes = omitted.filter(file => file.bytesIncluded);
    // Bytes travel for files the reviewer cannot read as text (VIBE-211): they are what lets the reviewer's VibeRails rebuild the tree.
    const bytesNote = asBytes.length
        ? `, ${asBytes.length === omitted.length ? (asBytes.length === 1 ? 'its' : 'their') : `${asBytes.length} of them with their`} exact bytes (${kib(disclosure.carriedBytes)}) so the reviewer's VibeRails can rebuild the tree`
        : '';
        sessions.length ? `${sessions.length} session${sessions.length === 1 ? '' : 's'}: ${sessions.map(session => `${session.name}${session.live ? ' (still running: uploads after it ends)' : ''}`).join(', ')}. ${disclosure.sessionDisclosure || ''}` : 'No sessions attached.',
        disclosure.note ? `Note: ${disclosure.note}` : ''
    ];
    return lines.filter(Boolean).join('\n\n');
}

export function bindRequestReview(editor, card, { app, hasDraft = () => false, onCommitApprovals = null }) {
    const host = editor.querySelector('[data-card-review-request]');
    if (!host || !card?.id) return null;
    const node = selector => host.querySelector(selector);
    const message = node('[data-card-review-message]');
    const list = node('[data-card-review-requests]');
    const previewHost = node('[data-card-review-preview]');
    const reviewers = node('[data-card-review-reviewers]');
    const note = node('[data-card-review-note]');
    const sessionsHost = node('[data-card-review-sessions]');
    if (sessionsHost) sessionsHost.innerHTML = sessionsHtml(card);
    let disposed = false, busy = false, previewLoaded = false, reviewersTouched = false, generation = 0, request;
    let requests = [];
    const current = () => !disposed && host.isConnected;
    const call = (suffix = '', method = 'GET', body = null, extra = {}) => app.apiCall(API + suffix, method, body,
        { showLoading: false, preferErrorResponseMessage: true, ...extra });
    const say = text => { if (current() && message) message.textContent = text; };
    const setBusy = value => {
        busy = value;
        if (current()) (host.querySelectorAll?.('button, input, textarea') ?? []).forEach(element => { element.disabled = value; });
    };
    const header = () => editor.closest?.('.modal-content')?.querySelector?.('.modal-header') ?? null;

    function renderBadge() {
        const bar = header();
        if (!bar) return;
        bar.querySelector('[data-board-review-status]')?.remove();
        const html = reviewStatusBadge(requests, card);
        if (!html) return;
        const close = bar.querySelector('.btn-close');
        const span = document.createElement('span');
        span.innerHTML = html;
        const badge = span.firstElementChild;
        badge.classList.add('ms-auto', 'me-2');
        if (close) close.before(badge); else bar.appendChild(badge);
    }
    function renderRequests() {
        if (!list) return;
        const ordered = orderRequests(requests, card);
        list.innerHTML = ordered.length ? ordered.map(item => requestRowHtml(item, card)).join('')
            : '<p class="board-side-empty">No review requests in this project.</p>';
        renderBadge();
    }
    async function loadRequests(keepMessage = true) {
        if (!current()) return;
        const version = ++generation;
        request?.abort();
        request = new AbortController();
        try {
            const result = await call('', 'GET', null, { signal: request.signal });
            if (!current() || version !== generation) return;
            requests = Array.isArray(result?.requests) ? result.requests : [];
            renderRequests();
            if (!keepMessage) say('');
        } catch (error) {
            if (current() && version === generation && error?.name !== 'AbortError') say(error?.message || 'Could not load review requests.');
        }
    }
    async function loadCommitApprovals() {
        const shas = [...new Set((card.commits || []).map(commit => String(commit?.sha || '').trim().toLowerCase()).filter(sha => /^[0-9a-f]{7,64}$/.test(sha)))].slice(0, COMMIT_LOOKUP_LIMIT);
        if (!shas.length || typeof onCommitApprovals !== 'function') return;
        try {
            const result = await call(`/commit-approvals?${shas.map(sha => `sha=${sha}`).join('&')}`, 'GET');
            if (!current()) return;
            const map = {};
            for (const entry of result?.commits || []) if (entry?.sha && Array.isArray(entry.approvals) && entry.approvals.length) map[String(entry.sha).toLowerCase()] = entry.approvals;
            onCommitApprovals(map);
        } catch { /* The rail simply shows no approval badge. */ }
    }
    async function loadPreview() {
        if (!current() || !previewHost) return;
        previewHost.innerHTML = '<p class="board-side-empty">Reading the staged changes…</p>';
        try {
            const preview = await call('/staged', 'GET');
            if (!current()) return;
            previewLoaded = true;
            previewHost.innerHTML = previewHtml(preview);
            if (reviewers && !reviewersTouched && !reviewers.value.trim() && Array.isArray(preview.suggestedReviewers) && preview.suggestedReviewers.length)
                reviewers.value = preview.suggestedReviewers.join('\n');
        } catch (error) {
            if (!current()) return;
            previewLoaded = false;
            previewHost.innerHTML = `<p class="board-side-empty" data-card-review-preview-error>${escapeHtml(error?.message || 'The staged changes could not be read.')}</p>`;
        }
    }
    async function create() {
        if (hasDraft()) { say('Save the card and post any draft comments before requesting a review. Your drafts are still here.'); return; }
        const people = parseReviewers(reviewers?.value);
        if (!people.length) { say('Name at least one reviewer by email address.'); return; }
        if (people.length > REVIEWER_LIMIT) { say(`Name up to ${REVIEWER_LIMIT} reviewers.`); return; }
        const sessions = [...(host.querySelectorAll?.('[data-card-review-session]:checked') ?? [])].map(input => input.value).slice(0, SESSION_LIMIT + 1);
        if (sessions.length > SESSION_LIMIT) { say(`Include up to ${SESSION_LIMIT} sessions.`); return; }
        const text = String(note?.value || '').trim();
        if (text.length > NOTE_LIMIT) { say(`The note is at most ${NOTE_LIMIT} characters.`); return; }
        setBusy(true);
        say('Reading the staged changes and creating the request…');
        try {
            const created = await call('', 'POST', { scope: 'staged', reviewers: people, sessions, cardKey: card.key, note: text || null });
            if (!current()) return;
            say(`Request created for tree ${String(created?.treeId || '').slice(0, 12)}. Nothing has been sent: press Send… on it after reading what leaves this computer.`);
            if (note) note.value = '';
        } catch (error) {
            if (current()) say(error?.message || 'The review request could not be created.');
        } finally {
            if (current()) { setBusy(false); await loadRequests(); }
        }
    }
    async function send(id) {
        setBusy(true);
        say('Reading what would leave this computer…');
        let disclosure;
        try {
            disclosure = await call(`/${encodeURIComponent(id)}/disclosure`, 'GET');
        } catch (error) {
            if (current()) { say(error?.message || 'The disclosure could not be read; nothing was sent.'); setBusy(false); }
            return;
        }
        if (!current()) return;
        const confirmed = await confirmDialog({ title: 'Send this review request?', message: describeDisclosure(disclosure), confirmLabel: 'Send' });
        if (!current()) return;
        if (!confirmed) { say('Not sent.'); setBusy(false); return; }
        say('Sending…');
        try {
            // A submitted send is never aborted: the host may already have the request.
            const result = await call(`/${encodeURIComponent(id)}/send`, 'POST', { confirmed: true });
            if (current()) say(result?.message || 'Sent.');
        } catch (error) {
            if (current()) say(error?.message || 'The request could not be sent.');
        } finally {
            if (current()) { setBusy(false); await loadRequests(); }
        }
    }
    async function mutate(id, action) {
        const confirmed = await confirmDialog(action === 'cancel'
            ? { title: 'Cancel review request', message: 'The request takes no more decisions. If it was sent, reviewers lose access to it on viberails.ai.', confirmLabel: 'Cancel request' }
            : { title: 'Delete review request', message: 'Remove the request and its decisions from this computer. A sent copy is cancelled on viberails.ai first; a copy that cannot be cancelled yet keeps the request here, cancelled, until it is.', confirmLabel: 'Delete', danger: true });
        if (!current() || !confirmed) return;
        setBusy(true);
        say(action === 'cancel' ? 'Cancelling…' : 'Deleting…');
        try {
            if (action === 'cancel') await call(`/${encodeURIComponent(id)}/cancel`, 'POST');
            else await call(`/${encodeURIComponent(id)}`, 'DELETE', null, { responseType: 'blob' });
            if (current()) say(action === 'cancel' ? 'Request cancelled.' : 'Request deleted.');
        } catch (error) {
            if (current()) say(error?.message || `The request could not be ${action === 'cancel' ? 'cancelled' : 'deleted'}.`);
        } finally {
            if (current()) { setBusy(false); await loadRequests(); }
        }
    }
    async function click(event) {
        const button = event.target.closest('[data-review-action]');
        if (!button || !current() || busy) return;
        const action = button.dataset.reviewAction;
        if (action === 'reload') { say(''); await Promise.all([loadPreview(), loadRequests(false), loadCommitApprovals()]); return; }
        if (action === 'create') { await create(); return; }
        const row = button.closest('[data-card-review-id]');
        const id = row?.dataset.cardReviewId;
        if (!id) return;
        if (action === 'copy') {
            const input = row.querySelector('[data-card-review-trailer]');
            try { await navigator.clipboard.writeText(input.value); say('Approval line copied. Paste it into the commit message, or let Git Guard apply it.'); }
            catch { if (current()) { input.focus(); input.select(); say('Select and copy the approval line.'); } }
            return;
        }
        if (action === 'send') await send(id);
        else if (action === 'cancel' || action === 'delete') await mutate(id, action);
    }
    const toggle = () => { if (host.open && !previewLoaded) void loadPreview(); };
    const touched = event => { if (event.target === reviewers) reviewersTouched = true; };
    host.addEventListener('toggle', toggle);
    host.addEventListener('click', click);
    host.addEventListener('input', touched);
    // The poller and the sender publish review_request_updated when a decision lands or a request is sent or removed.
    let refreshQueued = false;
    const unsubscribe = app.appEventClient?.on?.('review_request_updated', () => {
        if (refreshQueued || !current()) return;
        refreshQueued = true;
        queueMicrotask(() => { refreshQueued = false; if (current() && !busy) void Promise.all([loadRequests(), loadCommitApprovals()]); });
    });
    void Promise.all([loadRequests(), loadCommitApprovals()]);
    if (host.open) void loadPreview();
    return {
        refresh: () => Promise.all([loadRequests(), loadCommitApprovals()]),
        dispose() {
            disposed = true;
            ++generation;
            request?.abort();
            unsubscribe?.();
            host.removeEventListener('toggle', toggle);
            host.removeEventListener('click', click);
            host.removeEventListener('input', touched);
            header()?.querySelector('[data-board-review-status]')?.remove();
        }
    };
}
