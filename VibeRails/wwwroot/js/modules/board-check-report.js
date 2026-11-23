import { escapeHtml as esc } from './utils.js';
import { fileFindings } from './code-report/findings.js';
import { openFileDetailsModal } from './code-report/file-details-modal.js';
import { CodeReportViewer } from './code-report/viewer.js';

// A saved card check's report (VIBE-362), in a native dialog over the card editor so the card and its
// drafts stay as they are. It leads with what needs fixing: each file that needs work and its worst
// problems, judged by findings.js as the Quality page does, with the file reader one click away. What
// was not checked, the limitations, the code map and the capture details follow, folded.
//
// Opened from the check's comment ([label](check:<id>), see board-text.js), the card's Code reviews
// list and Advanced > Checks. Report text is untrusted: everything is escaped, nothing is executed.

const NEEDS_WORK = new Set(['AtRisk', 'NeedsWork']);
const RATING_ORDER = { AtRisk: 3, NeedsWork: 2, Okay: 1 };
const SHOWN_PROBLEMS = 5;
const STATUS_TONES = { Passed: 'good', Findings: 'warning', 'Needs review': 'warning', 'Failed to run': 'danger',
    'Completed with limitations': 'neutral', 'Skipped/not applicable': 'neutral', Cancelled: 'neutral' };

let current = null;
let nextId = 0;

/** Closes the open report, if any. The card editor calls this when it closes. */
export function closeCheckReport() { current?.close(false); }

/** Opens the saved check `checkId` of card `cardId`; a second call replaces the first report. */
export function openCheckReport({ app, cardId, checkId }) {
    const host = document.getElementById('modal-container');
    if (!host || !cardId || !/^[a-f0-9]{32}$/.test(String(checkId || ''))) return null;
    current?.close(false);
    const prefix = `board-check-report-${++nextId}`;
    const url = `/api/v1/board/cards/${encodeURIComponent(cardId)}/checks/${encodeURIComponent(checkId)}`;
    const layer = document.createElement('div');
    layer.className = 'code-report board-check-report-layer';
    layer.innerHTML = `<dialog class="board-check-report" role="dialog" aria-modal="true" aria-labelledby="${prefix}-title">
        <header class="board-check-report-header">
            <div><h2 id="${prefix}-title">Check report</h2><p class="board-check-report-meta" data-check-report-meta></p></div>
            <button type="button" class="btn btn-secondary" data-check-report-close autofocus>Close</button>
        </header>
        <div class="board-check-report-body" data-check-report-body><p role="status">Loading the saved report…</p></div>
    </dialog>`;
    const previousFocus = document.activeElement;
    host.append(layer);
    const dialog = layer.querySelector('dialog');
    const body = layer.querySelector('[data-check-report-body]');
    const request = new AbortController();
    let closed = false, viewer = null, fileModal = null, files = [], check = null, verifyRequest = null;
    const observer = new MutationObserver(() => { if (!layer.isConnected) close(false); });

    function close(restoreFocus = true) {
        if (closed) return;
        closed = true;
        if (current?.close === close) current = null;
        request.abort();
        verifyRequest?.abort();
        observer.disconnect();
        fileModal?.close(false);
        viewer?.destroy();
        if (dialog.open) dialog.close();
        layer.remove();
        if (restoreFocus && previousFocus?.isConnected) previousFocus.focus({ preventScroll: true });
    }
    dialog.addEventListener('cancel', event => { event.preventDefault(); close(); });
    // Consume Escape here: the app's document handler would otherwise also close the card editor.
    dialog.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || event.defaultPrevented) return;
        event.preventDefault();
        event.stopPropagation();
        close();
    });
    layer.addEventListener('click', event => {
        const button = event.target.closest('button');
        if (!button || !layer.contains(button)) return;
        if (button.hasAttribute('data-check-report-close')) close();
        else if (button.dataset.checkReportFile) openFile(files[Number(button.dataset.checkReportFile)]);
        else if (button.hasAttribute('data-check-report-verify')) void verify(button);
    });
    // The map is heavy and describes the current checkout, so it mounts only when asked for.
    layer.addEventListener('toggle', event => {
        if (event.target.matches?.('[data-check-report-map]') && event.target.open) void mountMap(event.target);
    }, true);

    function openFile(entry) {
        if (!entry || closed) return;
        // Saved Board reports have no live source, fix or ignore actions: the reader shows captured excerpts.
        fileModal = openFileDetailsModal({ app, file: entry.file, capturedUtc: check?.startedUtc, onClose: () => { fileModal = null; } });
    }

    async function mountMap(details) {
        const report = engineReport(check);
        const mount = details.querySelector('[data-check-report-map-host]');
        if (viewer || !report || !mount) return;
        viewer = new CodeReportViewer(mount, app);
        try {
            await viewer.setResponse({ success: true, report, analyzedFileCount: check.analyzedCount,
                skippedFileCount: check.skippedCount, startedUtc: check.startedUtc });
        } catch (error) {
            if (!closed) mount.textContent = error?.message || 'The code map is unavailable.';
        }
    }

    async function verify(button) {
        verifyRequest?.abort();
        verifyRequest = new AbortController();
        const status = layer.querySelector('[data-check-report-freshness]');
        button.disabled = true;
        status.textContent = 'Comparing the captured inputs with this checkout…';
        try {
            const response = await app.apiCall(`${url}?verify=true`, 'GET', null,
                { showLoading: false, signal: verifyRequest.signal, preferErrorResponseMessage: true });
            if (!closed) status.textContent = response.freshness;
        } catch (error) {
            if (!closed && error?.name !== 'AbortError') status.textContent = error?.message || 'Freshness is unknown.';
        } finally { if (!closed) button.disabled = false; }
    }

    async function load() {
        try {
            const response = await app.apiCall(url, 'GET', null, { showLoading: false, signal: request.signal, preferErrorResponseMessage: true });
            if (closed) return;
            check = response.check;
            files = reportFiles(engineReport(check));
            layer.querySelector(`#${prefix}-title`).textContent = `${check.tool} report`;
            layer.querySelector('[data-check-report-meta]').textContent = metaLine(check);
            body.innerHTML = reportHtml(response, files, prefix);
        } catch (error) {
            if (!closed && error?.name !== 'AbortError')
                body.innerHTML = `<p class="board-check-report-error" role="alert">${esc(error?.message || 'The report could not be loaded.')} Close this and try again.</p>`;
        }
    }

    observer.observe(host, { childList: true });
    dialog.showModal();
    current = { close };
    void load();
    return current;
}

