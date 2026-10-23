// PR Reviews page: wording, list arithmetic and markup. Pure functions over API data so the
// rules (what counts as live, which list a PR lands in, how counts line up) are node-tested.
// Escape-first: every value from the API or GitHub goes through esc() before it reaches markup.
import { escapeHtml, formatRelativeTime } from './utils.js';
import { safeCodeLinkUrl } from './board-code-links.js';

const esc = escapeHtml;

/** The four tracker states. The server derives the state; this decides how it reads. */
export const PR_STATUS = {
    in_review: { label: 'In review', tone: 'warning', icon: 'fa-hourglass-half', blurb: 'A review pass is open and unfinished.' },
    blocker: { label: 'Blocker', tone: 'danger', icon: 'fa-ban', blurb: 'The newest review found something that must be fixed before merge.' },
    complete: { label: 'Complete', tone: 'success', icon: 'fa-circle-check', blurb: 'Reviewed, with nothing blocking the merge.' },
    not_started: { label: 'No review', tone: 'muted', icon: 'fa-circle', blurb: 'No review pass has been recorded yet.' }
};

/** Most urgent first, for the filter row. */
export const STATUS_ORDER = ['blocker', 'in_review', 'complete', 'not_started'];

/**
 * GitHub's lifecycle, a different question from what the review found. Unknown means "not
 * synced yet" and is treated as live: the alternative files a PR away as finished on no evidence.
 */
export const GITHUB_STATE = {
    merged: { label: 'Merged', tone: 'accent', blurb: 'Merged on GitHub. Its review record is history now.' },
    closed: { label: 'Closed', tone: 'muted', blurb: 'Closed on GitHub without merging.' },
    open: { label: 'Open', tone: 'success', blurb: 'Still open on GitHub.' },
    unknown: { label: 'Unsynced', tone: 'muted', blurb: 'Not yet checked against GitHub.' }
};

export const VERDICTS = {
    recommend_approve: { label: 'Recommend approve', tone: 'success' },
    comments: { label: 'Comments', tone: 'info' },
    request_changes: { label: 'Request changes', tone: 'danger' }
};

/** Any blocker found means changes are requested; the switch can still override it. */
export const BLOCKING_VERDICT = 'request_changes';

export const QUEUE_REASON = {
    in_review: { label: 'Resume', tone: 'warning' },
    never_reviewed: { label: 'Never reviewed', tone: 'info' },
    new_commits: { label: 'New commits', tone: 'accent' }
};

export const SKIP_REASON = {
    draft: 'Draft',
    reviewed: 'Up to date',
    reviewed_sha_unknown: 'Reviewed'
};

export const LIFECYCLE_FILTERS = [
    ['live', 'Live'],
    ['merged', 'Merged'],
    ['closed', 'Closed'],
    ['all', 'Every']
];

/** Merged and closed are the only states that retire a PR from the working lists. */
export function isLive(pr) {
    return pr?.gitHubState !== 'merged' && pr?.gitHubState !== 'closed';
}

export function statusMeta(status) {
    return PR_STATUS[status] || { label: status || 'Unknown', tone: 'muted', icon: 'fa-circle', blurb: '' };
}

export function matchesQuery(pr, query) {
    const needle = String(query || '').trim().toLowerCase();
    if (!needle) return true;
    return [`#${pr.number}`, String(pr.number), pr.title, pr.author, pr.summary]
        .some(field => String(field ?? '').toLowerCase().includes(needle));
}

/**
 * Splits the tracked PRs into the page's lists. The working sections (in review, blocked) only
 * hold live PRs. Status counts are counted within the chosen lifecycle, so the Blocker count on
 * the filter row and the Blocked section above it can never disagree.
 */
