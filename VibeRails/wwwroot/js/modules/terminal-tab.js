import { VibeTerminal } from './vibe-terminal.js';

const RESIZE_PREFIX = '__resize__:';

// Trailing-edge debounce for the outgoing `__resize__` message to the PTY.
// Each new `onFitChange` callback restarts this timer (scheduleResizeToPty()
// calls clearPendingResizeToPty() before scheduling), so the server only sees
// one final resize once the user has been quiet on resize events for the
// chosen window. Default (140 ms) matches the community 150-200 ms norm.
//
// The 2000 ms "extended" value is opt-in via the per-terminal settings
// panel (Rendering → Resize debounce; backed by the
// `viberails_terminal_resizeDebounce` localStorage key). It suppresses the
// stacked-repaints bug during slow panel-drags (each unique cols x rows
// would otherwise fire a SIGWINCH, Claude Code repaints once per SIGWINCH,
// xterm.js renders the rapid repaints mid-reflow and persistent stacked
// copies of the UI appear). Trade-off: Claude Code learns the new terminal
// size ~2 s after the user stops dragging, so fast drag-then-type feels
// briefly off until the size settles. See runbooks/terminal/TERMINAL.md
// "Stacked repaints during drag-resize" entry. Do NOT reduce the extended
// value below 500 ms without re-running the slow-sidebar-drag repro
// (session fd7ac97f-0b7b-4c0b-9c32-4daf9030392d).
const RESIZE_SYNC_DEBOUNCE_MS_DEFAULT = 140;
const RESIZE_SYNC_DEBOUNCE_MS_EXTENDED = 2000;

function resolveResizeDebounceMs() {
    try {
        return localStorage.getItem('viberails_terminal_resizeDebounce') === 'extended'
            ? RESIZE_SYNC_DEBOUNCE_MS_EXTENDED
            : RESIZE_SYNC_DEBOUNCE_MS_DEFAULT;
    } catch {
        return RESIZE_SYNC_DEBOUNCE_MS_DEFAULT;
    }
}
const OUTPUT_CURSOR_IDLE_MS = 90;
const CONNECT_FOCUS_RETRY_MS = 180;

