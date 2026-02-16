// Terminal Controller - Manages embedded xterm.js terminal
export class TerminalController {
    constructor(app) {
        this.app = app;
        this.terminal = null;
        this.fitAddon = null;
        this.socket = null;
        this.isConnected = false;
        this.resizePrefix = '__resize__:';
        this.resizeDebounceId = null;
        this.resizeObserver = null;
        this.windowResizeHandler = null;
    }

    async checkStatus() {
        try {
            const response = await this.app.apiCall('/api/v1/terminal/status', 'GET');
            return response;
        } catch (error) {
            console.error('Failed to check terminal status:', error);
            return { hasActiveSession: false };
        }
    }

    async startSession(cli = null, environmentName = null) {
        try {
            const body = {};
            if (cli) body.cli = cli;
            if (environmentName) body.environmentName = environmentName;

            // Check if Make Remote toggle is checked
            const makeRemoteCheckbox = document.querySelector('#terminal-make-remote');
            if (makeRemoteCheckbox?.checked) {
                body.makeRemote = true;
            }

            const response = await this.app.apiCall('/api/v1/terminal/start', 'POST', body);

            if (response.hasActiveSession) {
                return true;
            }
            return false;
        } catch (error) {
            console.error('Failed to start terminal session:', error);
            this.app.showError('Failed to start terminal: ' + error.message);
            return false;
        }
    }

    async stopSession() {
        try {
            await this.app.apiCall('/api/v1/terminal/stop', 'POST');
            this.disconnect();
            return true;
        } catch (error) {
            console.error('Failed to stop terminal session:', error);
            return false;
        }
    }

