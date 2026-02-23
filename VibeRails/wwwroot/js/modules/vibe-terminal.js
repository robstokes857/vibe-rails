const DEFAULT_THEME = {
    background: '#1e1e1e',
    foreground: '#d4d4d4',
    cursor: '#d4d4d4',
    cursorAccent: '#1e1e1e',
    selectionBackground: '#264f78',
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
};

function isLikelyMobileViewport() {
    try {
        if (window.matchMedia?.('(hover: none) and (pointer: coarse)')?.matches) {
            return true;
        }

        if (window.matchMedia?.('(max-width: 900px)')?.matches) {
            return true;
        }
    } catch {
        // no-op
    }

    return false;
}

/**
 * Reusable xterm.js renderer for all Web UI terminal surfaces.
 * Handles creation, fit lifecycle, resize listeners, and safe byte writes.
 */
export class VibeTerminal {
    constructor({
        outputEl,
        cols = 120,
        rows = 30,
        disableStdin = false,
        desktopFontSize = 14,
        mobileFontSize = 14,
        desktopLineHeight = 1.12,
        mobileLineHeight = 1.2,
        scrollOnWrite = true
    } = {}) {
        if (!outputEl) {
            throw new Error('VibeTerminal requires { outputEl }.');
        }
        if (typeof window.Terminal !== 'function') {
            throw new Error('xterm Terminal global was not found. Load xterm.js before VibeTerminal.');
        }

        this._outputEl = outputEl;
        this._desktopFontSize = desktopFontSize;
        this._mobileFontSize = mobileFontSize;
        this._desktopLineHeight = desktopLineHeight;
        this._mobileLineHeight = mobileLineHeight;
        this._scrollOnWrite = scrollOnWrite;

        this._onFitChange = null;
        this._lastCols = null;
        this._lastRows = null;

        this._resizeDebounceId = null;
        this._resizeObserver = null;
        this._windowResizeHandler = null;
        this._visualViewportResizeHandler = null;
        this._visualViewportScrollHandler = null;

        const metrics = this._getResponsiveMetrics();
        this._terminal = new window.Terminal({
            cols,
            rows,
            cursorBlink: false,
            fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
            fontSize: metrics.fontSize,
            lineHeight: metrics.lineHeight,
            allowProposedApi: true,
            unicodeVersion: '11',
            disableStdin,
            convertEol: false,
            cursorStyle: 'block',
            cursorInactiveStyle: 'none',
            theme: DEFAULT_THEME
        });

        this._fitAddon = null;
        if (window.FitAddon?.FitAddon) {
            this._fitAddon = new window.FitAddon.FitAddon();
            this._terminal.loadAddon(this._fitAddon);
        }

        this._terminal.open(this._outputEl);
        this.patchTextarea();
    }

    _getResponsiveMetrics() {
        const mobile = isLikelyMobileViewport();
        return {
            fontSize: mobile ? this._mobileFontSize : this._desktopFontSize,
            lineHeight: mobile ? this._mobileLineHeight : this._desktopLineHeight
        };
    }

    _notifyFitChange(force = false) {
        if (typeof this._onFitChange !== 'function' || !this._terminal) {
            return;
        }

        const cols = this._terminal.cols;
        const rows = this._terminal.rows;
        if (!force && cols === this._lastCols && rows === this._lastRows) {
            return;
        }

        this._lastCols = cols;
        this._lastRows = rows;
        this._onFitChange(cols, rows);
    }

    _debounceFit(delayMs) {
        if (this._resizeDebounceId) {
            clearTimeout(this._resizeDebounceId);
        }
        this._resizeDebounceId = setTimeout(() => this.fit(), delayMs);
    }

    get terminal() {
        return this._terminal;
    }

    get textarea() {
        return this._terminal?.textarea || null;
    }

    get cols() {
        return this._terminal?.cols ?? 80;
    }

    get rows() {
        return this._terminal?.rows ?? 24;
    }

    set onFitChange(callback) {
        this._onFitChange = typeof callback === 'function' ? callback : null;
    }

    patchTextarea() {
        const ta = this.textarea;
        if (!ta) return;

        ta.setAttribute('autocorrect', 'off');
        ta.setAttribute('autocapitalize', 'none');
        ta.setAttribute('autocomplete', 'off');
        ta.setAttribute('spellcheck', 'false');
        ta.setAttribute('data-gramm', 'false');
        ta.setAttribute('data-gramm_editor', 'false');
        ta.setAttribute('data-enable-grammarly', 'false');
        ta.spellcheck = false;
    }

    onData(callback) {
        if (!this._terminal || typeof callback !== 'function') {
            return () => {};
        }

        const disposable = this._terminal.onData(callback);
        return () => {
            try {
                disposable?.dispose?.();
            } catch {
                // no-op
            }
        };
    }