// Once the user has typed this many plain characters on a single input line
// (no Enter yet), nudge them toward the "Open in text editor" button — long
// prompts are far nicer to compose in the editor than in a raw TUI line.
const EDITOR_NUDGE_CHAR_THRESHOLD = 120;

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

        // Discovery-nudge counter: length of the current run of plain typed
        // characters since the last Enter. Purely passive — we only count, never
        // buffer the text or send anything to the PTY. Reset on Enter/Ctrl+C.
        this._typedRunLength = 0;
        this._nudgeRequestedThisRun = false;
    }

    hasOpenSocket() {
        return this.socket && this.socket.readyState === WebSocket.OPEN;
    }

    // Inject a block of text into the live PTY exactly as a clipboard paste would
    // (mirrors the attachClipboardPaste handler in ensureTerminal). Bracketed-paste
    // wrapping keeps multi-line text atomic. Paste-only: no trailing newline is
    // appended, so the cursor is left after the text and the user presses Enter
    // themselves. Returns false when there is no open socket to send through.
    injectText(text) {
        if (typeof text !== 'string' || text.length === 0) {
            return false;
        }
        if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return false;
        }
        const payload = this.vibeTerminal?.createBracketedPastePayload(text) ?? text;
        // Defensive cap: the server closes the socket on any single message over
        // TerminalControlProtocol.MaxMessageBytes (256 KiB). Refuse to send an oversized
        // payload rather than kill the session. Callers that want a user-facing message
        // (e.g. the editor modal) pre-check the size before calling.
        if (new TextEncoder().encode(payload).length > 256 * 1024) {
            return false;
        }
        this.socket.send(payload);
        return true;
    }

    // Passive keystroke counter behind the "Open in text editor" discovery nudge.
    // We only COUNT plain printable characters typed on the current line — we never
    // store the text and never send anything to the PTY, so this cannot affect the
    // terminal. The run resets on Enter (\r/\n) or Ctrl+C (\x03); Backspace shrinks
    // it; any chunk containing a control/escape byte (arrow keys, history recall,
    // bracketed-paste markers, function keys) is ignored rather than counted, since
    // it isn't plain typing. When a single run crosses the threshold we ask the
    // manager to surface the one-time nudge (the manager dedupes + honors dismissal).
    _trackTypingForNudge(data) {
        if (typeof data !== 'string' || data.length === 0) {
            return;
        }

        if (data === '\r' || data === '\n' || data === '\x03') {
            this._typedRunLength = 0;
            this._nudgeRequestedThisRun = false;
            return;
        }

        if (data === '\x7f' || data === '\b') {
            this._typedRunLength = Math.max(0, this._typedRunLength - 1);
            return;
        }

        // Skip anything that isn't a clean run of printable characters.
        if (/[\x00-\x1f\x7f]/.test(data)) {
            return;
        }

        this._typedRunLength += data.length;
        if (this._typedRunLength >= EDITOR_NUDGE_CHAR_THRESHOLD && !this._nudgeRequestedThisRun) {
            this._nudgeRequestedThisRun = true;
            this.manager?.maybeShowEditorNudge();
        }
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
            this._trackTypingForNudge(data);
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

                // A long paste is exactly when the editor scratchpad shines, so
                // surface the one-time discovery nudge for pasted prompts too. The
                // typing counter (_trackTypingForNudge) deliberately ignores paste,
                // so without this a big pasted prompt would never trigger the tip.
                if (typeof text === 'string' && text.length >= EDITOR_NUDGE_CHAR_THRESHOLD) {
                    this.manager?.maybeShowEditorNudge();
                }
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

    scheduleResizeToPty({ force = false, delayMs = null } = {}) {
        if (!this.isActive || !this.vibeTerminal || !this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return;
        }

        const effectiveDelay = delayMs ?? resolveResizeDebounceMs();
        this.clearPendingResizeToPty();
        this._pendingResizeSyncTimeoutId = window.setTimeout(() => {
            this._pendingResizeSyncTimeoutId = null;
            this.sendResizeToPty({ force });
        }, effectiveDelay);
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
            // to xterm.write via queueMicrotask, which drains at the end of the current
            // task. Zero delay, no cross-task batching. Immune to occlusion throttling.
            // We do NOT use setTimeout (Chromium clamps it to 1s when the renderer is
            // occluded — VS Code webview is occluded during cold start, before the
            // workbench finishes its first paint — turning every echo into an ~800ms
            // stall). We also do NOT use rAF, because rAF couples byte *processing* to
            // visibility: an occluded webview would accumulate bytes in pendingChunks
            // until visible, then dump them in one big synchronous parse. We also tried
            // scheduler.postTask({ delay: 10 }) as a runtime-selectable alternative for
            // cross-task batching — pulled 2026-05-07 because it felt slow in practice
            // (suspected delay clamping or scheduler queuing under VS Code's webview).
            // Multi-frame TUI redraw tearing is already prevented by the server-side
            // coalesce (NormalCoalesceDelayMs in WebSocketConsumer.cs) and by xterm's
            // per-frame rAF render, not by this client-side gate.
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

                // Cursor suppression is CODEX-ONLY (TERMINAL.md "## 2026-06-11").
                // Codex's repaints briefly park a visible cursor on its status line,
                // and the in-box Windows ConPTY re-emits that transient state outside
                // the ?2026 sync brackets, so xterm paints it (~10x/sec hop while
                // thinking). Hiding the cursor during output masks it; Codex's ~32ms
                // output cadence stays under the 90ms restore timer, so the timer's
                // own blink artifact doesn't manifest for Codex.
                // Do NOT extend this to Claude: its output gaps straddle 90ms and the
                // suppression itself becomes a ~3Hz blink — the bug fixed in 1.7.3
                // (d1d273d). Claude manages cursor visibility fine via DECTCEM.
                if ((this.state.cli || '').toLowerCase() === 'codex') {
                    this.vibeTerminal?.suppressCursorDuringOutput?.(OUTPUT_CURSOR_IDLE_MS);
                }
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
            this.state.cli = response?.cli || body?.cli || null;
            this.state.workingDirectory = response?.workingDirectory || body?.workingDirectory || this.state.workingDirectory || null;
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
            return response || true;
        } catch (error) {
            this.state.hasActiveSession = false;
            this.state.sessionId = null;
            this.state.cli = null;
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
        this.state.cli = null;
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