    async connect(terminalElement) {
        // Clean up any stale state from previous navigation
        // (DOM was destroyed but our references weren't cleared)
        if (this.terminal) {
            try { this.terminal.dispose(); } catch (e) { /* already disposed */ }
            this.terminal = null;
        }
        this.fitAddon = null;
        this.teardownResizeHandling();
        if (this.socket) {
            try { this.socket.close(); } catch (e) { /* already closed */ }
            this.socket = null;
        }
        this.isConnected = false;

        // Initialize xterm.js with Unicode support
        this.terminal = new Terminal({
            cols: 120,
            rows: 30,
            cursorBlink: true,
            fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
            fontSize: 14,
            allowProposedApi: true,
            unicodeVersion: '11',
            disableStdin: false,
            cursorStyle: 'block',
            theme: {
                background: '#1e1e1e',
                foreground: '#d4d4d4',
                cursor: '#d4d4d4',
                cursorAccent: '#1e1e1e',
                selection: '#264f78',
                black: '#1e1e1e',
                red: '#f44747',
                green: '#608b4e',
                yellow: '#dcdcaa',
                blue: '#569cd6',
                magenta: '#c586c0',
                cyan: '#4ec9b0',
                white: '#d4d4d4',
                brightBlack: '#808080',
                brightRed: '#f44747',
                brightGreen: '#608b4e',
                brightYellow: '#dcdcaa',
                brightBlue: '#569cd6',
                brightMagenta: '#c586c0',
                brightCyan: '#4ec9b0',
                brightWhite: '#ffffff'
            }
        });

        if (window.FitAddon?.FitAddon) {
            this.fitAddon = new window.FitAddon.FitAddon();
            this.terminal.loadAddon(this.fitAddon);
        }

        this.terminal.open(terminalElement);
        this.setupResizeHandling(terminalElement);
        this.fitAndSyncTerminal();
        this.terminal.focus();

        // Connect to WebSocket
        const baseUrl = window.__viberails_API_BASE__ || '';
        const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        let wsUrl;

        if (baseUrl) {
            // In VS Code webview, baseUrl is like http://localhost:PORT
            wsUrl = baseUrl.replace(/^http/, 'ws') + '/api/v1/terminal/ws';
        } else {
            wsUrl = `${wsProtocol}//${window.location.host}/api/v1/terminal/ws`;
        }

        this.socket = new WebSocket(wsUrl);
        this.socket.binaryType = 'arraybuffer';

        this.socket.onopen = () => {
            this.isConnected = true;
            this.fitAndSyncTerminal();
            console.log('Terminal WebSocket connected - CLI is already running');
        };

        this.socket.onmessage = (event) => {
            if (typeof event.data === 'string') {
                this.terminal.write(event.data);
                return;
            }
            this.terminal.write(new Uint8Array(event.data));
        };

        this.socket.onclose = (event) => {
            this.isConnected = false;
            if (this.terminal) {
                const reason = event.reason || 'Terminal disconnected';
                const color = reason.includes('taken over') ? '33' : '90';
                this.terminal.write(`\r\n\x1b[${color}m[${reason}]\x1b[0m\r\n`);
            }
            console.log('Terminal WebSocket closed');

            // Show reconnect button and update status badge
            const panel = document.getElementById('terminal-panel');
            if (panel) {
                const reconnectBtn = panel.querySelector('#terminal-reconnect-btn');
                const statusBadge = panel.querySelector('#terminal-status-badge');
                if (reconnectBtn) reconnectBtn.classList.remove('d-none');
                if (statusBadge) {
                    statusBadge.textContent = 'Disconnected';
                    statusBadge.classList.remove('bg-success');
                    statusBadge.classList.add('bg-warning');
                }
            }
        };

        this.socket.onerror = (error) => {
            console.error('Terminal WebSocket error:', error);
            this.app.showError('Terminal connection error');
        };

        // Send user input to WebSocket
        this.terminal.onData((data) => {
            if (this.socket && this.socket.readyState === WebSocket.OPEN) {
                this.socket.send(data);
            }
        });

        // Handle clipboard paste events explicitly
        this.terminal.attachCustomKeyEventHandler((event) => {
            // Ctrl+V or Cmd+V (paste)
            if ((event.ctrlKey || event.metaKey) && event.key === 'v' && event.type === 'keydown') {
                navigator.clipboard.readText().then(text => {
                    if (this.socket && this.socket.readyState === WebSocket.OPEN) {
                        this.socket.send(text);
                    }
                }).catch(err => {
                    console.error('Failed to read clipboard:', err);
                });
                return false; // Prevent default handling
            }
            return true; // Allow other keys to be handled normally
        });
    }

    sendResizeToPty() {
        if (!this.terminal || !this.socket || this.socket.readyState !== WebSocket.OPEN) return;
        this.socket.send(`${this.resizePrefix}${this.terminal.cols},${this.terminal.rows}`);
    }

    fitAndSyncTerminal() {
        if (!this.terminal) return;
        if (this.fitAddon) {
            this.fitAddon.fit();
        }
        this.sendResizeToPty();
    }

    setupResizeHandling(terminalElement) {
        const debouncedFit = () => {
            if (this.resizeDebounceId) clearTimeout(this.resizeDebounceId);
            this.resizeDebounceId = setTimeout(() => this.fitAndSyncTerminal(), 100);
        };

        this.windowResizeHandler = debouncedFit;
        window.addEventListener('resize', this.windowResizeHandler);

        if (typeof ResizeObserver !== 'undefined') {
            this.resizeObserver = new ResizeObserver(() => debouncedFit());
            this.resizeObserver.observe(terminalElement);
        }
    }

    teardownResizeHandling() {
        if (this.resizeDebounceId) {
            clearTimeout(this.resizeDebounceId);
            this.resizeDebounceId = null;
        }

        if (this.resizeObserver) {
            try { this.resizeObserver.disconnect(); } catch (e) { /* no-op */ }
            this.resizeObserver = null;
        }

        if (this.windowResizeHandler) {
            window.removeEventListener('resize', this.windowResizeHandler);
            this.windowResizeHandler = null;
        }
    }