export function summarizePullRequests(prs, { lifecycle = 'live', status = 'all', query = '' } = {}) {
    const list = Array.isArray(prs) ? prs : [];
    const retiredBlockers = list.filter(pr => pr.status === 'blocker' && !isLive(pr));
    const inLifecycle = list.filter(pr => lifecycle === 'all' ? true
        : lifecycle === 'live' ? isLive(pr) : pr.gitHubState === lifecycle);
    const statusCounts = Object.fromEntries(STATUS_ORDER.map(key => [key, 0]));
    for (const pr of inLifecycle) statusCounts[pr.status] = (statusCounts[pr.status] || 0) + 1;
    return {
        pending: list.filter(pr => pr.status === 'in_review' && isLive(pr)),
        blocked: list.filter(pr => pr.status === 'blocker' && isLive(pr)),
        retiredBlockers: {
            total: retiredBlockers.length,
            merged: retiredBlockers.filter(pr => pr.gitHubState === 'merged').length,
            closed: retiredBlockers.filter(pr => pr.gitHubState === 'closed').length
        },
        lifecycleCounts: {
            all: list.length,
            live: list.filter(isLive).length,
            merged: list.filter(pr => pr.gitHubState === 'merged').length,
            closed: list.filter(pr => pr.gitHubState === 'closed').length
        },
        statusCounts: { all: inLifecycle.length, ...statusCounts },
        visible: inLifecycle.filter(pr => (status === 'all' || pr.status === status) && matchesQuery(pr, query))
    };
}

/** "N more PRs carry a standing blocker but ... merged anyway" or '' when there are none. */
export function retiredBlockerNote(retired) {
    if (!retired?.total) return '';
    const parts = [
        retired.merged ? `${retired.merged} merged anyway` : '',
        retired.closed ? `${retired.closed} closed without merging` : ''
    ].filter(Boolean).join(' and ');
    return `${retired.total} more ${retired.total === 1 ? 'PR carries' : 'PRs carry'} a standing blocker but ${parts}. `
        + 'They stay in the log below under the Merged and Closed filters.';
}

export function shortSha(sha) {
    return typeof sha === 'string' && sha ? sha.slice(0, 7) : '—';
}

export function formatDate(value, withTime = false) {
    if (!value) return '—';
    const date = new Date(value);
    if (Number.isNaN(date.valueOf())) return String(value);
    return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', ...(withTime ? { timeStyle: 'short' } : {}) }).format(date);
}

/** Only a github.com pull request link ever becomes an anchor. */
export function pullRequestLink(url, label, className = '') {
    const safe = safeCodeLinkUrl(url);
    const text = esc(label);
    return safe
        ? `<a class="${esc(className)}" href="${esc(safe)}" target="_blank" rel="noopener noreferrer" data-pr-external>${text}</a>`
        : `<span class="${esc(className)}">${text}</span>`;
}

export function repositoryLink(url, label) {
    return /^https:\/\/github\.com\/[A-Za-z0-9-]+\/[A-Za-z0-9._-]+$/.test(String(url || ''))
        ? `<a href="${esc(url)}" target="_blank" rel="noopener noreferrer" data-pr-external>${esc(label)}</a>`
        : esc(label);
}

export function badge(label, tone, { icon = '', title = '' } = {}) {
    return `<span class="pr-badge" data-tone="${esc(tone)}"${title ? ` title="${esc(title)}"` : ''}>`
        + `${icon ? `<i class="fa-solid ${esc(icon)}" aria-hidden="true"></i>` : ''}${esc(label)}</span>`;
}

export function statusBadge(status) {
    const meta = statusMeta(status);
    return badge(meta.label, meta.tone, { icon: meta.icon, title: meta.blurb });
}

/** Open PRs say nothing beyond the status badge; merged, closed and unsynced do. */
export function gitHubBadge(state, at) {
    if (!state || state === 'open') return '';
    const meta = GITHUB_STATE[state] || GITHUB_STATE.unknown;
    return badge(meta.label, meta.tone, { title: at ? `${meta.blurb} ${formatDate(at)}` : meta.blurb });
}

export function verdictBadge(verdict) {
    const meta = VERDICTS[verdict] || { label: verdict || 'No verdict', tone: 'muted' };
    return badge(meta.label, meta.tone);
}

export function sectionHeading(eyebrow, title, count, tone = '') {
    return `<div class="pr-section-heading">
        <span class="pr-eyebrow"${tone ? ` data-tone="${esc(tone)}"` : ''}>${esc(eyebrow)}</span>
        <h2 class="pr-section-title">${esc(title)} <span class="pr-count">${esc(count)}</span></h2>
    </div>`;
}

