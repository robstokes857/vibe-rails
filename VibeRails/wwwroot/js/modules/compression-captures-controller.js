const esc = v => String(v ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
const shortId = v => !v ? 'n/a' : (v.length <= 14 ? v : v.slice(0, 8) + '…');
const fmtNum = n => (n ?? 0).toLocaleString();
const fmtDate = v => {
    if (!v) return 'n/a';
    // Capture timestamps are UTC. SQLite hands them back without a zone marker, so tag them
    // before parsing or the browser reads them as local and every row is hours off.
    const raw = String(v);
    const d = new Date(/[zZ]|[+-]\d{2}:?\d{2}$/.test(raw) ? raw : raw + 'Z');
    if (Number.isNaN(d.getTime())) return 'n/a';
    return new Intl.DateTimeFormat(undefined, { month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(d);
};

const PAGE_SIZE = 25;

/**
 * The capture browser: what the token saver actually did to one tool_result, and what a different
 * stage selection would have done to the same bytes.
 *
 * Two rules this file lives by:
 *
 *  - Capture text is arbitrary output from the user's machine. It reaches the DOM through
 *    textContent, never through an innerHTML interpolation.
 *  - The stage list is never hardcoded. Everything renders from GET /api/v1/compression/catalog,
 *    which is the same source of truth the pipeline resolves against.
 */
export class CompressionCapturesController {
    constructor(app, root) {
        this.app = app;
        this.root = root;

        this.state = {
            catalog: null,
            captures: [],
            total: 0,
            hasMore: false,
            selectedId: null,
            detail: null,
            preview: null,
            open: true,
            loading: false,
        };
        // Bumped on every selectCapture — lets a slow detail response for a row the user has
        // already navigated away from drop itself instead of clobbering the newer selection.
        this._detailGen = 0;

        const q = sel => this.root.querySelector(sel);
        this.nodes = {
            section: q('.insp-cap'),
            head: q('.insp-cap-head'),
            toggle: q('[data-cap-toggle]'),
            refresh: q('[data-cap-refresh]'),
            clear: q('[data-cap-clear]'),
            body: q('[data-cap-body]'),
            banner: q('[data-cap-banner]'),
            list: q('[data-cap-list]'),
            moreRow: q('[data-cap-more-row]'),
            more: q('[data-cap-more]'),
            lens: q('[data-cap-lens]'),
            count: q('[data-cap-count]'),
            countLabel: q('[data-cap-count-label]'),
        };
    }

    async init() {
        if (!this.nodes.section) return;
        this._bindEvents();
        await this.refresh({ silent: true });
    }

    _bindEvents() {
        this.nodes.toggle.addEventListener('click', () => this._toggleOpen());
        this.nodes.refresh.addEventListener('click', () => this.refresh());
        this.nodes.clear.addEventListener('click', () => this.confirmClear());
        this.nodes.more.addEventListener('click', () => this.loadMore());

        this.nodes.list.addEventListener('click', e => {
            const copy = e.target.closest('[data-cap-copy]');
            if (copy) {
                // The row underneath is a select target; a copy click is not a select.
                e.stopPropagation();
                this._copy(copy.getAttribute('data-cap-copy'), copy);
                return;
            }
            const row = e.target.closest('[data-cap-id]');
            if (row) this.selectCapture(row.getAttribute('data-cap-id'));
        });

        // The rows are divs (they contain a copy button, and a button inside a button is invalid),
        // so they need their keyboard affordance wired by hand.
        this.nodes.list.addEventListener('keydown', e => {
            if (e.key !== 'Enter' && e.key !== ' ') return;
            const row = e.target.closest('[data-cap-id]');
            if (!row) return;
            e.preventDefault();
            this.selectCapture(row.getAttribute('data-cap-id'));
        });

        this.nodes.lens.addEventListener('click', e => {
            const copy = e.target.closest('[data-cap-copy]');
            if (copy) { this._copy(copy.getAttribute('data-cap-copy'), copy); return; }
            if (e.target.closest('[data-cap-run]')) { void this.runPreview(); return; }
            if (e.target.closest('[data-cap-reset]')) { this._seedWhatIf(); return; }
        });
    }

    // ─── fetch helpers ────────────────────────────────────────────────────

    setBanner(msg, tone) {
        this.nodes.banner.textContent = msg || '';
        this.nodes.banner.className = 'insp-banner' + (tone ? ' ' + tone : '');
    }

    setBusy(flag) {
        this.state.loading = flag;
        this.nodes.refresh.disabled = flag;
        this.nodes.clear.disabled = flag;
        this.nodes.more.disabled = flag;
    }

    _toggleOpen() {
        this.state.open = !this.state.open;
        this.nodes.body.classList.toggle('open', this.state.open);
        this.nodes.toggle.setAttribute('aria-expanded', String(this.state.open));
    }

    // ─── data flows ───────────────────────────────────────────────────────

    async refresh(options = {}) {
        const silent = options?.silent === true;
        this.setBusy(true);
        try {
            // The catalog names the stages and drives the what-if checkboxes. Fetch it once —
            // it only changes when the server build does.
            if (!this.state.catalog) {
                this.state.catalog = await this.app.apiCall('/api/v1/compression/catalog', 'GET', null, { showLoading: false });
            }
            const page = await this._fetchPage(0);
            this.state.captures = page;
            this.state.selectedId = null;
            this.state.detail = null;
            this.state.preview = null;
            this._renderList();
            this._renderLens();
            if (!silent) this.setBanner(`${this.state.captures.length} capture${this.state.captures.length === 1 ? '' : 's'} loaded.`, 'success');
        } catch (err) {
            this.setBanner(err.message, 'error');
        } finally {
            this.setBusy(false);
        }
    }

    async loadMore() {
        this.setBusy(true);
        try {
            const page = await this._fetchPage(this.state.captures.length);
            this.state.captures = this.state.captures.concat(page);
            this._renderList();
        } catch (err) {
            this.setBanner(err.message, 'error');
        } finally {
            this.setBusy(false);
        }
    }

    confirmClear() {
        this.app.showModal('Clear Compression Captures', `
            <p>Delete every stored raw/compressed tool output and trace?</p>
            <p class="text-muted small">This cannot be undone. New captures will still be recorded if diagnostic capture is enabled in Settings.</p>
            <div class="d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="button" class="btn btn-danger" id="confirm-clear-compression-captures">Clear Captures</button>
            </div>`);

        document.getElementById('confirm-clear-compression-captures')?.addEventListener('click', async () => {
            this.app.closeModal();
            this.setBusy(true);
            try {
                const result = await this.app.apiCall(
                    '/api/v1/compression/captures', 'DELETE', null, { showLoading: false });
                await this.refresh({ silent: true });
                this.setBanner(`${fmtNum(result?.deleted)} capture${result?.deleted === 1 ? '' : 's'} deleted.`, 'success');
            } catch (err) {
                this.setBanner(`Could not clear captures: ${err.message}`, 'error');
            } finally {
                this.setBusy(false);
            }
        });
    }

    // Asks for one row more than a page so "is there another page" is answered by the data
    // rather than guessed from a count the list endpoint doesn't return.
    async _fetchPage(skip) {
        const rows = await this.app.apiCall(
            `/api/v1/compression/captures?take=${PAGE_SIZE + 1}&skip=${skip}`, 'GET', null, { showLoading: false });
        const page = Array.isArray(rows) ? rows : [];
        this.state.hasMore = page.length > PAGE_SIZE;
        return this.state.hasMore ? page.slice(0, PAGE_SIZE) : page;
    }

    async selectCapture(id) {
        if (!id) return;
        const gen = ++this._detailGen;
        this.state.selectedId = id;
        this.state.detail = null;
        this.state.preview = null;
        this._renderList();
        this._renderLens();
        try {
            const detail = await this.app.apiCall(
                `/api/v1/compression/captures/${encodeURIComponent(id)}`, 'GET', null, { showLoading: false });
            if (gen !== this._detailGen) return; // a newer selection superseded us
            this.state.detail = detail;
            this._renderLens();
            this._seedWhatIf();
            this.nodes.lens.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        } catch (err) {
            if (gen !== this._detailGen) return;
            this.setBanner(`Could not load capture: ${err.message}`, 'error');
        }
    }

    async runPreview() {
        const detail = this.state.detail;
        if (!detail) return;
        const runBtn = this.nodes.lens.querySelector('[data-cap-run]');
        const enabledIds = this._collectWhatIf();
        if (runBtn) runBtn.disabled = true;
        try {
            // The real pipeline over the real stored RawText — not a simulation. Empty stays
            // empty: [] means "every stage off", which the server honours as a real answer.
            const preview = await this.app.apiCall('/api/v1/compression/preview', 'POST', {
                captureId: detail.id,
                enabledIds: enabledIds
            }, { showLoading: false });
            this.state.preview = { ...preview, enabledIds };
            this._renderLens();
            this._applyWhatIf(enabledIds);
        } catch (err) {
            this.setBanner(`Preview failed: ${err.message}`, 'error');
        } finally {
            const btn = this.nodes.lens.querySelector('[data-cap-run]');
            if (btn) btn.disabled = false;
        }
    }

    // ─── rendering: list ──────────────────────────────────────────────────

    _renderList() {
        const items = this.state.captures;
        this.nodes.count.textContent = fmtNum(items.length);
        this.nodes.countLabel.textContent = items.length === 1 ? 'capture' : 'captures';
        this.nodes.moreRow.hidden = !this.state.hasMore;

        if (!items.length) {
            this.nodes.list.innerHTML = `<div class="insp-cap-empty">No captures yet. Turn on <b>Capture requests</b> in Settings → Token Saver, then run an agent through an enabled proxy.</div>`;
            return;
        }

        this.nodes.list.innerHTML = items.map(c => {
            const saved = (c.charsBefore || 0) - (c.charsAfter || 0);
            const pct = c.charsBefore > 0 ? Math.round((saved / c.charsBefore) * 100) : 0;
            const sel = this.state.selectedId === c.id;
            const disposition = c.rewriteAccepted
                ? { cls: 'yes', text: 'applied' }
                : c.changed
                    ? { cls: 'warn', text: 'rejected' }
                    : { cls: 'no', text: 'as-is' };
            return `
                <div class="insp-cap-row ${sel ? 'sel' : ''}" role="button" tabindex="0"
                     data-cap-id="${esc(c.id)}" title="${esc(c.command || c.toolName || '')}">
                    <span class="insp-cap-id">
                        ${esc(shortId(c.id))}
                        <button class="insp-cap-copy" type="button" data-cap-copy="${esc(c.id)}" title="Copy full GUID">copy</button>
                    </span>
                    <span class="insp-cap-cmd"><span class="tool">${esc(c.toolName || '?')}</span>${esc(c.command || '—')}</span>
                    <span class="insp-cap-delta">${fmtNum(c.charsBefore)}→${fmtNum(c.charsAfter)}${saved > 0 ? ` <b>−${pct}%</b>` : ''}</span>
                    <span class="insp-cap-flag ${disposition.cls}">${disposition.text}</span>
                    <span class="insp-cap-when">${esc(fmtDate(c.createdUtc))}</span>
                </div>`;
        }).join('');
    }

    // ─── rendering: detail lens ───────────────────────────────────────────

    _renderLens() {
        const d = this.state.detail;
        if (!this.state.selectedId) {
            this.nodes.lens.hidden = true;
            this.nodes.lens.innerHTML = '';
            return;
        }
        this.nodes.lens.hidden = false;
        if (!d) {
            this.nodes.lens.innerHTML = `<div class="insp-cap-placeholder">Loading capture…</div>`;
            return;
        }

        const saved = (d.charsBefore || 0) - (d.charsAfter || 0);
        const pct = d.charsBefore > 0 ? ((saved / d.charsBefore) * 100).toFixed(1) : '0.0';
        const metaCells = [
            ['captured', fmtDate(d.createdUtc)],
            ['provider', d.provider || '—'],
            ['tool', d.toolName || '—'],
            ['chars', `${fmtNum(d.charsBefore)} → ${fmtNum(d.charsAfter)}`],
            ['candidate saved', `${fmtNum(saved)} (${pct}%)`],
            ['candidate changed', d.changed ? 'yes' : 'no'],
            ['forwarded', d.rewriteAccepted ? 'compressed candidate' : 'original text'],
        ];

        this.nodes.lens.innerHTML = `
            <div class="insp-cap-guid">
                <span class="lbl">Capture</span>
                <span class="val" data-cap-guid>${esc(d.id)}</span>
                <button class="insp-cap-copy" type="button" data-cap-copy="${esc(d.id)}" title="Copy this GUID — it is the handle used with runbooks/compress_runbook.md">Copy GUID</button>
            </div>

            <div class="insp-cap-meta">
                ${metaCells.map(([k, v]) => `<div class="insp-cap-meta-cell"><div class="k">${esc(k)}</div><div class="v">${esc(v)}</div></div>`).join('')}
                <div class="insp-cap-meta-cell" style="grid-column:1/-1">
                    <div class="k">command</div>
                    <div class="v" data-cap-command></div>
                </div>
            </div>

            <div class="insp-cap-sec">
                <div class="insp-cap-sec-head">
                    <span>Raw uncompressed text</span>
                    <span><b>${fmtNum(d.charsBefore)}</b> chars</span>
                </div>
                <pre class="insp-cap-text" data-cap-raw></pre>
            </div>

            <div class="insp-cap-whatif">
                <div class="insp-cap-sec-head">
                    <span>What if — re-run these bytes under a different selection</span>
                </div>
                <div class="insp-cap-checks" data-cap-checks></div>
                <div class="insp-cap-whatif-actions">
                    <button class="insp-run" type="button" data-cap-run>Run&nbsp;<span class="arrow">→</span></button>
                    <button class="insp-refresh" type="button" data-cap-reset title="Set the checkboxes back to the selection this capture actually ran under">Reset to actual</button>
                    <span class="insp-banner" data-cap-whatif-note></span>
                </div>
            </div>

            <div class="insp-cap-compare">
                <div class="insp-cap-pane actual">
                    <div class="insp-cap-pane-title">${d.rewriteAccepted
                        ? 'What we actually forwarded'
                        : d.changed
                            ? 'Pipeline candidate — rejected, not forwarded'
                            : 'Pipeline result — forwarded unchanged'}</div>
                    ${this._paneHtml('actual', d.charsBefore, d.charsAfter)}
                </div>
                <div class="insp-cap-pane whatif">
                    <div class="insp-cap-pane-title">What if</div>
                    ${this.state.preview
                        ? this._paneHtml('preview', this.state.preview.charsBefore, this.state.preview.charsAfter)
                        : `<div class="insp-cap-placeholder">Tick a different set of stages above and press Run. The result lands here, next to the real one.</div>`}
                </div>
            </div>`;

        // Capture text is verbatim output from the user's machine — it goes in as text, never as
        // markup. Same for the command, which is equally attacker-shaped.
        this._setText('[data-cap-command]', d.command || '—');
        this._setText('[data-cap-raw]', d.rawText ?? '');
        this._setText('[data-cap-actual-out]', d.compressedText ?? '');
        this._renderTraceInto('[data-cap-actual-trace]', d.trace, d.enabledIds);

        if (this.state.preview) {
            this._setText('[data-cap-preview-out]', this.state.preview.output ?? '');
            this._renderTraceInto('[data-cap-preview-trace]', this.state.preview.trace, this.state.preview.enabledIds);
        }

        this._renderWhatIfChecks();
        const whatIfNote = this.nodes.lens.querySelector('[data-cap-whatif-note]');
        if (whatIfNote && this.state.preview?.scopeAllowed === false) {
            whatIfNote.textContent = 'This selection excludes the captured tool; the real proxy would forward the raw text.';
        }
    }

    _paneHtml(kind, before, after) {
        const saved = (before || 0) - (after || 0);
        const pct = before > 0 ? ((saved / before) * 100).toFixed(1) : '0.0';
        return `
            <div class="insp-cap-sec-head">
                <span>Output</span>
                <span><b>${fmtNum(after)}</b> chars · ${saved > 0 ? `−${pct}%` : 'no change'}</span>
            </div>
            <pre class="insp-cap-text" data-cap-${kind}-out></pre>
            <div class="insp-cap-sec-head"><span>Stage trace</span></div>
            <div class="insp-cap-trace" data-cap-${kind}-trace></div>`;
    }

    _setText(selector, text) {
        const el = this.nodes.lens.querySelector(selector);
        if (el) el.textContent = text;
    }

    // ─── rendering: trace ─────────────────────────────────────────────────

    _orderedStages() {
        const stages = this.state.catalog?.stages || [];
        return [...stages].sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
    }

    /**
     * Renders every catalog stage, including the ones that were off. That is the whole point of
     * the trace being a contract: "that stage was disabled" and "that stage ran and declined" are
     * the two answers you actually need, and a table that only listed the stages that did
     * something would make them indistinguishable.
     */
    _renderTraceInto(selector, trace, enabledIds) {
        const host = this.nodes.lens.querySelector(selector);
        if (!host) return;

        const byId = new Map((trace || []).map(t => [t.stageId, t]));
        const enabled = new Set(enabledIds || []);
        const rows = [];

        for (const stage of this._orderedStages()) {
            rows.push({ name: stage.name, id: stage.id, entry: byId.get(stage.id) });
            byId.delete(stage.id);
        }
        // Anything the capture traced that this build's catalog no longer lists. A capture is a
        // record of the build that made it, so show these rather than quietly dropping them.
        for (const [id, entry] of byId) rows.push({ name: id, id, entry, unknown: true });

        host.innerHTML = `
            <table>
                <thead><tr><th>stage</th><th>outcome</th><th class="num">chars removed</th></tr></thead>
                <tbody>
                    ${rows.map(r => {
                        const outcome = r.entry?.outcome ?? (enabled.has(r.id) ? '—' : 'Disabled');
                        const cls = String(outcome).toLowerCase().replace(/[^a-z]/g, '');
                        const off = outcome === 'Disabled' || outcome === '—';
                        const removed = r.entry?.charsRemoved ?? 0;
                        return `<tr class="${off ? 'off' : ''}">
                            <td title="${esc(r.id)}">${esc(r.name)}${r.unknown ? ' <span class="insp-cap-flag no">unknown</span>' : ''}</td>
                            <td><span class="insp-cap-out ${cls}">${esc(outcome)}</span></td>
                            <td class="num">${removed > 0 ? fmtNum(removed) : '—'}</td>
                        </tr>`;
                    }).join('')}
                </tbody>
            </table>`;
    }

    // ─── rendering: what-if controls ──────────────────────────────────────

    _renderWhatIfChecks() {
        const host = this.nodes.lens.querySelector('[data-cap-checks]');
        if (!host) return;
        const catalog = this.state.catalog;
        if (!catalog?.stages?.length) {
            host.innerHTML = `<div class="insp-cap-placeholder">Catalog unavailable — cannot build a what-if selection.</div>`;
            return;
        }

        const stageBox = s => `
            <label class="insp-cap-check ${String(s.kind).toLowerCase() === 'lossy' ? 'lossy' : ''}" title="${esc(s.summary)}">
                <input type="checkbox" data-cap-check="${esc(s.id)}">
                <span>${esc(s.name)}</span>
            </label>`;
        const scopeBox = s => `
            <label class="insp-cap-check ${s.warning ? 'risky' : ''}" title="${esc(s.warning || s.summary)}">
                <input type="checkbox" data-cap-check="${esc(s.id)}">
                <span>${esc(s.name)}${s.warning ? ' ⚠' : ''}</span>
            </label>`;

        host.innerHTML = this._orderedStages().map(stageBox).join('')
            + (catalog.scopes || []).map(scopeBox).join('');

        // Re-check against whatever the last render was showing: the preview's selection if one
        // has been run, otherwise the set this capture actually ran under.
        this._applyWhatIf(this.state.preview?.enabledIds ?? this.state.detail?.enabledIds ?? []);
    }

    _seedWhatIf() {
        this._applyWhatIf(this.state.detail?.enabledIds ?? []);
        const note = this.nodes.lens.querySelector('[data-cap-whatif-note]');
        if (note) note.textContent = '';
    }

    _collectWhatIf() {
        return Array.from(this.nodes.lens.querySelectorAll('[data-cap-check]'))
            .filter(input => input.checked)
            .map(input => input.getAttribute('data-cap-check'));
    }

    _applyWhatIf(ids) {
        const enabled = new Set(ids || []);
        this.nodes.lens.querySelectorAll('[data-cap-check]').forEach(input => {
            input.checked = enabled.has(input.getAttribute('data-cap-check'));
        });
    }

    // ─── clipboard ────────────────────────────────────────────────────────

    async _copy(text, btn) {
        if (!text) return;
        let ok = false;
        try {
            await navigator.clipboard.writeText(text);
            ok = true;
        } catch {
            // execCommand is deprecated but is still the only fallback that works when the
            // async clipboard is unavailable — notably inside the VS Code webview.
            try {
                const ta = document.createElement('textarea');
                ta.value = text;
                ta.style.position = 'fixed';
                ta.style.opacity = '0';
                document.body.appendChild(ta);
                ta.select();
                ok = document.execCommand('copy');
                document.body.removeChild(ta);
            } catch { ok = false; }
        }
        if (!btn) return;
        const original = btn.textContent;
        btn.textContent = ok ? 'copied' : 'failed';
        btn.classList.toggle('done', ok);
        setTimeout(() => {
            btn.textContent = original;
            btn.classList.remove('done');
        }, 1200);
    }
}
