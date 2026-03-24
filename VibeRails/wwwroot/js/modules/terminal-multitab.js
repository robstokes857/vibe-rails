import { VibeTerminal } from './vibe-terminal.js';
import { ChatHistorySidebar } from './chat-history-sidebar.js';
import {
    buildLlmSelectionValue,
    parseLlmSelection,
    populateLlmSelectionSelect
} from './utils.js';

const RESIZE_PREFIX = '__resize__:';
const DEFAULT_SELECTION = null;
const ACTIVE_TAB_KEY = 'viberails_terminal_active_tab_id';
const TAB_SELECTION_PREFIX = 'viberails_terminal_tab_selection_';
const TAB_TITLE_PREFIX = 'viberails_terminal_tab_title_';
const TAB_META_PREFIX = 'viberails_terminal_tab_meta_';

function lower(value) {
    return (value || '').toString().trim().toLowerCase();
}

function cleanString(value) {
    if (typeof value !== 'string') return null;
    const trimmed = value.trim();
    return trimmed.length > 0 ? trimmed : null;
}

function shorten(text, max = 26) {
    if (!text) return '';
    if (text.length <= max) return text;
    return `${text.slice(0, max - 1)}\u2026`;
}

class TerminalTab {
    getSafeTabTokenForWebSocket(tabToken) {
        if (!tabToken || typeof tabToken !== 'string') {
            return '';
        }

        return tabToken
            .replace(/\+/g, '-')
            .replace(/\//g, '_')
            .replace(/=/g, '');
    }

    constructor(manager, state) {
        this.manager = manager;
        this.state = state;
        this.vibeTerminal = null;
        this.terminal = null;
        this.socket = null;
        this.onDataDispose = null;
        this.inputFocusHandler = null;
        this.isActive = false;
        this.lastResizeSignature = null;
        this._initialConnectActive = false;
        this._connectFocusTimeouts = [];
    }

    hasOpenSocket() {
        return this.socket && this.socket.readyState === WebSocket.OPEN;
    }

    ensureTerminal() {
        if (this.vibeTerminal || !this.state.ui?.terminalElement) {
            return;
        }

        this.vibeTerminal = new VibeTerminal({
            outputEl: this.state.ui.terminalElement,
            cols: 120,
            rows: 40,
            disableStdin: false,
            desktopFontSize: 14,
            mobileFontSize: 15,
            desktopLineHeight: 1.12,
            mobileLineHeight: 1.2
        });
        this.vibeTerminal.onFitChange = () => this.sendResizeToPty();
        this.vibeTerminal.onProgress = (progress) => this.manager.updateTabProgress(this.state.id, progress);
        this.terminal = this.vibeTerminal.terminal;
        this.manager.applySavedTerminalSettingsForTab(this.state.id);

        this.configureInputTarget();
        this.installInputFocusHandlers();

        this.onDataDispose = this.vibeTerminal.onData((data) => {
            if (this.socket && this.socket.readyState === WebSocket.OPEN) {
                this.socket.send(data);
            }
        });

        this.vibeTerminal.addCustomKeyEventHandler((event) => {
            const isModifiedEnter = ((event.key || '') === 'Enter' || event.keyCode === 13)
                && !event.altKey
                && !event.metaKey
                && (event.shiftKey || event.ctrlKey);

            if (!isModifiedEnter || !this.vibeTerminal?.isBracketedPasteModeEnabled()) {
                return true;
            }

            if (event.type !== 'keydown' && event.type !== 'keypress') {
                return true;
            }

            // xterm invokes custom handlers for both keydown and keypress.
            // Paste once on keydown, then suppress the follow-up keypress so
            // Enter does not still submit the prompt.
            event.preventDefault();
            event.stopPropagation();

            if (event.type === 'keydown'
                && this.socket
                && this.socket.readyState === WebSocket.OPEN) {
                const payload = this.vibeTerminal?.createBracketedPastePayload('\n');
                if (payload) {
                    this.socket.send(payload);
                }
            }

            return false;
        });

        this.vibeTerminal.attachClipboardPaste((text) => {
            if (this.socket && this.socket.readyState === WebSocket.OPEN) {
                const payload = this.vibeTerminal?.createBracketedPastePayload(text) ?? text;
                this.socket.send(payload);
            }
        });

        if (this.isActive) {
            this.setupResizeHandling();
            this.fitAndSyncTerminal();
            this.scheduleFitPasses();
        }
    }

    writeData(data) {
        this.vibeTerminal?.write(data);
    }

    openSearch() {
        if (!this.vibeTerminal) {
            return false;
        }

        return this.vibeTerminal.openSearchPrompt();
    }

    getHelperTextarea() {
        return this.vibeTerminal?.textarea
            || this.state.ui?.terminalElement?.querySelector('.xterm-helper-textarea')
            || null;
    }

    configureInputTarget() {
        const input = this.getHelperTextarea();
        if (!input) {
            return;
        }

        // Mobile keyboards often inject autocorrect/capitalization into shell input.
        input.setAttribute('autocapitalize', 'off');
        input.setAttribute('autocomplete', 'off');
        input.setAttribute('autocorrect', 'off');
        input.setAttribute('spellcheck', 'false');
        input.setAttribute('enterkeyhint', 'enter');
        input.spellcheck = false;
    }

    focusInput() {
        if (!this.vibeTerminal) {
            return;
        }

        this.vibeTerminal.focus();
        this.configureInputTarget();

        const input = this.getHelperTextarea();
        if (!input) {
            return;
        }

        try {
            input.focus({ preventScroll: true });
        } catch {
            input.focus();
        }
    }

    installInputFocusHandlers() {
        if (this.inputFocusHandler || !this.state.ui?.terminalElement) {
            return;
        }

        const element = this.state.ui.terminalElement;
        this.inputFocusHandler = () => {
            this.focusInput();
            this.scheduleFitPasses();
        };

        element.addEventListener('click', this.inputFocusHandler);
        element.addEventListener('pointerdown', this.inputFocusHandler, { passive: true });
    }

    teardownInputFocusHandlers() {
        if (!this.inputFocusHandler || !this.state.ui?.terminalElement) {
            return;
        }

        const element = this.state.ui.terminalElement;
        element.removeEventListener('click', this.inputFocusHandler);
        element.removeEventListener('pointerdown', this.inputFocusHandler);
        this.inputFocusHandler = null;
    }

    disconnectSocketOnly() {
        this.clearConnectFocusTimeouts();
        this._initialConnectActive = false;
        if (!this.socket) {
            return;
        }

        try {
            this.socket.close();
        } catch (e) {
            // no-op
        }

        this.socket = null;
        this.lastResizeSignature = null;
    }

    disposeTerminalInstance() {
        if (!this.vibeTerminal) {
            return;
        }

        this.vibeTerminal.dispose();
        this.vibeTerminal = null;
        this.terminal = null;

        if (this.onDataDispose) {
            try { this.onDataDispose(); } catch (e) { /* no-op */ }
            this.onDataDispose = null;
        }
    }

    clearConnectFocusTimeouts() {
        while (this._connectFocusTimeouts.length > 0) {
            const timeoutId = this._connectFocusTimeouts.pop();
            clearTimeout(timeoutId);
        }
    }

    scheduleConnectFocusPasses(socket) {
        if (!this.isActive || this.socket !== socket) {
            return;
        }

        this.clearConnectFocusTimeouts();
        for (const delay of [0, 50, 150, 300, 600, 1000]) {
            const timeoutId = window.setTimeout(() => {
                if (!this.isActive || this.socket !== socket) {
                    return;
                }

                this.focusInput();
            }, delay);
            this._connectFocusTimeouts.push(timeoutId);
        }
    }

    disconnect({ disposeTerminal = false, preserveStatus = false } = {}) {
        this.teardownResizeHandling();
        this.disconnectSocketOnly();

        if (disposeTerminal) {
            this.disposeTerminalInstance();
        }

        if (!preserveStatus) {
            this.state.status = this.state.hasActiveSession ? 'disconnected' : 'not-started';
        }
    }

    dispose() {
        this.disconnect({ disposeTerminal: true, preserveStatus: true });
        this.teardownInputFocusHandlers();
    }

    setActive(active) {
        this.isActive = active;

        if (!this.vibeTerminal) {
            return;
        }

        if (active) {
            this.setupResizeHandling();
            this.fitAndSyncTerminal();
            this.scheduleFitPasses();
            this.focusInput();
            return;
        }

        this.teardownResizeHandling();
    }

    sendResizeToPty({ force = false } = {}) {
        if (!this.isActive || !this.vibeTerminal || !this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return;
        }

        const cols = this.vibeTerminal.cols;
        const rows = this.vibeTerminal.rows;
        const signature = `${cols}x${rows}`;
        const signatureChanged = this.lastResizeSignature !== signature;
        if (!force && !signatureChanged) {
            return;
        }

        if (signatureChanged && this.shouldResetDisplayBeforeResize(cols, rows)) {
            // Terminal apps do not always erase stale right-edge cells when their
            // layout shrinks. Only clear on real shrink transitions; doing this
            // on the first post-connect sync causes visible cursor jumping.
            this.vibeTerminal.resetDisplayOnly();
        }

        this.lastResizeSignature = signature;
        this.socket.send(`${RESIZE_PREFIX}${cols},${rows}`);
    }

    getLastResizeGeometry() {
        if (typeof this.lastResizeSignature !== 'string') {
            return null;
        }

        const match = /^(\d+)x(\d+)$/.exec(this.lastResizeSignature);
        if (!match) {
            return null;
        }

        const cols = Number.parseInt(match[1], 10);
        const rows = Number.parseInt(match[2], 10);
        if (!Number.isFinite(cols) || !Number.isFinite(rows)) {
            return null;
        }

        return { cols, rows };
    }

    shouldResetDisplayBeforeResize(nextCols, nextRows) {
        if (!this.isActive || !this.state.hasActiveSession || !this.hasOpenSocket()) {
            return false;
        }

        const previous = this.getLastResizeGeometry();
        if (!previous) {
            return false;
        }

        return nextCols < previous.cols || nextRows < previous.rows;
    }

    fitAndSyncTerminal() {
        if (!this.vibeTerminal || !this.isActive) {
            return;
        }

        // Keep connect/activation deterministic: force a fit pass but emit
        // exactly one resize control frame to avoid redraw churn in TUIs.
        this.vibeTerminal.fit({ force: true, notify: false });
        this.sendResizeToPty({ force: true });
    }

    applyFontSize(size) {
        if (!this.vibeTerminal) {
            return;
        }

        const shouldReseedDisplay = this.isActive && this.hasOpenSocket();
        if (shouldReseedDisplay) {
            this.vibeTerminal.resetDisplayOnly();
            this.lastResizeSignature = null;
        }

        this.vibeTerminal.setFontSize(size, { fit: false });

        if (this.isActive) {
            this.fitAndSyncTerminal();
            this.scheduleFitPasses();
        }
    }

    applyFontFamily(family) {
        if (!this.vibeTerminal) {
            return;
        }

        const shouldReseedDisplay = this.isActive && this.hasOpenSocket();
        if (shouldReseedDisplay) {
            this.vibeTerminal.resetDisplayOnly();
            this.lastResizeSignature = null;
        }

        this.vibeTerminal.setFontFamily(family, { fit: false });

        if (this.isActive) {
            this.fitAndSyncTerminal();
            this.scheduleFitPasses();
        }
    }

    scheduleFitPasses() {
        if (!this.vibeTerminal || !this.isActive) {
            return;
        }

        this.vibeTerminal.scheduleFitPasses();
    }

    setupResizeHandling() {
        if (!this.vibeTerminal) {
            return;
        }

        this.vibeTerminal.startResizeHandling({
            debounceMs: 100,
            includeVisualViewport: true,
            includeVisualViewportScroll: false
        });
    }

    teardownResizeHandling() {
        this.vibeTerminal?.stopResizeHandling();
    }

    async connect() {
        if (!this.state.hasActiveSession) {
            return false;
        }

        this.disconnectSocketOnly();
        this.disposeTerminalInstance();
        this.ensureTerminal();
        this.clearConnectFocusTimeouts();
        this._initialConnectActive = true;
        this.lastResizeSignature = null;

        // Fit now that the container is visible (showTerminal was called before
        // connect in activateTab). This gives us the real cols/rows to send to
        // the backend so it can resize the PTY *before* sending the replay.
        if (this.vibeTerminal) {
            this.vibeTerminal.fit({ force: true, notify: false });
        }
        const preConnectCols = this.vibeTerminal?.cols;
        const preConnectRows = this.vibeTerminal?.rows;

        // Prime the resize signature with the pre-connect dimensions. The server
        // receives these in the WebSocket URL and resizes the PTY before sending
        // the replay, so the post-connect fit in onopen must not re-send the same
        // dimensions — a redundant __resize__ triggers SIGWINCH, causing TUI apps
        // to redraw right on top of the just-loaded replay (cursor flicker).
        this.lastResizeSignature = `${preConnectCols}x${preConnectRows}`;

        this.state.status = 'connecting';
        this.manager.updateUi();

        const tabToken = this.getSafeTabTokenForWebSocket(
            sessionStorage.getItem('viberails_tab')
        );
        const wsUrl = this.manager.getWebSocketUrl(this.state.id, preConnectCols, preConnectRows);
        const socket = new WebSocket(wsUrl, tabToken ? [tabToken] : []);
        socket.binaryType = 'arraybuffer';
        this.socket = socket;

        let opened = false;

        return await new Promise((resolve) => {
            socket.onopen = () => {
                if (this.socket !== socket) {
                    resolve(false);
                    return;
                }

                opened = true;
                this.state.status = 'connected';
                this.manager.updateUi();

                if (this.isActive) {
                    this.setupResizeHandling();
                    // Fit but only send resize if dimensions changed since the
                    // pre-connect fit (signature already primed above). Avoids
                    // a spurious SIGWINCH → TUI redraw over the loaded replay.
                    this.vibeTerminal.fit({ force: true, notify: false });
                    this.sendResizeToPty();
                    this.scheduleFitPasses();
                    this.focusInput();
                    this.scheduleConnectFocusPasses(socket);
                }

                resolve(true);
            };

            // Client-side write coalescing: buffer incoming binary frames and flush
            // them into a single term.write() call via queueMicrotask. This reduces
            // the number of distinct write() calls xterm.js sees, which in turn
            // reduces the number of rAF render cycles. TUI apps like Codex split
            // their screen updates across several small PTY writes (erase, ?2026h
            // sync-on, content, ?2026l sync-off) that arrive as separate WebSocket
            // frames a few ms apart. If each triggers its own term.write(), xterm.js
            // may render an intermediate torn state (e.g. erased cells visible for
            // one frame before ?2026h suppresses rendering). Batching them into one
            // write() processes the whole sequence atomically.
            let replayFocusDone = false;
            let pendingChunks = [];
            let flushScheduled = false;

            const flushPendingChunks = () => {
                flushScheduled = false;
                if (pendingChunks.length === 0) return;
                if (this.socket !== socket) { pendingChunks = []; return; }

                let data;
                if (pendingChunks.length === 1) {
                    data = pendingChunks[0];
                } else {
                    let total = 0;
                    for (const c of pendingChunks) total += c.byteLength;
                    const merged = new Uint8Array(total);
                    let offset = 0;
                    for (const c of pendingChunks) { merged.set(c, offset); offset += c.byteLength; }
                    data = merged;
                }
                pendingChunks = [];

                this.vibeTerminal?.write(data);

                // Re-focus once after the replay renders. Scheduled here (after write)
                // so the rAF fires AFTER xterm.js has processed and rendered the data.
                // Any page-level focus event that lands between socket.onopen and the
                // render rAF can steal focus, causing "paste works, keys don't".
                if (!replayFocusDone && this.isActive) {
                    replayFocusDone = true;
                    requestAnimationFrame(() => {
                        if (this.socket === socket && this.isActive) {
                            this.focusInput();
                        }
                    });
                }
            };

            socket.onmessage = (event) => {
                if (this.socket !== socket) return;

                pendingChunks.push(new Uint8Array(event.data));
                if (!flushScheduled) {
                    flushScheduled = true;
                    queueMicrotask(flushPendingChunks);
                }

                // Fire first-message init synchronously (before flush) — these actions
                // (fit, focus, resize) don't need the data to be written to xterm yet.
                if (this._initialConnectActive) {
                    this._initialConnectActive = false;
                    if (this.isActive) {
                        this.scheduleFitPasses();
                        this.focusInput();
                        this.scheduleConnectFocusPasses(socket);
                    }
                }
            };

            socket.onclose = (event) => {
                if (this.socket !== socket) return;

                this.teardownResizeHandling();
                this.clearConnectFocusTimeouts();
                this._initialConnectActive = false;
                this.socket = null;
                this.lastResizeSignature = null;

                this.state.status = this.state.hasActiveSession ? 'disconnected' : 'not-started';
                if (this.terminal && this.state.hasActiveSession) {
                    const reason = event.reason || 'Terminal disconnected';
                    const color = reason.includes('taken over') ? '33' : '90';
                    this.writeData(`\r\n\x1b[${color}m[${reason}]\x1b[0m\r\n`);
                }

                this.manager.updateUi();
                if (!opened) {
                    resolve(false);
                }
            };

            socket.onerror = () => {
                if (this.socket !== socket) return;
                this.manager.updateUi();
            };
        });
    }

    async startSession(body) {
        try {
            const response = await this.manager.app.apiCall(`/api/v1/terminal/tabs/${encodeURIComponent(this.state.id)}/start`, 'POST', body);
            this.state.hasActiveSession = response?.hasActiveSession === true;
            this.state.sessionId = response?.sessionId || null;
            if (!this.state.hasActiveSession) {
                this.state.status = 'not-started';
                this.manager.updateUi();
                return false;
            }

            this.state.status = 'connecting';
            this.manager.updateUi();
            this.disconnect({ disposeTerminal: true, preserveStatus: true });
            await this.connect();
            return true;
        } catch (error) {
            this.state.hasActiveSession = false;
            this.state.sessionId = null;
            this.state.status = 'not-started';
            this.manager.updateUi();
            this.manager.app.showError(`Failed to start terminal: ${error.message}`);
            return false;
        }
    }

    async stopSession() {
        try {
            await this.manager.app.apiCall(`/api/v1/terminal/tabs/${encodeURIComponent(this.state.id)}/stop`, 'POST');
        } catch (error) {
            this.manager.app.showError(`Failed to stop terminal: ${error.message}`);
            return false;
        }

        this.state.hasActiveSession = false;
        this.state.sessionId = null;
        this.state.status = 'not-started';
        this.disconnect({ disposeTerminal: true, preserveStatus: true });
        this.manager.updateUi();
        return true;
    }
}

class TerminalManager {
    constructor(app, container, options = {}) {
        this.app = app;
        this.container = container;
        this.options = options;
        this._destroyed = false;

        this.maxTabs = 8;
        this.tabs = new Map();
        this.tabOrder = [];
        this.activeTabId = null;

        this.panel = null;
        this.tabList = null;
        this.tabAdd = null;
        this.tabSelect = null;
        this.tabPanels = null;
        this.placeholder = null;
        this.terminalContainer = null;
        this.statusBadge = null;
        this.windowTitle = null;

        this.startBtn = null;
        this.reconnectBtn = null;
        this.stopBtn = null;
        this.controlsBar = null;
        this.headerSelect = null;
        this.closeDot = null;
        this.minimizeDot = null;
        this.maximizeDot = null;
        this.lockBtn = null;
        this.focusBtn = null;
        this.keyboardBtn = null;

        this.lockLayoutHandler = null;
        this.lockedPanel = null;
        this.lockScrollTop = 0;
        this.isScrollLocked = false;
        this.focusLayoutHandler = null;
        this.focusLayoutRaf = null;
        this.downloadMenu = null;
        this.downloadMenuDismissHandler = null;
        this._themeSwatches = [];
    }

