import { escapeHtml } from './utils.js';

/**
 * One readable state for a lane step (VIBE-338): the card rail used to print
 * "Process: succeeded · Result: passed" and "Action completed: succeeded" as block text.
 * Tones follow DESIGN_SYSTEM.md's status tones.
 */
export function stepRunState(entry = {}, run = null) {
    const process = run?.status || entry.processStatus || 'queued';
    const verdict = entry.verdict || run?.verdict || null;
    if (entry.skippedByAgent) return { label: 'Skipped by agent', tone: 'warning', icon: 'forward-step' };
    if (verdict === 'skipped' || entry.skipRequested) return { label: 'Skipped', tone: 'neutral', icon: 'forward-step' };
    if (verdict === 'passed') return { label: 'Passed', tone: 'success', icon: 'circle-check' };
    if (verdict === 'failed') return { label: 'Failed', tone: 'danger', icon: 'circle-xmark' };
    if (verdict === 'reviewing') return { label: 'Reviewing', tone: 'active', icon: 'magnifying-glass' };
    if (verdict === 'fixing') return { label: 'Fixing', tone: 'active', icon: 'wrench' };
    if (['claimed', 'running'].includes(process)) return { label: 'Running', tone: 'active', icon: 'spinner' };
    if (process === 'uncertain') return { label: 'Checking', tone: 'warning', icon: 'circle-question' };
    if (process === 'succeeded') return { label: 'Finished', tone: 'neutral', icon: 'circle-check' };
    if (process === 'cancelled') return { label: 'Stopped', tone: 'neutral', icon: 'circle-stop' };
    if (['failed', 'timed-out', 'interrupted'].includes(process)) return { label: process === 'timed-out' ? 'Timed out' : 'Failed', tone: 'danger', icon: 'circle-xmark' };
    return { label: entry.runId ? 'Queued' : 'Waiting', tone: 'neutral', icon: 'hourglass-half' };
}

export function stepPillMarkup(state) {
    return `<span class="board-step-pill is-${escapeHtml(state.tone)}"><i class="fa-solid fa-${escapeHtml(state.icon)}" aria-hidden="true"></i>${escapeHtml(state.label)}</span>`;
}

const LONG_SUMMARY = 180;
/** BoardLaunchEvent.AgentSkipPrefix: the stored summary of an agent's skip. */
const AGENT_SKIP_PREFIX = 'Skipped by agent: ';

/** The step's report, unless it only repeats the pill ("Action completed: succeeded"). */
export function stepSummaryText(entry = {}, run = null) {
    const text = String(entry.summary || run?.error || '').trim();
    if (!text || /^Action completed: \S+$/.test(text) || text === 'User skipped this lane entry.') return '';
    // The pill already says "Skipped by agent"; the row shows only the agent's reason.
    return entry.skippedByAgent && text.startsWith(AGENT_SKIP_PREFIX) ? text.slice(AGENT_SKIP_PREFIX.length) : text;
}

/** A summary, clamped to a few lines with a Show more toggle when it is long. */
export function stepSummaryMarkup(text, id) {
    if (!text) return '';
    const long = text.length > LONG_SUMMARY;
    return `<p class="board-step-run-summary${long ? ' is-clamped' : ''}" data-step-summary="${escapeHtml(id)}">${escapeHtml(text)}</p>`
        + (long ? `<button type="button" class="btn btn-link btn-sm board-step-run-more" data-step-more="${escapeHtml(id)}" aria-expanded="false">Show more</button>` : '');
}

/** The rail row for one current lane entry. */
export function laneEntryRowMarkup(entry, name) {
    const state = stepRunState(entry);
    const canSkip = !entry.skipRequested && !['passed', 'skipped'].includes(entry.verdict);
    // The same wording as the lane panel's legacy steps: a queued or running step is stopped first.
    const running = ['queued', 'claimed', 'running', 'uncertain'].includes(entry.processStatus);
    return `<article class="board-step-run">
        <div class="board-step-run-head"><span class="board-step-run-name">${escapeHtml(name)}</span>${stepPillMarkup(state)}</div>
        ${stepSummaryMarkup(stepSummaryText(entry), entry.id)}
        ${canSkip ? `<div class="board-step-run-actions"><button type="button" class="btn btn-sm btn-outline-secondary" data-launch-skip="${escapeHtml(entry.id)}">${running ? 'Stop and skip' : 'Skip'}</button></div>` : ''}
    </article>`;
}

/** The rail row for one run of this card (manual or lane), with its recording. */
export function cardRunRowMarkup(run) {
    const name = run.snapshot?.preset?.name || run.snapshot?.action?.name || 'Run';
    const when = run.endedUtc || run.startedUtc || run.queuedUtc;
    const time = when ? new Date(when) : null;
    return `<article class="board-step-run is-history">
        <div class="board-step-run-head"><span class="board-step-run-name">${escapeHtml(name)}</span>${stepPillMarkup(stepRunState({ processStatus: run.status, runId: run.id }, run))}</div>
        ${time && !Number.isNaN(time.getTime()) ? `<span class="board-step-run-meta">${escapeHtml(time.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }))}</span>` : ''}
        ${run.sessionId ? `<div class="board-step-run-actions"><button type="button" class="btn btn-sm btn-link" data-launch-replay="${escapeHtml(run.sessionId)}">Recorded terminal</button></div>` : ''}
    </article>`;
}

/** The open agent skip, kept until a person dismisses it (VIBE-338). */
export function agentSkipNoticeMarkup(skip) {
    if (!skip) return '';
    const more = skip.openCount > 1 ? `<span class="board-agent-skip-more">${skip.openCount - 1} earlier ${skip.openCount === 2 ? 'skip' : 'skips'} in Comments</span>` : '';
    return `<div class="board-agent-skip-notice" role="status">
        <div class="board-agent-skip-title"><i class="fa-solid fa-forward-step" aria-hidden="true"></i><strong>Skipped by agent</strong><span>${escapeHtml(skip.stepName)}</span></div>
        <p class="board-agent-skip-reason">${escapeHtml(skip.reason)}</p>
        <div class="board-agent-skip-foot"><span>${escapeHtml(skip.authorLabel)}</span>${more}
            <button type="button" class="btn btn-sm btn-outline-secondary" data-board-dismiss-agent-skip>Dismiss</button></div>
    </div>`;
}

/** Show more / Show less on a clamped summary. */
export function toggleStepSummary(host, button) {
    const summary = [...host.querySelectorAll('[data-step-summary]')].find(item => item.dataset.stepSummary === button.dataset.stepMore);
    if (!summary) return;
    const expanded = summary.classList.toggle('is-expanded');
    button.textContent = expanded ? 'Show less' : 'Show more';
    button.setAttribute('aria-expanded', String(expanded));
}