export function emptyState(message) {
    return `<div class="pr-empty">${esc(message)}</div>`;
}

/** Conflicts remain inspectable even when the current copy of the PR was deleted. */
export function legacyConflictPanel(conflict) {
    const history = (label, value) => `<details><summary>${esc(label)}</summary>
        ${value?.pullRequest ? `<h3 class="h6 mt-2">${esc(value.pullRequest.title)}</h3>
            <p>@${esc(value.pullRequest.author)} · ${esc(formatDate(value.pullRequest.updatedUtc, true))}</p>
            <pre class="pr-feedback">${esc(value.pullRequest.summary)}</pre>` : '<p>This copy of the PR was deleted.</p>'}
        ${(value?.reviews || []).map(review => `<article class="mt-2">
            <p>${esc(review.status === 'in_progress' ? 'Pending draft' : 'Completed pass')} · ${esc(review.verdict || 'No verdict')}
            ${review.blocking ? ' · Blocker' : ''} · ${esc(formatDate(review.reviewedUtc || review.startedUtc, true))}
            · head <code>${esc(review.headSha || '—')}</code></p>
            <pre class="pr-feedback">${esc(review.feedback)}</pre></article>`).join('')}</details>`;
    return `<section class="pr-card pr-alert" data-tone="warning">
        <h2 class="pr-section-title">Two histories for PR #${esc(conflict.number)}</h2>
        <p>An older VibeRails version changed this PR after the current history was edited.
        Inspect both copies and choose which to continue using. Both copies remain saved.</p>
        ${history('Current history', conflict.current)}${history('Legacy history', conflict.legacy)}
        <div class="pr-form-actions">
            <button type="button" class="btn btn-outline-secondary" data-pr-resolve-legacy="${esc(conflict.number)}" data-use-legacy="false">Keep current history</button>
            <button type="button" class="btn btn-outline-warning" data-pr-resolve-legacy="${esc(conflict.number)}" data-use-legacy="true">Use legacy history</button>
        </div></section>`;
}

/** One tracked PR in a list. The whole row opens the PR's detail page. */
export function trackedRow(pr, tone = '') {
    const reviewed = pr.lastReviewedUtc ? ` · reviewed ${esc(formatRelativeTime(pr.lastReviewedUtc))}` : '';
    return `<button type="button" class="pr-row"${tone ? ` data-tone="${esc(tone)}"` : ''} data-pr-open="${esc(pr.number)}"
        aria-label="Open PR #${esc(pr.number)}: ${esc(pr.title)}">
        <span class="pr-number">#${esc(pr.number)}</span>
        <span class="pr-row-main">
            <span class="pr-row-title">${esc(pr.title)}</span>
            <span class="pr-row-meta">@${esc(pr.author)} · ${esc(pr.completedCount)} completed ${pr.completedCount === 1 ? 'pass' : 'passes'}${reviewed}</span>
        </span>
        <span class="pr-row-badges">${statusBadge(pr.status)}${gitHubBadge(pr.gitHubState, pr.gitHubStateUtc)}</span>
    </button>`;
}

/** One open GitHub PR, with what the tracker knows and the one action that fits. */
export function openRow(entry, starting) {
    const tracked = entry.reviewCount > 0;
    const reason = entry.reason ? QUEUE_REASON[entry.reason] : null;
    const queueBadge = reason
        ? badge(reason.label, reason.tone, { title: entry.why })
        : badge(SKIP_REASON[entry.skippedBecause] || 'Skipped', 'muted', { title: entry.why });
    const action = tracked
        ? `<button type="button" class="btn btn-sm btn-outline-secondary" data-pr-open="${esc(entry.number)}">Open</button>`
        : `<button type="button" class="btn btn-sm btn-primary" data-pr-start="${esc(entry.number)}"${starting ? ' disabled' : ''}>Start review</button>`;
    return `<div class="pr-row pr-open-row">
        <span class="pr-number">#${esc(entry.number)}</span>
        <span class="pr-row-main">
            ${pullRequestLink(entry.url, entry.title, 'pr-row-title')}
            <span class="pr-row-meta">@${esc(entry.author)} · opened ${esc(formatDate(entry.openedUtc))} · head <code>${esc(shortSha(entry.headSha))}</code></span>
        </span>
        <span class="pr-row-badges">${tracked ? statusBadge(entry.trackerStatus) : ''}${queueBadge}</span>
        <span class="pr-row-action">${action}</span>
    </div>`;
}

