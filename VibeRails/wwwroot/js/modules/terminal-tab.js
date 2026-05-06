import { VibeTerminal } from './vibe-terminal.js';

const RESIZE_PREFIX = '__resize__:';
const VIEWER_SNAPSHOT_REPLAY_COMMAND = '__cmd__:replay';

// Collapse layout-settle/font-load fit bursts into one PTY resize so ConPTY does not
// redraw the full TUI multiple times while the browser is still stabilizing.
const RESIZE_SYNC_DEBOUNCE_MS = 140;
// Cross-task coalesce window for the optional scheduler.postTask flush mode.
// scheduler.postTask is not subject to occlusion throttling, so this delay
// is honored on cold start (unlike setTimeout, which Chromium clamps to 1s).
const POST_TASK_COALESCE_DELAY_MS = 10;
const OUTPUT_CURSOR_IDLE_MS = 90;
const CONNECT_FOCUS_RETRY_MS = 180;

export class TerminalTab {
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
        this._pendingResizeSyncTimeoutId = null;
        this._initialConnectActive = false;
        this._connectFocusTimeouts = [];
        this.statusController = null;
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
        this.vibeTerminal.onFitChange = () => this.scheduleResizeToPty();
        this.vibeTerminal.onProgress = (progress) => this.manager.updateTabProgress(this.state.id, progress);
        this.terminal = this.vibeTerminal.terminal;
        this.manager.applySavedTerminalSettingsForTab(this.state.id);

        this.configureInputTarget();
        this.installInputFocusHandlers();