    isDestroyed() {
        return this._destroyed;
    }

    _loadRendererPreference() {
        try {
            return localStorage.getItem('viberails_terminal_webgl') === 'true' ? 'webgl' : 'canvas';
        } catch {
            return 'canvas';
        }
    }

    _loadThemePreference() {
        try {
            return localStorage.getItem('viberails_terminal_theme') || null;
        } catch {
            return null;
        }
    }

    _loadCursorBlink() {
        try {
            const val = localStorage.getItem('viberails_terminal_cursorBlink');
            return val === null ? true : val === 'true';
        } catch {
            return true;
        }
    }

    _loadCursorStyle() {
        try {
            return localStorage.getItem('viberails_terminal_cursorStyle') || 'block';
        } catch {
            return 'block';
        }
    }

    _loadCursorInactiveStyle() {
        try {
            return localStorage.getItem('viberails_terminal_cursorInactiveStyle') || 'outline';
        } catch {
            return 'outline';
        }
    }

    _applySavedTerminalSettings(tab) {
        const vibe = tab?.instance?.vibeTerminal;
        if (!vibe?._terminal) return;

        const themeKey = this._loadThemePreference();
        if (themeKey && window.CXL_THEMES?.[themeKey]) {
            vibe.setTheme(window.CXL_THEMES[themeKey]);
        }

        vibe.setCursorBlink(this._loadCursorBlink());
        vibe.setCursorStyle(this._loadCursorStyle());
        vibe.setCursorInactiveStyle(this._loadCursorInactiveStyle());
    }

    applySavedTerminalSettingsForTab(tabId) {
        this._applySavedTerminalSettings(this.tabs.get(tabId));
    }