    attachClipboardPaste(callback) {
        if (!this._terminal) return;

        this._terminal.attachCustomKeyEventHandler((event) => {
            const isPaste = event.type === 'keydown'
                && (event.ctrlKey || event.metaKey)
                && !event.altKey
                && (event.key || '').toLowerCase() === 'v';

            if (!isPaste) {
                return true;
            }

            if (typeof callback !== 'function' || typeof navigator.clipboard?.readText !== 'function') {
                return false;
            }

            navigator.clipboard.readText()
                .then((text) => {
                    if (text) {
                        callback(text);
                    }
                })
                .catch(() => {
                    // Clipboard permission may be denied.
                });

            return false;
        });
    }

    write(data) {
        if (!this._terminal || data == null) return;

        if (typeof data === 'string') {
            this._terminal.write(data);
        } else if (data instanceof ArrayBuffer) {
            this._terminal.write(new Uint8Array(data));
        } else if (ArrayBuffer.isView(data)) {
            this._terminal.write(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
        }

        if (this._scrollOnWrite) {
            this._terminal.scrollToBottom();
        }
    }

    focus() {
        if (!this._terminal) return;
        this._terminal.focus();
        this.patchTextarea();
    }

    reset() {
        if (this._terminal) {
            this._terminal.reset();
        }
    }

    setInteractive(active) {
        if (!this._terminal) return;
        this._terminal.options.cursorBlink = !!active;
    }

    fit({ notify = true, forceNotify = false } = {}) {
        if (!this._terminal) return false;

        if (this._fitAddon) {
            const rect = this._outputEl.getBoundingClientRect();
            if (rect.width > 0 && rect.height > 0) {
                const metrics = this._getResponsiveMetrics();
                if (this._terminal.options.fontSize !== metrics.fontSize) {
                    this._terminal.options.fontSize = metrics.fontSize;
                }
                if (this._terminal.options.lineHeight !== metrics.lineHeight) {
                    this._terminal.options.lineHeight = metrics.lineHeight;
                }

                try {
                    this._fitAddon.fit();
                } catch {
                    return false;
                }
            } else {
                return false;
            }
        }

        if (notify) {
            this._notifyFitChange(forceNotify);
        }
        return true;
    }

    scheduleFitPasses() {
        if (!this._terminal) return;

        requestAnimationFrame(() => {
            this.fit();
            setTimeout(() => this.fit(), 120);
        });
    }

    startResizeHandling({
        debounceMs = 100,
        includeVisualViewport = true,
        includeVisualViewportScroll = false
    } = {}) {
        this.stopResizeHandling();

        this._windowResizeHandler = () => this._debounceFit(debounceMs);
        window.addEventListener('resize', this._windowResizeHandler);

        if (includeVisualViewport && window.visualViewport) {
            this._visualViewportResizeHandler = () => this._debounceFit(debounceMs);
            window.visualViewport.addEventListener('resize', this._visualViewportResizeHandler);

            if (includeVisualViewportScroll) {
                this._visualViewportScrollHandler = () => this._debounceFit(debounceMs);
                window.visualViewport.addEventListener('scroll', this._visualViewportScrollHandler);
            }
        }

        if (typeof ResizeObserver !== 'undefined') {
            this._resizeObserver = new ResizeObserver(() => this._debounceFit(debounceMs));
            this._resizeObserver.observe(this._outputEl);
        }
    }

    stopResizeHandling() {
        if (this._resizeDebounceId) {
            clearTimeout(this._resizeDebounceId);
            this._resizeDebounceId = null;
        }

        if (this._resizeObserver) {
            try {
                this._resizeObserver.disconnect();
            } catch {
                // no-op
            }
            this._resizeObserver = null;
        }

        if (this._windowResizeHandler) {
            window.removeEventListener('resize', this._windowResizeHandler);
            this._windowResizeHandler = null;
        }

        if (window.visualViewport) {
            if (this._visualViewportResizeHandler) {
                window.visualViewport.removeEventListener('resize', this._visualViewportResizeHandler);
                this._visualViewportResizeHandler = null;
            }

            if (this._visualViewportScrollHandler) {
                window.visualViewport.removeEventListener('scroll', this._visualViewportScrollHandler);
                this._visualViewportScrollHandler = null;
            }
        }
    }

    dispose() {
        this.stopResizeHandling();

        if (this._terminal) {
            this._terminal.dispose();
            this._terminal = null;
        }

        this._fitAddon = null;
        this._onFitChange = null;
        this._lastCols = null;
        this._lastRows = null;
    }
}
