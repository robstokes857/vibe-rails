const RENDERER_LABELS = {
    webgl: 'WebGL',
    canvas: 'Canvas',
    dom: 'DOM (slow)'
};

export function renderTerminalSettingsPanelHtml() {
    return `
        <div class="vb-terminal-settings-panel" id="vb-terminal-settings-panel">
            <div class="vb-terminal-settings-header">
                <span>Features and Settings</span>
                <button type="button" id="terminal-settings-close">&#x2715;</button>
            </div>
            <div class="vb-terminal-settings-body">
                <div class="vb-terminal-settings-actions" role="group" aria-label="Terminal actions">
                    <button type="button" class="vb-terminal-settings-action-btn" id="terminal-multirun-btn">
                        <i class="fa-solid fa-layer-group" aria-hidden="true"></i>
                        <span>Multi Run</span>
                    </button>
                    <button type="button" class="vb-terminal-settings-action-btn" id="terminal-senddebug-btn">
                        <i class="fa-solid fa-bug" aria-hidden="true"></i>
                        <span>Send debug log</span>
                    </button>
                </div>
                <div class="vb-terminal-settings-section open" data-settings-section="theme">
                    <button type="button" class="vb-terminal-settings-section-title" id="terminal-settings-section-theme" aria-expanded="true" aria-controls="terminal-settings-section-theme-content" data-settings-toggle="theme">
                        <span class="vb-terminal-settings-section-label">
                            <i class="fa-solid fa-palette" aria-hidden="true"></i>
                            <span>Theme</span>
                        </span>
                        <i class="fa-solid fa-chevron-down vb-terminal-settings-section-chevron" aria-hidden="true"></i>
                    </button>
                    <div class="vb-terminal-settings-section-content" id="terminal-settings-section-theme-content" role="region" aria-labelledby="terminal-settings-section-theme">
                        <div class="vb-terminal-settings-section-inner">
                            <div class="vb-terminal-settings-theme-list" id="vb-terminal-settings-theme-list"></div>
                        </div>
                    </div>
                </div>
                <div class="vb-terminal-settings-section" data-settings-section="rendering">
                    <button type="button" class="vb-terminal-settings-section-title" id="terminal-settings-section-rendering" aria-expanded="false" aria-controls="terminal-settings-section-rendering-content" data-settings-toggle="rendering">
                        <span class="vb-terminal-settings-section-label">
                            <i class="fa-solid fa-microchip" aria-hidden="true"></i>
                            <span>Rendering</span>
                        </span>
                        <span class="vb-terminal-settings-section-meta">
                            <span class="vb-terminal-settings-section-status" id="terminal-settings-renderer-active">—</span>
                            <i class="fa-solid fa-chevron-down vb-terminal-settings-section-chevron" aria-hidden="true"></i>
                        </span>
                    </button>
                    <div class="vb-terminal-settings-section-content" id="terminal-settings-section-rendering-content" role="region" aria-labelledby="terminal-settings-section-rendering">
                        <div class="vb-terminal-settings-section-inner">
                            <div class="vb-terminal-settings-row">
                                <label>Renderer</label>
                                <select id="terminal-settings-renderer">
                                    <option value="webgl">WebGL (Preferred)</option>
                                    <option value="canvas">Canvas</option>
                                </select>
                            </div>
                            <div class="vb-terminal-settings-row">
                                <label title="Wait this long after the last resize event before notifying the running CLI of the new size. Extended (2 s) suppresses the 'stacked repaints' bug during slow panel drags, at the cost of a brief delay before Claude Code reflows to the new size.">Resize debounce</label>
                                <select id="terminal-settings-resize-debounce">
                                    <option value="default">Standard (140&nbsp;ms)</option>
                                    <option value="extended">Extended (2&nbsp;s, experimental)</option>
                                </select>
                            </div>
                        </div>
                    </div>
                </div>
                <div class="vb-terminal-settings-section" data-settings-section="font">
                    <button type="button" class="vb-terminal-settings-section-title" id="terminal-settings-section-font" aria-expanded="false" aria-controls="terminal-settings-section-font-content" data-settings-toggle="font">
                        <span class="vb-terminal-settings-section-label">
                            <i class="fa-solid fa-font" aria-hidden="true"></i>
                            <span>Font</span>
                        </span>
                        <i class="fa-solid fa-chevron-down vb-terminal-settings-section-chevron" aria-hidden="true"></i>
                    </button>
                    <div class="vb-terminal-settings-section-content" id="terminal-settings-section-font-content" role="region" aria-labelledby="terminal-settings-section-font">
                        <div class="vb-terminal-settings-section-inner">
                            <div class="vb-terminal-settings-row">
                                <label>Size</label>
                                <input type="number" id="terminal-settings-font-size" min="6" max="72">
                            </div>
                        </div>
                    </div>
                </div>
                <div class="vb-terminal-settings-section" data-settings-section="notifications">
                    <button type="button" class="vb-terminal-settings-section-title" id="terminal-settings-section-notifications" aria-expanded="false" aria-controls="terminal-settings-section-notifications-content" data-settings-toggle="notifications">
                        <span class="vb-terminal-settings-section-label">
                            <i class="fa-solid fa-bell" aria-hidden="true"></i>
                            <span>Notifications</span>
                        </span>
                        <i class="fa-solid fa-chevron-down vb-terminal-settings-section-chevron" aria-hidden="true"></i>
                    </button>
                    <div class="vb-terminal-settings-section-content" id="terminal-settings-section-notifications-content" role="region" aria-labelledby="terminal-settings-section-notifications">
                        <div class="vb-terminal-settings-section-inner">
                            <div class="vb-terminal-settings-row">
                                <label for="terminal-settings-computer-name">Computer name</label>
                                <input type="text" id="terminal-settings-computer-name" maxlength="80" autocomplete="off" placeholder="This computer">
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    `;
}
// NOTE: the Cursor section (active/inactive style selects) was deliberately
// removed 2026-06-12 — the cursor is owned by the TUI/xterm defaults now.
// Do not reintroduce user-facing cursor styling; see TERMINAL.md
// ("Cursor flicker" entries) for the history that motivated this.