    async initialize() {
        if (this._destroyed) {
            return;
        }

        this.panel = this.container.querySelector('#vb-terminal-panel');
        this.tabList = this.container.querySelector('#vb-terminal-tab-list');
        this.tabAdd = this.container.querySelector('#vb-terminal-tab-add-btn');
        this.tabSelect = this.container.querySelector('#vb-terminal-tab-select-btn');
        this.tabPanels = this.container.querySelector('#vb-terminal-tab-panels');
        this.placeholder = this.container.querySelector('#terminal-placeholder');
        this.terminalContainer = this.container.querySelector('#terminal-container');
        this.statusBadge = this.container.querySelector('#terminal-status-badge');
        this.windowTitle = this.container.querySelector('#vb-terminal-window-title');

        this.startBtn = this.container.querySelector('#terminal-start-btn');
        this.reconnectBtn = this.container.querySelector('#terminal-reconnect-btn');
        this.stopBtn = this.container.querySelector('#terminal-stop-btn');
        this.controlsBar = this.container.querySelector('#vb-terminal-controls-bar');
        this.headerSelect = this.container.querySelector('#terminal-header-select');
        this.closeDot = this.container.querySelector('#terminal-close-dot');
        this.minimizeDot = this.container.querySelector('#terminal-minimize-dot');
        this.maximizeDot = this.container.querySelector('#terminal-maximize-dot');
        this.lockBtn = this.container.querySelector('#terminal-lock-btn');
        this.focusBtn = this.container.querySelector('#terminal-popout-btn');
        this.keyboardBtn = this.container.querySelector('#terminal-keyboard-btn');

        this.zoomInBtn     = this.container.querySelector('#terminal-zoom-in-btn');
        this.zoomOutBtn    = this.container.querySelector('#terminal-zoom-out-btn');
        this.fontSizeLabel = this.container.querySelector('#vb-terminal-font-size-label');
        this.settingsBtn   = this.container.querySelector('#terminal-settings-btn');
        this.settingsPanel = this.container.querySelector('#vb-terminal-settings-panel');
        this.settingsClose = this.container.querySelector('#terminal-settings-close');
        this.historyBtn    = document.getElementById('terminal-history-btn');

        this.populateSelect();
        this.bindActions();
        this._initSettingsPanel();
        await this.restoreTabs();

        if (this._destroyed) {
            return;
        }

        if (this.tabOrder.length === 0) {
            const initialSelection = this.getInitialSelection();
            await this.createAndActivateTab({ selection: initialSelection });
        } else {
            const preferredTabId = this.options.preferredTabId || this.getActiveTabIdFromStorage();
            const target = preferredTabId && this.tabs.has(preferredTabId)
                ? preferredTabId
                : this.tabOrder[0];
            await this.activateTab(target, { connectIfNeeded: true });

            if (typeof this.options.preferredSelection === 'string' && this.options.preferredSelection.length > 0) {
                const active = this.getActiveTab();
                if (active && !active.hasActiveSession) {
                    this.applySelection(active, this.options.preferredSelection);
                }
            }
        }

        this.setupFocusLayoutHandling();
        this.updateFocusContainerHeight();
        this.updateUi();
    }

    destroy() {
        if (this._destroyed) {
            return;
        }

        this._destroyed = true;
        this.removeFocusLayoutHandler();
        this.disableLockedLayout(this.lockedPanel);
        if (this.downloadMenuDismissHandler) {
            document.removeEventListener('click', this.downloadMenuDismissHandler);
            this.downloadMenuDismissHandler = null;
        }
        this.downloadMenu = null;
        document.body.classList.remove('vb-terminal-active-session');

        this.tabs.forEach((tab) => tab.instance.dispose());
        this.tabs.clear();
        this.tabOrder = [];
        this.activeTabId = null;
    }

    resetLayoutStateForNavigation() {
        this.removeFocusLayoutHandler();
        this.disableLockedLayout(this.lockedPanel);
        this.cleanupStaleLockState();
    }

    getInitialSelection() {
        if (typeof this.options.preferredSelection === 'string' && this.options.preferredSelection.length > 0) {
            return this.options.preferredSelection;
        }

        if (this.options.preselectedEnvId) {
            const env = (this.app.data.environments || []).find((item) => item.id === this.options.preselectedEnvId);
            if (env) {
                return buildLlmSelectionValue(env.cli, env.id);
            }
        }

        return DEFAULT_SELECTION;
    }

    saveActiveTerminalSession(format) {
        const active = this.getActiveTab();
        const vt = active?.instance?.vibeTerminal;
        if (!vt) return;
        const label = (active.state.label || 'terminal-session')
            .replace(/[^a-zA-Z0-9_-]/g, '-')
            .toLowerCase();
        if (format === 'html') {
            vt.downloadAsHtml(`${label}.html`);
        } else {
            vt.downloadAsText(`${label}.txt`);
        }
    }

    bindActions() {
        this.tabAdd?.addEventListener('click', () => {
            void this.createAndActivateTab({ selection: DEFAULT_SELECTION });
        });

        this.tabSelect?.addEventListener('click', () => {
            const active = this.getActiveTab();
            this.openSelectionPicker({
                triggerElement: !active?.state?.selection ? this.tabSelect : null,
                animate: !active?.state?.selection
            });
        });

        this.headerSelect?.addEventListener('change', (e) => {
            const active = this.getActiveTab();
            if (active && !active.state.hasActiveSession) {
                this.applySelection(active, e.target.value);
            }
        });

        this.startBtn?.addEventListener('click', () => {
            void this.startActiveTab();
        });

        this.reconnectBtn?.addEventListener('click', () => {
            void this.reconnectActiveTab();
        });

        this.stopBtn?.addEventListener('click', () => {
            const active = this.getActiveTab();
            if (active?.state?.hasActiveSession && active.state.status === 'disconnected') {
                void this.reconnectActiveTab();
                return;
            }

            void this.stopActiveTab();
        });

        this.closeDot?.addEventListener('click', () => {
            void this.stopActiveTab();
        });

        this.minimizeDot?.addEventListener('click', () => this.toggleMinimize());
        this.maximizeDot?.addEventListener('click', () => this.toggleExpand());
        this.lockBtn?.addEventListener('click', () => this.toggleLock());
        this.focusBtn?.addEventListener('click', () => this.openFocusView());
        this.keyboardBtn?.addEventListener('click', () => this.focusActiveTerminalInput());

        this.zoomInBtn?.addEventListener('click',  () => this.adjustFontSize(1));
        this.zoomOutBtn?.addEventListener('click', () => this.adjustFontSize(-1));

        this.downloadMenu = this.container.querySelector('#vb-terminal-download-menu');
        if (window.__viberails_VSCODE__) {
            const downloadWrap = this.container.querySelector('#vb-terminal-download-wrap');
            if (downloadWrap) downloadWrap.style.display = 'none';
        } else {
            this.container.querySelector('#terminal-download-btn')
                ?.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.downloadMenu?.toggleAttribute('hidden');
                });
            if (this.downloadMenuDismissHandler) {
                document.removeEventListener('click', this.downloadMenuDismissHandler);
            }
            this.downloadMenuDismissHandler = () => this.downloadMenu?.setAttribute('hidden', '');
            document.addEventListener('click', this.downloadMenuDismissHandler);
            this.container.querySelector('#terminal-save-text')
                ?.addEventListener('click', () => {
                    this.downloadMenu?.setAttribute('hidden', '');
                    this.saveActiveTerminalSession('text');
                });
            this.container.querySelector('#terminal-save-html')
                ?.addEventListener('click', () => {
                    this.downloadMenu?.setAttribute('hidden', '');
                    this.saveActiveTerminalSession('html');
                });
        }

