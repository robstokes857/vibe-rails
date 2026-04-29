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
function fmtBytes(n) {
    if (!Number.isFinite(n) || n <= 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    let v = n;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 100 || i === 0 ? 0 : v >= 10 ? 1 : 2)} ${units[i]}`;
}

export class VibeRailsAiController {
    constructor(app) {
        this.app = app;
    }

    loadView() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('vibe-rails-ai-template');
        this.root = fragment.querySelector('[data-view="vibe-rails-ai"]');
        content.appendChild(fragment);
        if (!this.root) return;

        this.state = {
            status: null,
            recent: [],
            recentSessions: [],
            results: [],
            sessionCaptures: [],
            activeSessionId: null,
            lastSearch: null,
            selected: null,
            selectedId: null,
            queryCount: 0,
            detailLoading: false,
            listMode: 'sessions-recent'
        };

        const q = sel => this.root.querySelector(sel);
        this.nodes = {
            docs: q('[data-vra-docs]'),
            sessions: q('[data-vra-sessions]'),
            queries: q('[data-vra-queries]'),
            statusPills: q('[data-vra-status-pills]'),
            headerGrid: q('[data-vra-header-grid]'),
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
        this.nodes.search.addEventListener('click', () => this.runSearch());
        this.nodes.query.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); this.runSearch(); } });
        this.nodes.sessionSearch.addEventListener('click', () => this.runSessionLookup());
        this.nodes.sessionId.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); this.runSessionLookup(); } });
        this.nodes.refresh.addEventListener('click', () => this.refreshAll());
        this.nodes.results.addEventListener('click', e => {
            const sessionBtn = e.target.closest('[data-session-id]');
            if (sessionBtn) {
                this.loadCapturesForSession(sessionBtn.getAttribute('data-session-id'));
                return;
            }
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

        const baseUrl = window.__viberails_API_BASE__ || '';
        const res = await fetch(baseUrl + url, { credentials: 'include', cache: 'no-store', ...init, headers });
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
            `<span class="vra-pill ${s.databaseExists ? 'ok' : 'bad'}" title="Vector database">${s.databaseExists ? 'Vector DB OK' : 'Vector DB Missing'}</span>` +
            `<span class="vra-pill ${s.stateDatabaseExists ? 'ok' : 'warn'}" title="State database">${s.stateDatabaseExists ? 'State DB OK' : 'State DB Missing'}</span>` +
            `<span class="vra-pill ${s.modelAvailable ? 'ok' : 'bad'}" title="ONNX model + vocab">${s.modelAvailable ? 'Model OK' : 'Model Missing'}</span>` +
            `<span class="vra-pill ${s.semanticSearchAvailable ? 'ok' : 'warn'}" title="Semantic search">${s.semanticSearchAvailable ? 'Semantic Ready' : 'Semantic N/A'}</span>`;
        const semOpt = this.nodes.mode.querySelector('option[value="semantic"]');
        if (semOpt) semOpt.disabled = !s.semanticSearchAvailable;
        if (!s.semanticSearchAvailable && this.nodes.mode.value === 'semantic') this.nodes.mode.value = 'text';
        this._renderHeaderGrid(s);
    }

    _renderHeaderGrid(s) {
        if (!this.nodes.headerGrid) return;
        const cards = [
            ['Model', s.modelName || 'n/a'],
            ['Embedding Dim', s.embeddingDimension ? `${s.embeddingDimension}` : 'n/a'],
            ['Max Seq Length', s.maxSequenceLength ? `${s.maxSequenceLength} tokens` : 'n/a'],
            ['Vectors', s.vectorCount != null ? s.vectorCount.toLocaleString() : 'n/a'],
            ['Latest Capture', s.latestCaptureUTC ? fmtDate(s.latestCaptureUTC) : 'none'],
            ['Vector DB', `${fmtBytes(s.vectorDatabaseSizeBytes)}`, s.databasePath],
            ['State DB', `${fmtBytes(s.stateDatabaseSizeBytes)}`, s.stateDatabasePath],
            ['Model File', `${fmtBytes(s.modelFileSizeBytes)}`, s.modelPath],
            ['Vocab File', `${fmtBytes(s.vocabFileSizeBytes)}`, s.vocabPath],
            ['Data Dir', '', s.dataDirectory]
        ];
        this.nodes.headerGrid.innerHTML = cards.map(([label, value, path]) => {
            const pathHtml = path ? `<code style="font-size:10px;color:var(--color-accent);word-break:break-all;display:block;margin-top:0.2rem;line-height:1.3">${esc(path)}</code>` : '';
            const valueHtml = value ? `<div class="value">${esc(value)}</div>` : '';
            return `<div class="vra-meta-card" title="${esc(path || '')}"><div class="label">${esc(label)}</div>${valueHtml}${pathHtml}</div>`;
        }).join('');
    }

    renderResultsList() {
        const { state, nodes } = this;

        if (state.listMode === 'sessions-recent') {
            this._renderSessionsList();
            return;
        }

        const items = state.listMode === 'search' ? state.results : state.listMode === 'session' ? state.sessionCaptures : state.recent;
        const showScore = state.listMode === 'search';

        if (!items || items.length === 0) {
            const msg = state.listMode === 'search'
                ? (state.lastSearch ? `No results for "${state.lastSearch.query}".` : 'Run a search to see results.')
                : state.listMode === 'session'
                    ? (state.activeSessionId ? `No captures found for session ${shortId(state.activeSessionId)}.` : 'Click a session from Recent Sessions, or enter an ID above.')
                    : 'No recent captures found.';
            nodes.results.innerHTML = `<div class="vra-empty">${esc(msg)}</div>`;
            nodes.resultsSub.textContent = (state.listMode === 'search' && state.lastSearch) ? '0 hits' : '';
            return;
        }

        let subText;
        if (state.listMode === 'search' && state.lastSearch) {
            subText = `${state.lastSearch.results.length} hit(s) in ${state.lastSearch.searchTimeMs}ms`;
        } else if (state.listMode === 'session' && state.activeSessionId) {
            subText = `${items.length} capture(s) in ${shortId(state.activeSessionId)}`;
        } else {
            subText = `${items.length} capture(s)`;
        }
        nodes.resultsSub.textContent = subText;

        nodes.results.innerHTML = items.map(item => {
            const scoreHtml = showScore && typeof item.score === 'number'
                ? `<span class="vra-score-chip ${scoreClass(item.score)}">${fmtScore(item.score)}</span>`
                : '';
            return `<button class="vra-item ${item.documentId === state.selectedId ? 'sel' : ''}" type="button" data-doc-id="${esc(item.documentId)}">
              <div class="vra-item-top">
                <div class="vra-item-chips">
                  <span class="vra-chip" style="font-size:10px">${esc(item.cli || '?')}</span>
                  <span class="vra-chip" style="font-size:10px">#${esc(item.sequence ?? '?')}</span>
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

    _renderSessionsList() {
        const { state, nodes } = this;
        const sessions = state.recentSessions;

        if (!sessions || sessions.length === 0) {
            nodes.results.innerHTML = `<div class="vra-empty">${esc('No recent sessions found.')}</div>`;
            nodes.resultsSub.textContent = '';
            return;
        }

        nodes.resultsSub.textContent = `${sessions.length} session(s)`;
        nodes.results.innerHTML = sessions.map(s => `
            <button class="vra-item ${s.sessionId === state.activeSessionId ? 'sel' : ''}" type="button" data-session-id="${esc(s.sessionId)}">
              <div class="vra-item-top">
                <div class="vra-item-chips">
                  <span class="vra-chip" style="font-size:10px">${esc(s.cli || '?')}</span>
                  <span class="vra-chip" style="font-size:10px">${esc(shortId(s.sessionId))}</span>
                </div>
                <span class="vra-score-chip" style="color:var(--color-accent)">${s.captureCount}&times;</span>
              </div>
              <div class="vra-item-text">${esc(s.latestPreview || '[no text]')}</div>
              <div class="vra-item-bottom">
                <span>${esc(fmtDate(s.latestTimestampUTC))}</span>
                <span>${esc(s.workingDirectory || '')}</span>
              </div>
            </button>`).join('');
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
        const p = await this._fetchJson('/api/v1/bert/captures?take=200');
        this.state.recent = p.captures || [];
        this.state.recentSessions = this._groupBySession(this.state.recent);
        if (this.state.listMode === 'recent' || this.state.listMode === 'sessions-recent') {
            this.renderResultsList();
        }
    }

    _groupBySession(captures) {
        const map = new Map();
        for (const c of captures) {
            if (!c.sessionId) continue;
            const existing = map.get(c.sessionId);
            if (!existing) {
                map.set(c.sessionId, {
                    sessionId: c.sessionId,
                    cli: c.cli,
                    workingDirectory: c.workingDirectory,
                    captureCount: 1,
                    latestTimestampUTC: c.timestampUTC,
                    latestPreview: c.userTextPreview
                });
            } else {
                existing.captureCount += 1;
                if (c.timestampUTC && (!existing.latestTimestampUTC || new Date(c.timestampUTC) > new Date(existing.latestTimestampUTC))) {
                    existing.latestTimestampUTC = c.timestampUTC;
                    existing.latestPreview = c.userTextPreview;
                    existing.cli = c.cli || existing.cli;
                    existing.workingDirectory = c.workingDirectory || existing.workingDirectory;
                }
            }
        }
        return Array.from(map.values()).sort((a, b) => {
            const ta = a.latestTimestampUTC ? new Date(a.latestTimestampUTC).getTime() : 0;
            const tb = b.latestTimestampUTC ? new Date(b.latestTimestampUTC).getTime() : 0;
            return tb - ta;
        });
    }

    async loadCapturesForSession(sessionId) {
        if (!sessionId) return;
        this.setBusy(true);
        this.setSessionBanner('');
        this.state.sessionCaptures = [];
        this.state.activeSessionId = sessionId;
        this.state.selected = null;
        this.state.selectedId = null;
        this.state.listMode = 'session';
        this.nodes.resultsSource.value = 'session';
        this.nodes.sessionId.value = sessionId;
        this.renderResultsList();
        this.renderDetail();
        try {
            const p = await this._fetchJson(`/api/v1/bert/captures/by-session/${encodeURIComponent(sessionId)}`);
            this.state.sessionCaptures = p.captures || [];
            this.renderResultsList();
            if (this.state.sessionCaptures.length) {
                await this.loadCapture(this.state.sessionCaptures[0].documentId, 'session');
                this.setSessionBanner(`${this.state.sessionCaptures.length} capture(s) in session ${shortId(sessionId)}.`, 'success');
            } else {
                this.setSessionBanner('No captures for this session.', 'error');
            }
        } catch (err) {
            this.state.sessionCaptures = [];
            this.renderResultsList();
            this.setSessionBanner(err.message, 'error');
        } finally {
            this.setBusy(false);
        }
    }

    async loadCapture(id, source) {
        this.state.selectedId = id;
        const summary = this.state.results.find(r => r.documentId === id)
            || this.state.sessionCaptures.find(r => r.documentId === id)
            || this.state.recent.find(r => r.documentId === id);
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
        this.nodes.sessionSearch.disabled = true;
        try {
            await this.loadCapturesForSession(sid);
        } finally {
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
