import { escapeHtml as esc } from './utils.js';

// The latest saved check of each tool, under the card editor's Advanced section. **View report**
// opens the check's report dialog (board-check-report.js) through the editor's data-board-ref-check
// handler, the same one a check's comment and the Code reviews list use (VIBE-362).

export function cardChecksSection() {
    return `<section class="board-block board-checks" data-board-checks aria-label="Checks">
        <div class="board-checks-heading"><h3 class="board-block-label">Checks</h3>
            <button type="button" class="btn btn-sm btn-secondary" data-check-refresh>Refresh checks</button></div>
        <p data-check-message role="status" aria-live="polite">Loading checks…</p>
        <div class="board-checks-summary" data-check-summary></div>
    </section>`;
}

const plural = (count, noun) => `${count} ${noun}${count === 1 ? '' : 's'}`;

/** "2 files checked · 1 needs work · 4 not checked" for Code quality; VCA counts rules. */
function counts(check) {
    if (check.tool === 'VCA') return `${plural(check.analyzedCount || 0, 'rule')} checked · ${plural(check.findingCount || 0, 'finding')} · ${plural(check.skippedCount || 0, 'file')} skipped`;
    const findings = check.findingCount || 0;
    return `${plural(check.analyzedCount || 0, 'file')} checked · ${findings === 1 ? '1 needs' : `${findings} need`} work · ${check.skippedCount || 0} not checked`;
}

export function checkRow(check) {
    const pending = String(check.id).startsWith('pending-') || String(check.id).startsWith('entry-');
    return `<article class="board-check-result">
        <div><strong>${esc(check.tool)}</strong><span class="board-check-status" data-status="${esc(check.status)}">${esc(check.status)}</span></div>
        <p>${counts(check)}</p>
        ${pending || check.status === 'Needs review' ? `<p>${esc(check.summary)}</p>` : ''}
        <p>Scope: ${esc(check.scope)} · ${esc(new Date(check.startedUtc).toLocaleString())}</p>
        ${pending ? (check.endedUtc ? '' : '<p class="board-editor-muted">Waiting for completed evidence.</p>')
            : `<button type="button" class="btn btn-link btn-sm" data-board-ref-check="${esc(check.id)}">View report</button>`}
    </article>`;
}

export function bindCardChecks(editor, card, app) {
    const host = editor.querySelector('[data-board-checks]');
    if (!host || !card?.id) return null;
    const find = key => host.querySelector(`[data-check-${key}]`);
    let disposed = false, generation = 0, latestRows = [];
    let request;
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
    const onClick = event => { if (event.target.closest('[data-check-refresh]')) void refresh(); };
    host.addEventListener('click', onClick);
    const poll = () => { if (!app.isUiHidden()) void refresh(); };
    const timer = setInterval(poll, 10000);
    const unsubscribeVisible = app.onUiVisible(poll);
    void refresh();
    return { refresh, dispose() {
        disposed = true; ++generation; clearInterval(timer);
        unsubscribeVisible();
        request?.abort();
        host.removeEventListener('click', onClick);
    } };
}