        this.settingsBtn?.addEventListener('click',   () => this.toggleSettingsPanel());
        this.settingsClose?.addEventListener('click', () => this.toggleSettingsPanel(false));
        this.historyBtn?.addEventListener('click',    () => this.toggleHistoryPanel());
        this.container.querySelector('#terminal-settings-font-size')
            ?.addEventListener('change', (e) => this.adjustFontSize(0, parseInt(e.target.value, 10)));
        this.container.querySelector('#terminal-settings-font-family')
            ?.addEventListener('change', (e) => this.applyFontFamily(e.target.value));
        this.container.querySelector('#terminal-settings-renderer')
            ?.addEventListener('change', (e) => this.applyRendererPreference(e.target.value));
        this.container.querySelector('#terminal-settings-cursor-blink')
            ?.addEventListener('change', (e) => this.applyCursorBlink(e.target.checked));
        this.container.querySelector('#terminal-settings-cursor-style')
            ?.addEventListener('change', (e) => this.applyCursorStyle(e.target.value));
        this.container.querySelector('#terminal-settings-cursor-inactive')
            ?.addEventListener('change', (e) => this.applyCursorInactiveStyle(e.target.value));
    }

    async restoreTabs() {
        if (this._destroyed) {
            return;
        }

        let response;
        try {
            response = await this.app.apiCall('/api/v1/terminal/tabs', 'GET');
        } catch {
            response = { tabs: [], maxTabs: 8 };
        }

        if (this._destroyed) {
            return;
        }

        this.maxTabs = Number.isFinite(response?.maxTabs) ? response.maxTabs : 8;

        const tabs = Array.isArray(response?.tabs) ? response.tabs : [];
        tabs.forEach((tabInfo) => {
            const selection = this.getTabSelectionFromStorage(tabInfo.tabId) || DEFAULT_SELECTION;
            const title = this.getTabTitleFromStorage(tabInfo.tabId);
            const metadata = this.getTabMetaFromStorage(tabInfo.tabId);
            this.addLocalTab(tabInfo, {
                selection,
                title,
                label: metadata?.label || null,
                icon: metadata?.icon || null,
                accentColor: metadata?.accentColor || null,
                taskKey: metadata?.taskKey || null
            });
        });
    }

    addLocalTab(tabInfo, options = {}) {
        if (this._destroyed) {
            return null;
        }

        const selection = options.selection || DEFAULT_SELECTION;
        const meta = this.getSelectionMeta(selection);

        const state = {
            id: tabInfo.tabId,
            selection,
            label: cleanString(options.label) || meta.displayName,
            title: options.title || null,
            icon: options.icon || null,
            accentColor: this.normalizeAccentColor(options.accentColor),
            taskKey: cleanString(options.taskKey),
            hasActiveSession: tabInfo.hasActiveSession === true,
            sessionId: tabInfo.sessionId || null,
            status: tabInfo.hasActiveSession ? 'disconnected' : 'not-started',
            viewState: {
                mode: 'normal',
                locked: false
            },
            ui: null
        };

        const item = document.createElement('div');
        item.className = 'vb-terminal-tab-item';
        item.dataset.tabId = state.id;

        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'vb-terminal-tab-button';
        button.textContent = shorten(state.label);
        button.title = state.label;
        button.addEventListener('click', () => {
            if (!state.selection && !state.hasActiveSession) {
                void this.activateTab(state.id, { connectIfNeeded: false }).then(() => {
                    this.showSelectionRequiredFeedback(this.tabSelect || this.startBtn || button);
                });
                return;
            }
            void this.activateTab(state.id, { connectIfNeeded: false });
        });

        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'vb-terminal-tab-close';
        close.innerHTML = '&times;';
        close.title = 'Close tab';
        close.addEventListener('click', (event) => {
            event.stopPropagation();
            void this.closeTab(state.id);
        });

        item.appendChild(button);
        item.appendChild(close);

        const panel = document.createElement('div');
        panel.className = 'vb-terminal-tab-panel';
        panel.dataset.tabId = state.id;
        panel.style.display = 'none';

        const terminalElement = document.createElement('div');
        terminalElement.className = 'vb-terminal-element';
        terminalElement.style.width = '100%';
        terminalElement.style.height = '100%';
        panel.appendChild(terminalElement);

        this.tabList?.appendChild(item);
        this.tabPanels?.appendChild(panel);

        state.ui = { item, button, close, panel, terminalElement };

        const instance = new TerminalTab(this, state);
        const tab = { state, instance };

        this.tabs.set(state.id, tab);
        this.tabOrder.push(state.id);

        this.saveTabSelection(state.id, selection);
        if (state.title) {
            this.saveTabTitle(state.id, state.title);
        }
        this.saveTabMeta(state.id, {
            label: state.label,
            icon: state.icon,
            accentColor: state.accentColor,
            taskKey: state.taskKey
        });
        this.renderTabButton(tab);
        this.applyTabAccent(tab);

        this.updateAddButtonState();
        return tab;
    }

    getActiveTab() {
        if (!this.activeTabId) {
            return null;
        }

        return this.tabs.get(this.activeTabId) || null;
    }

    async activateTab(tabId, options = {}) {
        if (this._destroyed) {
            return;
        }

        const target = this.tabs.get(tabId);
        if (!target) {
            return;
        }

        const previous = this.getActiveTab();
        if (previous && previous.state.id !== target.state.id) {
            previous.state.ui.item.classList.remove('active');
            previous.state.ui.panel.style.display = 'none';
            previous.instance.setActive(false);
        }

        this.activeTabId = target.state.id;
        this.saveActiveTabId(target.state.id);

        target.state.ui.item.classList.add('active');
        target.state.ui.panel.style.display = 'block';
        target.instance.setActive(true);

        if (options.connectIfNeeded && target.state.hasActiveSession && !target.instance.hasOpenSocket()) {
            // Make the terminal container visible before connecting so fit() can
            // compute real pixel dimensions. Those dimensions are forwarded to the
            // backend via the WebSocket URL, which lets the server resize the PTY
            // *before* sending the replay buffer. Without this the replay arrives
            // at the old (stale) PTY size, then a SIGWINCH redraws at the new size,
            // causing content to appear twice ("double print / 2 cursors" bug).
            this.showTerminal();
            await target.instance.connect();
            if (this._destroyed) {
                return;
            }
        }

        this.applyPanelState();
        this.updateUi();
    }

    async createAndActivateTab(options = {}) {
        if (this._destroyed) {
            return null;
        }

        if (this.tabOrder.length >= this.maxTabs) {
            this.app.showError(`Maximum of ${this.maxTabs} terminal tabs reached.`);
            return null;
        }

        this.tabAdd && (this.tabAdd.disabled = true);
        try {
            const tabInfo = await this.app.apiCall('/api/v1/terminal/tabs', 'POST');
            if (this._destroyed) {
                try {
                    await this.app.apiCall(`/api/v1/terminal/tabs/${encodeURIComponent(tabInfo.tabId)}`, 'DELETE');
                } catch {
                    // no-op
                }
                return null;
            }

            const tab = this.addLocalTab(tabInfo, {
                selection: options.selection || DEFAULT_SELECTION,
                title: options.title || null,
                icon: options.icon || null,
                label: options.label || null,
                accentColor: options.accentColor || null,
                taskKey: options.taskKey || null
            });
            if (!tab) {
                return null;
            }
            await this.activateTab(tab.state.id, { connectIfNeeded: false });
            this.updateUi();
            requestAnimationFrame(() => this.updateFocusContainerHeight());
            return tab;
        } catch (error) {
            this.app.showError(`Failed to create terminal tab: ${error.message}`);
            return null;
        } finally {
            if (this.tabAdd) {
                this.tabAdd.disabled = false;
            }
            this.updateAddButtonState();
        }
    }

    async closeTab(tabId) {
        const tab = this.tabs.get(tabId);
        if (!tab) {
            return;
        }

        try {
            await this.app.apiCall(`/api/v1/terminal/tabs/${encodeURIComponent(tabId)}`, 'DELETE');
        } catch (error) {
            // 404 means tab already expired server-side (e.g. LLM never selected) — close silently
            if (error.message !== 'API call failed: Not Found') {
                this.app.showError(`Failed to close terminal tab: ${error.message}`);
            }
            // Always fall through to local cleanup so the tab is removed from the UI
        }

        tab.instance.dispose();
        tab.state.ui.item.remove();
        tab.state.ui.panel.remove();

        this.tabs.delete(tabId);
        this.tabOrder = this.tabOrder.filter((id) => id !== tabId);
        this.clearTabSelection(tabId);
        this.clearTabTitle(tabId);
        this.clearTabMeta(tabId);

        if (this.activeTabId === tabId) {
            this.activeTabId = null;
        }

        if (this.tabOrder.length === 0) {
            await this.createAndActivateTab({ selection: DEFAULT_SELECTION });
            return;
        }

        if (!this.activeTabId) {
            const nextId = this.tabOrder[Math.max(0, this.tabOrder.length - 1)];
            await this.activateTab(nextId, { connectIfNeeded: false });
        }

        this.updateUi();
    }

    async startFromSelection(selection) {
        let tab = this.getActiveTab();
        if (!tab) {
            tab = await this.createAndActivateTab({ selection: selection || null });
        } else if (tab.state.hasActiveSession) {
            tab = await this.createAndActivateTab({ selection: selection || tab.state.selection || null });
        } else if (selection) {
            this.applySelection(tab, selection);
        }

        if (!tab) {
            return;
        }

        const meta = this.getSelectionMeta(tab.state.selection);
        if (!meta.cli) {
            this.showSelectionRequiredFeedback(this.startBtn || this.tabSelect);
            return;
        }

        const body = { cli: meta.cli };
        if (meta.environmentName) {
            body.environmentName = meta.environmentName;
        }

        const started = await tab.instance.startSession(body);
        if (!started) {
            return;
        }

        tab.state.hasActiveSession = true;
        tab.state.title = `${meta.displayName} Terminal`;
        this.saveTabTitle(tab.state.id, tab.state.title);
        this.updateUi();

        this.app.showToast('Terminal Started', `Launching ${meta.displayName}...`, 'success');
    }

    async startWithOptions(options) {
        const selection = this.resolveSelectionFromOptions(options);
        const meta = this.getSelectionMeta(selection);
        const requestedLabel = cleanString(options?.tabLabel)
            || cleanString(options?.title)
            || meta.displayName;
        const requestedTitle = cleanString(options?.title)
            ? `${cleanString(options.title)} Terminal`
            : `${requestedLabel || meta.displayName} Terminal`;
        const requestedIcon = cleanString(options?.icon);
        const requestedColor = this.normalizeAccentColor(options?.color || options?.accentColor);
        const requestedTaskKey = cleanString(options?.taskKey);

        let tab = null;
        let reusedExisting = false;

        if (cleanString(options?.reuseTabId)) {
            const existing = this.tabs.get(cleanString(options.reuseTabId));
            if (existing) {
                tab = existing;
                reusedExisting = true;
                await this.activateTab(tab.state.id, { connectIfNeeded: true });
            }
        }

        if (!tab && requestedTaskKey) {
            const existingByTask = this.findTabByTaskKey(requestedTaskKey);
            if (existingByTask) {
                tab = existingByTask;
                reusedExisting = true;
                await this.activateTab(tab.state.id, { connectIfNeeded: true });
            }
        }

        if (!tab) {
            tab = this.getActiveTab();
            if (!tab || tab.state.hasActiveSession) {
                tab = await this.createAndActivateTab({
                    selection,
                    title: requestedTitle,
                    label: requestedLabel,
                    icon: requestedIcon,
                    accentColor: requestedColor,
                    taskKey: requestedTaskKey
                });
            } else {
                this.applySelection(tab, selection);
            }
        } else if (!tab.state.hasActiveSession) {
            this.applySelection(tab, selection);
        }

        if (!tab) {
            return { tabId: null, started: false, reusedExisting: false };
        }

        this.updateTabMetadata(tab, {
            label: requestedLabel,
            title: requestedTitle,
            icon: requestedIcon,
            accentColor: requestedColor,
            taskKey: requestedTaskKey
        });

        if (tab.state.hasActiveSession) {
            if (!tab.instance.hasOpenSocket()) {
                await tab.instance.connect();
            }
            this.updateUi();
            this.focusActiveTerminalInput();
            return {
                tabId: tab.state.id,
                started: false,
                reusedExisting: true
            };
        }

        const body = {
            cli: lower(options.cli),
            environmentName: options.environmentName || null,
            workingDirectory: options.workingDirectory || null,
            title: options.title || null
        };

        const started = await tab.instance.startSession(body);
        if (!started) {
            return {
                tabId: tab.state.id,
                started: false,
                reusedExisting
            };
        }

        tab.state.hasActiveSession = true;
        this.updateTabMetadata(tab, {
            label: requestedLabel,
            title: requestedTitle,
            icon: requestedIcon,
            accentColor: requestedColor,
            taskKey: requestedTaskKey
        });
        this.updateUi();

        this.app.showToast('Terminal Started', `Launching ${requestedLabel || meta.displayName}...`, 'success');
        return {
            tabId: tab.state.id,
            started: true,
            reusedExisting
        };
    }

    async startActiveTab() {
        const tab = this.getActiveTab();
        if (tab && tab.state.hasActiveSession) {
            this.app.showToast('Terminal Running', 'The active tab already has a running session.', 'info');
            return;
        }

        if (!tab?.state.selection) {
            this.showSelectionRequiredFeedback(this.startBtn || this.tabSelect);
            return;
        }

        await this.startFromSelection(tab.state.selection);
    }

    async stopActiveTab() {
        const tab = this.getActiveTab();
        if (!tab || !tab.state.hasActiveSession) {
            return;
        }

        const stopped = await tab.instance.stopSession();
        if (!stopped) {
            return;
        }

        tab.state.hasActiveSession = false;
        tab.state.sessionId = null;
        tab.state.title = `${tab.state.label} Terminal`;
        this.saveTabTitle(tab.state.id, tab.state.title);
        this.updateUi();

        this.app.showToast('Terminal Stopped', 'Terminal session ended', 'info');
    }

    async reconnectActiveTab() {
        const tab = this.getActiveTab();
        if (!tab || !tab.state.hasActiveSession) {
            return;
        }

        if (tab.instance.hasOpenSocket()) {
            tab.state.status = 'connected';
            this.updateUi();
            return;
        }

        tab.state.status = 'connecting';
        this.updateUi();

        const connected = await tab.instance.connect();
        if (!connected) {
            this.app.showError('Failed to reconnect terminal session.');
            return;
        }

        this.updateUi();
        this.app.showToast('Terminal Reconnected', 'Successfully reconnected to terminal session', 'success');
    }

    getSelectionMeta(selection) {
        const parsed = parseLlmSelection(selection, this.app.data.environments || []);
        if (!parsed.cli) {
            return {
                cli: null,
                envId: null,
                environmentName: null,
                displayName: 'Select LLM to launch Terminal. Terminals run safely in the background even if you navigate away.'
            };
        }

        return {
            cli: parsed.cli,
            envId: parsed.envId,
            environmentName: parsed.environmentName,
            displayName: parsed.displayName
        };
    }

    resolveSelectionFromOptions(options) {
        const cli = lower(options?.cli || 'claude');
        if (!options?.environmentName) {
            return buildLlmSelectionValue(cli);
        }

        const env = (this.app.data.environments || []).find((item) =>
            lower(item.name) === lower(options.environmentName)
            && lower(item.cli) === cli
        );

        return env ? buildLlmSelectionValue(cli, env.id) : buildLlmSelectionValue(cli);
    }

    applySelection(tab, selection) {
        const meta = this.getSelectionMeta(selection);
        tab.state.selection = selection;
        tab.state.label = meta.displayName;
        this.saveTabSelection(tab.state.id, selection);
        this.saveTabMeta(tab.state.id, {
            label: tab.state.label,
            icon: tab.state.icon,
            accentColor: tab.state.accentColor,
            taskKey: tab.state.taskKey
        });
        this.updateUi();
    }

    normalizeAccentColor(value) {
        const color = cleanString(value);
        if (!color) return null;

        const colorPattern = /^(#[0-9a-fA-F]{3,8}|rgb\(\s*[\d\s.,%]+\)|rgba\(\s*[\d\s.,%]+\)|hsl\(\s*[\d\s.,%]+\)|hsla\(\s*[\d\s.,%]+\)|[a-zA-Z]+)$/;
        return colorPattern.test(color) ? color : null;
    }

    renderTabButton(tab) {
        const button = tab?.state?.ui?.button;
        if (!button) return;

        button.innerHTML = '';
        if (tab.state.icon) {
            const iconSpan = document.createElement('span');
            iconSpan.className = 'vb-terminal-tab-icon';
            iconSpan.textContent = `${tab.state.icon} `;
            button.appendChild(iconSpan);
        }

        const labelSpan = document.createElement('span');
        labelSpan.className = 'vb-terminal-tab-label';
        labelSpan.textContent = shorten(tab.state.label || 'Terminal');
        button.appendChild(labelSpan);
        button.title = tab.state.label || 'Terminal';
    }

    applyTabAccent(tab) {
        const item = tab?.state?.ui?.item;
        if (!item) return;

        const accent = this.normalizeAccentColor(tab.state.accentColor);
        if (!accent) {
            item.classList.remove('has-custom-accent');
            item.style.removeProperty('--vb-terminal-tab-accent');
            return;
        }

        item.classList.add('has-custom-accent');
        item.style.setProperty('--vb-terminal-tab-accent', accent);
    }

    findTabByTaskKey(taskKey) {
        const key = cleanString(taskKey);
        if (!key) return null;

        for (const id of this.tabOrder) {
            const tab = this.tabs.get(id);
            if (tab?.state?.taskKey === key) {
                return tab;
            }
        }

        return null;
    }

    async focusTab(tabId, options = {}) {
        const tab = this.tabs.get(tabId);
        if (!tab) return false;

        await this.activateTab(tab.state.id, {
            connectIfNeeded: options.connectIfNeeded !== false
        });
        this.focusActiveTerminalInput();
        return true;
    }

    updateTabMetadata(tab, metadata = {}) {
        if (!tab) return;

        const label = cleanString(metadata.label);
        const title = cleanString(metadata.title);
        const icon = Object.prototype.hasOwnProperty.call(metadata, 'icon')
            ? cleanString(metadata.icon)
            : undefined;
        const accentColor = Object.prototype.hasOwnProperty.call(metadata, 'accentColor')
            ? this.normalizeAccentColor(metadata.accentColor)
            : undefined;
        const taskKey = Object.prototype.hasOwnProperty.call(metadata, 'taskKey')
            ? cleanString(metadata.taskKey)
            : undefined;

        if (label) {
            tab.state.label = label;
        }
        if (title) {
            tab.state.title = title;
            this.saveTabTitle(tab.state.id, tab.state.title);
        }
        if (icon !== undefined) {
            tab.state.icon = icon;
        }
        if (accentColor !== undefined) {
            tab.state.accentColor = accentColor;
        }
        if (taskKey !== undefined) {
            tab.state.taskKey = taskKey;
        }

        this.saveTabMeta(tab.state.id, {
            label: tab.state.label,
            icon: tab.state.icon,
            accentColor: tab.state.accentColor,
            taskKey: tab.state.taskKey
        });

        this.applyTabAccent(tab);
        this.renderTabButton(tab);
    }

    populateSelect() {
        if (!this.headerSelect || this.headerSelect.tagName !== 'SELECT') return;
        populateLlmSelectionSelect(this.headerSelect, this.app.data.environments || [], {
            placeholder: 'Select LLM...'
        });
    }

    openSelectionPicker(options = {}) {
        const selectEl = this.headerSelect;
        if (!selectEl || selectEl.disabled) {
            return;
        }

        const triggerElement = options.triggerElement || null;
        if (options.animate === true) {
            [triggerElement, selectEl].filter(Boolean).forEach((el) => {
                el.classList.remove('vb-terminal-selection-shake');
                // Force reflow so repeated clicks replay the animation.
                void el.offsetWidth;
                el.classList.add('vb-terminal-selection-shake');
            });
        }

        selectEl.focus();
        if (typeof selectEl.showPicker === 'function') {
            try {
                selectEl.showPicker();
                return;
            } catch {
                // Fallback below.
            }
        }

        try {
            selectEl.click();
            selectEl.dispatchEvent(new KeyboardEvent('keydown', {
                key: 'ArrowDown',
                bubbles: true
            }));
        } catch {
            // no-op
        }
    }

    showSelectionRequiredFeedback(triggerElement = null) {
        this.openSelectionPicker({
            triggerElement,
            animate: true
        });
    }

    // progress: { state: 0-4, value: 0-100 }
    // states: 0=none, 1=normal, 2=error, 3=indeterminate, 4=paused
    updateTabProgress(tabId, progress) {
        const tab = this.tabs.get(tabId);
        if (!tab) return;
        const item = tab.state.ui.item;
        item.removeAttribute('data-progress');
        item.style.removeProperty('--tab-progress');
        if (progress.state === 0) return;
        const stateNames = ['', 'normal', 'error', 'indeterminate', 'paused'];
        item.dataset.progress = stateNames[progress.state] || 'normal';
        item.style.setProperty('--tab-progress', `${progress.value}%`);
    }

    updateUi() {
        const active = this.getActiveTab();
        const hasActiveSession = this.tabOrder.some((id) => this.tabs.get(id)?.state?.hasActiveSession === true);
        document.body.classList.toggle('vb-terminal-active-session', hasActiveSession);

        this.tabs.forEach((tab) => {
            this.renderTabButton(tab);
            this.applyTabAccent(tab);
            tab.state.ui.item.classList.toggle('active', !!active && active.state.id === tab.state.id);

            const isConnected = tab.state.hasActiveSession && tab.state.status === 'connected';
            const isDisconnected = tab.state.hasActiveSession && tab.state.status === 'disconnected';
            tab.state.ui.item.classList.toggle('is-connected', isConnected);
            tab.state.ui.item.classList.toggle('is-disconnected', isDisconnected);
        });

        if (!active) {
            this.keyboardBtn?.classList.add('d-none');
            this.setBadge('Not Started', 'bg-secondary');
            this.updateActionButtons({ start: true, reconnect: false, stop: false });
            this.showPlaceholder();
            this.updateAddButtonState();
            return;
        }

        const badge = this.getBadge(active.state);
        this.setBadge(badge.text, badge.className);

        this.updateActionButtons({
            start: !active.state.hasActiveSession,
            reconnect: active.state.hasActiveSession && active.state.status === 'disconnected',
            stop: active.state.hasActiveSession
        });

        if (this.keyboardBtn) {
            this.keyboardBtn.classList.toggle('d-none', !active.state.hasActiveSession);
        }

        if (active.state.hasActiveSession) {
            this.showTerminal();
        } else {
            this.showPlaceholder();
        }

        this.updateWindowControlState();
        this.updateAddButtonState();
    }

    getBadge(state) {
        if (!state.hasActiveSession) {
            return { text: 'Not Started', className: 'bg-secondary' };
        }
        if (state.status === 'connected') {
            return { text: 'Connected', className: 'bg-secondary' };
        }
        if (state.status === 'connecting') {
            return { text: 'Connecting', className: 'bg-warning' };
        }
        return { text: 'Disconnected', className: 'bg-warning' };
    }

    setBadge(text, className) {
        if (!this.statusBadge) return;

        const hide = className === 'bg-secondary';
        this.statusBadge.classList.toggle('d-none', hide);
        if (!hide) {
            this.statusBadge.textContent = text;
            this.statusBadge.classList.remove('bg-secondary', 'bg-success', 'bg-warning', 'bg-danger', 'bg-info');
            this.statusBadge.classList.add(className);
        }
    }

    updateActionButtons({ start, reconnect, stop }) {
        this.startBtn?.classList.toggle('d-none', !start);
        this.reconnectBtn?.classList.add('d-none');
        this.stopBtn?.classList.toggle('d-none', !stop);
        this.updateConnectionButtonMode(reconnect);

        // connected = has active session AND not in disconnected state
        const connected = stop && !reconnect;
        this.controlsBar?.classList.toggle('d-none', connected);
    }

    updateConnectionButtonMode(reconnect) {
        if (!this.stopBtn) return;

        const label = this.stopBtn.querySelector('span');
        const nextLabel = reconnect ? 'Connect' : 'Disconnect';
        const nextTitle = reconnect ? 'Reconnect terminal session' : 'Disconnect terminal session';

        this.stopBtn.title = nextTitle;
        this.stopBtn.setAttribute('aria-label', nextTitle);
        this.stopBtn.classList.toggle('btn-outline-success', reconnect);
        this.stopBtn.classList.toggle('btn-outline-danger', !reconnect);

        if (label) {
            label.textContent = nextLabel;
        }
    }

    showPlaceholder() {
        if (this.placeholder) this.placeholder.style.display = 'block';
        if (this.terminalContainer) this.terminalContainer.style.display = 'none';
    }

    showTerminal() {
        if (this.placeholder) this.placeholder.style.display = 'none';
        if (this.terminalContainer) this.terminalContainer.style.display = 'block';
        this.updateFocusContainerHeight();
        // Re-fit now that the container is visible and has real dimensions
        this.getActiveTab()?.instance.scheduleFitPasses();
    }

    updateAddButtonState() {
        if (this.tabAdd) {
            const atLimit = this.tabOrder.length >= this.maxTabs;
            this.tabAdd.disabled = atLimit;
            this.tabAdd.title = atLimit
                ? `Maximum of ${this.maxTabs} tabs reached`
                : 'Open a new terminal tab';
        }

        if (this.tabSelect) {
            const active = this.getActiveTab();
            const isBlocked = active && active.state.hasActiveSession;
            this.tabSelect.disabled = !!isBlocked;
            this.tabSelect.title = isBlocked
                ? 'Stop terminal to change CLI/environment'
                : 'Select CLI/environment for active tab';
        }

        if (this.headerSelect) {
            const active = this.getActiveTab();
            const canSelect = active && !active.state.hasActiveSession;
            this.headerSelect.disabled = !canSelect;
            if (active) {
                this.headerSelect.value = active.state.selection || '';
            } else {
                this.headerSelect.value = '';
            }
        }
    }

    focusActiveTerminalInput() {
        const tab = this.getActiveTab();
        if (!tab || !tab.state.hasActiveSession) {
            return;
        }

        tab.instance.ensureTerminal();
        tab.instance.focusInput();
        tab.instance.scheduleFitPasses();
    }

    searchActiveTab() {
        const tab = this.getActiveTab();
        if (!tab || !tab.state.hasActiveSession) {
            return;
        }

        tab.instance.ensureTerminal();
        tab.instance.openSearch();
        tab.instance.focusInput();
    }

    getWebSocketUrl(tabId, cols, rows) {
        const encodedId = encodeURIComponent(tabId);
        const baseUrl = window.__viberails_API_BASE__ || '';
        let url;
        if (baseUrl) {
            url = `${baseUrl.replace(/^http/, 'ws')}/api/v1/terminal/tabs/${encodedId}/ws`;
        } else {
            const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            url = `${protocol}//${window.location.host}/api/v1/terminal/tabs/${encodedId}/ws`;
        }

        if (cols > 0 && rows > 0) {
            url += `?cols=${cols}&rows=${rows}`;
        }
        return url;
    }

    parseSelectionMetadata(selection) {
        const parsed = parseLlmSelection(selection, this.app.data.environments || []);
        if (parsed.kind !== 'environment') {
            return { preselectedEnvId: null };
        }

        return {
            preselectedEnvId: parsed.envId
        };
    }

    openFocusView() {
        if (this.options.focusView === true) {
            this.app.goBack();
            return;
        }

        const active = this.getActiveTab();
        const selection = active?.state.selection || DEFAULT_SELECTION;
        const { preselectedEnvId } = this.parseSelectionMetadata(selection);

        this.app.navigate('terminal-focus', {
            preselectedEnvId,
            preferredSelection: selection,
            preferredTabId: active?.state.id || null
        });
    }

    toggleMinimize() {
        const tab = this.getActiveTab();
        if (!tab) return;

        if (tab.state.viewState.mode === 'minimized') {
            tab.state.viewState.mode = 'normal';
        } else {
            tab.state.viewState.mode = 'minimized';
            tab.state.viewState.locked = false;
        }

        this.applyPanelState();
        this.updateWindowControlState();
    }

    toggleExpand() {
        const tab = this.getActiveTab();
        if (!tab) return;

        if (tab.state.viewState.mode === 'expanded') {
            tab.state.viewState.mode = 'normal';
        } else {
            tab.state.viewState.mode = 'expanded';
        }

        this.applyPanelState();
        this.updateWindowControlState();
    }

    toggleLock() {
        const tab = this.getActiveTab();
        if (!tab) return;

        tab.state.viewState.locked = !tab.state.viewState.locked;
        if (tab.state.viewState.locked && tab.state.viewState.mode === 'minimized') {
            tab.state.viewState.mode = 'normal';
        }

        this.applyPanelState();
        this.updateWindowControlState();
    }

    applyPanelState() {
        if (!this.panel) return;

        const tab = this.getActiveTab();
        const mode = tab?.state.viewState.mode || 'normal';

        this.panel.classList.remove('vb-terminal-minimized', 'vb-terminal-expanded');
        if (mode === 'minimized') {
            this.panel.classList.add('vb-terminal-minimized');
        } else if (mode === 'expanded') {
            this.panel.classList.add('vb-terminal-expanded');
        }

        if (tab?.state.viewState.locked) {
            this.enableLockedLayout(this.panel);
        } else {
            this.disableLockedLayout(this.panel);
        }

        this.updateFocusContainerHeight();
        tab?.instance.scheduleFitPasses();
    }

    setupFocusLayoutHandling() {
        if (this.options.focusView !== true || !this.panel || !this.terminalContainer || this.focusLayoutHandler) {
            return;
        }

        const viewport = window.visualViewport;
        this.focusLayoutHandler = () => {
            if (this.focusLayoutRaf) {
                window.cancelAnimationFrame(this.focusLayoutRaf);
            }

            this.focusLayoutRaf = window.requestAnimationFrame(() => {
                this.focusLayoutRaf = null;
                this.updateFocusContainerHeight();
                this.getActiveTab()?.instance.scheduleFitPasses();
            });
        };

        window.addEventListener('resize', this.focusLayoutHandler);
        viewport?.addEventListener('resize', this.focusLayoutHandler);

        this.focusLayoutHandler();
    }

    removeFocusLayoutHandler() {
        if (this.focusLayoutRaf) {
            window.cancelAnimationFrame(this.focusLayoutRaf);
            this.focusLayoutRaf = null;
        }

        if (this.focusLayoutHandler) {
            const viewport = window.visualViewport;
            window.removeEventListener('resize', this.focusLayoutHandler);
            viewport?.removeEventListener('resize', this.focusLayoutHandler);
            this.focusLayoutHandler = null;
        }

        this.panel?.style.removeProperty('--terminal-available-height');
        document.getElementById('ch-sidebar')?.style.removeProperty('--ch-available-height');
    }

    updateFocusContainerHeight() {
        if (this.options.focusView !== true || !this.panel || !this.terminalContainer) {
            return;
        }

        const viewportHeight = window.visualViewport?.height
            || window.innerHeight
            || document.documentElement.clientHeight
            || 0;
        const containerRect = this.terminalContainer.getBoundingClientRect();
        const availableHeight = Math.max(0, Math.round(viewportHeight - containerRect.top - 12));
        this.panel.style.setProperty('--terminal-available-height', `${availableHeight}px`);

        const historySidebar = document.getElementById('ch-sidebar');
        if (historySidebar) {
            const panelRect = this.panel.getBoundingClientRect();
            const sidebarHeight = Math.max(0, Math.round(panelRect.height));
            historySidebar.style.setProperty('--ch-available-height', `${sidebarHeight}px`);
        }
    }

    updateWindowControlState() {
        const tab = this.getActiveTab();
        const isMinimized = tab?.state.viewState.mode === 'minimized';
        const isExpanded = tab?.state.viewState.mode === 'expanded';
        const isLocked = tab?.state.viewState.locked === true;
        const isFocusView = this.options.focusView === true;

        const setLabel = (button, text) => {
            const label = button?.querySelector('.vb-terminal-control-text');
            if (label) label.textContent = text;
        };

        if (this.minimizeDot) {
            this.minimizeDot.classList.toggle('active', isMinimized);
            this.minimizeDot.setAttribute('aria-pressed', String(isMinimized));
            this.minimizeDot.title = isMinimized ? 'Restore terminal panel height' : 'Minimize terminal panel';
            setLabel(this.minimizeDot, isMinimized ? 'Restore' : 'Minimize');
        }

        if (this.maximizeDot) {
            this.maximizeDot.classList.toggle('active', isExpanded);
            this.maximizeDot.setAttribute('aria-pressed', String(isExpanded));
            this.maximizeDot.title = isExpanded ? 'Restore terminal to default size' : 'Expand terminal panel';
            setLabel(this.maximizeDot, isExpanded ? 'Normal Size' : 'Expand');
        }

        if (this.lockBtn) {
            this.lockBtn.classList.toggle('active', isLocked);
            this.lockBtn.setAttribute('aria-pressed', String(isLocked));
            this.lockBtn.title = isLocked
                ? 'Unlock terminal from sticky focus mode'
                : 'Lock terminal in sticky focus mode while scrolling';
            setLabel(this.lockBtn, isLocked ? 'Unlock Focus' : 'Lock Focus');
        }

        if (this.focusBtn) {
            this.focusBtn.classList.remove('active');
            this.focusBtn.setAttribute('aria-pressed', 'false');
            this.focusBtn.title = 'Open terminal in focused page view';
            setLabel(this.focusBtn, 'Open In Fullscreen');
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
            document.body.classList.add('vb-terminal-scroll-locked');
            document.body.style.top = `-${this.lockScrollTop}px`;
            this.isScrollLocked = true;
            return;
        }

        if (!this.isScrollLocked && !document.body.classList.contains('vb-terminal-scroll-locked')) {
            return;
        }

        document.body.classList.remove('vb-terminal-scroll-locked');
        document.body.style.removeProperty('top');
        const restoreTop = Number.isFinite(this.lockScrollTop) ? this.lockScrollTop : 0;
        this.lockScrollTop = 0;
        this.isScrollLocked = false;
        window.scrollTo(0, restoreTop);
    }

    updateLockedPanelPosition(panel) {
        if (!panel) return;

        const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
        const panelRect = panel.getBoundingClientRect();
        const cardHeader = panel.querySelector('.card-header');
        const cardHeaderHeight = cardHeader ? Math.round(cardHeader.getBoundingClientRect().height) : 0;

        const topOffset = Math.max(8, Math.round(panelRect.top));
        const bottomPadding = 12;
        const availableShellHeight = Math.max(220, Math.round(viewportHeight - topOffset - bottomPadding - cardHeaderHeight));

        panel.style.setProperty('--terminal-lock-max-height', `${availableShellHeight}px`);
    }

    enableLockedLayout(panel) {
        if (!panel) return;

        panel.scrollIntoView({ block: 'center', inline: 'nearest' });
        this.removeLockLayoutHandler();
        panel.classList.remove('vb-terminal-minimized');
        panel.classList.add('vb-terminal-locked');
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
            this.getActiveTab()?.instance.scheduleFitPasses();
        };

        window.addEventListener('resize', this.lockLayoutHandler);
        this.updateLockedPanelPosition(panel);
        this.getActiveTab()?.instance.scheduleFitPasses();
    }

    disableLockedLayout(panel) {
        const target = panel || this.lockedPanel;
        if (target) {
            target.classList.remove('vb-terminal-locked');
            target.style.removeProperty('--terminal-lock-max-height');
        }

        if (this.lockedPanel === target) {
            this.lockedPanel = null;
        }

        this.removeLockLayoutHandler();
        this.setPageScrollLock(false);
    }

    removeLockLayoutHandler() {
        if (this.lockLayoutHandler) {
            window.removeEventListener('resize', this.lockLayoutHandler);
            this.lockLayoutHandler = null;
        }
    }

    saveTabSelection(tabId, selection) {
        try { window.sessionStorage.setItem(`${TAB_SELECTION_PREFIX}${tabId}`, selection || ''); } catch {}
    }

    getTabSelectionFromStorage(tabId) {
        try {
            const value = window.sessionStorage.getItem(`${TAB_SELECTION_PREFIX}${tabId}`);
            if (!value || value === 'null') return null;
            return value;
        } catch { return null; }
    }

    clearTabSelection(tabId) {
        try { window.sessionStorage.removeItem(`${TAB_SELECTION_PREFIX}${tabId}`); } catch {}
    }

    saveTabTitle(tabId, title) {
        try { window.sessionStorage.setItem(`${TAB_TITLE_PREFIX}${tabId}`, title || ''); } catch {}
    }

    getTabTitleFromStorage(tabId) {
        try {
            const value = window.sessionStorage.getItem(`${TAB_TITLE_PREFIX}${tabId}`);
            return value || null;
        } catch {
            return null;
        }
    }

    clearTabTitle(tabId) {
        try { window.sessionStorage.removeItem(`${TAB_TITLE_PREFIX}${tabId}`); } catch {}
    }

    saveTabMeta(tabId, metadata = {}) {
        try {
            const payload = {
                label: cleanString(metadata.label) || null,
                icon: cleanString(metadata.icon) || null,
                accentColor: this.normalizeAccentColor(metadata.accentColor),
                taskKey: cleanString(metadata.taskKey) || null
            };
            window.sessionStorage.setItem(`${TAB_META_PREFIX}${tabId}`, JSON.stringify(payload));
        } catch {}
    }

    getTabMetaFromStorage(tabId) {
        try {
            const raw = window.sessionStorage.getItem(`${TAB_META_PREFIX}${tabId}`);
            if (!raw) return null;
            const parsed = JSON.parse(raw);
            return {
                label: cleanString(parsed?.label) || null,
                icon: cleanString(parsed?.icon) || null,
                accentColor: this.normalizeAccentColor(parsed?.accentColor),
                taskKey: cleanString(parsed?.taskKey) || null
            };
        } catch {
            return null;
        }
    }

    clearTabMeta(tabId) {
        try { window.sessionStorage.removeItem(`${TAB_META_PREFIX}${tabId}`); } catch {}
    }

    saveActiveTabId(tabId) {
        try { window.sessionStorage.setItem(ACTIVE_TAB_KEY, tabId || ''); } catch {}
    }

    getActiveTabIdFromStorage() {
        try { return window.sessionStorage.getItem(ACTIVE_TAB_KEY); } catch { return null; }
    }

    // -------------------------------------------------------------------------
    // Font size
    // -------------------------------------------------------------------------

    _loadFontSize() {
        try { return parseInt(localStorage.getItem('viberails_terminal_fontSize'), 10) || 14; } catch { return 14; }
    }

    _saveFontSize(size) {
        try { localStorage.setItem('viberails_terminal_fontSize', size); } catch {}
    }

    adjustFontSize(delta, absolute) {
        const current = this._loadFontSize();
        const next = Math.max(6, Math.min(72, absolute != null ? absolute : current + delta));
        this._saveFontSize(next);
        if (this.fontSizeLabel) this.fontSizeLabel.textContent = next;
        const sizeInput = this.container.querySelector('#terminal-settings-font-size');
        if (sizeInput) sizeInput.value = next;
        this.tabs.forEach((tab) => tab.instance.applyFontSize(next));
    }

    // -------------------------------------------------------------------------
    // Settings panel
    // -------------------------------------------------------------------------

    _initSettingsPanel() {
        const size = this._loadFontSize();
        if (this.fontSizeLabel) this.fontSizeLabel.textContent = size;
        const sizeInput = this.container.querySelector('#terminal-settings-font-size');
        if (sizeInput) sizeInput.value = size;

        this._themeItems = [];
        const themeList = this.container.querySelector('#vb-terminal-settings-theme-list');
        if (themeList && window.CXL_THEMES) {
            themeList.innerHTML = '';
            for (const [key, theme] of Object.entries(window.CXL_THEMES)) {
                const item = document.createElement('div');
                item.className = 'vb-terminal-settings-theme-item';
                item.dataset.theme = key;
                item.title = theme.name;

                const preview = document.createElement('div');
                preview.className = 'vb-terminal-settings-theme-preview';
                preview.style.background = theme.background;

                // Add accent pills to preview
                const accents = [theme.red, theme.green, theme.blue, theme.magenta, theme.cyan, theme.yellow];
                const activeAccents = accents.filter(c => c).slice(0, 4);
                activeAccents.forEach(color => {
                    const pill = document.createElement('div');
                    pill.className = 'accent-pill';
                    pill.style.background = color;
                    preview.appendChild(pill);
                });

                const name = document.createElement('div');
                name.className = 'vb-terminal-settings-theme-name';
                name.textContent = theme.name;

                item.appendChild(preview);
                item.appendChild(name);

                item.addEventListener('click', () => this.applyTheme(key));
                themeList.appendChild(item);
                this._themeItems.push(item);
            }
        }

        const familySelect = this.container.querySelector('#terminal-settings-font-family');
        if (familySelect && window.CXL_FONTS) {
            for (const [, font] of Object.entries(window.CXL_FONTS)) {
                const opt = document.createElement('option');
                opt.value = font.value;
                opt.textContent = font.name;
                familySelect.appendChild(opt);
            }
            const saved = localStorage.getItem('viberails_terminal_fontFamily');
            if (saved) familySelect.value = saved;
        }

        const rendererSelect = this.container.querySelector('#terminal-settings-renderer');
        if (rendererSelect) rendererSelect.value = this._loadRendererPreference();

        const cursorBlinkCheck = this.container.querySelector('#terminal-settings-cursor-blink');
        if (cursorBlinkCheck) cursorBlinkCheck.checked = this._loadCursorBlink();

        const cursorStyleSelect = this.container.querySelector('#terminal-settings-cursor-style');
        if (cursorStyleSelect) cursorStyleSelect.value = this._loadCursorStyle();

        const cursorInactiveSelect = this.container.querySelector('#terminal-settings-cursor-inactive');
        if (cursorInactiveSelect) cursorInactiveSelect.value = this._loadCursorInactiveStyle();

        const savedTheme = this._loadThemePreference();
        if (savedTheme) {
            this._themeItems?.forEach((item) => {
                item.classList.toggle('active', item.dataset.theme === savedTheme);
            });
        }
    }

    toggleSettingsPanel(forceOpen) {
        const open = forceOpen ?? !this.settingsPanel?.classList.contains('open');
        this.settingsPanel?.classList.toggle('open', open);
        this.settingsBtn?.classList.toggle('active', open);
        if (!open) this.focusActiveTerminalInput();
    }

    syncHistoryPanelState(forceOpen) {
        const sidebar = document.getElementById('ch-sidebar');
        if (!sidebar) return;

        const open = typeof forceOpen === 'boolean'
            ? forceOpen
            : !sidebar.classList.contains('ch-sidebar-collapsed');
        const layout = sidebar.closest('.vb-terminal-focus-layout');

        sidebar.classList.toggle('ch-sidebar-collapsed', !open);
        layout?.classList.toggle('vb-history-under', !open);
        this.historyBtn?.classList.toggle('active', open);
        this.updateFocusContainerHeight();

        if (!open) {
            this.focusActiveTerminalInput();
        }
    }

    toggleHistoryPanel(forceOpen) {
        const sidebar = document.getElementById('ch-sidebar');
        if (!sidebar) return;

        const open = typeof forceOpen === 'boolean'
            ? forceOpen
            : sidebar.classList.contains('ch-sidebar-collapsed');

        this.syncHistoryPanelState(open);
    }

    applyTheme(key) {
        if (!window.CXL_THEMES?.[key]) return;
        try { localStorage.setItem('viberails_terminal_theme', key); } catch {}
        const theme = window.CXL_THEMES[key];
        this.tabs.forEach((tab) => {
            if (tab.instance.vibeTerminal?._terminal) {
                tab.instance.vibeTerminal.setTheme(theme);
            }
        });
        this._themeItems?.forEach(s => s.classList.toggle('active', s.dataset.theme === key));
    }

    applyFontFamily(family) {
        try { localStorage.setItem('viberails_terminal_fontFamily', family); } catch {}
        this.tabs.forEach((tab) => {
            tab.instance.applyFontFamily(family);
        });
    }

    applyRendererPreference(renderer) {
        const useWebgl = renderer === 'webgl';
        try { localStorage.setItem('viberails_terminal_webgl', String(useWebgl)); } catch {}
        this.app.showToast(
            'Renderer Updated',
            'Renderer preference saved. Restart active terminal tabs to apply.',
            'info'
        );
    }

    applyCursorBlink(blink) {
        try { localStorage.setItem('viberails_terminal_cursorBlink', String(blink)); } catch {}
        this.tabs.forEach((tab) => {
            tab.instance.vibeTerminal?.setCursorBlink(blink);
        });
    }

    applyCursorStyle(style) {
        try { localStorage.setItem('viberails_terminal_cursorStyle', style); } catch {}
        this.tabs.forEach((tab) => {
            tab.instance.vibeTerminal?.setCursorStyle(style);
        });
    }

    applyCursorInactiveStyle(style) {
        try { localStorage.setItem('viberails_terminal_cursorInactiveStyle', style); } catch {}
        this.tabs.forEach((tab) => {
            tab.instance.vibeTerminal?.setCursorInactiveStyle(style);
        });
    }
}

