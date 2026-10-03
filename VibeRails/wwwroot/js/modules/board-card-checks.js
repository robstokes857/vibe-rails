import { escapeHtml as esc } from './utils.js';
import { CodeReportViewer } from './code-report/viewer.js';

export function cardChecksSection() {
    return `<section class="board-block board-checks" data-board-checks aria-label="Checks">
        <div class="board-checks-heading"><h3 class="board-block-label">Checks</h3>
            <button type="button" class="btn btn-sm btn-outline-secondary" data-check-refresh>Refresh checks</button></div>
        <p data-check-message role="status" aria-live="polite">Loading checks…</p>
        <div class="board-checks-summary" data-check-summary></div>
        <section data-check-report hidden aria-label="Saved check report"></section>
    </section>`;
}

export function checkRow(check) {
    const pending = String(check.id).startsWith('pending-');
    return `<article class="board-check-result">
        <div><strong>${esc(check.tool)}</strong><span class="board-check-status" data-status="${esc(check.status)}">${esc(check.status)}</span></div>
        <p>${check.analyzedCount || 0} ${check.tool === 'VCA' ? 'rules' : 'files'} analyzed · ${check.findingCount || 0} findings · ${check.skippedCount || 0} files skipped</p>
        ${pending ? `<p>${esc(check.summary)}</p>` : ''}
        <p>Scope: ${esc(check.scope)} · ${esc(new Date(check.startedUtc).toLocaleString())}</p>
        <p class="board-editor-muted">${check.endedUtc ? 'Unknown freshness — view report to compare captured inputs.' : 'Waiting for completed evidence.'}</p>
        ${pending ? '' : `<button type="button" class="btn btn-link btn-sm" data-check-view="${esc(check.id)}">View report</button>`}
    </article>`;
}

