import { BoardApi } from './board-api.js';
import { escapeHtml as esc } from './utils.js';
import { switchReviewerDefaults, routingSummary, reviewerTargetMarkup, mountReviewerTarget, routingEditorMarkup, mountRoutingEditor } from './reviewer-routing.js';

export function cardReviewsSection(card) {
    return `<section class="board-block" data-board-reviews aria-label="Code reviews">
        <h3 class="board-block-label">Code reviews</h3>
        ${card?.id ? `<p class="small" data-review-resolution role="status">Loading Switch reviewer…</p>
            <label class="form-check"><input class="form-check-input" type="checkbox" data-review-override> Override reviewer for this review</label>
            <div data-review-target-host hidden>${reviewerTargetMarkup()}</div>
            <button type="button" class="btn btn-sm btn-outline-primary mt-2" data-review-run disabled>Run review</button>
            <details class="mt-2" data-review-settings><summary>Coding source, scope and mappings</summary>
                <p class="small">Choose the session that contributed the coding work. Session links and assignment alone do not establish authorship. Use Mixed if other agents or people also contributed.</p>
                <label class="form-label">Coding source<select class="form-select" data-review-source>
                    <option value="unknown">Unknown</option><option value="mixed">Mixed sources</option><option value="human">Human / external work</option>
                    ${(card.sessions || []).filter(s => !['chat', 'planning', 'code_review'].includes(s.origin)).map(s => `<option value="session:${esc(s.id)}">${esc(s.displayName || s.cli)} · ${esc(s.id)}</option>`).join('')}
                </select></label>
                <label class="form-label d-block">Coding work in this scope<textarea class="form-control" maxlength="2000" data-review-description></textarea></label>
                <label class="form-label">Review scope<select class="form-select" data-review-scope>
                    <option value="working-tree">Uncommitted changes</option><option value="unpushed">Unpushed commits</option><option value="range">Commit range</option><option value="repository">Repository</option><option value="unknown">Unknown — reviewer must report Incomplete</option>
                </select></label>
                <div data-review-range hidden><input class="form-control" data-review-base placeholder="Base commit (full SHA)" aria-label="Review base commit"><input class="form-control" data-review-head placeholder="Head commit (full SHA)" aria-label="Review head commit"></div>
                <label class="form-check"><input class="form-check-input" type="checkbox" data-review-dirty> Also include uncommitted changes</label>
                <p class="small">Review checkout: this project directory. This explicitly chosen scope also applies when the coding session or reviewer environment uses a clone.</p>
                <div data-review-routing-editor>${routingEditorMarkup()}</div>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-review-save>Save review inputs</button>
                <p class="small" data-review-settings-message role="status"></p>
            </details>
            <p class="board-editor-muted">Uses saved review inputs. New reviews capture current inputs; queued runs and retries keep their recorded routing and scope. Lane reviews use their Worker’s mappings.</p>
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
        ${row.routing ? `<p>${esc(routingSummary(row.routing))}</p>` : ''}
        <p>Process: ${esc(row.processStatus)} · ${esc(row.result || 'Report missing')}</p>
        <p>${esc(row.scopeDescription || row.routing?.scopeDescription || 'Scope not captured')} · ${esc(row.freshness || 'Unknown freshness')}</p>
        <small>${esc(new Date(row.createdUtc).toLocaleString())}</small>
        ${row.error ? `<p class="text-danger">${esc(row.error)}</p>` : ''}
        ${row.reportedUtc ? `<button type="button" class="btn btn-sm btn-link" data-review-view="${esc(row.id)}">View report</button>` : ''}
        ${session ? `<button type="button" class="btn btn-sm btn-link" data-board-open-session="${esc(session)}">Terminal / replay</button>` : '<p>No terminal session yet.</p>'}
    </article>`;
}

export function reviewReport(row) {
    return `<h4>${esc(row.result || 'Report missing')}</h4>
        ${row.routing ? `<p>${esc(routingSummary(row.routing))}</p><p>Coding source checkout: ${esc(row.routing.source?.workspace || 'Unknown')}</p>` : ''}
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
    const target = mountReviewerTarget(app, find('target-host'), { selection: 'base:codex' }, () => void preview());
    let routingEditor, settingsRequest, previewRequest, previewGeneration = 0, settingsBusy = false, ready = false;
    const picker = find('target-host').querySelector('[data-reviewer-target]');
    picker.setAttribute('data-review-picker', '');
    const url = `/api/v1/board/cards/${encodeURIComponent(card.id)}/reviews`;
    let disposed = false, busy = false, generation = 0, reportGeneration = 0, offset = 0, rows = [], request, reportRequest;
    const current = () => !disposed && host.isConnected;
    const message = text => { if (current()) find('message').textContent = text; };
    const options = signal => ({ showLoading: false, preferErrorResponseMessage: true, signal });
    const launchRequest = () => ({ override: find('override').checked ? target.read() : null });
    async function preview() {
        if (!current() || !ready) return;
        const version = ++previewGeneration;
        previewRequest?.abort(); previewRequest = new AbortController(); find('run').disabled = true;
        try {
            const r = await app.apiCall(`${url}/preview`, 'POST', launchRequest(), options(previewRequest.signal));
            if (!current() || version !== previewGeneration) return;
            find('resolution').textContent = `${routingSummary(r)}. ${r.scopeDescription} — ${r.workspace}${r.source?.workspace && r.source.workspace !== r.workspace ? `. Coding source checkout: ${r.source.workspace}` : ''}${r.limitations ? `. ${r.limitations}` : ''}${r.problem ? `. ${r.problem}` : ''}`;
            find('run').disabled = busy || settingsBusy || Boolean(r.problem);
        } catch (error) {
            if (current() && version === previewGeneration && !previewRequest.signal.aborted) find('resolution').textContent = error?.message || 'Could not resolve reviewer.';
        }
    }
    async function loadSettings() {
        settingsRequest = new AbortController();
        try {
            const value = await app.apiCall(`${url}/settings`, 'GET', null, options(settingsRequest.signal));
            if (!current()) return;
            const source = value.sourceKind === 'session' ? `session:${value.sourceSessionId}` : value.sourceKind || 'unknown';
            if (![...find('source').options].some(o => o.value === source)) {
                const option = document.createElement('option'); option.value = source; option.textContent = `Selected coding session ${value.sourceSessionId}`; find('source').append(option);
            }
            find('source').value = source; find('description').value = value.description || ''; find('scope').value = value.scope || 'working-tree';
            find('base').value = value.baseCommit || ''; find('head').value = value.headCommit || ''; find('dirty').checked = value.includeDirty === true;
            find('range').hidden = find('scope').value !== 'range';
            routingEditor = mountRoutingEditor(app, find('routing-editor'), value.routing || switchReviewerDefaults(), settingsChanged);
            ready = true; void preview();
        } catch (error) { if (current() && !settingsRequest.signal.aborted) find('resolution').textContent = error?.message || 'Could not load review inputs.'; }
    }
    function settingsChanged() { if (current()) find('settings-message').textContent = 'Unsaved review inputs. Save to use them for a new review.'; }
    async function saveSettings() {
        if (!ready || busy || settingsBusy) return;
        settingsBusy = true; find('save').disabled = true; find('run').disabled = true;
        const source = find('source').value;
        try {
            const value = { sourceKind: source.startsWith('session:') ? 'session' : source, sourceSessionId: source.startsWith('session:') ? source.slice(8) : null,
                description: find('description').value, scope: find('scope').value, baseCommit: find('base').value || null, headCommit: find('head').value || null,
                includeDirty: find('dirty').checked, routing: routingEditor.read() };
            await app.apiCall(`${url}/settings`, 'PUT', value, options());
            if (current()) { find('settings-message').textContent = 'Review inputs saved. Existing runs retain their snapshots.'; }
        } catch (error) { if (current()) find('settings-message').textContent = error?.message || 'Could not save review inputs.'; }
        finally { settingsBusy = false; if (current()) { find('save').disabled = false; void preview(); } }
    }
    const inputChange = event => {
        if (event.target === find('override')) { find('target-host').hidden = !event.target.checked; void preview(); }
        else if (find('settings').contains(event.target)) { find('range').hidden = find('scope').value !== 'range'; settingsChanged(); }
    };
    host.addEventListener('change', inputChange);
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
        if (busy || settingsBusy || !ready || !current() || find('run').disabled) return;
        if (editor._boardSaving || editor._boardUploading || editor._boardStarting) { message('Wait for the current card action to finish.'); return; }
        busy = true; ++generation; request?.abort(); find('run').disabled = true;
        message('Starting code review…');
        try {
            await BoardApi.launchBoardCardAsync(card.id, { intent: 'code_review', review: launchRequest() });
            if (current()) { busy = false; await refresh(); onQueued?.(); }
        } catch (error) { busy = false; await refresh(); message(error?.message || 'Could not start review.'); }
        finally { busy = false; if (current()) void preview(); }
    }
    const click = event => {
        const button = event.target.closest('button');
        if (!button || !host.contains(button)) return;
        if (button.hasAttribute('data-review-run')) void run();
        else if (button.hasAttribute('data-review-save')) void saveSettings();
        else if (button.hasAttribute('data-review-refresh')) { void refresh(); void preview(); }
        else if (button.hasAttribute('data-review-more')) void refresh(true);
        else if (button.dataset.reviewView) void report(button.dataset.reviewView, true);
        else if (button.dataset.reviewCompare) void report(button.dataset.reviewCompare, true);
    };
    host.addEventListener('click', click);
    void refresh(); void loadSettings();
    return { refresh, dispose() {
        disposed = true; ++generation; ++reportGeneration;
        request?.abort(); reportRequest?.abort(); settingsRequest?.abort(); previewRequest?.abort(); target.dispose(); routingEditor?.dispose();
        host.removeEventListener('change', inputChange); host.removeEventListener('click', click);
    } };
}