export class TerminalController {
    constructor(app) {
        this.app = app;
        this.manager = null;
        this.managerInitPromise = null;
        this.managerGeneration = 0;
    }

    resetLayoutStateForNavigation() {
        this.managerGeneration += 1;
        this.managerInitPromise = null;

        if (!this.manager) {
            return;
        }

        const manager = this.manager;
        this.manager = null;
        manager.resetLayoutStateForNavigation();
        manager.destroy();
    }

    async ensureManager(container) {
        if (this.manager && this.manager.container === container && !this.manager.isDestroyed()) {
            if (this.managerInitPromise) {
                await this.managerInitPromise;
            }
            if (this.manager && this.manager.container === container && !this.manager.isDestroyed()) {
                return this.manager;
            }
        }

        await this.bindTerminalActions(container, null, {});
        if (this.managerInitPromise) {
            await this.managerInitPromise;
        }
        if (this.manager && this.manager.container === container && !this.manager.isDestroyed()) {
            return this.manager;
        }

        return null;
    }

    async bindTerminalActions(container, preselectedEnvId = null, options = {}) {
        const generation = ++this.managerGeneration;

        if (this.manager) {
            this.manager.destroy();
            this.manager = null;
        }

        const manager = new TerminalManager(this.app, container, {
            preselectedEnvId,
            ...options,
            focusView: options.focusView === true || !!container.closest('[data-view="terminal-focus"]')
        });
        this.manager = manager;

        const initPromise = manager.initialize();
        this.managerInitPromise = initPromise;

        try {
            await initPromise;
        } finally {
            if (this.managerInitPromise === initPromise) {
                this.managerInitPromise = null;
            }
        }

        if (this.managerGeneration !== generation || this.manager !== manager || manager.isDestroyed()) {
            return;
        }
    }