    disconnect() {
        this.teardownResizeHandling();
        if (this.socket) {
            this.socket.close();
            this.socket = null;
        }
        if (this.terminal) {
            this.terminal.dispose();
            this.terminal = null;
        }
        this.fitAddon = null;
        this.isConnected = false;
    }

    renderTerminalPanel() {
        return `
            <div class="card mb-4" id="terminal-panel">
                <div class="card-header d-flex justify-content-between align-items-center gap-3 flex-wrap">
                    <div class="terminal-header-main">
                        <div class="d-flex align-items-center gap-2 flex-wrap">
                            <span class="card-title d-inline-flex align-items-center gap-2">
                                <img src="assets/img/icons/terminal-solid-full.svg" alt="Terminal" style="height: 18px; width: 18px; opacity: 0.85;">
                                Web Terminal
                            </span>
                            <span class="badge bg-secondary" id="terminal-status-badge">Not Started</span>
                        </div>
                        <p class="text-muted small mb-0 mt-1">Launch a web-based terminal session for interacting with your selected CLI and connect to it remotely.</p>
                    </div>
                    <div class="d-flex gap-2 align-items-center flex-wrap justify-content-end" id="terminal-actions">
                        <select class="form-select form-select-sm" id="terminal-cli-select" style="width: auto;">
                            <option value="claude" selected>Claude</option>
                            <option value="codex">Codex</option>
                            <option value="gemini">Gemini</option>
                        </select>
                        <div class="form-check form-switch ms-2" id="terminal-make-remote-toggle" style="display: none;">
                            <input class="form-check-input" type="checkbox" id="terminal-make-remote" title="Share this session with remote VibeRails-Front server">
                            <label class="form-check-label small" for="terminal-make-remote">Make Remote</label>
                        </div>
                        <button class="btn btn-sm btn-primary d-inline-flex align-items-center gap-1" id="terminal-start-btn">
                            <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" fill="currentColor" viewBox="0 0 16 16" aria-hidden="true">
                                <path d="M6.79 5.093A.5.5 0 0 0 6 5.5v5a.5.5 0 0 0 .79.407l3.5-2.5a.5.5 0 0 0 0-.814z"/>
                                <path d="M0 4a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H2a2 2 0 0 1-2-2zm2-1a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V4a1 1 0 0 0-1-1z"/>
                            </svg>
                            <span>Start</span>
                        </button>
                        <button class="btn btn-sm btn-outline-light d-none" id="terminal-reconnect-btn" title="Reconnect to active session">
                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="vertical-align: -2px;"><path d="M21.5 2v6h-6M2.5 22v-6h6M2 11.5a10 10 0 0 1 18.8-4.3M22 12.5a10 10 0 0 1-18.8 4.2"/></svg>
                            Reconnect
                        </button>
                        <button class="btn btn-sm btn-danger d-none" id="terminal-stop-btn">
                            Stop
                        </button>
                    </div>
                </div>
                <div class="card-body p-0" id="terminal-container" style="display: none; height: 520px; overflow: hidden;">
                    <div id="terminal-element" style="width: 100%; height: 100%;"></div>
                </div>
                <div class="card-body text-center text-muted" id="terminal-placeholder">
                    <p class="mb-3">Launch an embedded terminal session.</p>
                    <p class="small">Select a CLI and click "Start" to begin.</p>
                </div>
            </div>
        `;
    }