/** What an unattended run would take next; the same answer GET /queue gives an agent. */
export function nextUpCard(queue, starting) {
    if (!queue) return '';
    const { next, counts } = queue;
    if (!next) {
        return `<div class="pr-next" data-tone="muted"><span class="pr-eyebrow">Next up</span>
            <p class="pr-muted mb-0">Nothing to review. All ${esc(counts.open - counts.drafts)} non-draft open PRs are reviewed at their current head.</p></div>`;
    }
    const reason = QUEUE_REASON[next.reason] || { label: next.reason, tone: 'info' };
    const resuming = next.reason === 'in_review';
    const action = resuming
        ? `<button type="button" class="btn btn-outline-warning" data-pr-open="${esc(next.number)}">Open review</button>`
        : `<button type="button" class="btn btn-primary" data-pr-start="${esc(next.number)}"${starting ? ' disabled' : ''}>${starting ? 'Starting…' : 'Start review'}</button>`;
    return `<div class="pr-next" data-tone="${esc(reason.tone)}">
        <div class="pr-next-main">
            <span class="pr-eyebrow">Next up · ${esc(counts.eligible)} of ${esc(counts.open)} open PRs waiting</span>
            <div class="pr-next-title"><span class="pr-number">#${esc(next.number)}</span>
                ${pullRequestLink(next.url, next.title, 'pr-row-title')} ${badge(reason.label, reason.tone, { title: next.why })}</div>
            <span class="pr-row-meta">@${esc(next.author)} · opened ${esc(formatDate(next.openedUtc))} · head <code>${esc(shortSha(next.headSha))}</code></span>
        </div>
        ${action}
    </div>`;
}

export function verdictOptions(selected) {
    return Object.entries(VERDICTS).map(([value, meta]) =>
        `<option value="${esc(value)}"${value === selected ? ' selected' : ''}>${esc(meta.label)}</option>`).join('');
}

/** The open pass: overview, feedback, verdict and blocker switch, with draft and complete. */
export function pendingReviewForm(pr, pending, busy = '') {
    const verdict = pending.verdict || 'comments';
    const hasDraft = pending.updatedUtc && pending.updatedUtc !== pending.startedUtc;
    return `<form class="pr-card pr-pending" data-pr-pending-form="${esc(pending.id)}" data-blocking="${pending.blocking ? 'true' : 'false'}">
        <div class="pr-card-head">
            <div><span class="pr-eyebrow" data-tone="warning">Review in progress</span>
                <h2 class="pr-section-title">Finish this review</h2>
                ${hasDraft ? `<span class="pr-muted">Draft saved ${esc(formatRelativeTime(pending.updatedUtc))}</span>` : ''}</div>
            ${statusBadge('in_review')}
        </div>
        ${pending.headSha ? `<p class="pr-muted mb-2">Head <code>${esc(pending.headSha)}</code></p>` : ''}
        <label class="form-label" for="pr-summary">High-level overview</label>
        <textarea class="form-control mb-3" id="pr-summary" name="summary" rows="2" maxlength="10000">${esc(pr.summary)}</textarea>
        <label class="form-label" for="pr-feedback">Feedback given</label>
        <textarea class="form-control mb-3 pr-feedback-input" id="pr-feedback" name="feedback" rows="10" maxlength="100000"
            placeholder="Paste the full review body here.">${esc(pending.feedback)}</textarea>
        <div class="pr-form-row">
            <label class="form-label mb-0" for="pr-verdict">Verdict</label>
            <select class="form-select form-select-sm pr-verdict" id="pr-verdict" name="verdict" data-pr-verdict>${verdictOptions(verdict)}</select>
            <div class="form-check form-switch mb-0">
                <input class="form-check-input" type="checkbox" role="switch" id="pr-blocking" name="blocking" data-pr-blocking${pending.blocking ? ' checked' : ''}>
                <label class="form-check-label" for="pr-blocking">A blocker was found</label>
            </div>
        </div>
        <p class="pr-alert" data-tone="danger" data-pr-blocker-note${pending.blocking ? '' : ' hidden'}>
            Completing this review leaves PR #${esc(pr.number)} at <strong>Blocker</strong>, not Complete.
            Clear the switch only when nothing must be fixed before merge.</p>
        <div class="pr-form-actions">
            <span class="pr-muted">The server records the reviewed date.</span>
            <button type="button" class="btn btn-outline-secondary" data-pr-save-draft${busy ? ' disabled' : ''}>${busy === 'draft' ? 'Saving…' : 'Save draft'}</button>
            <button type="submit" class="btn ${pending.blocking ? 'btn-danger' : 'btn-primary'}" data-pr-complete${busy ? ' disabled' : ''}>
                ${busy === 'complete' ? 'Completing…' : pending.blocking ? 'Complete as blocker' : 'Complete review'}</button>
        </div>
    </form>`;
}