    async startTerminal(container, selection) {
        const manager = await this.ensureManager(container);
        if (!manager) {
            return;
        }
        await manager.startFromSelection(selection || DEFAULT_SELECTION);
    }

    /**
     * Wire session state events from AppEventClient to tab progress indicators.
     * Safe to call before the manager is created — handlers null-check this.manager.
     */
    bindSessionEvents(appEventClient) {
        const findTab = (sessionId) => {
            if (!this.manager) return null;
            for (const tab of this.manager.tabs.values()) {
                if (tab.state.sessionId === sessionId) return tab;
            }
            return null;
        };

        appEventClient.on('session_started', (payload) => {
            const tab = findTab(payload?.sessionId);
            if (!tab) return;
            this.manager.updateTabProgress(tab.state.id, { state: 3, value: 0 });
        });

        appEventClient.on('session_busy', (payload) => {
            const tab = findTab(payload?.sessionId);
            if (!tab) return;
            this.manager.updateTabProgress(tab.state.id, { state: 3, value: 0 });
        });

        appEventClient.on('session_idle', (payload) => {
            const tab = findTab(payload?.sessionId);
            const cli = payload?.cli || 'Session';
            if (tab) {
                this.manager.updateTabProgress(tab.state.id, { state: 4, value: 0 });
            } else {
                this.app.showToast(cli, 'Terminal is idle', 'info');
            }
        });

        appEventClient.on('session_completed', (payload) => {
            const tab = findTab(payload?.sessionId);
            const cli = payload?.cli || 'Session';
            const exitCode = payload?.exitCode;
            const exitText = exitCode != null ? ` (exit ${exitCode})` : '';
            if (tab) {
                this.manager.updateTabProgress(tab.state.id, { state: 0, value: 0 });
                this.app.showToast(cli, `Terminal session completed${exitText}`, exitCode === 0 ? 'success' : 'info');
            } else {
                this.app.showToast(cli, `Headless session completed${exitText}`, exitCode === 0 ? 'success' : 'info');
            }
        });
    }

