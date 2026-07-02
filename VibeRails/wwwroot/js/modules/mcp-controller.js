import { getXtermPayload, renderXtermPayload } from './terminal-snapshot-renderer.js';

const esc = v => String(v ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');

// Starter arguments for the known local tools so a bare "Call" works without hand-writing JSON.
const MCP_ARG_EXAMPLES = {
    search_history: { query: 'websocket timeout' },
    validate_vca: {},
    list_terminals: {},
    open_terminal: { cli: 'Shell' },
    send_terminal_input: { text: '', submit: false },
    get_terminal_snapshot: {},
    run_shell_command: { command: 'dotnet --info', timeoutSeconds: 60, waitForCompletion: true },
    get_shell_command_status: { jobId: 'shell-...' },
    cancel_shell_command: { jobId: 'shell-...' },
    web_search: { query: 'Model Context Protocol', maxResults: 5 },
    web_fetch: { url: 'https://example.com', maxChars: 12000 }
};

export class McpController {
    constructor(app) {
        this.app = app;
        // Only the (untrusted) remote endpoint is remembered. Local always uses the
        // in-process loopback /mcp, and remote is NEVER auto-connected — the user must
        // explicitly click Connect, so we never silently reach out to a saved server.
        this.storageKey = 'viberails:mcp-explorer-remote-endpoint';
        this.resultDisposers = [];
        this._onRemoteMessage = null;
    }

    loadView() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('mcp-template');
        this.root = fragment.querySelector('[data-view="mcp"]');
        content.appendChild(fragment);
        if (!this.root) return;

        this.state = {
            mode: 'local',
            // Local (trusted)
            localTools: [],
            localSelected: null,
            localConnected: false,
            localFilter: '',
            // Remote (untrusted, rendered inside the sandboxed iframe)
            remoteTools: [],
            remoteConnected: false,
            remoteEndpoint: '',
            remoteHeaders: {},
            remoteReady: false,
            remotePending: null
        };

        const q = sel => this.root.querySelector(sel);
        this.nodes = {
            modeButtons: Array.from(this.root.querySelectorAll('[data-mcp-mode]')),
            localPanel: q('[data-mcp-local]'),
            remotePanel: q('[data-mcp-remote]'),
            toolCount: q('[data-mcp-tool-count]'),
            // local
            filter: q('[data-mcp-filter]'),
            toolList: q('[data-mcp-tool-list]'),
            toolDetail: q('[data-mcp-tool-detail]'),
            // remote
            endpoint: q('[data-mcp-endpoint]'),
            headers: q('[data-mcp-headers]'),
            connect: q('[data-mcp-connect]'),
            status: q('[data-mcp-status]'),
            target: q('[data-mcp-target]'),
            frame: q('[data-mcp-remote-frame]')
        };

        this._bindEvents();
        this._restoreRemoteEndpoint();
        this.setMode('local');
        this.connectLocal();
    }

    _bindEvents() {
        this.nodes.modeButtons.forEach(btn =>
            btn.addEventListener('click', () => this.setMode(btn.getAttribute('data-mcp-mode'))));

        this.nodes.filter.addEventListener('input', e => {
            this.state.localFilter = e.target.value.trim().toLowerCase();
            this.renderLocalToolList();
        });
        this.nodes.toolList.addEventListener('click', e => {
            const item = e.target.closest('[data-tool]');
            if (!item) return;
            this.state.localSelected = item.getAttribute('data-tool');
            this.renderLocalToolList();
            this.renderLocalToolDetail();
        });

        this.nodes.connect.addEventListener('click', () => this.connectRemote());
        this.nodes.endpoint.addEventListener('keydown', e => {
            if (e.key === 'Enter') this.connectRemote();
        });

        // Broker for the sandboxed remote iframe. Remove any listener from a prior loadView
        // so we never accumulate duplicates when the view is reopened.
        if (this._onRemoteMessage) {
            window.removeEventListener('message', this._onRemoteMessage);
        }
        this._onRemoteMessage = event => this._handleRemoteMessage(event);
        window.addEventListener('message', this._onRemoteMessage);
    }

    setMode(mode) {
        if (mode !== 'local' && mode !== 'remote') return;
        this.state.mode = mode;
        this.nodes.modeButtons.forEach(btn =>
            btn.classList.toggle('is-active', btn.getAttribute('data-mcp-mode') === mode));
        if (this.nodes.localPanel) this.nodes.localPanel.hidden = mode !== 'local';
        if (this.nodes.remotePanel) this.nodes.remotePanel.hidden = mode !== 'remote';
        this.nodes.toolCount.textContent = String(
            mode === 'local' ? this.state.localTools.length : this.state.remoteTools.length);
    }

    // ----- Local (trusted, in-process /mcp) -----

    async connectLocal() {
        this.nodes.toolList.innerHTML = '<div class="mcp-empty">Loading tools.</div>';
        try {
            // GET /api/v1/mcp/tools never accepts a caller endpoint — it is always the
            // in-process loopback server, so nothing here can be steered off-box.
            const tools = await this._fetchJson('/api/v1/mcp/tools');
            this.state.localTools = Array.isArray(tools) ? tools : [];
            this.state.localConnected = true;
            this.state.localSelected = this.state.localTools[0]?.name || null;
        } catch (err) {
            this.state.localTools = [];
            this.state.localConnected = false;
            this.state.localError = err.message;
        }
        if (this.state.mode === 'local') {
            this.nodes.toolCount.textContent = String(this.state.localTools.length);
        }
        this.renderLocalToolList();
        this.renderLocalToolDetail();
    }

    filteredLocalTools() {
        if (!this.state.localFilter) return this.state.localTools;
        return this.state.localTools.filter(tool => {
            const text = `${tool.name || ''} ${tool.title || ''} ${tool.description || ''}`.toLowerCase();
            return text.includes(this.state.localFilter);
        });
    }

    renderLocalToolList() {
        const tools = this.filteredLocalTools();

        if (!this.state.localConnected) {
            this.nodes.toolList.innerHTML = `<div class="mcp-empty">${esc(this.state.localError || 'Local MCP server unavailable.')}</div>`;
            return;
        }
        if (!tools.length) {
            this.nodes.toolList.innerHTML = '<div class="mcp-empty">No matching tools.</div>';
            return;
        }

        this.nodes.toolList.innerHTML = tools.map(tool => `
            <button class="mcp-tool-item${this.state.localSelected === tool.name ? ' selected' : ''}" data-tool="${esc(tool.name)}" type="button">
                <span class="mcp-tool-name">${esc(tool.name)}</span>
                <span class="mcp-tool-desc">${esc(tool.title || tool.description || 'No description')}</span>
            </button>
        `).join('');
    }

    renderLocalToolDetail() {
        this.disposeRenderedResults();
        const detail = this.nodes.toolDetail;
        const tool = this.state.localTools.find(t => t.name === this.state.localSelected);

        if (!this.state.localConnected) {
            detail.innerHTML = '<div class="mcp-empty">Local MCP server unavailable.</div>';
            return;
        }
        if (!tool) {
            detail.innerHTML = '<div class="mcp-empty">Select a tool.</div>';
            return;
        }

        const inputSchemaText = this.formatJson(tool.inputSchema || {});
        const returnSchemaText = tool.returnSchema ? this.formatJson(tool.returnSchema) : 'No return schema advertised.';
        const args = this.formatJson(this.buildExampleArgs(tool));

        detail.innerHTML = `
            <div class="mcp-detail-head">
                <div>
                    <div class="mcp-kicker">Tool</div>
                    <h5>${esc(tool.name)}</h5>
                    <p>${esc(tool.description || tool.title || 'No description')}</p>
                </div>
                <button class="mcp-button primary" data-mcp-call type="button"><i class="fas fa-play"></i><span>Call</span></button>
            </div>
            <div class="mcp-detail-grid">
                <section class="mcp-section">
                    <label for="mcp-tool-args">Arguments JSON</label>
                    <textarea id="mcp-tool-args" class="mcp-control mcp-textarea" data-mcp-tool-args spellcheck="false">${esc(args)}</textarea>
                    <div class="mcp-call-line">
                        <span class="mcp-call-banner" data-mcp-call-banner></span>
                    </div>
                </section>
                <section class="mcp-section">
                    <label>Input Schema</label>
                    <pre class="mcp-pre">${esc(inputSchemaText)}</pre>
                </section>
            </div>
            <details class="mcp-schema-details">
                <summary>Return Schema</summary>
                <pre class="mcp-pre">${esc(returnSchemaText)}</pre>
            </details>
            <section class="mcp-section mcp-result" data-mcp-call-result hidden>
                <label>Result</label>
                <div data-mcp-special-result hidden></div>
                <pre class="mcp-pre"></pre>
            </section>
        `;

        detail.querySelector('[data-mcp-call]').addEventListener('click', () => this.callLocalTool(tool.name));
    }

    async callLocalTool(name) {
        const detail = this.nodes.toolDetail;
        const button = detail.querySelector('[data-mcp-call]');
        const banner = detail.querySelector('[data-mcp-call-banner]');
        const result = detail.querySelector('[data-mcp-call-result]');
        const specialResult = result?.querySelector('[data-mcp-special-result]');
        const resultPre = result?.querySelector('pre');

        let args = {};
        try {
            const raw = detail.querySelector('[data-mcp-tool-args]').value.trim();
            if (raw) args = JSON.parse(raw);
        } catch (err) {
            banner.textContent = err.message || 'Invalid JSON.';
            banner.className = 'mcp-call-banner error';
            return;
        }

        button.disabled = true;
        banner.textContent = 'Calling';
        banner.className = 'mcp-call-banner';
        this.disposeRenderedResults();
        if (result) result.hidden = true;
        if (specialResult) {
            specialResult.hidden = true;
            specialResult.innerHTML = '';
        }

        try {
            // No endpoint in the body → the backend calls the in-process loopback /mcp.
            const response = await this._fetchJson(`/api/v1/mcp/tools/${encodeURIComponent(name)}`, {
                method: 'POST',
                body: JSON.stringify({ arguments: args })
            });

            banner.textContent = response.success ? 'Call succeeded.' : 'Call failed.';
            banner.className = response.success ? 'mcp-call-banner success' : 'mcp-call-banner error';
            const output = response.error ?? response.result ?? '(empty response)';
            const parsedOutput = this.parseJsonObject(output);
            const xtermPayload = getXtermPayload(parsedOutput);
            resultPre.textContent = xtermPayload
                ? this.formatJson(this.summarizeSpecialPayload(parsedOutput))
                : (typeof output === 'string' ? output : this.formatJson(output));
            result.hidden = false;

            if (response.success && xtermPayload && specialResult) {
                specialResult.hidden = false;
                const rendered = await renderXtermPayload(specialResult, xtermPayload);
                if (rendered?.dispose) {
                    this.resultDisposers.push(rendered.dispose);
                }
            }
        } catch (err) {
            banner.textContent = err.message;
            banner.className = 'mcp-call-banner error';
        } finally {
            button.disabled = false;
        }
    }

    // ----- Remote (untrusted, isolated in the sandboxed iframe) -----

    async connectRemote() {
        const endpoint = this.nodes.endpoint.value.trim();
        let headers;
        try {
            headers = this._parseHeaders();
        } catch (err) {
            this._setRemoteStatus('error', err.message);
            return;
        }
        if (!endpoint) {
            this._setRemoteStatus('error', 'Enter a remote MCP endpoint.');
            return;
        }

        this.nodes.connect.disabled = true;
        this._setRemoteStatus('loading', 'Connecting');
        this.state.remoteConnected = false;
        this.state.remoteTools = [];
        this._postToFrame({ type: 'mcp-remote-reset' });

        try {
            const result = await this._fetchJson('/api/v1/mcp/inspect', {
                method: 'POST',
                body: JSON.stringify({ endpoint, headers })
            });

            this.state.remoteTools = Array.isArray(result.tools) ? result.tools : [];
            this.state.remoteConnected = true;
            this.state.remoteEndpoint = result.endpoint || endpoint;
            this.state.remoteHeaders = headers;
            this.nodes.endpoint.value = this.state.remoteEndpoint;
            this._saveRemoteEndpoint(this.state.remoteEndpoint);
            this._setRemoteStatus('success', result.message || 'Connected');
            if (this.state.mode === 'remote') {
                this.nodes.toolCount.textContent = String(this.state.remoteTools.length);
            }
            this._sendToolsToFrame();
        } catch (err) {
            this.state.remoteConnected = false;
            this._setRemoteStatus('error', err.message);
            this._postToFrame({ type: 'mcp-remote-status', message: err.message });
        } finally {
            this.nodes.connect.disabled = false;
        }
    }

    _sendToolsToFrame() {
        // The iframe announces itself with 'mcp-remote-ready'. If it hasn't yet, stash the
        // tools and flush them when it does.
        if (!this.state.remoteReady) {
            this.state.remotePending = this.state.remoteTools;
            return;
        }
        this._postToFrame({
            type: 'mcp-remote-tools',
            tools: this.state.remoteTools,
            message: `${this.state.remoteTools.length} tool(s)`
        });
    }

    _postToFrame(msg) {
        const frame = this.nodes.frame;
        // The frame is an opaque-origin sandbox, so we can only target '*'. That is safe:
        // messages to it carry only the untrusted MCP data it is meant to render.
        if (frame && frame.contentWindow) {
            frame.contentWindow.postMessage(msg, '*');
        }
    }

    async _handleRemoteMessage(event) {
        const frame = this.nodes.frame;
        // Only accept messages from OUR sandboxed frame; never trust arbitrary senders.
        if (!frame || event.source !== frame.contentWindow) return;
        const data = event.data;
        if (!data || typeof data !== 'object') return;

        if (data.type === 'mcp-remote-ready') {
            this.state.remoteReady = true;
            if (this.state.remotePending) {
                const tools = this.state.remotePending;
                this.state.remotePending = null;
                this._postToFrame({ type: 'mcp-remote-tools', tools, message: `${tools.length} tool(s)` });
            }
            return;
        }

        if (data.type === 'mcp-remote-call') {
            const name = typeof data.name === 'string' ? data.name : '';
            if (!name || !this.state.remoteConnected) return;
            try {
                const response = await this._fetchJson(`/api/v1/mcp/tools/${encodeURIComponent(name)}`, {
                    method: 'POST',
                    body: JSON.stringify({
                        endpoint: this.state.remoteEndpoint,
                        headers: this.state.remoteHeaders || {},
                        arguments: (data.args && typeof data.args === 'object') ? data.args : {}
                    })
                });
                const output = response.error ?? response.result ?? '(empty response)';
                this._postToFrame({ type: 'mcp-remote-result', name, ok: !!response.success, output });
            } catch (err) {
                this._postToFrame({ type: 'mcp-remote-result', name, ok: false, output: err.message });
            }
        }
    }

    _setRemoteStatus(kind, message) {
        const { status, target } = this.nodes;
        if (status) {
            status.className = `mcp-status ${kind}`;
            status.innerHTML = `<span class="mcp-status-dot"></span><span>${esc(message || 'Idle')}</span>`;
        }
        if (target) {
            target.textContent = this.nodes.endpoint.value.trim() || 'No endpoint';
        }
    }

    _parseHeaders() {
        const raw = this.nodes.headers.value.trim();
        if (!raw) return {};

        const parsed = JSON.parse(raw);
        if (!parsed || Array.isArray(parsed) || typeof parsed !== 'object') {
            throw new Error('Headers must be a JSON object.');
        }

        const headers = {};
        for (const [key, value] of Object.entries(parsed)) {
            if (!key.trim()) throw new Error('Header names cannot be blank.');
            if (value == null) continue;
            headers[key] = String(value);
        }
        return headers;
    }

    _restoreRemoteEndpoint() {
        try {
            const saved = localStorage.getItem(this.storageKey);
            if (saved && this.nodes.endpoint) this.nodes.endpoint.value = saved;
        } catch {
            /* ignore storage errors */
        }
    }

    _saveRemoteEndpoint(endpoint) {
        try {
            localStorage.setItem(this.storageKey, endpoint);
        } catch {
            /* ignore storage errors */
        }
    }

    // ----- Shared helpers -----

    buildExampleArgs(tool) {
        if (Object.hasOwn(MCP_ARG_EXAMPLES, tool.name)) {
            return MCP_ARG_EXAMPLES[tool.name];
        }

        const schema = tool.inputSchema || {};
        const properties = schema.properties && typeof schema.properties === 'object' ? schema.properties : {};
        const args = {};

        for (const [name, propSchema] of Object.entries(properties)) {
            args[name] = this.sampleValue(propSchema);
        }

        return args;
    }

    sampleValue(schema) {
        if (!schema || typeof schema !== 'object') return null;
        if (Object.hasOwn(schema, 'default')) return schema.default;
        if (Array.isArray(schema.enum) && schema.enum.length) return schema.enum[0];

        const rawType = Array.isArray(schema.type) ? schema.type.find(t => t !== 'null') : schema.type;
        switch (rawType) {
            case 'string':
                return '';
            case 'integer':
            case 'number':
                return 0;
            case 'boolean':
                return false;
            case 'array':
                return [];
            case 'object':
                return {};
            default:
                return null;
        }
    }

    parseJsonObject(value) {
        if (value && typeof value === 'object' && !Array.isArray(value)) {
            return value;
        }

        if (typeof value !== 'string') return null;
        const trimmed = value.trim();
        if (!trimmed.startsWith('{') || !trimmed.endsWith('}')) return null;

        try {
            const parsed = JSON.parse(trimmed);
            return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : null;
        } catch {
            return null;
        }
    }

    summarizeSpecialPayload(value) {
        if (!value || typeof value !== 'object') return value;

        let clone;
        try {
            clone = JSON.parse(JSON.stringify(value));
        } catch {
            return value;
        }

        if (clone.xterm_ui_bytes?.base64) {
            const byteLength = clone.xterm_ui_bytes.byte_length || clone.xterm_ui_bytes.base64.length;
            clone.xterm_ui_bytes = {
                ...clone.xterm_ui_bytes,
                base64: `<base64 xterm replay: ${byteLength} bytes>`
            };
        }

        if (clone.xterm_png_string) {
            clone.xterm_png_string = '<browser-rendered PNG data URL>';
        }

        return clone;
    }

    disposeRenderedResults() {
        for (const dispose of this.resultDisposers.splice(0)) {
            try { dispose(); } catch { /* no-op */ }
        }
    }

    formatJson(value) {
        try {
            return JSON.stringify(value ?? {}, null, 2);
        } catch {
            return String(value ?? '');
        }
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
        if (!res.ok) throw new Error(data.error || data.message || 'Request failed.');
        return data;
    }
}