/** One completed pass. Only the newest sets the PR status, so only it offers the blocker toggle. */
export function reviewCard(review, { isLatest = false, editing = false } = {}) {
    if (editing) {
        return `<form class="pr-card" data-pr-review-edit="${esc(review.id)}">
            <label class="form-label" for="pr-edit-feedback-${esc(review.id)}">Feedback</label>
            <textarea class="form-control mb-3 pr-feedback-input" id="pr-edit-feedback-${esc(review.id)}" name="feedback" rows="8"
                maxlength="100000" required>${esc(review.feedback)}</textarea>
            <div class="pr-form-row">
                <label class="form-label mb-0" for="pr-edit-verdict-${esc(review.id)}">Verdict</label>
                <select class="form-select form-select-sm pr-verdict" id="pr-edit-verdict-${esc(review.id)}" name="verdict">${verdictOptions(review.verdict || 'comments')}</select>
                <div class="form-check form-switch mb-0">
                    <input class="form-check-input" type="checkbox" role="switch" id="pr-edit-blocking-${esc(review.id)}" name="blocking"${review.blocking ? ' checked' : ''}>
                    <label class="form-check-label" for="pr-edit-blocking-${esc(review.id)}">A blocker was found</label>
                </div>
            </div>
            <div class="pr-form-actions">
                <button type="button" class="btn btn-outline-secondary" data-pr-cancel-edit>Cancel</button>
                <button type="submit" class="btn btn-primary">Save review</button>
            </div>
        </form>`;
    }
    const long = (review.feedback?.length || 0) > 900;
    return `<article class="pr-card pr-review"${review.blocking ? ' data-tone="danger"' : ''}>
        <div class="pr-card-head">
            <div>
                <div class="pr-row-badges">${verdictBadge(review.verdict)}${review.blocking ? badge('Blocker', 'danger', { icon: 'fa-ban' }) : ''}
                    ${isLatest ? badge('Sets PR status', 'info', { title: 'The newest completed review is the one that sets the PR status.' }) : ''}</div>
                <span class="pr-muted">Reviewed ${esc(formatDate(review.reviewedUtc, true))}${review.headSha ? ` · head <code>${esc(shortSha(review.headSha))}</code>` : ''}</span>
            </div>
            <div class="pr-card-actions">
                ${isLatest ? `<button type="button" class="btn btn-sm ${review.blocking ? 'btn-outline-success' : 'btn-outline-danger'}"
                    data-pr-toggle-blocker="${esc(review.id)}" data-blocking="${review.blocking ? 'true' : 'false'}">${review.blocking ? 'Clear blocker' : 'Mark as blocker'}</button>` : ''}
                <button type="button" class="btn btn-sm btn-outline-secondary" data-pr-edit-review="${esc(review.id)}">Edit</button>
                <button type="button" class="btn btn-sm btn-outline-danger" data-pr-delete-review="${esc(review.id)}" aria-label="Delete this review">Delete</button>
            </div>
        </div>
        <pre class="pr-feedback${long ? ' is-collapsed' : ''}" data-pr-feedback>${esc(review.feedback)}</pre>
        ${long ? '<button type="button" class="btn btn-sm btn-link px-0" data-pr-expand>Show full review</button>' : ''}
    </article>`;
}