    async populateTerminalSelector(container, preselectedEnvId = null) {
        const cliSelect = container.querySelector('#terminal-cli-select');
        if (!cliSelect) return;

        // Fetch environments from app data (already loaded in dashboard)
        const environments = this.app.data.environments || [];

        // Clear existing options
        cliSelect.innerHTML = '';

        // Base CLIs optgroup
        const baseGroup = document.createElement('optgroup');
        baseGroup.label = 'Base CLIs';
        baseGroup.innerHTML = `
            <option value="base:claude">Claude (default)</option>
            <option value="base:codex">Codex (default)</option>
            <option value="base:gemini">Gemini (default)</option>
        `;
        cliSelect.appendChild(baseGroup);

        // Custom Environments optgroup
        if (environments.length > 0) {
            const envGroup = document.createElement('optgroup');
            envGroup.label = 'Custom Environments';

            environments.forEach(env => {
                const option = document.createElement('option');
                option.value = `env:${env.id}:${env.cli}`;
                option.textContent = `${env.name} (${env.cli})`;
                option.dataset.envName = env.name;
                option.dataset.envCli = env.cli;
                if (preselectedEnvId && env.id === preselectedEnvId) {
                    option.selected = true;
                }
                envGroup.appendChild(option);
            });

            cliSelect.appendChild(envGroup);
        }
    }

    async bindTerminalActions(container, preselectedEnvId = null) {
        // Populate selector with environments
        await this.populateTerminalSelector(container, preselectedEnvId);

        // Check if remote access is configured and show/hide Make Remote toggle
        try {
            const settings = await this.app.apiCall('/api/v1/settings', 'GET');
            const makeRemoteToggle = container.querySelector('#terminal-make-remote-toggle');
            if (makeRemoteToggle) {
                if (settings.remoteAccess && settings.apiKey) {
                    makeRemoteToggle.style.removeProperty('display');
                } else {
                    makeRemoteToggle.style.display = 'none';
                }
            }
        } catch (error) {
            console.error('Failed to fetch settings for Make Remote toggle:', error);
        }

        const startBtn = container.querySelector('#terminal-start-btn');
        const reconnectBtn = container.querySelector('#terminal-reconnect-btn');
        const stopBtn = container.querySelector('#terminal-stop-btn');
        const cliSelect = container.querySelector('#terminal-cli-select');

        if (startBtn) {
            startBtn.addEventListener('click', async () => {
                const selection = cliSelect?.value || 'base:claude';
                await this.startTerminal(container, selection);
            });
        }

        if (reconnectBtn) {
            reconnectBtn.addEventListener('click', async () => {
                await this.reconnectTerminal(container);
            });
        }

        if (stopBtn) {
            stopBtn.addEventListener('click', async () => {
                await this.stopTerminal(container);
            });
        }

        // Check if there's already an active session
        this.checkAndRestoreSession(container);
    }

    async checkAndRestoreSession(container) {
        const status = await this.checkStatus();
        if (status.hasActiveSession) {
            await this.showActiveTerminal(container);
        }
    }

    async startTerminal(container, selection) {
        let displayName = null;
        let cli = null;
        let environmentName = null;

        if (selection.startsWith('base:')) {
            // Base CLI
            cli = selection.replace('base:', '').toLowerCase();
            displayName = cli.charAt(0).toUpperCase() + cli.slice(1);
        } else if (selection.startsWith('env:')) {
            // Custom environment: env:id:cli
            const parts = selection.split(':');
            const envId = parseInt(parts[1]);
            const cliType = parts[2];
            const env = this.app.data.environments.find(e => e.id === envId);
            const envName = env?.name || '';
            displayName = `${envName} (${cliType})`;
            cli = cliType.toLowerCase();  // Ensure lowercase for backend
            environmentName = envName;
        }

        // Single API call - start session with CLI directly (like the CLI path)
        const success = await this.startSession(cli, environmentName);
        if (!success) return;

        await this.showActiveTerminal(container);
        this.app.showToast('Terminal Started', `Launching ${displayName}...`, 'success');
    }

