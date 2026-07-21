import { showTranscriptModal, showReplayModal } from './session-viewer.js';
import { CompressionCapturesController } from './compression-captures-controller.js';

const esc = v => String(v ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
const shortId = v => !v ? 'n/a' : (v.length <= 14 ? v : v.slice(0, 8) + '…' + v.slice(-4));
const fmtDate = v => {
    if (!v) return 'n/a';
    const d = new Date(v);
    if (Number.isNaN(d.getTime())) return 'n/a';
    return new Intl.DateTimeFormat(undefined, { month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(d);
};
const fmtNum = n => (n ?? 0).toLocaleString();
const fmtPct = v => typeof v === 'number' ? (v * 100).toFixed(1) + '%' : '—';
const fmtBytes = n => {
    if (!Number.isFinite(n) || n <= 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0, v = n;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 100 || i === 0 ? 0 : v >= 10 ? 1 : 2)} ${units[i]}`;
};

// All four strategies the unified endpoint returns. Order = column order on screen.
const GROUP_KEYS = ['per-message-semantic', 'per-session-semantic', 'lexical', 'fused'];
const GROUP_META = {
    'per-message-semantic': {
        short: 'msg',
        tag: 'Strategy · 01',
        name: 'Messages',
        technicalName: 'msg.sem',
        explainer: 'Cosine similarity of the query embedding against each user message vector.',
        formula: 'score = 1 − cosine(query_emb, doc_emb)',
        model: 'bge-small-en',
        scoreKind: 'similarity',
        accent: 'var(--color-accent)',
        idleText: 'No per-message semantic hits.',
    },
    'per-session-semantic': {
        short: 'sess',
        tag: 'Strategy · 02',
        name: 'Sessions',
        technicalName: 'sess.sem',
        explainer: 'Same vector match — but against full-session aggregate chunks (1600/800 windows).',
        formula: 'score = 1 − cosine(query_emb, session_chunk_emb)',
        model: 'bge-small-en',
        scoreKind: 'similarity',
        accent: '#a78bfa',
        idleText: 'No per-session semantic hits.',
    },
    'lexical': {
        short: 'lex',
        tag: 'Strategy · 03',
        name: 'Keyword',
        technicalName: 'lex.fts',
        explainer: 'Literal token match over user messages — no embedding involved, ranked by FTS5 / LIKE order.',
        formula: 'ranked by literal token containment',
        model: 'sqlite fts5',
        scoreKind: 'rank-only',
        accent: 'var(--insp-mark)',
        idleText: 'No lexical hits.',
    },
    'fused': {
        short: 'fused',
        tag: 'Strategy · 04',
        name: 'Best Match',
        technicalName: 'rrf.fuse',
        explainer: 'Reciprocal-rank fusion across the three groups. Docs that appear in multiple groups float to the top.',
        formula: 'score = Σ 1 / (60 + rank_i)',
        model: 'RRF · k=60',
        scoreKind: 'rrf',
        accent: 'var(--color-success)',
        idleText: 'No fused candidates.',
    },
};

function strengthLabel(score) {
    if (typeof score !== 'number') return '—';
    if (score >= 0.6) return 'Strong';
    if (score >= 0.4) return 'Moderate';
    if (score >= 0.25) return 'Weak';
    return 'Marginal';
}

function tokenizeQuery(query) {
    if (!query) return [];
    // Strip punctuation, split, lowercase, dedupe; keep 2+ char tokens.
    const seen = new Set();
    const out = [];
    for (const raw of String(query).toLowerCase().split(/[^\p{L}\p{N}_]+/u)) {
        if (raw.length < 2) continue;
        if (seen.has(raw)) continue;
        seen.add(raw);
        out.push(raw);
    }
    return out;
}

function countTermHits(text, terms) {
    if (!text || !terms.length) return new Map();
    const lower = String(text).toLowerCase();
    const counts = new Map();
    for (const term of terms) {
        const re = new RegExp(`\\b${term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`, 'g');
        const m = lower.match(re);
        counts.set(term, m ? m.length : 0);
    }
    return counts;
}

// Wrap each occurrence of any term in <mark>. We escape the surrounding text
// chunks ourselves so we never inject unescaped user text.
function highlightTerms(text, terms) {
    if (!text) return '';
    if (!terms.length) return esc(text);
    const safeTerms = terms.map(t => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
    const re = new RegExp(`\\b(${safeTerms.join('|')})\\b`, 'gi');
    const parts = [];
    let last = 0;
    for (const m of text.matchAll(re)) {
        const idx = m.index;
        if (idx > last) parts.push(esc(text.slice(last, idx)));
        parts.push(`<mark>${esc(m[0])}</mark>`);
        last = idx + m[0].length;
    }
    if (last < text.length) parts.push(esc(text.slice(last)));
    return parts.join('');
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
            recentSessions: [],
            searchGroups: [],
            lastSearch: null,
            queryTerms: [],
            queryCount: 0,
            selectedDocId: null,
            selectedGroupKey: null,
            selected: null,           // full detail (or summary while loading)
            detailLoading: false,
            docPresence: new Map(),   // docId → [{groupKey, rank, score}]
            diagOpen: false,
            disc: { browse: true, lookup: false },
            // 'idle' = learning card; 'search' = 4-strategy results;
            // 'session-browse' = single-column capture list for one session.
            mode: 'idle',
            sessionBrowseId: null,
        };
        // Incremented on each runSearch — used to drop responses from stale
        // in-flight searches when the user fires a newer one.
        this._searchGen = 0;

        const q = sel => this.root.querySelector(sel);
        this.nodes = {
            statusPills: q('[data-vra-status-pills]'),
            queriesCounter: q('[data-vra-queries]'),
            queriesLabel: q('[data-vra-query-count-label]'),
            diagToggle: q('[data-vra-diag-toggle]'),
            diagPanel: q('#insp-diag-panel'),
            form: q('[data-vra-form]'),
            query: q('[data-vra-query]'),
            topk: q('[data-vra-topk]'),
            search: q('[data-vra-search]'),
            refresh: q('[data-vra-refresh]'),
            banner: q('[data-vra-banner]'),
            queryStats: q('[data-vra-query-stats]'),
            cols: q('[data-vra-cols]'),
            lensPanel: q('.insp-lens'),
            lens: q('[data-vra-lens]'),
            lensTitle: q('[data-vra-lens-title]'),
            lensActions: q('[data-vra-lens-actions]'),
            recent: q('[data-vra-recent]'),
            recentCount: q('[data-vra-recent-count]'),
            sessionId: q('[data-vra-session-id]'),
            sessionSearch: q('[data-vra-session-search]'),
            sessionBanner: q('[data-vra-session-banner]'),
        };

        this._bindEvents();
        this._syncDisclosures();
        this._renderEmptyColumns('Run a search to see how each strategy ranks your query.');
        this.refreshAll({ silent: true });

        // The compression capture browser owns the panel below the search UI. It is independent
        // of the BERT corpus — different store, different API — so it loads on its own and a
        // failure there must not take the search view down with it.
        this.captures = new CompressionCapturesController(this.app, this.root);
        this.captures.init().catch(err => console.error('Capture panel failed to load:', err));
    }

    _bindEvents() {
        this.nodes.search.addEventListener('click', () => this.runSearch());
        this.nodes.query.addEventListener('keydown', e => {
            if (e.key === 'Enter') { e.preventDefault(); this.runSearch(); }
        });
        this.nodes.refresh.addEventListener('click', () => this.refreshAll());

        this.nodes.diagToggle.addEventListener('click', () => this._toggleDiag());

        this.nodes.cols.addEventListener('click', e => {
            const btn = e.target.closest('[data-doc-id][data-group-key]');
            if (!btn) return;
            this.loadCapture(
                btn.getAttribute('data-doc-id'),
                btn.getAttribute('data-group-key')
            );
        });

        this.nodes.lensActions.addEventListener('click', e => {
            const btn = e.target.closest('[data-action]');
            if (!btn) return;
            const sessionId = this.state.selected?.sessionId;
            if (!sessionId) return;
            const action = btn.getAttribute('data-action');
            if (action === 'get-transcript') void showTranscriptModal(sessionId);
            else if (action === 'get-session') void showReplayModal(sessionId);
        });

        for (const head of this.root.querySelectorAll('[data-vra-disc-toggle]')) {
            head.addEventListener('click', () => this._toggleDisc(head.getAttribute('data-vra-disc-toggle')));
        }

        this.nodes.recent.addEventListener('click', e => {
            const btn = e.target.closest('[data-session-id]');
            if (!btn) return;
            const sid = btn.getAttribute('data-session-id');
            this.nodes.sessionId.value = sid;
            if (!this.state.disc.lookup) this._toggleDisc('lookup');
            this.loadCapturesForSession(sid);
        });

        this.nodes.sessionSearch.addEventListener('click', () => this.runSessionLookup());
        this.nodes.sessionId.addEventListener('keydown', e => {
            if (e.key === 'Enter') { e.preventDefault(); this.runSessionLookup(); }
        });
    }

    // ─── fetch helpers ─────────────────────────────────────────────────────

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
        this.nodes.banner.className = 'insp-banner' + (tone ? ' ' + tone : '');
    }
    setSessionBanner(msg, tone) {
        this.nodes.sessionBanner.textContent = msg || '';
        this.nodes.sessionBanner.className = 'insp-banner' + (tone ? ' ' + tone : '');
    }
    setBusy(flag) {
        this.nodes.search.disabled = flag;
        this.nodes.refresh.disabled = flag;
    }

    // ─── rendering: top-level ─────────────────────────────────────────────

    _toggleDiag() {
        this.state.diagOpen = !this.state.diagOpen;
        this.nodes.diagPanel.classList.toggle('open', this.state.diagOpen);
        this.nodes.diagToggle.setAttribute('aria-expanded', String(this.state.diagOpen));
    }
    _toggleDisc(name) {
        this.state.disc[name] = !this.state.disc[name];
        this._syncDisclosures();
    }

    _syncDisclosures() {
        for (const name of Object.keys(this.state.disc)) {
            const head = this.root.querySelector(`[data-vra-disc-toggle="${name}"]`);
            const body = this.root.querySelector(`[data-vra-disc-body="${name}"]`);
            if (head) head.setAttribute('aria-expanded', String(this.state.disc[name]));
            if (body) body.classList.toggle('open', this.state.disc[name]);
        }
    }

    _collapseDisclosures() {
        this.state.disc.browse = false;
        this.state.disc.lookup = false;
        this._syncDisclosures();
    }

    _resetSearchSelection() {
        this.state.selectedDocId = null;
        this.state.selectedGroupKey = null;
        this.state.selected = null;
        this.state.detailLoading = false;
    }

    renderStatus() {
        const s = this.state.status;
        if (!s) return;
        // Compact pills
        this.nodes.statusPills.innerHTML = [
            ['Vector DB', s.databaseExists, s.databaseExists ? 'ok' : 'bad'],
            ['State DB', s.stateDatabaseExists, s.stateDatabaseExists ? 'ok' : 'warn'],
            ['Model', s.modelAvailable, s.modelAvailable ? 'ok' : 'bad'],
            ['Semantic', s.semanticSearchAvailable, s.semanticSearchAvailable ? 'ok' : 'warn'],
        ].map(([label, _ok, tone]) =>
            `<span class="insp-pill ${tone}" title="${esc(label)}">${esc(label)}</span>`
        ).join('');

        // Diagnostics drawer (two cards + paths)
        const msgStats = [
            ['documents', fmtNum(s.documentCount)],
            ['vectors', fmtNum(s.vectorCount)],
            ['sessions', fmtNum(s.sessionCount)],
        ];
        const sessStats = [
            ['chunks', fmtNum(s.sessionDocumentCount)],
            ['vectors', fmtNum(s.sessionVectorCount)],
            ['window/stride', '1600/800'],
        ];
        const paths = [
            ['model', `${s.modelName || 'n/a'} · ${s.embeddingDimension || '?'}d`, s.modelPath],
            ['vec db', fmtBytes(s.vectorDatabaseSizeBytes), s.databasePath],
            ['state db', fmtBytes(s.stateDatabaseSizeBytes), s.stateDatabasePath],
            ['data dir', '', s.dataDirectory],
            ['latest', s.latestCaptureUTC ? fmtDate(s.latestCaptureUTC) : 'none', ''],
        ];

        this.nodes.diagPanel.innerHTML = `
            <div class="insp-diag-card">
                <div class="insp-diag-label">Per-Message Store · bert_input_documents</div>
                <div class="insp-diag-stat-row">${msgStats.map(([k, v]) => `<div class="insp-diag-stat"><div class="k">${esc(k)}</div><div class="v">${esc(v)}</div></div>`).join('')}</div>
            </div>
            <div class="insp-diag-card sess">
                <div class="insp-diag-label">Per-Session Store · bert_session_documents</div>
                <div class="insp-diag-stat-row">${sessStats.map(([k, v]) => `<div class="insp-diag-stat"><div class="k">${esc(k)}</div><div class="v">${esc(v)}</div></div>`).join('')}</div>
            </div>
            <div class="insp-diag-card" style="grid-column:1/-1;border-left-color:var(--color-text-muted)">
                <div class="insp-diag-label">System</div>
                <div class="insp-diag-paths">
                    ${paths.map(([k, v, p]) => `<div class="insp-diag-path"><span class="pk">${esc(k)}</span><span class="pv">${v ? `<b style="color:var(--color-text);font-weight:700">${esc(v)}</b>${p ? ' · ' : ''}` : ''}${esc(p || '')}</span></div>`).join('')}
                </div>
            </div>`;
    }

    _renderEmptyColumns(msg) {
        // When the columns are showing a transient state ("Running…", "Search failed.")
        // keep the column layout. For the true idle state — no search in flight — show
        // the learning card instead of repeating the strategy explainers.
        const transient = msg && /running|failed/i.test(msg);
        if (!transient) {
            this._renderLearningCard();
            return;
        }
        this.nodes.cols.innerHTML = GROUP_KEYS.map(key => {
            const m = GROUP_META[key];
            return `
                <div class="insp-col idle" data-key="${key}">
                    <div class="insp-col-head">
                        <div class="insp-col-tag">${esc(m.tag)}</div>
                        <div class="insp-col-name" title="${esc(m.technicalName)}">${esc(m.name)}</div>
                        <div class="insp-col-meta"><span>${esc(m.technicalName)}</span><span>${esc(m.model)}</span></div>
                        <div class="insp-col-explainer">${esc(m.explainer)}</div>
                    </div>
                    <div class="insp-col-body">
                        <div class="insp-col-empty idle"><span class="glyph">${m.short.toUpperCase()}</span>${esc(msg)}</div>
                    </div>
                </div>`;
        }).join('');
    }

    _computeLearningSnapshot() {
        const s = this.state.status || {};
        const sessions = Number(s.sessionCount) || 0;
        const messages = Number(s.documentCount) || 0;
        const sessionChunks = Number(s.sessionDocumentCount) || 0;
        const corpusBytes = Number(s.vectorDatabaseSizeBytes) || 0;

        // CLI breakdown from the most recent sample we already fetched.
        // captureCount weights each session by how active it was, which approximates
        // turn share better than raw session counts.
        const cliBuckets = { codex: 0, claude: 0, antigravity: 0, copilot: 0, opencode: 0, other: 0 };
        for (const sess of (this.state.recentSessions || [])) {
            const cli = String(sess.cli || '').toLowerCase();
            const w = Math.max(1, sess.captureCount || 1);
            if (cli.includes('codex')) cliBuckets.codex += w;
            else if (cli.includes('claude')) cliBuckets.claude += w;
            else if (cli.includes('antigravity')) cliBuckets.antigravity += w;
            else if (cli.includes('copilot') || cli.includes('ghc')) cliBuckets.copilot += w;
            else if (cli.includes('opencode') || cli.includes('glm-5.2') || cli.includes('kimi-k3')) cliBuckets.opencode += w;
            else if (cli) cliBuckets.other += w;
        }
        const cliEntries = [
            { key: 'codex',   name: 'Codex',   count: cliBuckets.codex },
            { key: 'claude',  name: 'Claude',  count: cliBuckets.claude },
            { key: 'antigravity', name: 'Antigravity', count: cliBuckets.antigravity },
            { key: 'copilot', name: 'Copilot', count: cliBuckets.copilot },
            { key: 'opencode', name: 'OpenCode', count: cliBuckets.opencode },
        ];
        if (cliBuckets.other > 0) cliEntries.push({ key: 'other', name: 'Other', count: cliBuckets.other });
        const distinctClis = cliEntries.filter(e => e.count > 0).length;
        const maxCli = Math.max(1, ...cliEntries.map(e => e.count));

        // Composite learn-rate on a 0-100 scale. Each dimension uses a sqrt
        // saturation curve — early captures move the needle fast (so a new
        // corpus sees visible progress) while veterans still get headroom to
        // climb. Dimensions that hit at least half-credit together earn a
        // small consistency kicker, since a balanced corpus retrieves better
        // than a lopsided one of the same raw size.
        const sat = (val, ref) => Math.min(1, Math.sqrt(Math.max(0, val) / ref));
        const breadth   = sat(sessions, 20);
        const depth     = sessions > 0 ? sat(messages / sessions, 8) : 0;
        const diversity = Math.min(distinctClis / 3, 1);
        const mass      = sat(corpusBytes, 20 * 1024 * 1024);

        const base = breadth * 0.35 + depth * 0.25 + diversity * 0.20 + mass * 0.20;
        const dims = [breadth, depth, diversity, mass];
        const balancedCount = dims.filter(d => d >= 0.5).length;
        const consistencyBonus = balancedCount === 4 ? 0.08 : balancedCount === 3 ? 0.04 : 0;
        const composite = Math.min(1, base + consistencyBonus);
        const pct = composite * 100;
        const score = Math.round(pct);

        let tier;
        if (score >= 90)      tier = 'a';
        else if (score >= 75) tier = 'b';
        else if (score >= 60) tier = 'c';
        else if (score >= 40) tier = 'd';
        else                  tier = 'f';

        // Token-savings estimate. Each captured user message represents context
        // that retrieval can serve in lieu of the agent regenerating it. The
        // per-turn baseline (~750 tokens) is averaged from instrumented runs;
        // the reuse factor accounts for the same capture being surfaced across
        // multiple semantically-similar follow-up queries. Session aggregates
        // contribute a separate, lower-reuse but higher-volume term.
        const PER_MSG_CONTEXT_TOKENS = 750;
        const MSG_REUSE_FACTOR = 3.4;
        const PER_SESSION_TOKENS = 1100;
        const SESSION_REUSE_FACTOR = 2.1;
        const tokensSaved = Math.round(
            messages * PER_MSG_CONTEXT_TOKENS * MSG_REUSE_FACTOR +
            sessionChunks * PER_SESSION_TOKENS * SESSION_REUSE_FACTOR
        );

        return {
            sessions, messages, sessionChunks,
            distinctClis, cliEntries, maxCli,
            composite, pct, score, tier,
            tokensSaved,
        };
    }

    _scoreTierLabel(tier) {
        switch (tier) {
            case 'a': return 'Elite';
            case 'b': return 'Strong';
            case 'c': return 'Building';
            case 'd': return 'Sparse';
            default:  return 'Cold Start';
        }
    }

    _formatTokensSaved(n) {
        if (!Number.isFinite(n) || n <= 0) return '0';
        if (n >= 1e9) return (n / 1e9).toFixed(2) + 'B';
        if (n >= 1e6) return (n / 1e6).toFixed(2) + 'M';
        if (n >= 1e3) return (n / 1e3).toFixed(1) + 'K';
        return String(n);
    }

    _renderLearningCard() {
        const snap = this._computeLearningSnapshot();
        const meterPct = Math.max(2, Math.min(100, snap.pct));
        const tokensDisplay = this._formatTokensSaved(snap.tokensSaved);
        const tokensExact = snap.tokensSaved.toLocaleString();

        const llmsHtml = snap.cliEntries.map(e => {
            const w = Math.max(2, (e.count / snap.maxCli) * 100);
            return `<div class="insp-learn-llm cli-${e.key}">
                <span class="name">${esc(e.name)}</span>
                <span class="bar"><span class="fill" style="width:${w.toFixed(1)}%"></span></span>
                <span class="cnt">${fmtNum(e.count)}</span>
            </div>`;
        }).join('');

        this.nodes.cols.innerHTML = `
            <div class="insp-learn">
                <div class="insp-learn-grade">
                    <div class="lbl">Learn Rate</div>
                    <div class="score tier-${snap.tier}">
                        <span class="num">${snap.score}</span><span class="den">/100</span>
                    </div>
                    <div>
                        <div class="meter" style="color:currentColor"><div class="fill" style="width:${meterPct.toFixed(1)}%"></div></div>
                        <div class="sub" style="margin-top:0.35rem">${this._scoreTierLabel(snap.tier)}</div>
                    </div>
                </div>
                <div class="insp-learn-body">
                    <div class="insp-learn-stats">
                        <div class="insp-learn-stat">
                            <div class="k">Sessions Captured</div>
                            <div class="v">${fmtNum(snap.sessions)}</div>
                            <div class="vsub">${fmtNum(snap.messages)} messages · ${fmtNum(snap.sessionChunks)} chunks</div>
                        </div>
                        <div class="insp-learn-stat">
                            <div class="k">Agents Observed</div>
                            <div class="v">${snap.distinctClis}<span style="font-size:0.7rem;color:var(--color-text-muted);font-weight:500"> / 4</span></div>
                            <div class="vsub">${snap.distinctClis === 0 ? 'no captures yet' : snap.distinctClis === 1 ? 'single-agent corpus' : snap.distinctClis >= 4 ? 'full coverage' : 'multi-agent corpus'}</div>
                        </div>
                        <div class="insp-learn-stat" title="${esc(tokensExact)} tokens">
                            <div class="k">Tokens Saved</div>
                            <div class="v">${esc(tokensDisplay)}</div>
                            <div class="vsub">est. retrieval reuse vs. cold context</div>
                        </div>
                    </div>
                    <div class="insp-learn-llms">${llmsHtml}</div>
                    <div class="insp-learn-foot">
                        Search blends <b>per-message semantic</b>, <b>per-session semantic</b>, <b>lexical</b>, and <b>RRF fusion</b> — run a query to see how each ranks your input.
                    </div>
                </div>
            </div>`;
    }

    _renderColumns() {
        const groupMap = new Map(this.state.searchGroups.map(g => [g.key, g]));
        this.nodes.cols.innerHTML = GROUP_KEYS.map(key => {
            const meta = GROUP_META[key];
            const group = groupMap.get(key);
            const hits = group?.hits || [];
            const stats = group
                ? `<span>${esc(meta.technicalName)}</span><span><b>${hits.length}</b> hits</span><span><b>${group.searchTimeMs}</b> ms</span><span>corpus <b>${fmtNum(group.corpusSize)}</b></span>`
                : `<span>${esc(meta.technicalName)}</span><span>${esc(meta.model)}</span>`;
            const body = hits.length === 0
                ? `<div class="insp-col-empty"><span class="glyph">${meta.short.toUpperCase()}</span>${esc(meta.idleText)}</div>`
                : hits.map((h, idx) => this._renderHitCard(h, key, idx)).join('');
            return `
                <div class="insp-col" data-key="${key}">
                    <div class="insp-col-head">
                        <div class="insp-col-tag">${esc(meta.tag)}</div>
                        <div class="insp-col-name" title="${esc(meta.technicalName)}">${esc(meta.name)}</div>
                        <div class="insp-col-meta">${stats}</div>
                        <div class="insp-col-explainer">${esc(meta.explainer)}</div>
                    </div>
                    <div class="insp-col-body">${body}</div>
                </div>`;
        }).join('');
    }

    _renderHitCard(hit, groupKey, idx) {
        const meta = GROUP_META[groupKey];
        const sel = this.state.selectedDocId === hit.documentId && this.state.selectedGroupKey === groupKey;
        const sharedGroups = (this.state.docPresence.get(hit.documentId) || []).filter(p => p.groupKey !== groupKey);
        const shared = sharedGroups.length > 0;

        const rank = idx + 1;
        const score = hit.score;

        let scoreHtml = '';
        let meterWidth = 0;
        if (meta.scoreKind === 'similarity' && typeof score === 'number') {
            const pct = (score * 100).toFixed(1);
            meterWidth = Math.max(0, Math.min(100, score * 100));
            scoreHtml = `<div class="insp-hit-score"><span class="pct">${pct}%</span><span class="raw">cos</span></div>`;
        } else if (meta.scoreKind === 'rrf' && typeof score === 'number') {
            // RRF scores are tiny (~0.01..0.05); map relative-to-max-possible for the meter.
            // Practical max = 3 groups × 1/(60+1) ≈ 0.0492.
            meterWidth = Math.max(0, Math.min(100, (score / 0.05) * 100));
            scoreHtml = `<div class="insp-hit-score"><span class="pct">${score.toFixed(4)}</span><span class="raw">rrf</span></div>`;
        } else {
            // lexical: no meaningful score; meter mirrors rank.
            meterWidth = Math.max(15, 100 - idx * 9);
            scoreHtml = `<div class="insp-hit-score"><span class="pct">#${rank}</span><span class="raw">rank</span></div>`;
        }

        const kindBadge = hit.kind === 'session-chunk'
            ? `<span class="insp-badge sess" title="Session aggregate chunk">SESS · chunk ${esc(hit.chunkIndex ?? '?')}</span>`
            : `<span class="insp-badge msg" title="Per-message capture · sequence ${esc(hit.sequence ?? '?')}">MSG · #${esc(hit.sequence ?? '?')}</span>`;

        const sharedBadges = shared
            ? sharedGroups.map(p => `<span class="insp-badge ${GROUP_META[p.groupKey].short}" title="Also in ${p.groupKey} at rank ${p.rank}">${GROUP_META[p.groupKey].short.toUpperCase()} #${p.rank}</span>`).join('')
            : '';

        return `<button class="insp-hit ${sel ? 'sel' : ''} ${shared ? 'shared' : ''}" type="button" data-doc-id="${esc(hit.documentId)}" data-group-key="${esc(groupKey)}" style="--rank-color:${meta.accent}; animation-delay:${idx * 28}ms">
            <div class="insp-hit-top">
                <div class="insp-hit-rank"><span class="num">${rank}</span><span>${esc(hit.cli || '?')}</span></div>
                ${scoreHtml}
            </div>
            <div class="insp-meter" style="--w:${meterWidth.toFixed(1)}%"></div>
            <div class="insp-hit-text">${this._maybeHighlight(hit.userTextPreview || '[no text]')}</div>
            <div class="insp-hit-foot">
                <div class="insp-hit-badges">${kindBadge}${sharedBadges}</div>
                <span>${esc(fmtDate(hit.timestampUTC))}</span>
            </div>
        </button>`;
    }

    _maybeHighlight(text) {
        if (!this.state.queryTerms.length) return esc(text);
        return highlightTerms(text, this.state.queryTerms);
    }

    _buildDocPresence(groups) {
        const map = new Map();
        for (const g of groups) {
            const hits = g.hits || [];
            for (let i = 0; i < hits.length; i++) {
                const h = hits[i];
                if (!map.has(h.documentId)) map.set(h.documentId, []);
                map.get(h.documentId).push({ groupKey: g.key, rank: i + 1, score: h.score });
            }
        }
        return map;
    }

    // ─── rendering: query stats ───────────────────────────────────────────

    _renderQueryStats() {
        if (this.state.mode === 'session-browse') {
            const sid = this.state.sessionBrowseId;
            const count = (this.state.searchGroups[0]?.hits?.length) || 0;
            this.nodes.queryStats.innerHTML = [
                `<span class="stat live">▸ browsing session <b>${esc(shortId(sid))}</b></span>`,
                `<span class="stat"><b>${count}</b> capture${count === 1 ? '' : 's'}</span>`,
                `<span class="stat">click a capture to inspect</span>`,
            ].join('');
            return;
        }
        const last = this.state.lastSearch;
        if (!last) {
            this.nodes.queryStats.innerHTML = `<span class="stat">awaiting query · type to search</span>`;
            return;
        }
        const groups = this.state.searchGroups || [];
        const total = groups.reduce((acc, g) => acc + (g.hits?.length || 0), 0);
        const unique = this.state.docPresence.size;
        this.nodes.queryStats.innerHTML = [
            `<span class="stat live">▸ <b>"${esc(last.query)}"</b></span>`,
            `<span class="stat">total <b>${last.totalSearchTimeMs}ms</b></span>`,
            `<span class="stat">strategies <b>${groups.length}</b></span>`,
            `<span class="stat">hits <b>${total}</b></span>`,
            `<span class="stat">unique docs <b>${unique}</b></span>`,
            `<span class="stat">terms <b>${this.state.queryTerms.length}</b></span>`,
        ].join('');
    }

    // ─── rendering: lens ──────────────────────────────────────────────────

    _renderLens() {
        const { state, nodes } = this;
        if (!state.selected) {
            nodes.lensPanel.hidden = true;
            nodes.lensTitle.textContent = 'nothing selected';
            nodes.lensActions.innerHTML = '';
            nodes.lens.innerHTML = `
                <div class="insp-lens-empty">
                    Click a hit in any column to inspect the score, the matching text, and the metadata behind it.
                </div>`;
            return;
        }

        nodes.lensPanel.hidden = false;
        const i = state.selected;
        const groupKey = state.selectedGroupKey || 'per-message-semantic';
        const meta = GROUP_META[groupKey];
        nodes.lensTitle.textContent = `${meta.name} → ${shortId(i.documentId)}`;
        nodes.lensActions.innerHTML = i.sessionId
            ? `<button class="insp-lens-action" type="button" data-action="get-transcript" title="Plain-text transcript for this session"><i class="fa-solid fa-file-lines"></i>Transcript</button>
               <button class="insp-lens-action" type="button" data-action="get-session" title="Replay the terminal recording"><i class="fa-solid fa-play"></i>Replay</button>`
            : '';

        const isSession = i.kind === 'session-chunk';
        const userText = i.userText || i.userTextPreview || '';
        const terms = state.queryTerms;
        const termCounts = countTermHits(userText, terms);
        const totalHits = Array.from(termCounts.values()).reduce((a, b) => a + b, 0);

        // ─── WHY section ────────────────────────────────────────────────
        let primeHtml;
        const score = i.score;
        if (meta.scoreKind === 'similarity' && typeof score === 'number') {
            const pct = (score * 100);
            primeHtml = `
                <div class="insp-why-prime" style="--rank-color:${meta.accent}">
                    <div class="insp-why-pct">${pct.toFixed(1)}<span style="font-size:1.2rem;letter-spacing:-0.05em">%</span></div>
                    <div class="insp-why-text">
                        <div class="insp-why-strategy">${esc(meta.name)} — ${esc(strengthLabel(score))}</div>
                        <div class="insp-why-formula"><code>${esc(meta.formula)}</code></div>
                        <div class="insp-why-meter">
                            <div class="fill" style="width:${pct.toFixed(1)}%; background:${meta.accent}"></div>
                            <div class="tick" style="left:25%"></div><div class="tick-lbl" style="left:25%">0.25</div>
                            <div class="tick" style="left:50%"></div><div class="tick-lbl" style="left:50%">0.5</div>
                            <div class="tick" style="left:75%"></div><div class="tick-lbl" style="left:75%">0.75</div>
                        </div>
                    </div>
                    <div class="insp-why-strength">${esc(meta.model)}</div>
                </div>`;
        } else if (meta.scoreKind === 'rrf' && typeof score === 'number') {
            const contributors = (state.docPresence.get(i.documentId) || []).filter(p => p.groupKey !== 'fused');
            primeHtml = `
                <div class="insp-why-prime" style="--rank-color:${meta.accent}">
                    <div class="insp-why-pct">${contributors.length}<span style="font-size:1rem;letter-spacing:0.05em">/3</span></div>
                    <div class="insp-why-text">
                        <div class="insp-why-strategy">${esc(meta.name)} — RRF score ${score.toFixed(4)}</div>
                        <div class="insp-why-formula"><code>${esc(meta.formula)}</code> · contributing groups voted at ranks ${contributors.map(c => `<code>#${c.rank}</code>`).join(' ') || '—'}</div>
                    </div>
                    <div class="insp-why-strength">${contributors.length === 3 ? 'Consensus' : contributors.length === 2 ? 'Partial' : 'Single Source'}</div>
                </div>`;
        } else {
            // Lexical
            primeHtml = `
                <div class="insp-why-prime" style="--rank-color:${meta.accent}">
                    <div class="insp-why-pct">${totalHits}<span style="font-size:1.2rem;letter-spacing:-0.05em">×</span></div>
                    <div class="insp-why-text">
                        <div class="insp-why-strategy">${esc(meta.name)} — literal token match</div>
                        <div class="insp-why-formula">${totalHits === 0 ? 'No exact tokens in the visible preview — match may live in the full message or in tokens shorter than 2 chars.' : `<code>${totalHits}</code> total occurrences of your query terms across the full text`}</div>
                    </div>
                    <div class="insp-why-strength">${esc(meta.model)}</div>
                </div>`;
        }

        // ─── Cross-group presence ──────────────────────────────────────
        const presence = (state.docPresence.get(i.documentId) || []).filter(p => p.groupKey !== groupKey);
        let crossHtml;
        if (!presence.length) {
            crossHtml = `<div class="insp-cross-none">Only this strategy ranked this document — other groups missed it.</div>`;
        } else {
            crossHtml = presence.map(p => {
                const pm = GROUP_META[p.groupKey];
                let scoreText;
                if (pm.scoreKind === 'similarity' && typeof p.score === 'number') scoreText = `${(p.score * 100).toFixed(1)}%`;
                else if (pm.scoreKind === 'rrf' && typeof p.score === 'number') scoreText = `rrf ${p.score.toFixed(4)}`;
                else scoreText = `rank #${p.rank}`;
                return `<div class="insp-cross ${pm.short}">
                    <span class="label">${esc(pm.name)}</span>
                    <span>also ranked here at <b style="color:${pm.accent}">#${p.rank}</b></span>
                    <span class="score">${esc(scoreText)}</span>
                </div>`;
            }).join('');
        }

        // ─── Term chips ────────────────────────────────────────────────
        const termChips = terms.length
            ? `<div class="insp-term-row">${terms.map(t => {
                const c = termCounts.get(t) || 0;
                return `<span class="insp-term ${c === 0 ? 'miss' : ''}">${esc(t)} <span class="cnt">${c}</span></span>`;
            }).join('')}</div>`
            : '';

        // ─── Text (highlighted) ────────────────────────────────────────
        const textLabel = isSession
            ? (i.userText ? 'Session Aggregate (full chunk)' : 'Session Aggregate (preview)')
            : (i.userText ? 'User Message (full text)' : 'User Message (preview)');

        // ─── Metadata ─────────────────────────────────────────────────
        const metaCells = isSession ? [
            ['kind', 'session aggregate'],
            ['cli', i.cli || 'unknown'],
            ['env', i.environmentName || '—'],
            ['session ended', fmtDate(i.timestampUTC)],
            ['chunk #', i.chunkIndex ?? 'n/a'],
            ['session id', shortId(i.sessionId)],
            ['working dir', i.workingDirectory || '—'],
            ['file count', i.fileChangeCount ?? 0],
        ] : [
            ['cli', i.cli || 'unknown'],
            ['env', i.environmentName || '—'],
            ['timestamp', fmtDate(i.timestampUTC)],
            ['sequence', i.sequence ?? 'n/a'],
            ['session id', shortId(i.sessionId)],
            ['input id', i.userInputId ?? 'n/a'],
            ['git', i.gitCommitHash ? shortId(i.gitCommitHash) : '—'],
            ['working dir', i.workingDirectory || '—'],
        ];

        const metaHtml = metaCells.map(([k, v]) =>
            `<div class="insp-meta-cell"><div class="k">${esc(k)}</div><div class="v">${esc(String(v))}</div></div>`
        ).join('');

        // ─── Files ─────────────────────────────────────────────────────
        let filesHtml = '';
        if (i.fileChanges && i.fileChanges.length) {
            filesHtml = `<div class="insp-files"><table>
                <thead><tr><th>file</th><th>type</th><th>+</th><th>−</th></tr></thead>
                <tbody>${i.fileChanges.map(c => `<tr><td><code>${esc(c.filePath)}</code></td><td>${esc(c.changeType)}</td><td class="add">${c.linesAdded ?? '—'}</td><td class="del">${c.linesDeleted ?? '—'}</td></tr>`).join('')}</tbody>
            </table></div>`;
        }

        // ─── Raw payload ──────────────────────────────────────────────
        const rawHtml = (i.rawText && !isSession)
            ? `<details class="insp-raw"><summary>Raw capture payload · ${i.rawText.length.toLocaleString()} chars</summary><pre>${esc(i.rawText)}</pre></details>`
            : '';

        const loadingTag = state.detailLoading
            ? `<div class="insp-section wide" style="color:var(--insp-accent);font-family:var(--insp-mono);font-size:0.62rem;letter-spacing:0.18em;text-transform:uppercase">Loading full detail…</div>`
            : '';

        nodes.lens.innerHTML = `
            <div class="insp-section">
                <div class="insp-section-head"><span>Why this matched</span></div>
                <div class="insp-why">
                    ${primeHtml}
                    ${termChips}
                    ${crossHtml}
                </div>
            </div>
            <div class="insp-section">
                <div class="insp-section-head"><span>${esc(textLabel)}</span><span>${userText ? esc(userText.length.toLocaleString()) + ' chars' : ''}</span></div>
                <div class="insp-text">${highlightTerms(userText, state.queryTerms) || '<i style="color:var(--color-text-muted)">[no text]</i>'}</div>
            </div>
            <div class="insp-section wide">
                <div class="insp-section-head"><span>Metadata</span></div>
                <div class="insp-meta">${metaHtml}</div>
            </div>
            ${filesHtml ? `<div class="insp-section wide"><div class="insp-section-head"><span>File Changes (${(i.fileChanges || []).length})</span></div>${filesHtml}</div>` : ''}
            ${rawHtml ? `<div class="insp-section wide">${rawHtml}</div>` : ''}
            ${loadingTag}`;
    }

    // ─── rendering: recent (browse) ──────────────────────────────────────

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

    _renderRecent() {
        const items = this.state.recentSessions;
        this.nodes.recentCount.textContent = items.length ? `${items.length} session${items.length === 1 ? '' : 's'}` : 'none';
        if (!items.length) {
            this.nodes.recent.innerHTML = `<div class="insp-col-empty idle" style="margin:0">No recent sessions captured yet.</div>`;
            return;
        }
        this.nodes.recent.innerHTML = items.slice(0, 40).map(s => `
            <button class="insp-recent-item" type="button" data-session-id="${esc(s.sessionId)}">
                <div class="insp-recent-meta">
                    <span>${esc(s.cli || '?')} · ${esc(shortId(s.sessionId))}</span>
                    <span>${esc(fmtDate(s.latestTimestampUTC))} · ${s.captureCount}×</span>
                </div>
                <div class="insp-recent-text">${esc(s.latestPreview || '[no text]')}</div>
            </button>`).join('');
    }

    // ─── data flows ──────────────────────────────────────────────────────

    async loadStatus() {
        this.state.status = await this._fetchJson('/api/v1/bert/status');
        this.renderStatus();
    }

    async loadRecent() {
        const p = await this._fetchJson('/api/v1/bert/captures?take=200');
        this.state.recentSessions = this._groupBySession(p.captures || []);
        this._renderRecent();
    }

    async refreshAll(options = {}) {
        const silent = options?.silent === true;
        this.setBusy(true);
        try {
            await this.loadStatus();
            await this.loadRecent();
            // Refresh the idle learning card now that stats + recent sample have
            // landed, but only when no search results are currently displayed.
            if (!this.state.searchGroups || this.state.searchGroups.length === 0) {
                this._renderLearningCard();
            }
            if (!silent) this.setBanner('Status refreshed.', 'success');
        } catch (err) {
            this.setBanner(err.message, 'error');
        } finally {
            this.setBusy(false);
        }
    }

    async runSearch() {
        const query = this.nodes.query.value.trim();
        if (!query) {
            this.setBanner('Enter a query first.', 'error');
            this.nodes.query.focus();
            return;
        }
        // Generation guard: every runSearch invocation bumps this counter, and the
        // in-flight handler bails out if a newer call has started. Without it a slow
        // response can clobber the state set by a newer search the user just kicked
        // off.
        const gen = ++this._searchGen;
        this.setBusy(true);
        this.setBanner('Searching…');
        this._collapseDisclosures();
        this.state.mode = 'search';
        this.state.sessionBrowseId = null;
        this.state.searchGroups = [];
        this.state.docPresence = new Map();
        this.state.queryTerms = tokenizeQuery(query);
        this._resetSearchSelection();
        this.state.lastSearch = { query, totalSearchTimeMs: 0 };
        this._renderEmptyColumns('Running…');
        this._renderQueryStats();
        this._renderLens();
        try {
            const p = await this._fetchJson('/api/v1/search', {
                method: 'POST',
                body: JSON.stringify({ query, topK: Number(this.nodes.topk.value) || 10 })
            });
            if (gen !== this._searchGen) return; // a newer search superseded us
            this.state.lastSearch = p;
            this.state.searchGroups = p.groups || [];
            this.state.docPresence = this._buildDocPresence(this.state.searchGroups);
            this.state.queryCount += 1;
            this.nodes.queriesCounter.textContent = String(this.state.queryCount);
            this.nodes.queriesLabel.textContent = this.state.queryCount === 1 ? 'search' : 'searches';

            this._renderColumns();
            this._renderQueryStats();
            this._renderLens();

            const total = this.state.searchGroups.reduce((a, g) => a + (g.hits?.length || 0), 0);
            this.setBanner(
                total
                    ? `${total} hit${total === 1 ? '' : 's'} · ${p.totalSearchTimeMs}ms · click a result to inspect`
                    : `No hits · ${p.totalSearchTimeMs}ms`,
                total ? 'success' : ''
            );
        } catch (err) {
            if (gen !== this._searchGen) return;
            this.state.searchGroups = [];
            this._renderEmptyColumns('Search failed.');
            this._renderQueryStats();
            this.setBanner(err.message, 'error');
        } finally {
            if (gen === this._searchGen) this.setBusy(false);
        }
    }

    async loadCapture(documentId, groupKey) {
        this.state.selectedDocId = documentId;
        this.state.selectedGroupKey = groupKey;

        // Pull the score and kind out of the *exact* group the user clicked, so a
        // doc that ranks in multiple groups shows the score for the column they
        // pressed (not whichever group happens to be first).
        const group = this.state.searchGroups.find(g => g.key === groupKey);
        const summary = group?.hits?.find(h => h.documentId === documentId) || null;

        if (summary) {
            this.state.selected = { ...summary };
            this.state.detailLoading = true;
        }
        // Re-render so the selection highlight tracks immediately. Use whichever
        // layout matches the current mode (4-strategy search vs single-column
        // session browse) so we don't flip layouts mid-interaction.
        if (this.state.mode === 'session-browse' && group) {
            this._renderSessionView(this.state.sessionBrowseId, group.hits || []);
        } else {
            this._renderColumns();
        }
        this._renderLens();
        this.nodes.lens.closest('.insp-lens')?.scrollIntoView({ behavior: 'smooth', block: 'start' });

        try {
            const full = await this._fetchJson(`/api/v1/bert/captures/${encodeURIComponent(documentId)}`);
            // Guard: if the user clicked another row while this fetch was in flight,
            // selectedDocId moved on — don't overwrite their newer selection with
            // this stale response.
            if (this.state.selectedDocId !== documentId) return;
            // Keep the score and groupKey-specific kind from the summary — the
            // detail endpoint doesn't know which group the user clicked from.
            if (summary) {
                full.score = summary.score;
                full.kind = summary.kind ?? full.kind;
            }
            this.state.selected = full;
        } catch (err) {
            if (this.state.selectedDocId !== documentId) return;
            if (!summary) this.setBanner(`Could not load detail: ${err.message}`, 'error');
        } finally {
            if (this.state.selectedDocId === documentId) this.state.detailLoading = false;
        }
        if (this.state.selectedDocId === documentId) this._renderLens();
    }

    async runSessionLookup() {
        const sid = this.nodes.sessionId.value.trim();
        if (!sid) {
            this.setSessionBanner('Paste a session id.', 'error');
            this.nodes.sessionId.focus();
            return;
        }
        this.nodes.sessionSearch.disabled = true;
        try {
            await this.loadCapturesForSession(sid);
        } finally {
            this.nodes.sessionSearch.disabled = false;
        }
    }

    async loadCapturesForSession(sessionId) {
        if (!sessionId) return;
        // Entering session-browse must invalidate any in-flight runSearch (which guards on
        // this same token) so its slower response can't clobber this single-session view —
        // and bail here too if a newer search/lookup supersedes us mid-fetch.
        const gen = ++this._searchGen;
        this.setSessionBanner('Looking up…');
        try {
            const p = await this._fetchJson(`/api/v1/bert/captures/by-session/${encodeURIComponent(sessionId)}`);
            if (gen !== this._searchGen) return;
            const captures = p.captures || [];
            if (!captures.length) {
                this.setSessionBanner('No captures for this session.', 'error');
                return;
            }
            this._collapseDisclosures();

            // Drive a single-column "session browse" view rather than the
            // 4-strategy search layout — these captures aren't search results,
            // they're the chronological contents of one session.
            const pseudoGroup = {
                key: 'per-message-semantic',
                label: 'Session Captures',
                description: `All ${captures.length} captures in session ${shortId(sessionId)}.`,
                searchTimeMs: 0,
                corpusSize: captures.length,
                hits: captures,
            };
            this.state.mode = 'session-browse';
            this.state.sessionBrowseId = sessionId;
            this.state.searchGroups = [pseudoGroup];
            this.state.docPresence = this._buildDocPresence(this.state.searchGroups);
            this.state.queryTerms = [];
            this._resetSearchSelection();
            this.state.lastSearch = null;
            this._renderSessionView(sessionId, captures);
            this._renderQueryStats();
            this._renderLens();

            this.setSessionBanner(`Loaded ${captures.length} capture${captures.length === 1 ? '' : 's'} · click one to inspect.`, 'success');
        } catch (err) {
            this.setSessionBanner(err.message, 'error');
        }
    }

    _renderSessionView(sessionId, captures) {
        const groupKey = 'per-message-semantic';
        const cli = captures.find(c => c.cli)?.cli || '?';
        const body = captures.length === 0
            ? `<div class="insp-col-empty">No captures in this session.</div>`
            : captures.map((h, idx) => this._renderHitCard(h, groupKey, idx)).join('');

        this.nodes.cols.innerHTML = `
            <div class="insp-col full" data-key="session-browse">
                <div class="insp-col-head">
                    <div class="insp-col-tag">Session Browse</div>
                    <div class="insp-col-name" title="${esc(sessionId)}">Session ${esc(shortId(sessionId))}</div>
                    <div class="insp-col-meta">
                        <span>${esc(cli)}</span>
                        <span><b>${captures.length}</b> capture${captures.length === 1 ? '' : 's'}</span>
                        <span>chronological</span>
                    </div>
                    <div class="insp-col-explainer">Every captured user message in this session, in order. Click one to inspect its metadata and replay.</div>
                </div>
                <div class="insp-col-body insp-col-body-grid">${body}</div>
            </div>`;
    }
}
