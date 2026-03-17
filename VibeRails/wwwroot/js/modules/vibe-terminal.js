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

const DEFAULT_TERMINAL_FONT_FAMILY = '"Fira Code", "JetBrains Mono", "Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace';
const LIGATURES_ADDON_MODULE_PATH = '../../assets/xterm/addon-ligatures.js';

function isLikelyMobileViewport() {
    try {
        if (window.matchMedia?.('(hover: none) and (pointer: coarse)')?.matches) {
            return true;
        }

        if (window.matchMedia?.('(max-width: 992px)')?.matches) {
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
        fontFamily = DEFAULT_TERMINAL_FONT_FAMILY,
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
        this._fontFamily = fontFamily;

        this._onFitChange = null;
        this._onProgress = null;
        this._lastCols = null;
        this._lastRows = null;
        this._lastFitWidth = null;
        this._lastFitHeight = null;
        this._searchTerm = '';

        this._resizeDebounceId = null;
        this._scrollRafId = null;
        this._fitRafId = null;
        this._fitTimeoutId = null;
        this._resizeObserver = null;
        this._windowResizeHandler = null;
        this._visualViewportResizeHandler = null;
        this._visualViewportScrollHandler = null;
        this._customKeyEventHandlers = [];
        this._followOutput = true;
        this._onScrollDispose = null;

        const metrics = this._getResponsiveMetrics();
        this._terminal = new window.Terminal({
            cols,
            rows,
            cursorBlink: false,
            fontFamily: this._fontFamily,
            fontSize: metrics.fontSize,
            lineHeight: metrics.lineHeight,
            fontLigatures: true,
            allowProposedApi: true,
            unicodeVersion: '11',
            disableStdin,
            convertEol: false,
            minimumContrastRatio: 3,
            cursorStyle: 'block',
            cursorInactiveStyle: 'none',
            theme: DEFAULT_THEME
        });

        this._searchAddon = null;
        this._webLinksAddon = null;
        this._ligaturesAddon = null;
        this._webFontsAddon = null;
        this._imageAddon = null;
        this._progressAddon = null;
        this._ligaturesLoadPromise = null;

        this._terminal.attachCustomKeyEventHandler((event) => this._runCustomKeyEventHandlers(event));

        this._fitAddon = null;
        if (window.FitAddon?.FitAddon) {
            this._fitAddon = new window.FitAddon.FitAddon();
            this._terminal.loadAddon(this._fitAddon);
        }

        if (window.SearchAddon?.SearchAddon) {
            this._searchAddon = new window.SearchAddon.SearchAddon();
            this._terminal.loadAddon(this._searchAddon);
        }

        if (window.WebLinksAddon?.WebLinksAddon) {
            this._webLinksAddon = new window.WebLinksAddon.WebLinksAddon((_event, uri) => {
                if (!uri) {
                    return;
                }

                try {
                    window.open(uri, '_blank', 'noopener,noreferrer');
                } catch {
                    // no-op
                }
            });
            this._terminal.loadAddon(this._webLinksAddon);
        }

        if (window.WebFontsAddon?.WebFontsAddon) {
            this._webFontsAddon = new window.WebFontsAddon.WebFontsAddon({
                onLoaded: () => this.scheduleFitPasses()
            });
            this._terminal.loadAddon(this._webFontsAddon);
        }

        if (window.UnicodeGraphemesAddon?.UnicodeGraphemesAddon) {
            this._terminal.loadAddon(new window.UnicodeGraphemesAddon.UnicodeGraphemesAddon());
            this._terminal.unicode.activeVersion = '15-graphemes';
        }

        // ImageAddon uses WebAssembly which requires 'unsafe-eval' — skip in VS Code webview (strict CSP)
        if (window.ImageAddon?.ImageAddon && !window.__viberails_VSCODE__) {
            this._imageAddon = new window.ImageAddon.ImageAddon();
            this._terminal.loadAddon(this._imageAddon);
        }

        if (window.ProgressAddon?.ProgressAddon) {
            this._progressAddon = new window.ProgressAddon.ProgressAddon();
            this._terminal.loadAddon(this._progressAddon);
            this._progressAddon.onChange((progress) => {
                if (typeof this._onProgress === 'function') {
                    this._onProgress(progress);
                }
            });
        }

        this._bindSearchShortcuts();
        this._loadLigaturesAddon();

        this._terminal.open(this._outputEl);

        const useWebgl = (() => {
            try {
                // WebGL has shown paint artifacts for some users; keep it opt-in.
                return localStorage.getItem('viberails_terminal_webgl') === 'true';
            } catch {
                return false;
            }
        })();

        if (useWebgl && window.WebglAddon?.WebglAddon) {
            try {
                this._terminal.loadAddon(new window.WebglAddon.WebglAddon());
            } catch (e) {
                console.warn('WebGL addon failed, falling back to canvas renderer:', e);
            }
        }

        this._onScrollDispose = this._terminal.onScroll(() => {
            this._followOutput = this._isNearBottom(1);
        });

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

    _runCustomKeyEventHandlers(event) {
        for (const handler of this._customKeyEventHandlers) {
            try {
                if (handler(event) === false) {
                    return false;
                }
            } catch {
                // no-op
            }
        }

        return true;
    }

    _bindSearchShortcuts() {
        this.addCustomKeyEventHandler((event) => {
            if (event.type !== 'keydown') {
                return true;
            }

            const key = (event.key || '').toLowerCase();
            const hasModifier = event.ctrlKey || event.metaKey;
            if (!hasModifier || event.altKey) {
                return true;
            }

            if (key === 'f') {
                if (!this._searchAddon) {
                    return true;
                }
                this.openSearchPrompt();
                return false;
            }

            if (key === 'g') {
                if (!this._searchAddon) {
                    return true;
                }
                if (event.shiftKey) {
                    this.findPrevious();
                } else {
                    this.findNext();
                }

                return false;
            }

            return true;
        });
    }

    _loadLigaturesAddon() {
        // LigaturesAddon calls queryLocalFonts() which is blocked by VS Code webview Permissions Policy
        if (window.__viberails_VSCODE__) { return; }
        if (this._ligaturesLoadPromise || !this._terminal) {
            return;
        }

        this._ligaturesLoadPromise = import(LIGATURES_ADDON_MODULE_PATH)
            .then((module) => {
                if (!this._terminal) {
                    return;
                }

                const LigaturesAddon = module?.LigaturesAddon;
                if (typeof LigaturesAddon !== 'function') {
                    return;
                }

                this._ligaturesAddon = new LigaturesAddon();
                this._terminal.loadAddon(this._ligaturesAddon);
                this.scheduleFitPasses();
            })
            .catch(() => {
                // no-op
            });
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

    set onProgress(callback) {
        this._onProgress = typeof callback === 'function' ? callback : null;
    }

    addCustomKeyEventHandler(handler) {
        if (typeof handler !== 'function') {
            return () => {};
        }

        this._customKeyEventHandlers.push(handler);
        return () => {
            this._customKeyEventHandlers = this._customKeyEventHandlers.filter((item) => item !== handler);
        };
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

        // xterm v6 keeps the helper textarea at the live cursor position for IME.
        // Force the browser caret and focus-ring invisible at runtime so only
        // xterm's canvas cursor is ever visible, even if stylesheet ordering changes.
        ta.style.caretColor = 'transparent';
        ta.style.outline = 'none';
        ta.style.opacity = '0';
        ta.style.pointerEvents = 'none';
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

    paste(text) {
        if (!this._terminal || typeof text !== 'string' || text.length === 0) {
            return;
        }

        this._terminal.paste(text);
    }

    isBracketedPasteModeEnabled() {
        return this._terminal?.modes?.bracketedPasteMode === true;
    }

    createBracketedPastePayload(text) {
        if (typeof text !== 'string' || text.length === 0) {
            return '';
        }

        if (!this.isBracketedPasteModeEnabled()) {
            return text;
        }

        return `\u001b[200~${text}\u001b[201~`;
    }

    attachClipboardPaste(callback) {
        if (!this._terminal) return;

        // Handle ALL paste operations (Ctrl+V and right-click) via the paste event.
        // Using capture + stopImmediatePropagation blocks xterm's own bubble-phase paste
        // listener, which would otherwise also fire onData and cause a double-send.
        const ta = this._terminal.textarea;
        if (ta) {
            ta.addEventListener('paste', (e) => {
                e.preventDefault();
                e.stopImmediatePropagation();
                const text = e.clipboardData?.getData('text/plain') ?? '';
                if (text && typeof callback === 'function') {
                    callback(text);
                }
            }, true);
        }

        // Prevent xterm from processing Ctrl+V as a key; the actual paste text
        // arrives via the paste event above.
        this.addCustomKeyEventHandler((event) => {
            if (event.type === 'keydown'
                && (event.ctrlKey || event.metaKey)
                && !event.altKey
                && (event.key || '').toLowerCase() === 'v') {
                return false;
            }
            return true;
        });
    }

    openSearchPrompt(initialQuery = '') {
        if (!this._searchAddon) {
            return false;
        }

        const seed = typeof initialQuery === 'string' && initialQuery.trim().length > 0
            ? initialQuery.trim()
            : this._searchTerm;

        const promptText = 'Find in terminal (Ctrl/Cmd+G next, Shift+Ctrl/Cmd+G previous):';
        const nextSearchTerm = window.prompt(promptText, seed || '');
        if (nextSearchTerm === null) {
            return false;
        }

        const normalized = nextSearchTerm.trim();
        if (!normalized) {
            return false;
        }

        this._searchTerm = normalized;
        return this.findNext(normalized, { incremental: false });
    }

    findNext(term = '', options = {}) {
        if (!this._searchAddon) {
            return false;
        }

        if (typeof term === 'string' && term.trim().length > 0) {
            this._searchTerm = term.trim();
        }

        if (!this._searchTerm) {
            return false;
        }

        try {
            return this._searchAddon.findNext(this._searchTerm, {
                caseSensitive: false,
                regex: false,
                wholeWord: false,
                ...options
            });
        } catch {
            return false;
        }
    }

    findPrevious(term = '', options = {}) {
        if (!this._searchAddon) {
            return false;
        }

        if (typeof term === 'string' && term.trim().length > 0) {
            this._searchTerm = term.trim();
        }

        if (!this._searchTerm) {
            return false;
        }

        try {
            return this._searchAddon.findPrevious(this._searchTerm, {
                caseSensitive: false,
                regex: false,
                wholeWord: false,
                ...options
            });
        } catch {
            return false;
        }
    }

    write(data) {
        if (!this._terminal || data == null) return;

        const shouldFollow = this._scrollOnWrite && this._followOutput && this._isNearBottom(1);

        if (typeof data === 'string') {
            this._terminal.write(data);
        } else if (data instanceof ArrayBuffer) {
            this._terminal.write(new Uint8Array(data));
        } else if (ArrayBuffer.isView(data)) {
            this._terminal.write(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
        }

        if (shouldFollow && !this._scrollRafId) {
            this._scrollRafId = requestAnimationFrame(() => {
                this._scrollRafId = null;
                this._terminal?.scrollToBottom();
            });
        }
    }

    _isNearBottom(threshold = 1) {
        if (!this._terminal?.buffer?.active) {
            return true;
        }

        const buffer = this._terminal.buffer.active;
        const gap = buffer.baseY - buffer.viewportY;
        return gap <= threshold;
    }

    setFontSize(size, { fit = true, notify = true, forceNotify = false } = {}) {
        const clamped = Math.max(6, Math.min(72, size));
        this._desktopFontSize = clamped;
        this._mobileFontSize = clamped;
        this._lastFitWidth = null;
        this._lastFitHeight = null;
        this._lastCols = null;
        this._lastRows = null;
        if (this._terminal) {
            this._terminal.options.fontSize = clamped;
            if (fit) {
                this.fit({ force: true, notify, forceNotify });
            }
        }
    }

    setFontFamily(family, { fit = true, notify = true, forceNotify = false } = {}) {
        if (typeof family !== 'string' || family.trim().length === 0) {
            return;
        }

        this._fontFamily = family;
        this._lastFitWidth = null;
        this._lastFitHeight = null;
        this._lastCols = null;
        this._lastRows = null;

        if (this._terminal) {
            this._terminal.options.fontFamily = family;
            if (fit) {
                this.fit({ force: true, notify, forceNotify });
            }
        }
    }

    getFontSize() {
        return this._terminal?.options.fontSize ?? this._desktopFontSize;
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
        this._searchTerm = '';
    }

    resetDisplayOnly() {
        if (!this._terminal) {
            return;
        }

        this._terminal.reset();
        this._searchTerm = '';
        this._lastCols = null;
        this._lastRows = null;
    }

    setInteractive(active) {
        if (!this._terminal) return;
        this._terminal.options.cursorBlink = !!active;
    }

    fit({ notify = true, forceNotify = false, force = false } = {}) {
        if (!this._terminal) return false;

        if (this._fitAddon) {
            const rect = this._outputEl.getBoundingClientRect();
            if (rect.width > 0 && rect.height > 0) {
                const metrics = this._getResponsiveMetrics();
                const metricsChanged = this._terminal.options.fontSize !== metrics.fontSize
                    || this._terminal.options.lineHeight !== metrics.lineHeight;
                if (this._terminal.options.fontSize !== metrics.fontSize) {
                    this._terminal.options.fontSize = metrics.fontSize;
                }
                if (this._terminal.options.lineHeight !== metrics.lineHeight) {
                    this._terminal.options.lineHeight = metrics.lineHeight;
                }

                const width = Math.round(rect.width);
                const height = Math.round(rect.height);
                const sizeChanged = this._lastFitWidth !== width || this._lastFitHeight !== height;
                if (!force && !metricsChanged && !sizeChanged) {
                    return false;
                }

                this._lastFitWidth = width;
                this._lastFitHeight = height;

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

        if (this._fitRafId || this._fitTimeoutId) {
            return;
        }

        this._fitRafId = requestAnimationFrame(() => {
            this._fitRafId = null;
            this.fit();
            this._fitTimeoutId = setTimeout(() => {
                this._fitTimeoutId = null;
                this.fit();
            }, 120);
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

        if (this._scrollRafId) {
            cancelAnimationFrame(this._scrollRafId);
            this._scrollRafId = null;
        }

        if (this._fitRafId) {
            cancelAnimationFrame(this._fitRafId);
            this._fitRafId = null;
        }

        if (this._fitTimeoutId) {
            clearTimeout(this._fitTimeoutId);
            this._fitTimeoutId = null;
        }

        if (this._terminal) {
            this._terminal.dispose();
            this._terminal = null;
        }

        if (this._onScrollDispose) {
            try {
                this._onScrollDispose.dispose?.();
            } catch {
                // no-op
            }
            this._onScrollDispose = null;
        }

        this._customKeyEventHandlers = [];

        this._fitAddon = null;
        this._searchAddon = null;
        this._webLinksAddon = null;
        this._ligaturesAddon = null;
        this._webFontsAddon = null;
        this._imageAddon = null;
        this._progressAddon = null;
        this._ligaturesLoadPromise = null;
        this._onFitChange = null;
        this._onProgress = null;
        this._lastCols = null;
        this._lastRows = null;
        this._lastFitWidth = null;
        this._lastFitHeight = null;
        this._searchTerm = '';
    }
}
