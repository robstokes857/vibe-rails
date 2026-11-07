import { escapeHtml } from './utils.js';
import { reviewReport } from './board-card-reviews.js';
import { CodeReportViewer } from './code-report/viewer.js';

/** The immutable run points to either a saved Board review or Action check evidence. */
export function mountLaunchEvidence(app, host, api, run, signal) {
    const abort = new AbortController();
    let viewer;
    const dispose = () => {
        abort.abort();
        viewer?.destroy();
        signal.removeEventListener('abort', dispose);
    };
    signal.addEventListener('abort', dispose, { once: true });
    const current = () => !abort.signal.aborted && host.isConnected;
    host.innerHTML = '<p role="status">Loading saved result…</p>';
    const load = async () => {
        try {
            const cardId = run.snapshot?.context?.cardId;
            const isReview = !run.snapshot?.action && cardId;
            const result = await api.call(
                isReview
                    ? `board/cards/${encodeURIComponent(cardId)}/reviews/${encodeURIComponent(run.evidenceId)}`
                    : `runs/${encodeURIComponent(run.id)}/evidence`,
                'GET',
                null,
                { signal: abort.signal }
            );
            if (!current()) return;
            if (!result) {
                host.textContent = 'No saved result is available for this run.';
                return;
            }
            if (isReview) {
                host.innerHTML = reviewReport(result, { compare: false });
                return;
            }
            const evidence = result.resultJson
                ? JSON.parse(result.resultJson)
                : null;
            host.innerHTML = `<h4>${escapeHtml(result.tool || 'Check')} · ${escapeHtml(result.status || run.verdict || 'Not reported')}</h4>
                <p>${escapeHtml(result.summary || '')}</p>
                <p>Scope: ${escapeHtml(result.scope || 'Not recorded')} · ${Number(result.analyzedCount) || 0} analyzed · ${Number(result.findingCount) || 0} findings</p>
                <ul>${(result.limitations || []).map((text) => `<li>${escapeHtml(text)}</li>`).join('')}</ul>
                <div data-launch-quality></div>
                <details><summary>Full recorded output</summary><pre class="board-code">${escapeHtml(JSON.stringify(evidence || result, null, 2))}</pre></details>`;
            if (evidence?.details?.report) {
                viewer = new CodeReportViewer(
                    host.querySelector('[data-launch-quality]'),
                    app
                );
                await viewer.setResponse({
                    success: true,
                    report: JSON.parse(evidence.details.report),
                    analyzedFileCount: result.analyzedCount,
                    skippedFileCount: result.skippedCount,
                    startedUtc: result.startedUtc
                });
                if (!current()) viewer.destroy();
            }
        } catch (error) {
            if (current())
                host.textContent = error.message || 'Saved result unavailable.';
        }
    };
    void load();
    return dispose;
}