    async startTerminalWithOptions(options, container) {
        // options: { cli, environmentName?, workingDirectory?, title? }
        const body = { cli: options.cli };
        if (options.environmentName) body.environmentName = options.environmentName;
        if (options.workingDirectory) body.workingDirectory = options.workingDirectory;
        if (options.title) body.title = options.title;

        // Check if Make Remote toggle is checked
        const makeRemoteCheckbox = document.querySelector('#terminal-make-remote');
        if (makeRemoteCheckbox?.checked) {
            body.makeRemote = true;
        }

        try {
            const response = await this.app.apiCall('/api/v1/terminal/start', 'POST', body);
            if (!response.hasActiveSession) {
                this.app.showError('Failed to start terminal session');
                return;
            }
        } catch (error) {
            this.app.showError('Failed to start terminal: ' + error.message);
            return;
        }

        await this.showActiveTerminal(container);
        const displayName = options.title || options.cli;
        this.app.showToast('Terminal Started', `Launching ${displayName}...`, 'success');
    }

    async showActiveTerminal(container) {
        const placeholder = container.querySelector('#terminal-placeholder');
        const terminalContainer = container.querySelector('#terminal-container');
        const terminalElement = container.querySelector('#terminal-element');
        const startBtn = container.querySelector('#terminal-start-btn');
        const reconnectBtn = container.querySelector('#terminal-reconnect-btn');
        const stopBtn = container.querySelector('#terminal-stop-btn');
        const cliSelect = container.querySelector('#terminal-cli-select');
        const statusBadge = container.querySelector('#terminal-status-badge');

        if (placeholder) placeholder.style.display = 'none';
        if (terminalContainer) terminalContainer.style.display = 'block';
        if (startBtn) startBtn.classList.add('d-none');
        if (reconnectBtn) reconnectBtn.classList.add('d-none');
        if (stopBtn) stopBtn.classList.remove('d-none');
        if (cliSelect) cliSelect.disabled = true;
        if (statusBadge) {
            statusBadge.textContent = 'Connected';
            statusBadge.classList.remove('bg-secondary', 'bg-warning');
            statusBadge.classList.add('bg-success');
        }

        // Connect to the terminal - CLI is already running in the PTY
        await this.connect(terminalElement);
    }

    async reconnectTerminal(container) {
        const terminalElement = container.querySelector('#terminal-element');
        const reconnectBtn = container.querySelector('#terminal-reconnect-btn');
        const statusBadge = container.querySelector('#terminal-status-badge');

        // Disconnect existing connection
        this.disconnect();

        // Update status
        if (statusBadge) {
            statusBadge.textContent = 'Reconnecting...';
            statusBadge.classList.remove('bg-success');
            statusBadge.classList.add('bg-warning');
        }

        // Reconnect to the session
        await this.connect(terminalElement);

        // Hide reconnect button and update status on success
        if (reconnectBtn) reconnectBtn.classList.add('d-none');
        if (statusBadge) {
            statusBadge.textContent = 'Connected';
            statusBadge.classList.remove('bg-warning');
            statusBadge.classList.add('bg-success');
        }

        this.app.showToast('Terminal Reconnected', 'Successfully reconnected to terminal session', 'success');
    }

    async stopTerminal(container) {
        await this.stopSession();

        const placeholder = container.querySelector('#terminal-placeholder');
        const terminalContainer = container.querySelector('#terminal-container');
        const startBtn = container.querySelector('#terminal-start-btn');
        const reconnectBtn = container.querySelector('#terminal-reconnect-btn');
        const stopBtn = container.querySelector('#terminal-stop-btn');
        const cliSelect = container.querySelector('#terminal-cli-select');
        const statusBadge = container.querySelector('#terminal-status-badge');

        if (placeholder) placeholder.style.display = 'block';
        if (terminalContainer) terminalContainer.style.display = 'none';
        if (startBtn) startBtn.classList.remove('d-none');
        if (reconnectBtn) reconnectBtn.classList.add('d-none');
        if (stopBtn) stopBtn.classList.add('d-none');
        if (cliSelect) cliSelect.disabled = false;
        if (statusBadge) {
            statusBadge.textContent = 'Not Started';
            statusBadge.classList.remove('bg-success');
            statusBadge.classList.add('bg-secondary');
        }

        this.app.showToast('Terminal Stopped', 'Terminal session ended', 'info');
    }
}
