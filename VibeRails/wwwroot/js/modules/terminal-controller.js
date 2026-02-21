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
        this.lockLayoutHandler = null;
        this.lockedPanel = null;
        this.lockScrollTop = 0;
        this.isScrollLocked = false;
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
            cursorBlink: false,
            fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
            fontSize: 14,
            allowProposedApi: true,
            unicodeVersion: '11',
            disableStdin: false,
            cursorStyle: 'block',
            cursorInactiveStyle: 'none',
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
        this.scheduleFitPasses();
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

        // Auth is handled by cookie (browser) or monkey-patched WebSocket (VSCode webview).
        // No need to put the session token in the URL.
        const socket = new WebSocket(wsUrl);
        socket.binaryType = 'arraybuffer';
        this.socket = socket;

        socket.onopen = () => {
            if (this.socket !== socket) return;
            this.isConnected = true;
            this.fitAndSyncTerminal();
            this.scheduleFitPasses();
            console.log('Terminal WebSocket connected - CLI is already running');
        };

        socket.onmessage = (event) => {
            if (this.socket !== socket) return;
            this.writeTerminalData(event.data);
        };

        socket.onclose = (event) => {
            if (this.socket !== socket) return;
            this.isConnected = false;
            if (this.terminal) {
                const reason = event.reason || 'Terminal disconnected';
                const color = reason.includes('taken over') ? '33' : '90';
                this.writeTerminalData(`\r\n\x1b[${color}m[${reason}]\x1b[0m\r\n`);
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

        socket.onerror = (error) => {
            if (this.socket !== socket) return;
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

    writeTerminalData(data) {
        if (!this.terminal) return;

        if (typeof data === 'string') {
            this.terminal.write(data);
        } else {
            this.terminal.write(new Uint8Array(data));
        }

        // Keep the terminal pinned to latest output.
        this.terminal.scrollToBottom();
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

    scheduleFitPasses() {
        if (!this.terminal) return;

        requestAnimationFrame(() => {
            this.fitAndSyncTerminal();
            setTimeout(() => this.fitAndSyncTerminal(), 120);
        });
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
        this.disableLockedLayout(this.lockedPanel);
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

    removeLockLayoutHandler() {
        if (this.lockLayoutHandler) {
            window.removeEventListener('resize', this.lockLayoutHandler);
            this.lockLayoutHandler = null;
        }
    }

    cleanupStaleLockState() {
        if (this.lockedPanel && !document.body.contains(this.lockedPanel)) {
            this.removeLockLayoutHandler();
            this.lockedPanel = null;
            this.setPageScrollLock(false);
        }
    }

    setPageScrollLock(isLocked) {
        if (isLocked) {
            if (this.isScrollLocked) return;
            this.lockScrollTop = window.scrollY || window.pageYOffset || 0;
            document.body.classList.add('terminal-scroll-locked');
            document.body.style.top = `-${this.lockScrollTop}px`;
            this.isScrollLocked = true;
            return;
        }

        if (!this.isScrollLocked && !document.body.classList.contains('terminal-scroll-locked')) {
            return;
        }

        document.body.classList.remove('terminal-scroll-locked');
        document.body.style.removeProperty('top');
        const restoreTop = Number.isFinite(this.lockScrollTop) ? this.lockScrollTop : 0;
        this.lockScrollTop = 0;
        this.isScrollLocked = false;
        window.scrollTo(0, restoreTop);
    }

    resetLayoutStateForNavigation() {
        this.disableLockedLayout(this.lockedPanel);
        this.cleanupStaleLockState();
    }

    updateLockedPanelPosition(panel) {
        if (!panel) return;
        const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
        const panelRect = panel.getBoundingClientRect();
        const cardHeader = panel.querySelector('.card-header');
        const cardHeaderHeight = cardHeader
            ? Math.round(cardHeader.getBoundingClientRect().height)
            : 0;

        const topOffset = Math.max(8, Math.round(panelRect.top));
        const bottomPadding = 12;
        const availableShellHeight = Math.max(
            220,
            Math.round(viewportHeight - topOffset - bottomPadding - cardHeaderHeight)
        );

        panel.style.setProperty('--terminal-lock-max-height', `${availableShellHeight}px`);
    }

    enableLockedLayout(panel) {
        if (!panel) return;

        panel.scrollIntoView({ block: 'center', inline: 'nearest' });
        this.removeLockLayoutHandler();
        panel.classList.remove('terminal-minimized');
        panel.classList.add('terminal-locked');
        this.lockedPanel = panel;

        this.updateLockedPanelPosition(panel);
        this.setPageScrollLock(true);

        this.lockLayoutHandler = () => {
            if (!this.lockedPanel || !document.body.contains(this.lockedPanel)) {
                this.removeLockLayoutHandler();
                this.lockedPanel = null;
                this.setPageScrollLock(false);
                return;
            }
            this.updateLockedPanelPosition(this.lockedPanel);
            this.scheduleFitPasses();
        };

        window.addEventListener('resize', this.lockLayoutHandler);
        this.updateLockedPanelPosition(panel);
        this.scheduleFitPasses();
    }

    disableLockedLayout(panel) {
        const targetPanel = panel || this.lockedPanel;

        if (targetPanel) {
            targetPanel.classList.remove('terminal-locked');
            targetPanel.style.removeProperty('--terminal-lock-max-height');
        }

        if (this.lockedPanel === targetPanel) {
            this.lockedPanel = null;
        }

        this.removeLockLayoutHandler();
        this.setPageScrollLock(false);
    }

    getTerminalPanel(container) {
        return container?.querySelector('#terminal-panel');
    }

    setWindowTitle(container, titleText) {
        const title = container?.querySelector('#terminal-window-title');
        if (title) {
            title.textContent = titleText || 'Web Terminal';
        }
    }

    updateWindowControlState(container) {
        const panel = this.getTerminalPanel(container);
        if (!panel) return;

        const isMinimized = panel.classList.contains('terminal-minimized');
        const isExpanded = panel.classList.contains('terminal-expanded');
        const isLocked = panel.classList.contains('terminal-locked');
        const isFocusView = !!container?.closest('[data-view="terminal-focus"]');

        const closeBtn = container.querySelector('#terminal-close-dot');
        const minimizeBtn = container.querySelector('#terminal-minimize-dot');
        const maximizeBtn = container.querySelector('#terminal-maximize-dot');
        const lockBtn = container.querySelector('#terminal-lock-btn');
        const focusBtn = container.querySelector('#terminal-popout-btn');

        const setControlLabel = (button, text) => {
            const label = button?.querySelector('.terminal-control-text');
            if (label) label.textContent = text;
        };

        if (closeBtn) {
            closeBtn.setAttribute('aria-pressed', 'false');
            closeBtn.title = 'Stop and close this terminal session';
            setControlLabel(closeBtn, 'Close');
        }

        if (minimizeBtn) {
            minimizeBtn.classList.toggle('active', isMinimized);
            minimizeBtn.setAttribute('aria-pressed', String(isMinimized));
            minimizeBtn.title = isMinimized
                ? 'Restore terminal panel height'
                : 'Minimize terminal panel';
            setControlLabel(minimizeBtn, isMinimized ? 'Restore' : 'Minimize');
        }

        if (maximizeBtn) {
            maximizeBtn.classList.toggle('active', isExpanded);
            maximizeBtn.setAttribute('aria-pressed', String(isExpanded));
            maximizeBtn.title = isExpanded
                ? 'Restore terminal to default size'
                : 'Expand terminal panel';
            setControlLabel(maximizeBtn, isExpanded ? 'Normal Size' : 'Expand');
        }

        if (lockBtn) {
            lockBtn.classList.toggle('active', isLocked);
            lockBtn.setAttribute('aria-pressed', String(isLocked));
            lockBtn.title = isLocked
                ? 'Unlock terminal from sticky focus mode'
                : 'Lock terminal in sticky focus mode while scrolling';
            setControlLabel(lockBtn, isLocked ? 'Unlock Focus' : 'Lock Focus');
        }

        if (focusBtn) {
            focusBtn.classList.remove('active');
            focusBtn.setAttribute('aria-pressed', 'false');
            focusBtn.title = isFocusView
                ? 'Return to dashboard terminal section'
                : 'Open terminal in focused page view';
            setControlLabel(focusBtn, isFocusView ? 'Back to Dashboard' : 'Focus View');
        }
    }

    resetWindowModes(container) {
        const panel = this.getTerminalPanel(container);
        if (!panel) return;

        panel.classList.remove('terminal-minimized', 'terminal-expanded');
        this.disableLockedLayout(panel);
        this.updateWindowControlState(container);
    }

    toggleMinimize(container) {
        const panel = this.getTerminalPanel(container);
        if (!panel) return;

        if (panel.classList.contains('terminal-minimized')) {
            panel.classList.remove('terminal-minimized');
        } else {
            panel.classList.remove('terminal-expanded');
            panel.classList.add('terminal-minimized');
        }

        this.updateWindowControlState(container);
        this.scheduleFitPasses();
    }

    toggleExpand(container) {
        const panel = this.getTerminalPanel(container);
        if (!panel) return;

        if (panel.classList.contains('terminal-expanded')) {
            panel.classList.remove('terminal-expanded');
        } else {
            panel.classList.remove('terminal-minimized');
            panel.classList.add('terminal-expanded');
        }

        this.updateWindowControlState(container);
        this.scheduleFitPasses();
    }

    toggleLock(container) {
        const panel = this.getTerminalPanel(container);
        if (!panel) return;

        if (panel.classList.contains('terminal-locked')) {
            this.disableLockedLayout(panel);
        } else {
            this.enableLockedLayout(panel);
        }

        this.updateWindowControlState(container);
        this.scheduleFitPasses();
    }

    parseSelectionMetadata(selection) {
        if (!selection || !selection.startsWith('env:')) {
            return { preselectedEnvId: null };
        }

        const parts = selection.split(':');
        const envId = Number.parseInt(parts[1], 10);
        return {
            preselectedEnvId: Number.isFinite(envId) ? envId : null
        };
    }

    openTerminalInNewWindow(container) {
        if (container?.closest('[data-view="terminal-focus"]')) {
            this.app.goBack();
            return;
        }

        const cliSelect = container?.querySelector('#terminal-cli-select');
        const selection = cliSelect?.value || 'base:claude';
        const { preselectedEnvId } = this.parseSelectionMetadata(selection);
        const focusData = {
            preselectedEnvId,
            preferredSelection: selection
        };

        this.app.navigate('terminal-focus', focusData);
    }

    async loadTerminalFocusView(data = {}) {
        await this.app.refreshDashboardData();

        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = `
            <div class="view terminal-focus-view" data-view="terminal-focus">
                <div class="terminal-focus-topbar">
                    <button class="btn btn-outline-primary d-inline-flex align-items-center gap-2" type="button" data-action="go-back">
                        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                            <path fill-rule="evenodd" d="M15 8a.5.5 0 0 0-.5-.5H2.707l3.147-3.146a.5.5 0 1 0-.708-.708l-4 4a.5.5 0 0 0 0 .708l4 4a.5.5 0 0 0 .708-.708L2.707 8.5H14.5A.5.5 0 0 0 15 8"/>
                        </svg>
                        Back
                    </button>
                    <div class="terminal-focus-heading">
                        <h2 class="mb-1">Web Terminal Focus</h2>
                        <p class="text-muted mb-0">Focused terminal workspace with only essential controls.</p>
                    </div>
                </div>
                <div class="terminal-focus-body" data-terminal-focus-content></div>
            </div>
        `;

        const root = content.querySelector('[data-view="terminal-focus"]');
        const terminalContent = root?.querySelector('[data-terminal-focus-content]');
        if (!terminalContent) return;

        terminalContent.innerHTML = this.renderTerminalPanel({ focusView: true });
        await this.bindTerminalActions(terminalContent, data.preselectedEnvId || null);

        if (typeof data.preferredSelection === 'string' && data.preferredSelection.length > 0) {
            const cliSelect = terminalContent.querySelector('#terminal-cli-select');
            if (cliSelect && Array.from(cliSelect.options).some(opt => opt.value === data.preferredSelection)) {
                cliSelect.value = data.preferredSelection;
            }
        }
    }

    renderTerminalPanel(options = {}) {
        const isFocusView = options.focusView === true;
        const lockButtonHtml = isFocusView ? '' : `
                            <button type="button" class="terminal-control-btn icon-btn" id="terminal-lock-btn" title="Lock terminal in sticky focus mode" aria-label="Lock terminal focus mode">
                                <svg xmlns="http://www.w3.org/2000/svg" width="13" height="13" fill="currentColor" viewBox="0 0 16 16">
                                    <path d="M8 1a3 3 0 0 0-3 3v2H4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-1V4a3 3 0 0 0-3-3m2 5H6V4a2 2 0 1 1 4 0z"/>
                                </svg>
                                <span class="terminal-control-text">Lock Focus</span>
                            </button>
        `;

        return `
            <div class="card ${isFocusView ? 'terminal-page-mode terminal-expanded terminal-focus-card' : 'mb-4'}" id="terminal-panel">
                <div class="card-header d-flex justify-content-between align-items-center gap-3 flex-wrap">
                    <div class="terminal-header-main">
                        <div class="d-flex align-items-center gap-2 flex-wrap">
                            <span class="card-title d-inline-flex align-items-center gap-2">
                                <svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" fill="currentColor" viewBox="0 0 576 512" style="opacity: 0.85;">
                                    <path d="M9.4 86.6C-3.1 74.1-3.1 53.9 9.4 41.4s32.8-12.5 45.3 0l192 192c12.5 12.5 12.5 32.8 0 45.3l-192 192c-12.5 12.5-32.8 12.5-45.3 0s-12.5-32.8 0-45.3L178.7 256 9.4 86.6zM256 416l288 0c17.7 0 32 14.3 32 32s-14.3 32-32 32l-288 0c-17.7 0-32-14.3-32-32s14.3-32 32-32z"/>
                                </svg>
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
                        <button class="btn btn-sm btn-outline-info d-inline-flex align-items-center gap-1" id="terminal-start-btn">
                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                <path d="M6.79 5.093A.5.5 0 0 0 6 5.5v5a.5.5 0 0 0 .79.407l3.5-2.5a.5.5 0 0 0 0-.814z"/>
                                <path d="M0 4a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H2a2 2 0 0 1-2-2zm2-1a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V4a1 1 0 0 0-1-1z"/>
                            </svg>
                            <span>Start</span>
                        </button>
                        <button class="btn btn-sm btn-outline-light d-none d-inline-flex align-items-center gap-1" id="terminal-reconnect-btn" title="Reconnect to active session">
                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                <path fill-rule="evenodd" d="M8 3a5 5 0 1 0 4.546 2.914.5.5 0 0 1 .908-.417A6 6 0 1 1 8 2z"/>
                                <path d="M8 4.466V.534a.25.25 0 0 1 .41-.192l2.36 1.966c.12.1.12.284 0 .384L8.41 4.658A.25.25 0 0 1 8 4.466"/>
                            </svg>
                            <span>Reconnect</span>
                        </button>
                        <button class="btn btn-sm btn-outline-warning d-none d-inline-flex align-items-center gap-1" id="terminal-stop-btn">
                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                <path d="M5 3.5h6A1.5 1.5 0 0 1 12.5 5v6a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 11V5A1.5 1.5 0 0 1 5 3.5"/>
                            </svg>
                            <span>Stop</span>
                        </button>
                    </div>
                </div>
                <div class="terminal-window-shell">
                    <div class="terminal-window-header">
                        <div class="terminal-window-controls terminal-window-controls-left">
                            <button type="button" class="terminal-control-dot close" id="terminal-close-dot" title="Close terminal" aria-label="Close terminal"></button>
                            <button type="button" class="terminal-control-dot minimize" id="terminal-minimize-dot" title="Minimize terminal" aria-label="Minimize terminal"></button>
                            <button type="button" class="terminal-control-dot maximize" id="terminal-maximize-dot" title="Expand terminal" aria-label="Expand terminal"></button>
                        </div>
                        <div class="terminal-window-title" id="terminal-window-title">${isFocusView ? 'Web Terminal Focus View' : 'Web Terminal'}</div>
                        <div class="terminal-window-controls terminal-window-controls-right">
                            ${lockButtonHtml}
                            <button type="button" class="terminal-control-btn icon-btn" id="terminal-popout-btn" title="${isFocusView ? 'Return to dashboard terminal section' : 'Open terminal in focused page view'}" aria-label="${isFocusView ? 'Back to dashboard terminal view' : 'Open terminal focus page'}">
                                <svg xmlns="http://www.w3.org/2000/svg" width="13" height="13" fill="currentColor" viewBox="0 0 16 16">
                                    <path d="M6 3a2 2 0 0 0-2 2v7a1 1 0 0 0 1 1h7a2 2 0 0 0 2-2V6h-1v5a1 1 0 0 1-1 1H5V5a1 1 0 0 1 1-1z"/>
                                    <path d="M8.5 1a.5.5 0 0 0 0 1h4.793L6.146 9.146a.5.5 0 1 0 .708.708L14 2.707V7.5a.5.5 0 0 0 1 0V1z"/>
                                </svg>
                                <span class="terminal-control-text">${isFocusView ? 'Back to Dashboard' : 'Focus View'}</span>
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
        this.cleanupStaleLockState();

        // Populate selector with environments
        await this.populateTerminalSelector(container, preselectedEnvId);

        const startBtn = container.querySelector('#terminal-start-btn');
        const reconnectBtn = container.querySelector('#terminal-reconnect-btn');
        const stopBtn = container.querySelector('#terminal-stop-btn');
        const cliSelect = container.querySelector('#terminal-cli-select');
        const closeDot = container.querySelector('#terminal-close-dot');
        const minimizeDot = container.querySelector('#terminal-minimize-dot');
        const maximizeDot = container.querySelector('#terminal-maximize-dot');
        const lockBtn = container.querySelector('#terminal-lock-btn');
        const popoutBtn = container.querySelector('#terminal-popout-btn');

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

        if (closeDot) {
            closeDot.addEventListener('click', async () => {
                await this.stopTerminal(container);
            });
        }

        if (minimizeDot) {
            minimizeDot.addEventListener('click', () => {
                this.toggleMinimize(container);
            });
        }

        if (maximizeDot) {
            maximizeDot.addEventListener('click', () => {
                this.toggleExpand(container);
            });
        }

        if (lockBtn) {
            lockBtn.addEventListener('click', () => {
                this.toggleLock(container);
            });
        }

        if (popoutBtn) {
            popoutBtn.addEventListener('click', () => {
                this.openTerminalInNewWindow(container);
            });
        }

        this.updateWindowControlState(container);

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

        await this.showActiveTerminal(container, `${displayName} Terminal`);
        this.app.showToast('Terminal Started', `Launching ${displayName}...`, 'success');
    }

    async startTerminalWithOptions(options, container) {
        // options: { cli, environmentName?, workingDirectory?, title? }
        const body = { cli: options.cli };
        if (options.environmentName) body.environmentName = options.environmentName;
        if (options.workingDirectory) body.workingDirectory = options.workingDirectory;
        if (options.title) body.title = options.title;

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

        const displayName = options.title || options.cli;
        await this.showActiveTerminal(container, `${displayName} Terminal`);
        this.app.showToast('Terminal Started', `Launching ${displayName}...`, 'success');
    }

    async showActiveTerminal(container, windowTitle = null) {
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

        this.setWindowTitle(container, windowTitle || 'Active Terminal');
        this.updateWindowControlState(container);

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

    setTerminalUiNotStarted(container) {
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
            statusBadge.classList.remove('bg-success', 'bg-warning');
            statusBadge.classList.add('bg-secondary');
        }

        this.resetWindowModes(container);
        this.setWindowTitle(container, 'Web Terminal');
    }

    async stopTerminal(container) {
        await this.stopSession();
        this.setTerminalUiNotStarted(container);

        this.app.showToast('Terminal Stopped', 'Terminal session ended', 'info');
    }
}