function engineResult(check) {
    try { return check?.resultJson ? JSON.parse(check.resultJson) : null; } catch { return null; }
}

function engineReport(check) {
    const report = engineResult(check)?.details?.report;
    try { return report ? JSON.parse(report) : null; } catch { return null; }
}

/** Analyzed files with their findings: those that need work first, as the comment orders them. */
function reportFiles(report) {
    return (Array.isArray(report?.files) ? report.files : [])
        .filter(file => file && typeof file.file === 'string')
        .map(file => ({ file, findings: fileFindings(file), needsWork: NEEDS_WORK.has(file.rating) }))
        .sort((a, b) => (RATING_ORDER[b.file.rating] ?? 0) - (RATING_ORDER[a.file.rating] ?? 0)
            || (b.file.priority ?? 0) - (a.file.priority ?? 0) || (b.file.score ?? 0) - (a.file.score ?? 0)
            || a.file.file.localeCompare(b.file.file));
}

function scopeText(check) {
    return {
        card: "The card's linked commits", commit: `Commit ${String(check.headCommit || '').slice(0, 8)}`,
        range: 'A pinned commit range', 'working-tree': 'Working-tree changes', unpushed: 'Unpushed commits',
        repository: 'The whole repository'
    }[check.scope] || check.scope || 'Unknown scope';
}

function metaLine(check) {
    const when = new Date(check.endedUtc || check.startedUtc);
    return [Number.isNaN(when.valueOf()) ? null : when.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }),
        scopeText(check)].filter(Boolean).join(' · ');
}