export function bindCardChecks(editor, card, app) {
    const host = editor.querySelector('[data-board-checks]');
    if (!host || !card?.id) return null;
    const find = key => host.querySelector(`[data-check-${key}]`);
    let disposed = false, generation = 0, reportGeneration = 0, latestRows = [];
    let request, reportRequest, viewer;
    const current = () => !disposed && host.isConnected;
    const url = `/api/v1/board/cards/${encodeURIComponent(card.id)}/checks`;
    const message = text => { if (current()) find('message').textContent = text; };
    function render(pending = []) {
        const latest = ['Code quality', 'VCA'].map(tool => pending.find(row => row.tool === tool) || latestRows.find(row => row.tool === tool)
            || { tool, status: 'Not run', scope: 'Not selected', startedUtc: null });
        const summaryHtml = latest.map(row => row.startedUtc ? checkRow(row)
            : `<article class="board-check-result"><strong>${esc(row.tool)}</strong><span class="board-check-status">Not run</span><p>No saved evidence.</p></article>`).join('');
        if (find('summary').innerHTML !== summaryHtml) find('summary').innerHTML = summaryHtml;

    }
    async function refresh() {
        if (!current()) return;
        const version = ++generation;
        request?.abort(); request = new AbortController();
        try {
            const response = await app.apiCall(`${url}?offset=0`, 'GET', null,
                { showLoading: false, signal: request.signal, preferErrorResponseMessage: true });
            if (!current() || version !== generation) return;
            latestRows = response.latest || response.checks || [];
            render(response.pending || []);
            message('Results cover the recorded scope. A card is not a Git change boundary.');
        } catch (error) { if (current() && version === generation && error.name !== 'AbortError') message(error.message || 'Checks unavailable.'); }
    }
    async function showReport(id) {
        const version = ++reportGeneration;
        reportRequest?.abort(); reportRequest = new AbortController();
        viewer?.destroy(); viewer = null;
        const report = find('report'); report.hidden = false;
        report.innerHTML = '<p role="status">Loading saved evidence…</p>';
        try {
            const result = await app.apiCall(`${url}/${encodeURIComponent(id)}`, 'GET', null,
                { showLoading: false, signal: reportRequest.signal, preferErrorResponseMessage: true });
            if (!current() || version !== reportGeneration) return;
            const check = result.check;
            const evidence = check.resultJson ? JSON.parse(check.resultJson) : null;
            report.innerHTML = `<div class="board-checks-heading"><h4 tabindex="-1">${esc(check.tool)} · ${esc(check.status)}</h4>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-check-close>Close report</button></div>
                <p data-check-freshness role="status">${esc(result.freshness)}</p>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-check-verify="${esc(id)}">Compare inputs</button>
                <p>${esc(check.summary)}</p>
                <p>Scope: ${esc(check.scope)} · ${check.analyzedCount} ${check.tool === 'VCA' ? 'rules' : 'files'} analyzed · ${check.findingCount} findings · ${check.skippedCount} files skipped</p>
                <ul>${(check.limitations || []).map(text => `<li>${esc(text)}</li>`).join('')}</ul>
                <details><summary>Scope and capture details</summary><dl><dt>Base → head</dt><dd><code>${esc(check.baseCommit || 'unknown')} → ${esc(check.headCommit || 'unknown')}</code></dd>
                <dt>Captured checkout</dt><dd>${esc(check.workspacePath || 'unknown')}</dd><dt>Files in scope</dt><dd>${esc((check.scopeFiles || []).join(' · '))}</dd><dt>Run / version</dt><dd>${esc(check.runId)} / ${esc(check.toolVersion)}</dd><dt>Input / rule fingerprints</dt><dd><code>${esc(check.snapshotHash || 'unknown')} / ${esc(check.rulesHash || 'unknown')}</code></dd></dl></details>
                <div data-check-quality></div><details><summary>Full engine output and rule findings</summary><pre data-check-evidence></pre></details>`;
            report.querySelector('[data-check-evidence]').textContent = JSON.stringify(evidence, null, 2) || check.summary;
            report.querySelector('h4').focus();
            if (evidence?.details?.report) {
                viewer = new CodeReportViewer(report.querySelector('[data-check-quality]'), app);
                await viewer.setResponse({ success: true, report: JSON.parse(evidence.details.report),
                    analyzedFileCount: check.analyzedCount, skippedFileCount: check.skippedCount, startedUtc: check.startedUtc });
            }
        } catch (error) {
            if (current() && version === reportGeneration && error.name !== 'AbortError') report.textContent = error.message || 'Report unavailable.';
        }
    }
    async function onClick(event) {
        const button = event.target.closest('button'); if (!button) return;
        if (button.hasAttribute('data-check-refresh')) return refresh();
        if (button.dataset.checkView) return showReport(button.dataset.checkView);
        if (button.dataset.checkVerify) {
            const version = reportGeneration;
            reportRequest?.abort(); reportRequest = new AbortController();
            button.disabled = true;
            find('freshness').textContent = 'Comparing captured inputs with this checkout…';
            try {
                const response = await app.apiCall(`${url}/${encodeURIComponent(button.dataset.checkVerify)}?verify=true`, 'GET', null,
                    { showLoading: false, signal: reportRequest.signal, preferErrorResponseMessage: true });
                if (current() && version === reportGeneration) find('freshness').textContent = response.freshness;
            } catch (error) {
                if (current() && version === reportGeneration) find('freshness').textContent = error.message || 'Freshness unknown.';
            } finally { if (current() && version === reportGeneration) button.disabled = false; }
            return;
        }
        if (button.hasAttribute('data-check-close')) {
            ++reportGeneration; reportRequest?.abort(); viewer?.destroy(); viewer = null;
            find('report').hidden = true; find('report').replaceChildren(); find('refresh').focus(); return;
        }
    }
    host.addEventListener('click', onClick);
    const timer = setInterval(() => { if (!host.ownerDocument.hidden) void refresh(); }, 10000);
    void refresh();
    return { refresh, dispose() {
        disposed = true; ++generation; ++reportGeneration; clearInterval(timer);
        request?.abort(); reportRequest?.abort(); viewer?.destroy();
        host.removeEventListener('click', onClick);
    } };
}
