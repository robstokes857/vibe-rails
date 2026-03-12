import { VibeTerminal } from './vibe-terminal.js';

const RESIZE_PREFIX = '__resize__:';
const DEFAULT_SELECTION = null;
const ACTIVE_TAB_KEY = 'viberails_terminal_active_tab_id';
const TAB_SELECTION_PREFIX = 'viberails_terminal_tab_selection_';
const TAB_TITLE_PREFIX = 'viberails_terminal_tab_title_';
const TAB_META_PREFIX = 'viberails_terminal_tab_meta_';

function lower(value) {
    return (value || '').toString().trim().toLowerCase();
}

function capitalize(value) {
    if (!value) return '';
    return value.charAt(0).toUpperCase() + value.slice(1);
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

    disconnect({ disposeTerminal = false, preserveStatus = false } = {}) {
        this.teardownResizeHandling();
        this.disconnectSocketOnly();

        if (disposeTerminal && this.vibeTerminal) {
            this.vibeTerminal.dispose();
            this.vibeTerminal = null;
            this.terminal = null;
            if (this.onDataDispose) {
                try { this.onDataDispose(); } catch (e) { /* no-op */ }
                this.onDataDispose = null;
            }
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

        this.ensureTerminal();
        this.disconnectSocketOnly();
        this.lastResizeSignature = null;
        // Replay rebuilds the current screen; clear any local render state first
        // so reconnects do not append duplicated TUI content.
        this.vibeTerminal?.reset();

        // Fit now that the container is visible (showTerminal was called before
        // connect in activateTab). This gives us the real cols/rows to send to
        // the backend so it can resize the PTY *before* sending the replay.
        if (this.vibeTerminal) {
            this.vibeTerminal.fit({ force: true, notify: false });
        }
        const preConnectCols = this.vibeTerminal?.cols;
        const preConnectRows = this.vibeTerminal?.rows;

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
                    this.fitAndSyncTerminal();
                    this.scheduleFitPasses();
                    this.focusInput();
                }

                resolve(true);
            };

            socket.onmessage = (event) => {
                if (this.socket !== socket) return;
                this.writeData(event.data);
            };

            socket.onclose = (event) => {
                if (this.socket !== socket) return;

                this.teardownResizeHandling();
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
            this.vibeTerminal?.reset();
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
        this._themeSwatches = [];
    }

    isDestroyed() {
        return this._destroyed;
    }

    _loadCursorBlink() {
        try {
            return localStorage.getItem('viberails_terminal_cursorBlink') === 'true';
        } catch {
            return false;
        }
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

    _loadCursorStyle() {
        try {
            return localStorage.getItem('viberails_terminal_cursorStyle') || 'block';
        } catch {
            return 'block';
        }
    }

    _applySavedTerminalSettings(tab) {
        const vibe = tab?.instance?.vibeTerminal;
        const terminal = vibe?._terminal;
        if (!terminal) return;

        const themeKey = this._loadThemePreference();
        if (themeKey && window.CXL_THEMES?.[themeKey]) {
            terminal.options.theme = window.CXL_THEMES[themeKey];
        }

        const cursorStyle = this._loadCursorStyle();
        const cursorBlink = this._loadCursorBlink();
        terminal.options.cursorStyle = cursorStyle;
        terminal.options.cursorBlink = cursorBlink;
    }

    applySavedTerminalSettingsForTab(tabId) {
        this._applySavedTerminalSettings(this.tabs.get(tabId));
    }

    async initialize() {
        if (this._destroyed) {
            return;
        }

        this.panel = this.container.querySelector('#terminal-panel');
        this.tabList = this.container.querySelector('#terminal-tab-list');
        this.tabAdd = this.container.querySelector('#terminal-tab-add-btn');
        this.tabSelect = this.container.querySelector('#terminal-tab-select-btn');
        this.tabPanels = this.container.querySelector('#terminal-tab-panels');
        this.placeholder = this.container.querySelector('#terminal-placeholder');
        this.terminalContainer = this.container.querySelector('#terminal-container');
        this.statusBadge = this.container.querySelector('#terminal-status-badge');
        this.windowTitle = this.container.querySelector('#terminal-window-title');

        this.startBtn = this.container.querySelector('#terminal-start-btn');
        this.reconnectBtn = this.container.querySelector('#terminal-reconnect-btn');
        this.stopBtn = this.container.querySelector('#terminal-stop-btn');
        this.controlsBar = this.container.querySelector('#terminal-controls-bar');
        this.headerSelect = this.container.querySelector('#terminal-header-select');
        this.closeDot = this.container.querySelector('#terminal-close-dot');
        this.minimizeDot = this.container.querySelector('#terminal-minimize-dot');
        this.maximizeDot = this.container.querySelector('#terminal-maximize-dot');
        this.lockBtn = this.container.querySelector('#terminal-lock-btn');
        this.focusBtn = this.container.querySelector('#terminal-popout-btn');
        this.keyboardBtn = this.container.querySelector('#terminal-keyboard-btn');

        this.zoomInBtn     = this.container.querySelector('#terminal-zoom-in-btn');
        this.zoomOutBtn    = this.container.querySelector('#terminal-zoom-out-btn');
        this.fontSizeLabel = this.container.querySelector('#terminal-font-size-label');
        this.settingsBtn   = this.container.querySelector('#terminal-settings-btn');
        this.settingsPanel = this.container.querySelector('#terminal-settings-panel');
        this.settingsClose = this.container.querySelector('#terminal-settings-close');

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
        document.body.classList.remove('terminal-active-session');

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
                return `env:${env.id}:${lower(env.cli)}`;
            }
        }

        return DEFAULT_SELECTION;
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
        this.settingsBtn?.addEventListener('click',   () => this.toggleSettingsPanel());
        this.settingsClose?.addEventListener('click', () => this.toggleSettingsPanel(false));
        this.container.querySelector('#terminal-settings-font-size')
            ?.addEventListener('change', (e) => this.adjustFontSize(0, parseInt(e.target.value, 10)));
        this.container.querySelector('#terminal-settings-font-family')
            ?.addEventListener('change', (e) => this.applyFontFamily(e.target.value));
        this.container.querySelector('#terminal-settings-cursor-style')
            ?.addEventListener('change', (e) => this.applyCursorStyle(e.target.value));
        this.container.querySelector('#terminal-settings-cursor-blink')
            ?.addEventListener('change', (e) => this.applyCursorBlink(e.target.value === 'true'));
        this.container.querySelector('#terminal-settings-renderer')
            ?.addEventListener('change', (e) => this.applyRendererPreference(e.target.value));
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
        item.className = 'terminal-tab-item';
        item.dataset.tabId = state.id;

        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'terminal-tab-button';
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
        close.className = 'terminal-tab-close';
        close.innerHTML = '&times;';
        close.title = 'Close tab';
        close.addEventListener('click', (event) => {
            event.stopPropagation();
            void this.closeTab(state.id);
        });

        item.appendChild(button);
        item.appendChild(close);

        const panel = document.createElement('div');
        panel.className = 'terminal-tab-panel';
        panel.dataset.tabId = state.id;
        panel.style.display = 'none';

        const terminalElement = document.createElement('div');
        terminalElement.className = 'terminal-element';
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
            this.app.showError(`Failed to close terminal tab: ${error.message}`);
            return;
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
        if (!selection) {
            return {
                cli: null,
                envId: null,
                environmentName: null,
                displayName: 'Select LLM to launch Terminal. Terminals run safely in the background even if you navigate away.'
            };
        }
        if (!selection.startsWith('env:')) {
            const cli = selection.startsWith('base:')
                ? lower(selection.replace('base:', ''))
                : lower(selection);
            return {
                cli,
                envId: null,
                environmentName: null,
                displayName: capitalize(cli)
            };
        }

        const parts = selection.split(':');
        const envId = Number.parseInt(parts[1], 10);
        const cli = lower(parts[2]);
        const env = (this.app.data.environments || []).find((item) => item.id === envId);
        const envName = env?.name || `Env ${envId}`;

        return {
            cli,
            envId,
            environmentName: env?.name || null,
            displayName: `${envName} (${cli})`
        };
    }

    resolveSelectionFromOptions(options) {
        const cli = lower(options?.cli || 'claude');
        if (!options?.environmentName) {
            return `base:${cli}`;
        }

        const env = (this.app.data.environments || []).find((item) =>
            lower(item.name) === lower(options.environmentName)
            && lower(item.cli) === cli
        );

        return env ? `env:${env.id}:${cli}` : `base:${cli}`;
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
            iconSpan.className = 'terminal-tab-icon';
            iconSpan.textContent = `${tab.state.icon} `;
            button.appendChild(iconSpan);
        }

        const labelSpan = document.createElement('span');
        labelSpan.className = 'terminal-tab-label';
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
            item.style.removeProperty('--terminal-tab-accent');
            return;
        }

        item.classList.add('has-custom-accent');
        item.style.setProperty('--terminal-tab-accent', accent);
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

    getSelectionOptions() {
        const options = [
            { group: 'Base CLIs', value: 'base:claude', label: 'Claude (default)' },
            { group: 'Base CLIs', value: 'base:codex', label: 'Codex (default)' },
            { group: 'Base CLIs', value: 'base:gemini', label: 'Gemini (default)' },
            { group: 'Base CLIs', value: 'base:copilot', label: 'Copilot (default)' }
        ];

        (this.app.data.environments || []).forEach((env) => {
            options.push({
                group: 'Custom Environments',
                value: `env:${env.id}:${lower(env.cli)}`,
                label: `${env.name} (${lower(env.cli)})`
            });
        });

        return options;
    }

    populateSelect() {
        if (!this.headerSelect || this.headerSelect.tagName !== 'SELECT') return;

        this.headerSelect.innerHTML = '<option value="" disabled>Select LLM...</option>';

        const groups = {};
        this.getSelectionOptions().forEach((option) => {
            if (!groups[option.group]) groups[option.group] = [];
            groups[option.group].push(option);
        });

        Object.keys(groups).forEach(groupName => {
            const optgroup = document.createElement('optgroup');
            optgroup.label = groupName;
            groups[groupName].forEach(option => {
                const opt = document.createElement('option');
                opt.value = option.value;
                opt.textContent = option.label;
                optgroup.appendChild(opt);
            });
            this.headerSelect.appendChild(optgroup);
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
                el.classList.remove('terminal-selection-shake');
                // Force reflow so repeated clicks replay the animation.
                void el.offsetWidth;
                el.classList.add('terminal-selection-shake');
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
        document.body.classList.toggle('terminal-active-session', hasActiveSession);

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
            return { text: 'Connected', className: 'bg-success' };
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
        if (!selection || !selection.startsWith('env:')) {
            return { preselectedEnvId: null };
        }

        const parts = selection.split(':');
        const envId = Number.parseInt(parts[1], 10);
        return {
            preselectedEnvId: Number.isFinite(envId) ? envId : null
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

        this.panel.classList.remove('terminal-minimized', 'terminal-expanded');
        if (mode === 'minimized') {
            this.panel.classList.add('terminal-minimized');
        } else if (mode === 'expanded') {
            this.panel.classList.add('terminal-expanded');
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
    }

    updateWindowControlState() {
        const tab = this.getActiveTab();
        const isMinimized = tab?.state.viewState.mode === 'minimized';
        const isExpanded = tab?.state.viewState.mode === 'expanded';
        const isLocked = tab?.state.viewState.locked === true;
        const isFocusView = this.options.focusView === true;

        const setLabel = (button, text) => {
            const label = button?.querySelector('.terminal-control-text');
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
            this.focusBtn.title = isFocusView
                ? 'Return to dashboard terminal section'
                : 'Open terminal in focused page view';
            setLabel(this.focusBtn, isFocusView ? 'Back to Dashboard' : 'Open In Fullscreen');
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
            this.getActiveTab()?.instance.scheduleFitPasses();
        };

        window.addEventListener('resize', this.lockLayoutHandler);
        this.updateLockedPanelPosition(panel);
        this.getActiveTab()?.instance.scheduleFitPasses();
    }

    disableLockedLayout(panel) {
        const target = panel || this.lockedPanel;
        if (target) {
            target.classList.remove('terminal-locked');
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

        this._themeSwatches = [];
        const themeList = this.container.querySelector('#terminal-settings-theme-list');
        if (themeList && window.CXL_THEMES) {
            for (const [key, theme] of Object.entries(window.CXL_THEMES)) {
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'terminal-settings-theme-swatch';
                btn.dataset.theme = key;
                btn.title = theme.name;
                btn.style.background = `linear-gradient(135deg, ${theme.background} 50%, ${theme.foreground} 50%)`;
                btn.addEventListener('click', () => this.applyTheme(key));
                themeList.appendChild(btn);
                this._themeSwatches.push(btn);
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

        const cursorStyleSelect = this.container.querySelector('#terminal-settings-cursor-style');
        const savedCursorStyle = localStorage.getItem('viberails_terminal_cursorStyle');
        if (cursorStyleSelect && savedCursorStyle) cursorStyleSelect.value = savedCursorStyle;

        const cursorBlinkSelect = this.container.querySelector('#terminal-settings-cursor-blink');
        const savedCursorBlink = localStorage.getItem('viberails_terminal_cursorBlink');
        if (cursorBlinkSelect && savedCursorBlink) cursorBlinkSelect.value = savedCursorBlink;

        const rendererSelect = this.container.querySelector('#terminal-settings-renderer');
        if (rendererSelect) rendererSelect.value = this._loadRendererPreference();

        const savedTheme = this._loadThemePreference();
        if (savedTheme) {
            this._themeSwatches?.forEach((swatch) => {
                swatch.classList.toggle('active', swatch.dataset.theme === savedTheme);
            });
        }
    }

    toggleSettingsPanel(forceOpen) {
        const open = forceOpen ?? !this.settingsPanel?.classList.contains('open');
        this.settingsPanel?.classList.toggle('open', open);
        this.settingsBtn?.classList.toggle('active', open);
        if (!open) this.focusActiveTerminalInput();
    }

    applyTheme(key) {
        if (!window.CXL_THEMES?.[key]) return;
        try { localStorage.setItem('viberails_terminal_theme', key); } catch {}
        const theme = window.CXL_THEMES[key];
        this.tabs.forEach((tab) => {
            if (tab.instance.vibeTerminal?._terminal) {
                tab.instance.vibeTerminal._terminal.options.theme = theme;
            }
        });
        this._themeSwatches?.forEach(s => s.classList.toggle('active', s.dataset.theme === key));
    }

    applyFontFamily(family) {
        try { localStorage.setItem('viberails_terminal_fontFamily', family); } catch {}
        this.tabs.forEach((tab) => {
            tab.instance.applyFontFamily(family);
        });
    }

    applyCursorStyle(style) {
        try { localStorage.setItem('viberails_terminal_cursorStyle', style); } catch {}
        this.tabs.forEach((tab) => {
            if (tab.instance.vibeTerminal?._terminal) {
                tab.instance.vibeTerminal._terminal.options.cursorStyle = style;
            }
        });
    }

    applyCursorBlink(blink) {
        try { localStorage.setItem('viberails_terminal_cursorBlink', blink); } catch {}
        this.tabs.forEach((tab) => {
            if (tab.instance.vibeTerminal?._terminal) {
                tab.instance.vibeTerminal._terminal.options.cursorBlink = blink;
            }
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
            <div class="view terminal-focus-view" data-view="terminal-focus">
                <div class="terminal-focus-body" data-terminal-focus-content></div>
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
    }

    renderTerminalPanel(options = {}) {
        const isFocusView = options.focusView === true;
        const lockButtonHtml = isFocusView ? '' : `
                            <button type="button" class="terminal-control-btn icon-btn" id="terminal-lock-btn" title="Lock terminal in sticky focus mode" aria-label="Lock terminal focus mode">
                                <svg xmlns="http://www.w3.org/2000/svg" width="15" height="15" fill="currentColor" viewBox="0 0 16 16">
                                    <path d="M8 1a3 3 0 0 0-3 3v2H4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-1V4a3 3 0 0 0-3-3m2 5H6V4a2 2 0 1 1 4 0z"/>
                                </svg>
                                <span class="terminal-control-text">Lock Focus</span>
                            </button>
        `;

        return `
            <div class="card ${isFocusView ? 'terminal-page-mode terminal-expanded terminal-focus-card' : 'mb-4'}" id="terminal-panel">
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
                        <button class="btn btn-sm btn-outline-secondary d-inline-flex align-items-center gap-1 d-none d-md-none" id="terminal-keyboard-btn" title="Focus terminal input keyboard">
                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                <path d="M14 5H2a1 1 0 0 0-1 1v4a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1M2 4a2 2 0 0 0-2 2v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2z"/>
                                <path d="M2 7h1v1H2zm2 0h1v1H4zm2 0h1v1H6zm2 0h1v1H8zm2 0h1v1h-1zm2 0h1v1h-1zM2 9h8v1H2z"/>
                            </svg>
                            <span>Keyboard</span>
                        </button>
                        <button class="btn btn-sm btn-outline-danger d-none d-inline-flex align-items-center gap-1" id="terminal-stop-btn" title="Disconnect terminal session">
                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                <path d="M4.646 4.646a.5.5 0 0 1 .708 0L8 7.293l2.646-2.647a.5.5 0 0 1 .708.708L8.707 8l2.647 2.646a.5.5 0 0 1-.708.708L8 8.707l-2.646 2.647a.5.5 0 0 1-.708-.708L7.293 8 4.646 5.354a.5.5 0 0 1 0-.708"/>
                            </svg>
                            <span>Disconnect</span>
                        </button>
                    </div>
                </div>
                <div class="d-flex gap-2 align-items-center flex-wrap px-3 py-2 border-bottom terminal-controls-bar" id="terminal-controls-bar">
                    <select class="form-select form-select-sm" id="terminal-header-select" style="width: auto;">
                        <option value="" disabled selected>Select LLM...</option>
                    </select>
                    <div class="d-flex gap-2 align-items-center terminal-controls-actions">
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
                <div class="terminal-window-shell">
                    <div class="terminal-window-header">
                        <div class="terminal-tab-strip" id="terminal-tab-strip">
                            <div class="terminal-tab-list" id="terminal-tab-list"></div>
                            <button type="button" class="terminal-tab-add" id="terminal-tab-add-btn" title="Open a new terminal tab" aria-label="Open a new terminal tab">+</button>
                            <button type="button" class="terminal-tab-select" id="terminal-tab-select-btn" title="Select CLI/environment for active tab" aria-label="Select CLI/environment">
                                <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10" fill="currentColor" viewBox="0 0 16 16"><path fill-rule="evenodd" d="M1.646 4.646a.5.5 0 0 1 .708 0L8 10.293l5.646-5.647a.5.5 0 0 1 .708.708l-6 6a.5.5 0 0 1-.708 0l-6-6a.5.5 0 0 1 0-.708"/></svg>
                            </button>
                        </div>
                        <div class="terminal-window-controls terminal-window-controls-right">
                            <button type="button" class="terminal-control-btn icon-btn terminal-zoom-btn" id="terminal-zoom-out-btn" title="Decrease font size" aria-label="Decrease font size">&#x2212;</button>
                            <span class="terminal-font-size-label" id="terminal-font-size-label">14</span>
                            <button type="button" class="terminal-control-btn icon-btn terminal-zoom-btn" id="terminal-zoom-in-btn" title="Increase font size" aria-label="Increase font size">+</button>
                            <button type="button" class="terminal-control-btn icon-btn" id="terminal-settings-btn" title="Terminal settings" aria-label="Terminal settings">&#x2699;</button>
                            ${lockButtonHtml}
                            <button type="button" class="terminal-control-btn icon-btn" id="terminal-popout-btn" title="${isFocusView ? 'Return to dashboard' : 'Open in fullscreen'}" aria-label="${isFocusView ? 'Back to dashboard' : 'Open in fullscreen'}">
                                <svg xmlns="http://www.w3.org/2000/svg" width="15" height="15" fill="currentColor" viewBox="0 0 16 16">
                                    <path d="M6 3a2 2 0 0 0-2 2v7a1 1 0 0 0 1 1h7a2 2 0 0 0 2-2V6h-1v5a1 1 0 0 1-1 1H5V5a1 1 0 0 1 1-1z"/>
                                    <path d="M8.5 1a.5.5 0 0 0 0 1h4.793L6.146 9.146a.5.5 0 1 0 .708.708L14 2.707V7.5a.5.5 0 0 0 1 0V1z"/>
                                </svg>
                                <span class="terminal-control-text">${isFocusView ? 'Back to Dashboard' : 'Open In Fullscreen'}</span>
                            </button>
                        </div>
                    </div>
                    <div class="terminal-window-title-bar">
                        <div class="terminal-window-title" id="terminal-window-title">Terminals run safely in the background even if you navigate away.</div>
                    </div>
                    <div class="card-body p-0" id="terminal-container" style="display: none; overflow: hidden;">
                        <div id="terminal-tab-panels" class="terminal-tab-panels"></div>
                    </div>
                    <div class="card-body text-center text-muted" id="terminal-placeholder">
                        <p class="mb-3">Select a LLM to continue</p>
                        <p class="small mb-0">Use the <strong>+</strong> button to open a tab, then pick a CLI/environment.</p>
                    </div>
                    <div class="terminal-settings-panel" id="terminal-settings-panel">
                        <div class="terminal-settings-header">
                            <span>Terminal Settings</span>
                            <button type="button" id="terminal-settings-close">&#x2715;</button>
                        </div>
                        <div class="terminal-settings-body">
                            <div class="terminal-settings-section">
                                <div class="terminal-settings-section-title">Theme</div>
                                <div class="terminal-settings-theme-list" id="terminal-settings-theme-list"></div>
                            </div>
                            <div class="terminal-settings-section">
                                <div class="terminal-settings-section-title">Rendering</div>
                                <div class="terminal-settings-row">
                                    <label>Renderer</label>
                                    <select id="terminal-settings-renderer">
                                        <option value="canvas">Canvas (Recommended)</option>
                                        <option value="webgl">WebGL (GPU)</option>
                                    </select>
                                </div>
                            </div>
                            <div class="terminal-settings-section">
                                <div class="terminal-settings-section-title">Font</div>
                                <div class="terminal-settings-row">
                                    <label>Family</label>
                                    <select id="terminal-settings-font-family"></select>
                                </div>
                                <div class="terminal-settings-row">
                                    <label>Size</label>
                                    <input type="number" id="terminal-settings-font-size" min="6" max="72">
                                </div>
                            </div>
                            <div class="terminal-settings-section">
                                <div class="terminal-settings-section-title">Cursor</div>
                                <div class="terminal-settings-row">
                                    <label>Style</label>
                                    <select id="terminal-settings-cursor-style">
                                        <option value="block">Block</option>
                                        <option value="bar">Bar</option>
                                        <option value="underline">Underline</option>
                                    </select>
                                </div>
                                <div class="terminal-settings-row">
                                    <label>Blink</label>
                                    <select id="terminal-settings-cursor-blink">
                                        <option value="true">On</option>
                                        <option value="false">Off</option>
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