function stepText(response) {
    const check = response.check;
    if (response.failsStep) return 'This step failed';
    if (check.minimumGradeResult === 'Met') return `Minimum grade ${check.minimumGrade} met`;
    return check.tool === 'Code quality' ? 'Feedback only: findings do not fail the step' : 'Advisory';
}

function headline(check, files) {
    if (check.status === 'Failed to run' || check.status === 'Cancelled') return check.summary || 'The check did not finish.';
    if (!files.length) return check.summary || 'No saved findings.';
    const needsWork = files.filter(entry => entry.needsWork).length;
    if (!needsWork) return `No file needs work. ${files.length === 1 ? '1 file was' : `${files.length} files were`} checked.`;
    return `${needsWork === 1 ? '1 file needs' : `${needsWork} files need`} work, of ${files.length} checked.`;
}

const where = item => `${item.where} · ${item.limitText}`;

function problemList(problems) {
    return `<ol class="board-check-problems">${problems.map(item => `<li data-level="${esc(item.level)}">
        <span class="board-check-level">${esc(item.levelLabel)}</span>
        <span class="board-check-problem"><strong>${esc(item.label)} ${esc(item.valueText)}</strong><small>${esc(where(item))}</small></span></li>`).join('')}</ol>`;
}

function needsWorkHtml(entry, index) {
    const { file, findings } = entry;
    const more = findings.problems.length - SHOWN_PROBLEMS;
    return `<article class="board-check-file" data-tone="${esc(findings.tone)}">
        <header><code class="board-check-path">${esc(file.file)}</code>
            <span class="board-check-pill" data-tone="${esc(findings.tone)}">${esc(findings.ratingLabel)}</span>
            <span class="board-check-health">Health ${esc(findings.healthText)} / 100</span></header>
        <p class="board-check-file-summary">${esc(findings.problems.length ? findings.headline : 'No single measurement is past its limit; together they lower the score.')}${findings.hotspot ? ` ${esc(findings.hotspot)}` : ''}</p>
        ${findings.problems.length ? problemList(findings.problems.slice(0, SHOWN_PROBLEMS)) : ''}
        <div class="board-check-file-actions">
            <button type="button" class="btn btn-sm btn-secondary" data-check-report-file="${index}">Open details${more > 0 ? ` (${more} more ${more === 1 ? 'problem' : 'problems'})` : ''}</button>
            <span class="board-check-hint">Shows what each problem means, how to fix it and the captured code.</span>
        </div>
    </article>`;
}

function otherFileHtml(entry, index) {
    const { file, findings } = entry;
    const problems = findings.problems.length;
    return `<li><code class="board-check-path">${esc(file.file)}</code>
        <span class="board-check-pill" data-tone="${esc(findings.tone)}">${esc(findings.ratingLabel)}</span>
        <span class="board-check-health">Health ${esc(findings.healthText)} / 100${problems ? ` · ${problems === 1 ? '1 problem' : `${problems} problems`}` : ''}</span>
        <button type="button" class="btn btn-link btn-sm" data-check-report-file="${index}">Details</button></li>`;
}

function notCheckedHtml(check) {
    const named = Array.isArray(check.notReviewed) ? check.notReviewed : [];
    const total = named.length + (check.notReviewedOmitted || 0);
    if (!total) return check.notReviewed == null && check.skippedCount > 0
        ? `<section class="board-check-section"><h3>Not checked</h3><p>${check.skippedCount} files were not checked. This check is older than the list of reasons.</p></section>` : '';
    return `<details class="board-check-section board-check-fold"><summary><h3>Not checked <span class="board-check-count">${total}</span></h3>
            <span class="board-check-hint">Unsupported languages, ignored or exempted files, deletions and files without added lines.</span></summary>
        <ul class="board-check-skipped">${named.map(file => `<li><code class="board-check-path">${esc(file.path)}</code><span>${esc(file.reason)}</span></li>`).join('')}</ul>
        ${check.notReviewedOmitted ? `<p>And ${check.notReviewedOmitted} more. An agent can list every file with <code>read_board_check</code>.</p>` : ''}
    </details>`;
}

