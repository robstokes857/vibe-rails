import { BoardApi } from './board-api.js';
import { escapeHtml as esc } from './utils.js';
import { mountLlmPicker } from './pickers/llm-picker.js';

export function cardReviewsSection(card) {
    return `<section class="board-block" data-board-reviews aria-label="Code reviews">
        <h3 class="board-block-label">Code reviews</h3>
        ${card?.id ? `<div class="d-flex gap-2"><select data-review-picker aria-label="Reviewer"></select>
            <button type="button" class="btn btn-sm btn-outline-primary" data-review-run>Run review</button></div>
            <p class="board-editor-muted">Uses the saved card. Review scope is established in the actual checkout, including uncommitted work.</p>
            <p data-review-message role="status" aria-live="polite"></p>
            <button type="button" class="btn btn-sm btn-link" data-review-refresh>Refresh reviews</button>
            <div data-review-latest></div>
            <details><summary>Older runs</summary><div data-review-history></div>
                <button type="button" class="btn btn-sm btn-link" data-review-more hidden>Load older runs</button></details>
            <section data-review-report aria-label="Review report" hidden></section>`
            : '<p>Save the card to run a review.</p>'}
    </section>`;
}

export function reviewRow(row) {
    const session = row.terminalSessionId || row.sessionId;
    return `<article class="board-check-result">
        <strong>${esc(row.reviewer)} · ${esc(row.provider)}</strong>
        <p>Process: ${esc(row.processStatus)} · ${esc(row.result || 'Report missing')}</p>
        <p>${esc(row.scopeDescription || 'Scope not captured')} · ${esc(row.freshness || 'Unknown freshness')}</p>
        <small>${esc(new Date(row.createdUtc).toLocaleString())}</small>
        ${row.error ? `<p class="text-danger">${esc(row.error)}</p>` : ''}
        ${row.reportedUtc ? `<button type="button" class="btn btn-sm btn-link" data-review-view="${esc(row.id)}">View report</button>` : ''}
        ${session ? `<button type="button" class="btn btn-sm btn-link" data-board-open-session="${esc(session)}">Terminal / replay</button>` : '<p>No terminal session yet.</p>'}
    </article>`;
}

export function reviewReport(row) {
    return `<h4>${esc(row.result || 'Report missing')}</h4>
        <p>${esc(row.reviewer)} · ${esc(row.provider)} · ${esc(row.freshness)}</p>
        <dl><dt>Checkout</dt><dd>${esc(row.workspace || 'Unknown')}</dd>
            <dt>Scope</dt><dd>${esc(row.scopeDescription || 'Unknown')} (${esc(row.scope || 'unknown')})</dd>
            <dt>Base / head</dt><dd>${esc(row.baseCommit || 'unknown')} / ${esc(row.headCommit || 'unknown')}</dd>
            <dt>Dirty changes</dt><dd>${row.includeDirty || row.scope === 'working-tree' ? 'Included in capture' : 'Outside this scope'}</dd></dl>
        <details><summary>Captured files</summary><pre class="board-review-text">${esc((row.scopeFiles || []).join('\n') || 'Unavailable')}</pre></details>
        ${row.captureLimitations ? `<p>${esc(row.captureLimitations)}</p>` : ''}
        ${[['Findings', row.findings], ['Validation', row.validation], ['Limitations', row.limitations]].map(([label, text]) =>
            `<h5>${label}</h5><div class="board-review-text">${esc(text || 'Not reported')}</div>`).join('')}
        <button type="button" class="btn btn-sm btn-link" data-review-compare="${esc(row.id)}">Compare review inputs</button>`;
}

export function bindCardReviews(editor, card, app, onQueued) {
    const host = editor.querySelector('[data-board-reviews]');
    if (!host || !card?.id) return null;
    const find = key => host.querySelector(`[data-review-${key}]`);
    const picker = find('picker');
    const disposePicker = mountLlmPicker(app, picker, { context: 'sandbox', selectedValue: 'base:codex', placeholder: 'Choose reviewer' });
    const url = `/api/v1/board/cards/${encodeURIComponent(card.id)}/reviews`;
    let disposed = false, busy = false, generation = 0, reportGeneration = 0, offset = 0, rows = [], request, reportRequest;
    const current = () => !disposed && host.isConnected;
    const message = text => { if (current()) find('message').textContent = text; };
    const options = signal => ({ showLoading: false, preferErrorResponseMessage: true, signal });
    async function refresh(more = false) {
        if (!current() || busy) return;
        const version = ++generation;
        request?.abort(); request = new AbortController();
        try {
            const page = more ? offset + 50 : 0;
            const response = await app.apiCall(`${url}?offset=${page}`, 'GET', null, options(request.signal));
            if (!current() || version !== generation) return;
            rows = more ? [...rows, ...(response.reviews || [])].filter((r, i, all) => all.findIndex(x => x.id === r.id) === i) : response.reviews || [];
            offset = page;
            const latest = response.latest || rows.find(r => r.reportedUtc);
            find('latest').innerHTML = rows.length ? reviewRow(rows[0]) + (latest && latest.id !== rows[0].id ? `<h4>Latest report</h4>${reviewRow(latest)}` : '') : '<p>No code reviews yet.</p>';
            find('history').innerHTML = rows.slice(1).map(reviewRow).join('') || '<p>No older runs.</p>';
            find('more').hidden = !response.hasMore;
            message('A successful process exit without a report is not approval.');
        } catch (error) {
            if (current() && version === generation && !request.signal.aborted) message(error?.message || 'Could not load reviews.');
        }
    }
    async function report(id, verify) {
        const version = ++reportGeneration;
        reportRequest?.abort(); reportRequest = new AbortController();
        try {
            const row = await app.apiCall(`${url}/${encodeURIComponent(id)}?verify=${verify}`, 'GET', null, options(reportRequest.signal));
            if (!current() || version !== reportGeneration) return;
            find('report').hidden = false;
            find('report').innerHTML = reviewReport(row);
        } catch (error) {
            if (current() && version === reportGeneration && !reportRequest.signal.aborted) message(error?.message || 'Could not read review.');
        }
    }
    async function run() {
        if (busy || !current() || !picker.value) return;
        if (editor._boardSaving || editor._boardUploading || editor._boardStarting) { message('Wait for the current card action to finish.'); return; }
        busy = true; ++generation; request?.abort(); find('run').disabled = true;
        message('Starting code review…');
        try {
            await BoardApi.launchBoardCardAsync(card.id, { selection: picker.value, intent: 'code_review' });
            if (current()) { busy = false; await refresh(); onQueued?.(); }
        } catch (error) { message(error?.message || 'Could not start review.'); busy = false; await refresh(); }
        finally { busy = false; if (current()) find('run').disabled = false; }
    }
    const click = event => {
        const button = event.target.closest('button');
        if (!button || !host.contains(button)) return;
        if (button.hasAttribute('data-review-run')) void run();
        else if (button.hasAttribute('data-review-refresh')) void refresh();
        else if (button.hasAttribute('data-review-more')) void refresh(true);
        else if (button.dataset.reviewView) void report(button.dataset.reviewView, true);
        else if (button.dataset.reviewCompare) void report(button.dataset.reviewCompare, true);
    };
    host.addEventListener('click', click);
    void refresh();
    return { refresh, dispose() {
        disposed = true; ++generation; ++reportGeneration;
        request?.abort(); reportRequest?.abort(); disposePicker?.(); host.removeEventListener('click', click);
    } };
}