    refreshLayout() {
        if (!this.manager) {
            return;
        }

        this.manager.updateFocusContainerHeight();
        const activeTab = this.manager.getActiveTab?.();
        activeTab?.instance?.scheduleFitPasses?.();
    }

    async startTerminalWithOptions(options, container) {
        const manager = await this.ensureManager(container);
        if (!manager) {
            return { tabId: null, started: false, reusedExisting: false };
        }
        return await manager.startWithOptions(options);
    }

    async focusTerminalTab(container, tabId) {
        const manager = await this.ensureManager(container);
        if (!manager) {
            return false;
        }
        return await manager.focusTab(tabId, { connectIfNeeded: true });
    }

    async loadTerminalFocusView(data = {}) {
        await this.app.refreshDashboardData();

        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = `
            <div class="view vb-terminal-focus-view" data-view="terminal-focus">
                <div class="vb-terminal-focus-layout">
                    ${ChatHistorySidebar.renderHtml()}
                    <div class="vb-terminal-focus-body" data-terminal-focus-content></div>
                </div>
            </div>
        `;

        const root = content.querySelector('[data-view="terminal-focus"]');
        const terminalContent = root?.querySelector('[data-terminal-focus-content]');
        if (!terminalContent) return;

        terminalContent.innerHTML = this.renderTerminalPanel({ focusView: true });
        await this.bindTerminalActions(terminalContent, data.preselectedEnvId || null, {
            focusView: true,
            preferredSelection: data.preferredSelection || null,
            preferredTabId: data.preferredTabId || null
        });

        this._initChatHistorySidebar(root);
    }