function reportHtml(response, files, prefix) {
    const check = response.check;
    const status = check.status || 'Unknown';
    const needsWork = files.map((entry, index) => ({ entry, index })).filter(({ entry }) => entry.needsWork);
    const others = files.map((entry, index) => ({ entry, index })).filter(({ entry }) => !entry.needsWork);
    const engine = engineResult(check);
    const unlinked = Array.isArray(check.unlinkedCommits) ? check.unlinkedCommits : [];
    const limitations = Array.isArray(check.limitations) ? check.limitations : [];
    return `
        <section class="board-check-verdict" data-tone="${STATUS_TONES[status] || 'neutral'}" aria-labelledby="${prefix}-verdict">
            <span class="board-check-pill" data-tone="${STATUS_TONES[status] || 'neutral'}">${esc(status)}</span>
            <div><p class="board-check-headline" id="${prefix}-verdict">${esc(headline(check, files))}</p>
                <p class="board-check-step">${esc(stepText(response))}.</p>
                ${response.failureReason ? `<p class="board-check-reason">${esc(response.failureReason)}</p>` : ''}
                ${check.gradeSummary && !response.failureReason ? `<p class="board-check-reason">${esc(check.gradeSummary)}</p>` : ''}</div>
        </section>
        ${needsWork.length ? `<section class="board-check-section"><h3>What needs work</h3>${needsWork.map(({ entry, index }) => needsWorkHtml(entry, index)).join('')}</section>` : ''}
        ${others.length ? `<section class="board-check-section"><h3>${needsWork.length ? 'Other files checked' : 'Files checked'} <span class="board-check-count">${others.length}</span></h3>
            <ul class="board-check-others">${others.map(({ entry, index }) => otherFileHtml(entry, index)).join('')}</ul></section>` : ''}
        ${unlinked.length ? `<section class="board-check-section"><h3>Commits not linked to the card</h3>
            <p>These were made from this card's sessions, so their code was not checked. Link them, then run the step again.</p>
            <ul>${unlinked.map(commit => `<li><code>${esc(commit)}</code></li>`).join('')}</ul></section>` : ''}
        ${notCheckedHtml(check)}
        ${limitations.length ? `<details class="board-check-section board-check-fold"><summary><h3>Limitations <span class="board-check-count">${limitations.length}</span></h3></summary>
            <ul>${limitations.map(text => `<li>${esc(text)}</li>`).join('')}</ul></details>` : ''}
        ${engineReport(check) ? `<details class="board-check-section board-check-fold" data-check-report-map><summary><h3>Code map and charts</h3>
            <span class="board-check-hint">The saved scores over a map of this checkout as it is now.</span></summary>
            <div data-check-report-map-host></div></details>` : ''}
        <details class="board-check-section board-check-fold"><summary><h3>Capture details</h3></summary>
            <p data-check-report-freshness role="status">${esc(response.freshness || 'Unknown freshness.')}</p>
            <button type="button" class="btn btn-sm btn-secondary" data-check-report-verify>Compare inputs</button>
            <dl class="board-check-capture">
                <dt>Summary</dt><dd>${esc(check.summary || '')}</dd>
                <dt>Base → head</dt><dd><code>${esc(check.baseCommit || 'unknown')} → ${esc(check.headCommit || 'unknown')}</code></dd>
                <dt>Checkout</dt><dd>${esc(check.workspacePath || 'unknown')}</dd>
                <dt>Run / version</dt><dd>${esc(check.runId || 'unknown')} / ${esc(check.toolVersion || 'unknown')}</dd>
                <dt>Input / rule fingerprints</dt><dd><code>${esc(check.snapshotHash || 'unknown')} / ${esc(check.rulesHash || 'unknown')}</code></dd>
                <dt>Files in scope</dt><dd>${esc((check.scopeFiles || []).join(' · ') || 'unknown')}</dd>
            </dl>
            <details><summary>Full engine output</summary><pre class="board-check-engine">${esc(JSON.stringify(engine, null, 2) || check.summary || '')}</pre></details>
        </details>`;
}