export class TerminalSettings {
    constructor(container, manager) {
        this._container = container;
        this._manager = manager;
        this._themeItems = [];
        this._activeTabId = null;
        this._rendererUnsubs = new Map();

        this._panelEl = null;
        this._panelBtn = null;
        this._closeBtn = null;
        this._fontSizeInput = null;
        this._rendererSelect = null;
        this._rendererStatusEl = null;
        this._resizeDebounceSelect = null;
        this._computerNameInput = null;
        this._themeListEl = null;
        this._sectionToggles = [];
    }

    // ---------- Preference helpers (was _load* on the manager) ----------

    loadFontSize() {
        try { return parseInt(localStorage.getItem('viberails_terminal_fontSize'), 10) || 14; }
        catch { return 14; }
    }

    saveFontSize(size) {
        try { localStorage.setItem('viberails_terminal_fontSize', size); } catch {}
    }

    loadRenderer() {
        try { return localStorage.getItem('viberails_terminal_webgl') === 'false' ? 'canvas' : 'webgl'; }
        catch { return 'webgl'; }
    }

    loadTheme() {
        try { return localStorage.getItem('viberails_terminal_theme') || null; }
        catch { return null; }
    }

    // 'default' = 140 ms (matches community xterm fit-addon norm)
    // 'extended' = 2 s (opt-in; suppresses stacked-repaints during slow drags).
    // See runbooks/terminal/TERMINAL.md "Stacked repaints during drag-resize".
    // Consumed by terminal-tab.js via the same localStorage key (kept in sync
    // by string contract — do not rename without grepping for the key).
    loadResizeDebounce() {
        try { return localStorage.getItem('viberails_terminal_resizeDebounce') === 'extended' ? 'extended' : 'default'; }
        catch { return 'default'; }
    }

    loadComputerName() {
        const value = this._manager.app?.appSettings?.computerName;
        return typeof value === 'string' ? value : '';
    }

    // ---------- Mount + populate ----------

