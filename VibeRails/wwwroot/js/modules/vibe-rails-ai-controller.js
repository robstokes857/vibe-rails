import { showTranscriptModal, showReplayModal } from './session-viewer.js';

const esc = v => String(v ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
const shortId = v => !v ? 'n/a' : (v.length <= 14 ? v : v.slice(0, 8) + '\u2026' + v.slice(-4));
const fmtDate = v => {
    if (!v) return 'n/a';
    const d = new Date(v);
    if (Number.isNaN(d.getTime())) return 'n/a';
    return new Intl.DateTimeFormat(undefined, { month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(d);
};
const fmtScore = v => typeof v === 'number' ? (v * 100).toFixed(1) + '%' : '';
function scoreClass(v) { return v >= 0.5 ? 'high' : v >= 0.25 ? 'mid' : 'low'; }
function scoreColor(v) { return v >= 0.5 ? 'var(--vra-ok)' : v >= 0.25 ? 'var(--vra-warn)' : 'var(--vra-bad)'; }

export class VibeRailsAiController {
    constructor(app) {
        this.app = app;
    }

    loadView() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('vibe-rails-ai-template');
        const root = fragment.querySelector('[data-view="vibe-rails-ai"]');
        content.appendChild(fragment);

        this.root = document.querySelector('[data-view="vibe-rails-ai"]');
        if (!this.root) return;

        this.state = {
            status: null,
            recent: [],
            results: [],
            sessionCaptures: [],
            lastSearch: null,
            selected: null,
            selectedId: null,
            queryCount: 0,
            detailLoading: false,
            listMode: 'search'
        };

        const q = sel => this.root.querySelector(sel);
        this.nodes = {
            docs: q('[data-vra-docs]'),
            sessions: q('[data-vra-sessions]'),
            queries: q('[data-vra-queries]'),
            statusPills: q('[data-vra-status-pills]'),
            form: q('[data-vra-form]'),
            query: q('[data-vra-query]'),
            mode: q('[data-vra-mode]'),
            topk: q('[data-vra-topk]'),
            search: q('[data-vra-search]'),
            refresh: q('[data-vra-refresh]'),
            banner: q('[data-vra-banner]'),
            sessionId: q('[data-vra-session-id]'),
            sessionSearch: q('[data-vra-session-search]'),
            sessionBanner: q('[data-vra-session-banner]'),
            results: q('[data-vra-results]'),
            resultsSub: q('[data-vra-results-sub]'),
            resultsSource: q('[data-vra-results-source]'),
            detail: q('[data-vra-detail]'),
            detailSub: q('[data-vra-detail-sub]')
        };

        this._bindEvents();
        this.refreshAll();
    }

    _bindEvents() {
        this.nodes.form.addEventListener('submit', e => { e.preventDefault(); this.runSearch(); });
        this.nodes.sessionSearch.addEventListener('click', () => this.runSessionLookup());
        this.nodes.sessionId.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); this.runSessionLookup(); } });
        this.nodes.refresh.addEventListener('click', () => this.refreshAll());
        this.nodes.results.addEventListener('click', e => {
            const b = e.target.closest('[data-doc-id]');
            if (b) this.loadCapture(b.getAttribute('data-doc-id'), this.state.listMode);
        });
        this.nodes.resultsSource.addEventListener('change', () => {
            this.state.listMode = this.nodes.resultsSource.value;
            this.renderResultsList();
        });
        this.nodes.detail.addEventListener('click', e => {
            const btn = e.target.closest('[data-action]');
            if (!btn) return;
            const sessionId = this.state.selected?.sessionId;
            if (!sessionId) { this.setBanner('No session ID on this capture.', 'error'); return; }
            const action = btn.getAttribute('data-action');
            if (action === 'get-transcript') void showTranscriptModal(sessionId);
            else if (action === 'get-session') void showReplayModal(sessionId);
        });
    }

    async _fetchJson(url, init) {
        const tabToken = sessionStorage.getItem('viberails_tab');
        const headers = new Headers();
        if (init && init.headers) {
            const h = init.headers;
            if (h instanceof Headers) h.forEach((v, k) => headers.set(k, v));
            else for (const [k, v] of Object.entries(h)) { if (v != null) headers.set(k, String(v)); }
        }
        if (!headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
        if (tabToken) headers.set('viberails_tab', tabToken);

        const res = await fetch(url, { credentials: 'include', cache: 'no-store', ...init, headers });
        const text = await res.text();
        let data = {};
        if (text) { try { data = JSON.parse(text); } catch { data = { message: text }; } }
        if (!res.ok) {
            if (res.status === 404) throw new Error('Vibe Rails AI API routes not loaded. Restart the app from the updated build.');
            throw new Error(data.error || data.message || 'Request failed.');
        }
        return data;
    }

    setBanner(msg, tone) {
        this.nodes.banner.textContent = msg || '';
        this.nodes.banner.className = 'vra-banner' + (tone ? ' ' + tone : '');
    }

    setSessionBanner(msg, tone) {
        this.nodes.sessionBanner.textContent = msg || '';
        this.nodes.sessionBanner.className = 'vra-banner' + (tone ? ' ' + tone : '');
    }

    setBusy(flag) { this.nodes.search.disabled = flag; this.nodes.refresh.disabled = flag; }

    renderStatus() {
        const s = this.state.status;
        if (!s) return;
        this.nodes.docs.textContent = String(s.documentCount || 0);
        this.nodes.sessions.textContent = String(s.sessionCount || 0);
        this.nodes.statusPills.innerHTML =
            `<span class="vra-pill ${s.databaseExists ? 'ok' : 'bad'}" title="Database">${s.databaseExists ? 'DB OK' : 'DB Missing'}</span>` +
            `<span class="vra-pill ${s.semanticSearchAvailable ? 'ok' : 'warn'}" title="Semantic search">${s.semanticSearchAvailable ? 'Semantic Ready' : 'Semantic N/A'}</span>`;
        const semOpt = this.nodes.mode.querySelector('option[value="semantic"]');
        if (semOpt) semOpt.disabled = !s.semanticSearchAvailable;
        if (!s.semanticSearchAvailable && this.nodes.mode.value === 'semantic') this.nodes.mode.value = 'text';
    }

    renderResultsList() {
        const { state, nodes } = this;
        const items = state.listMode === 'search' ? state.results : state.listMode === 'session' ? state.sessionCaptures : state.recent;
        const showScore = state.listMode === 'search';

        if (!items || items.length === 0) {
            const msg = state.listMode === 'search'
                ? (state.lastSearch ? `No results for "${state.lastSearch.query}".` : 'Run a search to see results.')
                : state.listMode === 'session'
                    ? 'No captures found for this session. Enter a session ID above.'
                    : 'No recent captures found.';
            nodes.results.innerHTML = `<div class="vra-empty">${esc(msg)}</div>`;
            nodes.resultsSub.textContent = (state.listMode === 'search' && state.lastSearch) ? '0 hits' : '';
            return;
        }

        nodes.resultsSub.textContent = (state.listMode === 'search' && state.lastSearch)
            ? `${state.lastSearch.results.length} hit(s) in ${state.lastSearch.searchTimeMs}ms`
            : `${items.length} capture(s)`;

        nodes.results.innerHTML = items.map(item => {
            const scoreHtml = showScore && typeof item.score === 'number'
                ? `<span class="vra-score-chip ${scoreClass(item.score)}">${fmtScore(item.score)}</span>`
                : '';
            return `<button class="vra-item ${item.documentId === state.selectedId ? 'sel' : ''}" type="button" data-doc-id="${esc(item.documentId)}">
              <div class="vra-item-top">
                <div class="vra-item-chips">
                  <span class="vra-chip" style="font-size:10px">${esc(item.cli || '?')}</span>
                  <span class="vra-chip" style="font-size:10px">${esc(shortId(item.documentId))}</span>
                </div>
                ${scoreHtml}
              </div>
              <div class="vra-item-text">${esc(item.userTextPreview || '[no text]')}</div>
              <div class="vra-item-bottom">
                <span>${esc(fmtDate(item.timestampUTC))}</span>
                <span>${esc(item.fileChangeCount)} file(s)</span>
              </div>
            </button>`;
        }).join('');
    }

    renderDetail() {
        const { state, nodes } = this;
        if (!state.selected) {
            nodes.detailSub.textContent = '';
            nodes.detail.innerHTML = '<div class="vra-empty">Click a result to inspect it.</div>';
            return;
        }

        const i = state.selected;
        const hasFullDetail = !!i.rawText;
        nodes.detailSub.textContent = shortId(i.documentId);

        let scoreBanner = '';
        if (typeof i.score === 'number') {
            const pct = (i.score * 100).toFixed(1);
            const color = scoreColor(i.score);
            scoreBanner = `
              <div class="vra-score-banner">
                <div style="flex:1">
                  <div class="vra-score-label">Match Confidence</div>
                  <div class="vra-score-bar-track"><div class="vra-score-bar-fill" style="width:${pct}%;background:${color}"></div></div>
                </div>
                <div style="text-align:right">
                  <div class="vra-score-value" style="color:${color}">${pct}%</div>
                  <div class="vra-score-label">${i.score >= 0.5 ? 'Strong' : i.score >= 0.25 ? 'Moderate' : 'Weak'}</div>
                </div>
              </div>`;
        }

        const sessionActions = i.sessionId
            ? `<div class="vra-session-actions">
                <button class="vra-button" type="button" data-action="get-transcript" title="View the plain-text transcript for the session this capture came from"><i class="fa-solid fa-file-lines" style="margin-right:6px"></i>Get Transcript</button>
                <button class="vra-button" type="button" data-action="get-session" title="Replay the full terminal recording for the session this capture came from"><i class="fa-solid fa-play" style="margin-right:6px"></i>Get Session</button>
              </div>`
            : '';

        const userText = i.userText || i.userTextPreview || '';
        const textLabel = i.userText ? 'User Text (full)' : 'User Text (preview)';
        const textSection = userText
            ? `<div class="vra-section"><h3>${textLabel}</h3><pre class="vra-pre">${esc(userText)}</pre></div>`
            : '';

        const meta = [
            ['CLI', i.cli || 'Unknown'],
            ['Environment', i.environmentName || 'None'],
            ['Timestamp', fmtDate(i.timestampUTC)],
            ['Sequence', i.sequence ?? 'n/a'],
            ['Session', i.sessionId || 'n/a'],
            ['User Input ID', i.userInputId ?? 'n/a'],
            ['Git Commit', i.gitCommitHash || 'n/a'],
            ['Working Directory', i.workingDirectory || 'n/a'],
            ['File Changes', i.fileChangeCount ?? 0],
        ].map(([label, value]) =>
            `<div class="vra-meta-card"><div class="label">${esc(label)}</div><div class="value">${esc(String(value))}</div></div>`
        ).join('');

        let fileSection = '';
        if (i.fileChanges && i.fileChanges.length) {
            fileSection = `<div class="vra-section"><h3>File Changes</h3><div class="vra-ftable"><table>
              <thead><tr><th>File</th><th>Type</th><th>+</th><th>-</th></tr></thead>
              <tbody>${i.fileChanges.map(c => `<tr><td><code>${esc(c.filePath)}</code></td><td>${esc(c.changeType)}</td><td style="color:var(--vra-ok)">${c.linesAdded ?? '-'}</td><td style="color:var(--vra-bad)">${c.linesDeleted ?? '-'}</td></tr>`).join('')}</tbody>
            </table></div></div>`;
        } else if (hasFullDetail) {
            fileSection = `<div class="vra-section"><h3>File Changes</h3><div class="vra-empty" style="min-height:60px">No file changes recorded.</div></div>`;
        }

        const rawSection = hasFullDetail
            ? `<details class="vra-details"><summary>Raw capture payload (${i.rawText.length.toLocaleString()} chars)</summary><pre class="vra-pre">${esc(i.rawText)}</pre></details>`
            : '';

        const loadingHint = state.detailLoading
            ? '<div style="color:var(--vra-accent);font-size:11px">Loading full detail\u2026</div>'
            : '';

        nodes.detail.innerHTML = `<div class="vra-detail">
            ${scoreBanner}
            ${sessionActions}
            ${textSection}
            <div class="vra-section"><h3>Metadata</h3><div class="vra-meta-grid">${meta}</div></div>
            ${fileSection}
            ${rawSection}
            ${loadingHint}
          </div>`;
    }

    async loadStatus() {
        this.state.status = await this._fetchJson('/api/v1/bert/status');
        this.renderStatus();
    }

    async loadRecent() {
        const p = await this._fetchJson('/api/v1/bert/captures?take=50');
        this.state.recent = p.captures || [];
        if (this.state.listMode === 'recent') this.renderResultsList();
    }

    async loadCapture(id, source) {
        this.state.selectedId = id;
        const summary = this.state.results.find(r => r.documentId === id) || this.state.recent.find(r => r.documentId === id);
        if (summary) { this.state.selected = summary; this.state.detailLoading = true; }
        this.renderResultsList();
        this.renderDetail();

        try {
            const full = await this._fetchJson(`/api/v1/bert/captures/${encodeURIComponent(id)}`);
            if (summary && typeof summary.score === 'number') full.score = summary.score;
            this.state.selected = full;
        } catch (e) {
            if (!summary) this.setBanner(`Could not load detail: ${e.message}`, 'error');
        } finally {
            this.state.detailLoading = false;
        }
        this.renderResultsList();
        this.renderDetail();
    }

    async refreshAll() {
        this.setBusy(true);
        try {
            await this.loadStatus();
            await this.loadRecent();
            if (this.state.listMode === 'recent' && this.state.recent.length && !this.state.selectedId) {
                await this.loadCapture(this.state.recent[0].documentId, 'recent');
            }
            this.setBanner('Refreshed.', 'success');
        } catch (err) {
            this.setBanner(err.message, 'error');
        } finally {
            this.setBusy(false);
        }
    }

    async runSessionLookup() {
        const sid = this.nodes.sessionId.value.trim();
        if (!sid) { this.setSessionBanner('Enter a session ID.', 'error'); this.nodes.sessionId.focus(); return; }
        this.setBusy(true);
        this.nodes.sessionSearch.disabled = true;
        this.setSessionBanner('');
        this.state.sessionCaptures = [];
        this.state.selected = null;
        this.state.selectedId = null;
        this.state.listMode = 'session';
        this.nodes.resultsSource.value = 'session';
        this.renderResultsList();
        this.renderDetail();
        try {
            const p = await this._fetchJson(`/api/v1/bert/captures/by-session/${encodeURIComponent(sid)}`);
            this.state.sessionCaptures = p.captures || [];
            this.renderResultsList();
            if (this.state.sessionCaptures.length) {
                await this.loadCapture(this.state.sessionCaptures[0].documentId, 'session');
                this.setSessionBanner(`${this.state.sessionCaptures.length} capture(s) found.`, 'success');
            } else {
                this.setSessionBanner('No captures for this session.', 'error');
            }
        } catch (err) {
            this.state.sessionCaptures = [];
            this.renderResultsList();
            this.setSessionBanner(err.message, 'error');
        } finally {
            this.setBusy(false);
            this.nodes.sessionSearch.disabled = false;
        }
    }

    async runSearch() {
        const query = this.nodes.query.value.trim();
        if (!query) { this.setBanner('Enter a query first.', 'error'); this.nodes.query.focus(); return; }
        this.setBusy(true);
        this.state.results = [];
        this.state.selected = null;
        this.state.selectedId = null;
        this.state.listMode = 'search';
        this.nodes.resultsSource.value = 'search';
        this.state.lastSearch = { query, results: [], documentCount: this.state.status ? this.state.status.documentCount : 0, searchTimeMs: 0 };
        this.renderResultsList();
        this.renderDetail();
        try {
            const p = await this._fetchJson('/api/v1/bert/search', {
                method: 'POST',
                body: JSON.stringify({ query, mode: this.nodes.mode.value, topK: Number(this.nodes.topk.value) || 10 })
            });
            this.state.lastSearch = p;
            this.state.results = p.results || [];
            this.state.queryCount += 1;
            this.nodes.queries.textContent = String(this.state.queryCount);
            this.renderResultsList();
            if (this.state.results.length) await this.loadCapture(this.state.results[0].documentId, 'search');
            this.setBanner(`${p.results.length} hit(s) in ${p.searchTimeMs}ms`, 'success');
        } catch (err) {
            this.state.results = [];
            this.renderResultsList();
            this.setBanner(err.message, 'error');
        } finally {
            this.setBusy(false);
        }
    }
}
