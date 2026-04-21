const esc = v => String(v ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');

export class McpController {
    constructor(app) {
        this.app = app;
    }

    loadView() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('mcp-template');
        this.root = fragment.querySelector('[data-view="mcp"]');
        content.appendChild(fragment);
        if (!this.root) return;

        this.state = { status: null, tools: [], selectedTool: null };

        const q = sel => this.root.querySelector(sel);
        this.nodes = {
            serverStatus: q('[data-mcp-server-status]'),
            toolCount: q('[data-mcp-tool-count]'),
            statusGrid: q('[data-mcp-status-grid]'),
            serverPath: q('[data-mcp-server-path]'),
            refresh: q('[data-mcp-refresh]'),
            toolList: q('[data-mcp-tool-list]'),
            toolDetail: q('[data-mcp-tool-detail]')
        };

        this._bindEvents();
        this.refreshAll();
    }

    _bindEvents() {
        this.nodes.toolList.addEventListener('click', e => {
            const item = e.target.closest('[data-tool]');
            if (!item) return;
            this.state.selectedTool = item.getAttribute('data-tool');
            this.renderToolList();
            this.renderToolDetail();
        });
        this.nodes.refresh.addEventListener('click', () => this.refreshAll());
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
        if (!res.ok) throw new Error(data.error || data.message || 'Request failed.');
        return data;
    }

    renderStatus() {
        const s = this.state.status;
        const { statusGrid, serverPath, serverStatus } = this.nodes;
        if (!s) { statusGrid.innerHTML = ''; serverStatus.textContent = '—'; return; }
        serverStatus.textContent = s.serverAvailable ? '✓' : '✗';
        serverStatus.style.color = s.serverAvailable ? 'var(--mcp-ok)' : 'var(--mcp-bad)';
        serverPath.textContent = s.serverPath || 'Not configured';
        statusGrid.innerHTML = `
            <div class="mcp-card"><span>Available</span><strong><span class="mcp-pill ${s.serverAvailable ? 'ok' : 'bad'}">${s.serverAvailable ? 'Yes' : 'No'}</span></strong></div>
            <div class="mcp-card"><span>Tools Loaded</span><strong>${this.state.tools.length}</strong></div>
        `;
    }

    renderToolList() {
        const { toolList, toolCount } = this.nodes;
        toolCount.textContent = String(this.state.tools.length);
        if (!this.state.tools.length) {
            toolList.innerHTML = '<div class="mcp-empty">No tools found. Is the MCP server built and configured?</div>';
            return;
        }
        toolList.innerHTML = this.state.tools.map(t => `
            <div class="mcp-tool-item${this.state.selectedTool === t.name ? ' sel' : ''}" data-tool="${esc(t.name)}">
                <div class="mcp-tool-name">${esc(t.name)}</div>
                <div class="mcp-tool-desc">${esc(t.description || 'No description')}</div>
            </div>
        `).join('');
    }

    renderToolDetail() {
        const detail = this.nodes.toolDetail;
        if (!this.state.selectedTool) {
            detail.innerHTML = '<div class="mcp-empty">Select a tool from the list to inspect and call it.</div>';
            return;
        }
        const tool = this.state.tools.find(t => t.name === this.state.selectedTool);
        if (!tool) { detail.innerHTML = '<div class="mcp-empty">Tool not found.</div>'; return; }

        detail.innerHTML = `
            <h3 style="margin:0 0 4px;font-size:16px;color:var(--mcp-accent);">${esc(tool.name)}</h3>
            <p style="margin:0 0 16px;color:var(--mcp-muted);font-size:13px;line-height:1.5;">${esc(tool.description || 'No description')}</p>
            <div style="font-size:12px;color:var(--mcp-muted);text-transform:uppercase;letter-spacing:.08em;margin-bottom:8px;">Arguments (JSON)</div>
            <textarea class="mcp-control" data-mcp-tool-args rows="6" placeholder='{"key": "value"}' style="resize:vertical;font-size:13px;">{}</textarea>
            <div style="display:flex;gap:10px;align-items:center;margin-top:10px;">
                <button class="mcp-button primary" data-mcp-call-btn type="button">Call Tool</button>
                <span class="mcp-banner" data-mcp-call-banner></span>
            </div>
            <div data-mcp-call-result style="margin-top:12px;"></div>
        `;

        detail.querySelector('[data-mcp-call-btn]').addEventListener('click', () => this.callTool(tool.name));
    }

    async callTool(name) {
        const detail = this.nodes.toolDetail;
        const btn = detail.querySelector('[data-mcp-call-btn]');
        const banner = detail.querySelector('[data-mcp-call-banner]');
        const result = detail.querySelector('[data-mcp-call-result]');
        let args = {};
        try {
            const raw = detail.querySelector('[data-mcp-tool-args]').value.trim();
            if (raw) args = JSON.parse(raw);
        } catch {
            banner.textContent = 'Invalid JSON in arguments.';
            banner.className = 'mcp-banner error';
            return;
        }
        btn.disabled = true;
        banner.textContent = 'Calling...';
        banner.className = 'mcp-banner';
        result.innerHTML = '';
        try {
            const res = await this._fetchJson(`/api/v1/mcp/tools/${encodeURIComponent(name)}`, {
                method: 'POST',
                body: JSON.stringify({ arguments: args })
            });
            banner.textContent = res.success ? 'Call succeeded.' : 'Call failed.';
            banner.className = res.success ? 'mcp-banner success' : 'mcp-banner error';
            const output = res.error || res.result || '(empty response)';
            result.innerHTML = `<div style="font-size:12px;color:var(--mcp-muted);text-transform:uppercase;letter-spacing:.08em;margin:12px 0 6px;">Result</div><pre class="mcp-pre">${esc(typeof output === 'string' ? output : JSON.stringify(output, null, 2))}</pre>`;
        } catch (err) {
            banner.textContent = err.message;
            banner.className = 'mcp-banner error';
        } finally {
            btn.disabled = false;
        }
    }

    async loadStatus() {
        try {
            this.state.status = await this._fetchJson('/api/v1/mcp/status');
        } catch {
            this.state.status = { serverAvailable: false, serverPath: '', message: 'Failed to reach API' };
        }
        this.renderStatus();
    }

    async loadTools() {
        try {
            this.state.tools = await this._fetchJson('/api/v1/mcp/tools');
        } catch {
            this.state.tools = [];
        }
        this.renderToolList();
        this.renderStatus();
    }

    async refreshAll() {
        this.nodes.refresh.disabled = true;
        await this.loadStatus();
        if (this.state.status?.serverAvailable) await this.loadTools();
        else { this.state.tools = []; this.renderToolList(); this.renderStatus(); }
        this.nodes.refresh.disabled = false;
    }
}