    init() {
        this._panelBtn          = this._container.querySelector('#terminal-settings-btn');
        this._panelEl           = this._container.querySelector('#vb-terminal-settings-panel');
        this._closeBtn          = this._container.querySelector('#terminal-settings-close');
        this._fontSizeInput     = this._container.querySelector('#terminal-settings-font-size');
        this._rendererSelect    = this._container.querySelector('#terminal-settings-renderer');
        this._rendererStatusEl  = this._container.querySelector('#terminal-settings-renderer-active');
        this._resizeDebounceSelect = this._container.querySelector('#terminal-settings-resize-debounce');
        this._computerNameInput = this._container.querySelector('#terminal-settings-computer-name');
        this._themeListEl       = this._container.querySelector('#vb-terminal-settings-theme-list');
        this._sectionToggles    = Array.from(this._container.querySelectorAll('[data-settings-toggle]'));

        this._panelBtn?.addEventListener('click', () => this.togglePanel());
        this._closeBtn?.addEventListener('click', () => this.togglePanel(false));
        this._sectionToggles.forEach((toggle) => {
            toggle.addEventListener('click', () => this.toggleSection(toggle.dataset.settingsToggle));
        });

        // Actions block (top of panel, always visible) — these open modals, so
        // close the slide-over first or it would sit on top of the backdrop.
        this._container.querySelector('#terminal-multirun-btn')?.addEventListener('click', () => {
            this.togglePanel(false);
            this._manager._showMultiRunModal();
        });
        this._container.querySelector('#terminal-senddebug-btn')?.addEventListener('click', () => {
            this.togglePanel(false);
            this._manager._sendDebugLog();
        });

        this._fontSizeInput?.addEventListener('change', (e) => {
            this._manager.adjustFontSize(0, parseInt(e.target.value, 10));
        });
        this._rendererSelect?.addEventListener('change', (e) => this.applyRendererPreference(e.target.value));
        this._resizeDebounceSelect?.addEventListener('change', (e) => this.applyResizeDebounce(e.target.value));
        this._computerNameInput?.addEventListener('change', (e) => {
            void this.saveComputerName(e.target.value);
        });
        this._computerNameInput?.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                e.currentTarget.blur();
            }
        });

        this._populate();
        this._restoreSections();
    }

    _populate() {
        const size = this.loadFontSize();
        if (this._fontSizeInput) this._fontSizeInput.value = size;

        this._themeItems = [];
        if (this._themeListEl && window.CXL_THEMES) {
            this._themeListEl.innerHTML = '';
            for (const [key, theme] of Object.entries(window.CXL_THEMES)) {
                const item = document.createElement('div');
                item.className = 'vb-terminal-settings-theme-item';
                item.dataset.theme = key;
                item.title = theme.name;

                const preview = document.createElement('div');
                preview.className = 'vb-terminal-settings-theme-preview';
                preview.style.background = theme.background;

                const accents = [theme.red, theme.green, theme.blue, theme.magenta, theme.cyan, theme.yellow];
                accents.filter(c => c).slice(0, 4).forEach(color => {
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
                this._themeListEl.appendChild(item);
                this._themeItems.push(item);
            }
        }

        if (this._rendererSelect) this._rendererSelect.value = this.loadRenderer();
        if (this._resizeDebounceSelect) this._resizeDebounceSelect.value = this.loadResizeDebounce();
        if (this._computerNameInput) {
            this._computerNameInput.value = this.loadComputerName();
            // Show the live machine name (the blank-field default) as the placeholder.
            const machineName = this._manager.app?.appSettings?.machineName;
            if (machineName) this._computerNameInput.placeholder = machineName;
        }

        const savedTheme = this.loadTheme();
        if (savedTheme) {
            this._themeItems.forEach((item) => {
                item.classList.toggle('active', item.dataset.theme === savedTheme);
            });
        }

        this._refreshRendererStatus();
    }

    togglePanel(forceOpen) {
        const open = forceOpen ?? !this._panelEl?.classList.contains('open');
        this._panelEl?.classList.toggle('open', open);
        this._panelBtn?.classList.toggle('active', open);
        if (open) {
            // Re-read the active renderer in case GPU recovered or the active tab changed.
            this._refreshRendererStatus();
        } else {
            this._manager.focusActiveTerminalInput?.();
        }
    }

    loadOpenSections() {
        try {
            const raw = localStorage.getItem('viberails_terminal_settingsSections');
            if (raw === null) return new Set(['theme']);
            return new Set(raw ? raw.split(',').filter(Boolean) : []);
        } catch { return new Set(['theme']); }
    }

    saveOpenSections(set) {
        try { localStorage.setItem('viberails_terminal_settingsSections', Array.from(set).join(',')); } catch {}
    }

    _setSectionOpen(section, isOpen) {
        section.classList.toggle('open', isOpen);
        const toggle = section.querySelector('[data-settings-toggle]');
        const content = section.querySelector('.vb-terminal-settings-section-content');
        toggle?.setAttribute('aria-expanded', String(isOpen));
        content?.toggleAttribute('inert', !isOpen);
        content?.setAttribute('aria-hidden', String(!isOpen));
    }

    _restoreSections() {
        const open = this.loadOpenSections();
        const sections = this._container.querySelectorAll('[data-settings-section]');
        sections.forEach((section) => {
            this._setSectionOpen(section, open.has(section.dataset.settingsSection));
        });
    }

    toggleSection(sectionName) {
        const section = this._container.querySelector(`[data-settings-section="${sectionName}"]`);
        if (!section) return;
        const willOpen = !section.classList.contains('open');
        this._setSectionOpen(section, willOpen);

        const open = this.loadOpenSections();
        if (willOpen) open.add(sectionName); else open.delete(sectionName);
        this.saveOpenSections(open);
    }

    // ---------- Apply settings to terminals ----------

    applyToTerminal(vibeTerminal) {
        if (!vibeTerminal?._terminal) return;
        const themeKey = this.loadTheme();
        if (themeKey && window.CXL_THEMES?.[themeKey]) {
            vibeTerminal.setTheme(window.CXL_THEMES[themeKey]);
        }
        // Cursor style is intentionally NOT applied here — the TUI and xterm
        // defaults own the cursor (user-facing cursor settings removed 2026-06-12).
    }

    applyTheme(key) {
        if (!window.CXL_THEMES?.[key]) return;
        try { localStorage.setItem('viberails_terminal_theme', key); } catch {}
        const theme = window.CXL_THEMES[key];
        for (const tab of this._manager.tabs.values()) {
            tab.instance.vibeTerminal?.setTheme(theme);
        }
        this._themeItems.forEach(s => s.classList.toggle('active', s.dataset.theme === key));
    }

    applyRendererPreference(renderer) {
        const useWebgl = renderer === 'webgl';
        try { localStorage.setItem('viberails_terminal_webgl', String(useWebgl)); } catch {}
        this._manager.app?.showToast?.(
            'Renderer Updated',
            'Renderer preference saved. Restart active terminal tabs to apply.',
            'info'
        );
    }

    applyResizeDebounce(value) {
        // terminal-tab.js reads this same key per-resize, so the new value
        // takes effect on the next resize event — no tab restart needed.
        const normalized = value === 'extended' ? 'extended' : 'default';
        try { localStorage.setItem('viberails_terminal_resizeDebounce', normalized); } catch {}
    }

    async saveComputerName(value) {
        const app = this._manager.app;
        if (!app?.apiCall) return;

        const computerName = (typeof value === 'string' ? value : '').trim().slice(0, 80);
        if (this._computerNameInput && this._computerNameInput.value !== computerName) {
            this._computerNameInput.value = computerName;
        }

        // Narrow update: touches ONLY the computer name server-side, so it can't clobber
        // unrelated settings (remoteAccess, mcpEnabled, theme, …) with this client's
        // possibly-stale cache. The response is the fresh, full settings snapshot.
        try {
            const savedSettings = await app.apiCall('/api/v1/settings/computer-name', 'POST', {
                computerName
            }, { showLoading: false });
            app.setAppSettings?.(savedSettings);
            app.showToast?.('Notifications', 'Computer name saved', 'success', { compact: true });
        } catch (error) {
            app.showError?.('Failed to save computer name: ' + (error?.message || 'Unknown error'));
        }
    }

    syncFontSizeInput(size) {
        if (this._fontSizeInput) this._fontSizeInput.value = size;
    }

    // ---------- Per-tab renderer status tracking ----------

    bindTab(tabId, vibeTerminal) {
        if (!vibeTerminal?.onRendererChange) return;
        this._rendererUnsubs.get(tabId)?.();
        const unsub = vibeTerminal.onRendererChange(() => {
            if (this._activeTabId === tabId) this._refreshRendererStatus();
        });
        this._rendererUnsubs.set(tabId, unsub);
        if (this._activeTabId === tabId) this._refreshRendererStatus();
    }

    unbindTab(tabId) {
        const unsub = this._rendererUnsubs.get(tabId);
        if (unsub) {
            try { unsub(); } catch {}
            this._rendererUnsubs.delete(tabId);
        }
    }

    setActiveTab(tabId) {
        this._activeTabId = tabId;
        this._refreshRendererStatus();
    }

    _refreshRendererStatus() {
        if (!this._rendererStatusEl) return;
        const vibe = this._manager.tabs.get(this._activeTabId)?.instance?.vibeTerminal;
        const renderer = vibe?.getActiveRenderer?.() ?? null;
        const label = renderer ? (RENDERER_LABELS[renderer] ?? renderer) : '—';
        this._rendererStatusEl.textContent = label;
        this._rendererStatusEl.dataset.renderer = renderer ?? '';
    }
}