    _initChatHistorySidebar(root) {
        new ChatHistorySidebar(this.app).mount(root, {
            onToggle: (open) => this.manager?.syncHistoryPanelState(open)
        });
    }

    renderTerminalPanel(options = {}) {
        const isFocusView = options.focusView === true;
        const focusButtonHtml = isFocusView ? '' : `
                            <button type="button" class="vb-terminal-control-btn icon-btn" id="terminal-popout-btn" title="Open in fullscreen" aria-label="Open in fullscreen">
                                <svg xmlns="http://www.w3.org/2000/svg" width="15" height="15" fill="currentColor" viewBox="0 0 16 16">
                                    <path d="M6 3a2 2 0 0 0-2 2v7a1 1 0 0 0 1 1h7a2 2 0 0 0 2-2V6h-1v5a1 1 0 0 1-1 1H5V5a1 1 0 0 1 1-1z"/>
                                    <path d="M8.5 1a.5.5 0 0 0 0 1h4.793L6.146 9.146a.5.5 0 1 0 .708.708L14 2.707V7.5a.5.5 0 0 0 1 0V1z"/>
                                </svg>
                                <span class="vb-terminal-control-text">Open In Fullscreen</span>
                            </button>
        `;

        return `
            <div class="card ${isFocusView ? 'vb-terminal-page-mode vb-terminal-expanded vb-terminal-focus-card' : 'mb-4'}" id="vb-terminal-panel">
                <div class="card-header d-flex justify-content-between align-items-center gap-3 flex-wrap">
                    <div class="d-flex align-items-center gap-2 flex-wrap">
                        <span class="card-title d-inline-flex align-items-center gap-2 mb-0">
                            <svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" fill="currentColor" viewBox="0 0 576 512" style="opacity: 0.85;">
                                <path d="M9.4 86.6C-3.1 74.1-3.1 53.9 9.4 41.4s32.8-12.5 45.3 0l192 192c12.5 12.5 12.5 32.8 0 45.3l-192 192c-12.5 12.5-32.8 12.5-45.3 0s-12.5-32.8 0-45.3L178.7 256 9.4 86.6zM256 416l288 0c17.7 0 32 14.3 32 32s-14.3 32-32 32l-288 0c-17.7 0-32-14.3-32-32s14.3-32 32-32z"/>
                            </svg>
                            Web Terminal
                        </span>
                        <span class="badge bg-secondary d-none" id="terminal-status-badge"></span>
                    </div>
                    <div class="d-flex gap-2 align-items-center" id="terminal-actions">                        
                        <button class="btn btn-sm btn-outline-danger d-none d-inline-flex align-items-center gap-1" id="terminal-stop-btn" title="Disconnect terminal session">
                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                <path d="M4.646 4.646a.5.5 0 0 1 .708 0L8 7.293l2.646-2.647a.5.5 0 0 1 .708.708L8.707 8l2.647 2.646a.5.5 0 0 1-.708.708L8 8.707l-2.646 2.647a.5.5 0 0 1-.708-.708L7.293 8 4.646 5.354a.5.5 0 0 1 0-.708"/>
                            </svg>
                            <span>Disconnect</span>
                        </button>
                    </div>
                </div>
                <div class="d-flex gap-2 align-items-center flex-wrap px-3 py-2 border-bottom vb-terminal-controls-bar" id="vb-terminal-controls-bar">
                    <select class="form-select form-select-sm" id="terminal-header-select" style="width: auto;">
                        <option value="" disabled selected>Select LLM...</option>
                    </select>
                    <div class="d-flex gap-2 align-items-center vb-terminal-controls-actions">
                        <button class="btn btn-sm btn-outline-success d-inline-flex align-items-center gap-1" id="terminal-start-btn">
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
                    </div>
                </div>
                <div class="vb-terminal-window-shell">
                    <div class="vb-terminal-window-header">
                        <div class="vb-terminal-tab-strip" id="vb-terminal-tab-strip">
                            <div class="vb-terminal-tab-list" id="vb-terminal-tab-list"></div>
                            <button type="button" class="vb-terminal-tab-add" id="vb-terminal-tab-add-btn" title="Open a new terminal tab" aria-label="Open a new terminal tab">+</button>
                            <button type="button" class="vb-terminal-tab-select" id="vb-terminal-tab-select-btn" title="Select CLI/environment for active tab" aria-label="Select CLI/environment">
                                <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10" fill="currentColor" viewBox="0 0 16 16"><path fill-rule="evenodd" d="M1.646 4.646a.5.5 0 0 1 .708 0L8 10.293l5.646-5.647a.5.5 0 0 1 .708.708l-6 6a.5.5 0 0 1-.708 0l-6-6a.5.5 0 0 1 0-.708"/></svg>
                            </button>
                        </div>
                        <div class="vb-terminal-window-controls vb-terminal-window-controls-right">
                            <button type="button" class="vb-terminal-control-btn icon-btn vb-terminal-zoom-btn" id="terminal-zoom-out-btn" title="Decrease font size" aria-label="Decrease font size">&#x2212;</button>
                            <span class="vb-terminal-font-size-label" id="vb-terminal-font-size-label">14</span>
                            <button type="button" class="vb-terminal-control-btn icon-btn vb-terminal-zoom-btn" id="terminal-zoom-in-btn" title="Increase font size" aria-label="Increase font size">+</button>
                            <div class="vb-terminal-download-wrap" id="vb-terminal-download-wrap">
                                <button type="button" class="vb-terminal-control-btn icon-btn" id="terminal-download-btn" title="Save session" aria-label="Save session">
                                    <svg xmlns="http://www.w3.org/2000/svg" width="13" height="13" fill="currentColor" viewBox="0 0 16 16">
                                        <path d="M.5 9.9a.5.5 0 0 1 .5.5v2.5a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-2.5a.5.5 0 0 1 1 0v2.5a2 2 0 0 1-2 2H2a2 2 0 0 1-2-2v-2.5a.5.5 0 0 1 .5-.5"/>
                                        <path d="M7.646 11.854a.5.5 0 0 0 .708 0l3-3a.5.5 0 0 0-.708-.708L8.5 10.293V1.5a.5.5 0 0 0-1 0v8.793L5.354 8.146a.5.5 0 1 0-.708.708z"/>
                                    </svg>
                                </button>
                                <div class="vb-terminal-download-menu" id="vb-terminal-download-menu" hidden>
                                    <button type="button" id="terminal-save-text">Save as Text (.txt)</button>
                                    <button type="button" id="terminal-save-html">Save as HTML (.html)</button>
                                </div>
                            </div>
                            <button type="button" class="vb-terminal-control-btn icon-btn" id="terminal-settings-btn" title="Terminal settings" aria-label="Terminal settings">&#x2699;</button>
                            ${focusButtonHtml}
                        </div>
                    </div>
                    <div class="vb-terminal-window-title-bar">
                        <div class="vb-terminal-window-title" id="vb-terminal-window-title">Terminals run safely in the background even if you navigate away.</div>
                    </div>
                    <div class="card-body p-0" id="terminal-container" style="display: none; overflow: hidden;">
                        <div id="vb-terminal-tab-panels" class="vb-terminal-tab-panels"></div>
                    </div>
                    <div class="card-body text-center text-muted" id="terminal-placeholder">
                        <p class="mb-3">Select a LLM to continue</p>
                        <p class="small mb-0">Use the <strong>+</strong> button to open a tab, then pick a CLI/environment.</p>
                    </div>
                    <div class="vb-terminal-settings-panel" id="vb-terminal-settings-panel">
                        <div class="vb-terminal-settings-header">
                            <span>Terminal Settings</span>
                            <button type="button" id="terminal-settings-close">&#x2715;</button>
                        </div>
                        <div class="vb-terminal-settings-body">
                            <div class="vb-terminal-settings-section">
                                <div class="vb-terminal-settings-section-title">Theme</div>
                                <div class="vb-terminal-settings-theme-list" id="vb-terminal-settings-theme-list"></div>
                            </div>
                            <div class="vb-terminal-settings-section">
                                <div class="vb-terminal-settings-section-title">Rendering</div>
                                <div class="vb-terminal-settings-row">
                                    <label>Renderer</label>
                                    <select id="terminal-settings-renderer">
                                        <option value="canvas">Canvas (Preferred)</option>
                                        <option value="webgl">WebGL (GPU)</option>
                                    </select>
                                </div>
                            </div>
                            <div class="vb-terminal-settings-section">
                                <div class="vb-terminal-settings-section-title">Font</div>
                                <div class="vb-terminal-settings-row">
                                    <label>Family</label>
                                    <select id="terminal-settings-font-family"></select>
                                </div>
                                <div class="vb-terminal-settings-row">
                                    <label>Size</label>
                                    <input type="number" id="terminal-settings-font-size" min="6" max="72">
                                </div>
                            </div>
                            <div class="vb-terminal-settings-section">
                                <div class="vb-terminal-settings-section-title">Cursor</div>
                                <div class="vb-terminal-settings-row">
                                    <label>Blink</label>
                                    <input type="checkbox" id="terminal-settings-cursor-blink">
                                </div>
                                <div class="vb-terminal-settings-row">
                                    <label>Active style</label>
                                    <select id="terminal-settings-cursor-style">
                                        <option value="block">Block</option>
                                        <option value="bar">Bar</option>
                                        <option value="underline">Underline</option>
                                    </select>
                                </div>
                                <div class="vb-terminal-settings-row">
                                    <label>Inactive style</label>
                                    <select id="terminal-settings-cursor-inactive">
                                        <option value="outline">Outline</option>
                                        <option value="block">Block</option>
                                        <option value="bar">Bar</option>
                                        <option value="underline">Underline</option>
                                        <option value="none">None</option>
                                    </select>
                                </div>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        `;
    }
}



