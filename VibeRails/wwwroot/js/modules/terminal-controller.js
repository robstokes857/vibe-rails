// Terminal Controller - Manages embedded xterm.js terminal
export class TerminalController {
    constructor(app) {
        this.app = app;
        this.terminal = null;
        this.socket = null;
        this.isConnected = false;
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

    async startSession() {
        try {
            const response = await this.app.apiCall('/api/v1/terminal/start', 'POST', {});

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

    async connect(terminalElement, cliToLaunch = null) {
        if (this.isConnected) {
            return;
        }

        // Initialize xterm.js with Unicode support
        this.terminal = new Terminal({
            cols: 120,
            rows: 30,
            cursorBlink: true,
            fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
            fontSize: 14,
            allowProposedApi: true,
            unicodeVersion: '11',
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

        this.terminal.open(terminalElement);
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

        this.socket.onopen = () => {
            this.isConnected = true;
            console.log('Terminal WebSocket connected');

            // Auto-launch CLI after connection is established
            if (cliToLaunch) {
                setTimeout(() => {
                    this.socket.send(cliToLaunch + '\r');
                }, 300);
            }
        };

        this.socket.onmessage = async (event) => {
            // Handle binary data properly for Unicode
            if (event.data instanceof Blob) {
                const buffer = await event.data.arrayBuffer();
                const text = new TextDecoder('utf-8').decode(buffer);
                this.terminal.write(text);
            } else {
                this.terminal.write(event.data);
            }
        };

        this.socket.onclose = () => {
            this.isConnected = false;
            this.terminal.write('\r\n\x1b[33m[Terminal disconnected]\x1b[0m\r\n');
            console.log('Terminal WebSocket closed');
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
    }

    disconnect() {
        if (this.socket) {
            this.socket.close();
            this.socket = null;
        }
        if (this.terminal) {
            this.terminal.dispose();
            this.terminal = null;
        }
        this.isConnected = false;
    }

    renderTerminalPanel() {
        return `
            <div class="card mb-4" id="terminal-panel">
                <div class="card-header d-flex justify-content-between align-items-center">
                    <div class="d-flex align-items-center gap-2">
                        <img src="assets/img/icons/terminal-solid-full.svg" alt="Terminal" style="height: 20px; width: 20px;">
                        <span class="fw-bold">Terminal</span>
                        <span class="badge bg-secondary" id="terminal-status-badge">Not Started</span>
                    </div>
                    <div class="d-flex gap-2 align-items-center" id="terminal-actions">
                        <select class="form-select form-select-sm" id="terminal-cli-select" style="width: auto;">
                            <option value="claude" selected>Claude</option>
                            <option value="codex">Codex</option>
                            <option value="gemini">Gemini</option>
                        </select>
                        <button class="btn btn-sm btn-primary" id="terminal-start-btn">
                            Start
                        </button>
                        <button class="btn btn-sm btn-danger d-none" id="terminal-stop-btn">
                            Stop
                        </button>
                    </div>
                </div>
                <div class="card-body p-0" id="terminal-container" style="display: none; min-height: 400px;">
                    <div id="terminal-element" style="height: 100%; min-height: 400px;"></div>
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

        const startBtn = container.querySelector('#terminal-start-btn');
        const stopBtn = container.querySelector('#terminal-stop-btn');
        const cliSelect = container.querySelector('#terminal-cli-select');

        if (startBtn) {
            startBtn.addEventListener('click', async () => {
                const selection = cliSelect?.value || 'base:claude';
                await this.startTerminal(container, selection);
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
            await this.showActiveTerminal(container, null); // Don't auto-launch CLI on restore
        }
    }

    async startTerminal(container, selection) {
        const success = await this.startSession();
        if (!success) return;

        let command = null;
        let displayName = null;

        if (selection.startsWith('base:')) {
            // Base CLI: just the CLI name (sends directly to PTY shell)
            command = selection.replace('base:', '');
            displayName = command.charAt(0).toUpperCase() + command.slice(1);
        } else if (selection.startsWith('env:')) {
            // Custom environment: env:id:cli — ask backend for the bootstrap command
            const parts = selection.split(':');
            const envId = parseInt(parts[1]);
            const cliType = parts[2];
            const env = this.app.data.environments.find(e => e.id === envId);
            const envName = env?.name || '';
            displayName = `${envName} (${cliType})`;

            try {
                const params = new URLSearchParams({ cli: cliType });
                if (envName) params.append('environmentName', envName);
                const result = await this.app.apiCall(
                    `/api/v1/terminal/bootstrap-command?${params.toString()}`, 'GET');
                command = result.command;
            } catch (error) {
                console.error('Failed to get bootstrap command:', error);
                command = cliType; // Fallback to base CLI
            }
        }

        await this.showActiveTerminal(container, command);
        this.app.showToast('Terminal Started', `Launching ${displayName}...`, 'success');
    }

    async showActiveTerminal(container, command) {
        const placeholder = container.querySelector('#terminal-placeholder');
        const terminalContainer = container.querySelector('#terminal-container');
        const terminalElement = container.querySelector('#terminal-element');
        const startBtn = container.querySelector('#terminal-start-btn');
        const stopBtn = container.querySelector('#terminal-stop-btn');
        const cliSelect = container.querySelector('#terminal-cli-select');
        const statusBadge = container.querySelector('#terminal-status-badge');

        if (placeholder) placeholder.style.display = 'none';
        if (terminalContainer) terminalContainer.style.display = 'block';
        if (startBtn) startBtn.classList.add('d-none');
        if (stopBtn) stopBtn.classList.remove('d-none');
        if (cliSelect) cliSelect.disabled = true;
        if (statusBadge) {
            statusBadge.textContent = 'Connected';
            statusBadge.classList.remove('bg-secondary');
            statusBadge.classList.add('bg-success');
        }

        // Connect to the terminal and auto-launch the command
        await this.connect(terminalElement, command);
    }

    async stopTerminal(container) {
        await this.stopSession();

        const placeholder = container.querySelector('#terminal-placeholder');
        const terminalContainer = container.querySelector('#terminal-container');
        const startBtn = container.querySelector('#terminal-start-btn');
        const stopBtn = container.querySelector('#terminal-stop-btn');
        const cliSelect = container.querySelector('#terminal-cli-select');
        const statusBadge = container.querySelector('#terminal-status-badge');

        if (placeholder) placeholder.style.display = 'block';
        if (terminalContainer) terminalContainer.style.display = 'none';
        if (startBtn) startBtn.classList.remove('d-none');
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