/** The detail page's summary card: what the PR is, where it stands, and what can be done to it. */
export function detailHeader(pr, url) {
    return `<section class="pr-card pr-detail-head"${pr.status === 'blocker' && isLive(pr) ? ' data-tone="danger"' : ''}>
        <div class="pr-detail-main">
            <span class="pr-eyebrow">Pull request #${esc(pr.number)}</span>
            <h2 class="pr-detail-title">${esc(pr.title)}</h2>
            <p class="pr-detail-summary">${pr.summary ? esc(pr.summary) : '<span class="pr-muted">No high-level overview yet.</span>'}</p>
            <span class="pr-row-meta">Opened by <strong>@${esc(pr.author)}</strong> · ${esc(formatDate(pr.openedUtc))} · last write ${esc(formatDate(pr.updatedUtc, true))}</span>
        </div>
        <div class="pr-detail-side">
            <div class="pr-row-badges">${statusBadge(pr.status)}${gitHubBadge(pr.gitHubState, pr.gitHubStateUtc)}</div>
            ${safeCodeLinkUrl(url) ? `<a class="btn btn-sm btn-outline-secondary" href="${esc(url)}" target="_blank" rel="noopener noreferrer" data-pr-external>
                <i class="fa-brands fa-github" aria-hidden="true"></i> Open on GitHub</a>` : ''}
            <button type="button" class="btn btn-sm btn-outline-secondary" data-pr-edit-pr>Edit details</button>
            <button type="button" class="btn btn-sm btn-outline-danger" data-pr-delete-pr>Delete PR</button>
        </div>
    </section>`;
}

export function pullRequestEditForm(pr) {
    return `<form class="pr-card" data-pr-pr-edit>
        <label class="form-label" for="pr-edit-title">Title</label>
        <input class="form-control mb-3" id="pr-edit-title" name="title" maxlength="500" required value="${esc(pr.title)}">
        <label class="form-label" for="pr-edit-author">Author</label>
        <input class="form-control mb-3" id="pr-edit-author" name="author" maxlength="200" required value="${esc(pr.author)}">
        <label class="form-label" for="pr-edit-summary">High-level overview</label>
        <textarea class="form-control mb-3" id="pr-edit-summary" name="summary" rows="3" maxlength="10000">${esc(pr.summary)}</textarea>
        <div class="pr-form-actions">
            <button type="button" class="btn btn-outline-secondary" data-pr-cancel-pr-edit>Cancel</button>
            <button type="submit" class="btn btn-primary">Save details</button>
        </div>
    </form>`;
}

/** The detail page's alerts: a live blocker, or a PR GitHub has already retired. */
export function detailAlerts(pr) {
    if (pr.status === 'blocker' && isLive(pr)) {
        return `<div class="pr-alert" data-tone="danger" role="status"><strong>Blocked. Do not merge.</strong>
            The newest completed review found something that must be fixed first. Clearing the blocker flag on that review,
            or completing a later review with nothing blocking, moves this PR back to Complete.</div>`;
    }
    if (!isLive(pr)) {
        const meta = GITHUB_STATE[pr.gitHubState] || GITHUB_STATE.unknown;
        const when = pr.gitHubStateUtc ? ` · ${formatDate(pr.gitHubStateUtc)}` : '';
        // Said plainly rather than by hiding the blocker: the record stands, GitHub moved on without it.
        const body = pr.status === 'blocker'
            ? `This PR ${pr.gitHubState === 'merged' ? 'merged' : 'was closed'} with the review's blocker still standing. The review below is kept as the record; nothing here is waiting on anyone.`
            : 'The review history below is kept as the record. Nothing here is waiting on anyone.';
        return `<div class="pr-alert" data-tone="info" role="status"><strong>${esc(meta.label)} on GitHub${esc(when)}.</strong> ${esc(body)}</div>`;
    }
    return '';
}