        this.onDataDispose = this.vibeTerminal.onData((data) => {
            this.statusController?.onTerminalData(data);
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

        this.vibeTerminal.focus({ preventScroll: true });
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
        this.clearPendingResizeToPty();
        this._initialConnectActive = false;
        this.vibeTerminal?.restoreSuppressedCursor?.();
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
        const timeoutId = window.setTimeout(() => {
            if (!this.isActive || this.socket !== socket) {
                return;
            }

            this.focusInput();
        }, CONNECT_FOCUS_RETRY_MS);
        this._connectFocusTimeouts.push(timeoutId);
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
        this.statusController?.dispose();
        this.statusController = null;
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

        this.clearPendingResizeToPty();
        this.teardownResizeHandling();
    }

    clearPendingResizeToPty() {
        if (this._pendingResizeSyncTimeoutId) {
            clearTimeout(this._pendingResizeSyncTimeoutId);
            this._pendingResizeSyncTimeoutId = null;
        }
    }

    scheduleResizeToPty({ force = false, delayMs = RESIZE_SYNC_DEBOUNCE_MS } = {}) {
        if (!this.isActive || !this.vibeTerminal || !this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return;
        }

        this.clearPendingResizeToPty();
        this._pendingResizeSyncTimeoutId = window.setTimeout(() => {
            this._pendingResizeSyncTimeoutId = null;
            this.sendResizeToPty({ force });
        }, delayMs);
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

        // Reset resize signature so the next fit sends a fresh resize to the PTY.
        // Do NOT clear the display — xterm.js reflow engine preserves scrollback on resize.
        if (this.isActive && this.hasOpenSocket()) {
            this.lastResizeSignature = null;
        }

        this.vibeTerminal.setFontSize(size, { fit: false });

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

    requestViewerSnapshotReplay() {
        if (!this.hasOpenSocket()) {
            return false;
        }

        try {
            // Do not reset the local xterm here. The socket is still open and
            // live PTY bytes can be in flight in the WS pipe between this send
            // and the snapshot reply. A local reset would paint those in-flight
            // bytes onto a blank terminal until the snapshot prologue lands.
            // The server-side snapshot prologue (1049/1047/47 off, 2J/3J/H,
            // mode resets) is sufficient on its own for the active-socket
            // refresh path. The reconnect path in connect() still resets
            // locally because it has stale state that no server prologue is
            // about to wipe.
            this.socket.send(VIEWER_SNAPSHOT_REPLAY_COMMAND);
            return true;
        } catch {
            return false;
        }
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
        // Reuse the live xterm DOM across reconnects. Recreating it remounts
        // xterm's DOM children, and the synchronous fit() below can then
        // measure cell metrics before the browser paints them. Reusing the DOM
        // keeps fit stable, but we still need a full xterm protocol reset so
        // stale modes (alt-screen, bracketed paste, app cursor, mouse tracking)
        // do not leak into the incoming server snapshot.
        const hadExistingTerminal = !!this.vibeTerminal;
        this.ensureTerminal();
        if (hadExistingTerminal) {
            this.vibeTerminal?.resetForSnapshotReplay?.();
        }
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

        // FitAddon can return tiny pre-layout dimensions (e.g. 21x17) when the
        // container hasn't been measured yet — observed on reconnect after the
        // machine wakes from sleep. Sending those in the WS URL pre-resizes the
        // PTY into a useless geometry, the snapshot replays at the wrong size,
        // and the CLI redraws on a bad grid. If the pre-connect fit looks
        // suspicious, skip the URL hint — the post-onopen fit + scheduleFitPasses
        // will send a correct __resize__ once the DOM has settled.
        const preConnectDimsLookSane = preConnectCols >= 32 && preConnectRows >= 8;

        // Prime the resize signature with the pre-connect dimensions. The server
        // receives these in the WebSocket URL and resizes the PTY before sending
        // the replay, so the post-connect fit in onopen must not re-send the same
        // dimensions — a redundant __resize__ triggers SIGWINCH, causing TUI apps
        // to redraw right on top of the just-loaded replay (cursor flicker).
        // When the pre-connect dims aren't trustworthy we leave the signature
        // null so the post-onopen sendResizeToPty *will* fire with real dims.
        this.lastResizeSignature = preConnectDimsLookSane
            ? `${preConnectCols}x${preConnectRows}`
            : null;

        this.state.status = 'connecting';
        this.manager.updateUi();

        const tabToken = this.getSafeTabTokenForWebSocket(
            sessionStorage.getItem('viberails_tab')
        );
        const urlCols = preConnectDimsLookSane ? preConnectCols : 0;
        const urlRows = preConnectDimsLookSane ? preConnectRows : 0;
        const wsUrl = this.manager.getWebSocketUrl(this.state.id, urlCols, urlRows);
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
                this.statusController?.onSocketOpen();
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

            // Client-side write coalescing: buffer incoming binary frames and hand them
            // to xterm.write through one of two flush mechanisms, selectable at runtime
            // from Terminal Settings → "Output Coalescing":
            //   - 'microtask' (default): queueMicrotask, drains at end of current task.
            //     Zero delay, no cross-task batching. Immune to occlusion throttling.
            //   - 'postTask': scheduler.postTask({ delay: 10 }). 10ms cross-task batch
            //     window — the cross-task coalescing the original setTimeout(10) gave us
            //     — but NOT subject to Chromium's 1s occlusion clamp on setTimeout.
            //     Falls back to queueMicrotask if scheduler.postTask is unavailable.
            // We do NOT use setTimeout (Chromium clamps it to 1s when the renderer is
            // occluded — VS Code webview is occluded during cold start, before the
            // workbench finishes its first paint — turning every echo into an ~800ms
            // stall). We also do NOT use rAF, because rAF couples byte *processing* to
            // visibility: an occluded webview would accumulate bytes in pendingChunks
            // until visible, then dump them in one big synchronous parse. Multi-frame
            // TUI redraw tearing is already prevented by the server-side coalesce
            // (NormalCoalesceDelayMs in WebSocketConsumer.cs) and by xterm's per-frame
            // rAF render, not by this client-side gate.
            let replayFocusDone = false;
            let pendingChunks = [];
            let flushQueued = false;

            const flushPendingChunks = () => {
                flushQueued = false;
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

                this.vibeTerminal?.suppressCursorDuringOutput?.(OUTPUT_CURSOR_IDLE_MS);
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
                if (!flushQueued) {
                    flushQueued = true;
                    const mode = this.manager?._outputCoalesceMode;
                    if (mode === 'postTask' && typeof scheduler !== 'undefined' && scheduler?.postTask) {
                        scheduler.postTask(flushPendingChunks, {
                            delay: POST_TASK_COALESCE_DELAY_MS,
                            priority: 'user-visible',
                        });
                    } else {
                        queueMicrotask(flushPendingChunks);
                    }
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

                // Microtasks can't be cancelled, but flushPendingChunks bails out
                // when `this.socket !== socket`, so any queued flush after this no-ops.
                pendingChunks = [];
                flushQueued = false;
                this.teardownResizeHandling();
                this.clearConnectFocusTimeouts();
                this._initialConnectActive = false;
                this.socket = null;
                this.lastResizeSignature = null;

                this.state.status = this.state.hasActiveSession ? 'disconnected' : 'not-started';
                if (this.state.hasActiveSession) {
                    this.statusController?.onSocketClose();
                }
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
            if (body.resumeSessionId) {
                this.statusController?.markResumedSession();
            } else if (this._launchHasInitialPrompt(body)) {
                this.statusController?.markInitialPrompt();
            }
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

    _launchHasInitialPrompt(body) {
        // Mirror the server's env.customPrompt → initialPrompt resolution so the tab
        // can land directly in THINKING when the agent will boot with a pre-filled
        // prompt. Explicit body.initialPrompt wins; otherwise look up the env by name
        // in the already-loaded environments list. Default LLMs (not in the list)
        // fall through to false — they don't carry customPrompt today.
        if (body?.initialPrompt && body.initialPrompt.trim()) return true;
        if (!body?.environmentName) return false;
        const envs = this.manager.app.data?.environments || [];
        const env = envs.find(e => e.name === body.environmentName);
        return !!(env?.customPrompt && env.customPrompt.trim());
    }
}
