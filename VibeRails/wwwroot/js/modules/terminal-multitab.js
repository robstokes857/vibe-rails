import { ChatHistorySidebar } from './chat-history-sidebar.js';
import {
    buildLlmSelectionValue,
    parseLlmSelection,
    getCliBrand
} from './utils.js';
import { mountLlmPicker, setLlmPickerValue } from './pickers/llm-picker.js';
import { TabStatusController } from './terminal-tab-status.js';
import { TerminalSettings, renderTerminalSettingsPanelHtml } from './terminal-settings.js';
import { TerminalMenu } from './terminal-menu.js';
import { TerminalTab } from './terminal-tab.js';
import { TerminalMultiRun } from './terminal-multirun.js';
import { TerminalEditorModal } from './terminal-editor-modal.js';
import { TerminalToast } from './terminal-toast.js';
import { TerminalNotifications } from './terminal-notifications.js';
import { showSendDebugLogModal } from './debug-bundle.js';
import { resolvePromptTemplateForLaunch } from './prompt-template-modal.js';

// Pending-close grace window: how long a closed tab is held in the undo
// dropdown before the backend DELETE actually fires. Keep PENDING_CLOSE_MS
// and PENDING_CLOSE_LABEL in sync — the label is what the user sees in the
// undo button title and the dropdown header.
const PENDING_CLOSE_MS = 2 * 60 * 1000;
const PENDING_CLOSE_LABEL = (() => {
    const minutes = Math.round(PENDING_CLOSE_MS / 60000);
    return minutes === 1 ? '1 minute' : `${minutes} minutes`;
})();
// Allowlist for CSS filter values spliced into <img style="filter: ...">.
// Today logoFilter only comes from getCliBrand's hardcoded map, but lock the
// surface down so future user-supplied brand metadata can't inject CSS.
const SAFE_CSS_FILTER = /^[a-z0-9\s().,%#-]+$/i;
const DEFAULT_SELECTION = null;
const ACTIVE_TAB_KEY = 'viberails_terminal_active_tab_id';
const TAB_SELECTION_PREFIX = 'viberails_terminal_tab_selection_';
const TAB_TITLE_PREFIX = 'viberails_terminal_tab_title_';
const TAB_META_PREFIX = 'viberails_terminal_tab_meta_';
// One-time "compose long input in the text editor" nudge. '1' once the user has
// dismissed it or opened the editor at least once — then we never nudge again.
const EDITOR_NUDGE_SEEN_KEY = 'viberails_terminal_editor_nudge_seen';

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
        this.launchSelection = null;

        this._pendingCloses = new Map();
        this._pendingCloseMs = PENDING_CLOSE_MS;
        this._undoBtn = null;
        this._undoCount = null;
        this._undoWrapper = null;
        this._undoSelectEl = null;
        this._undoTomSelect = null;
        this._undoCountdownTimer = null;
        this._undoBodyClickHandler = null;

        this.panel = null;
        this.tabList = null;
        this.tabAdd = null;
        this.tabPanels = null;
        this.placeholder = null;
        this.terminalContainer = null;
        this.statusBadge = null;
        this.windowTitle = null;
        this.windowSession = null;
        this.windowSessionValue = null;

        this.startBtn = null;
        this.reconnectBtn = null;
        this.stopBtn = null;
        this.controlsBar = null;
        this.headerSelect = null;
        this._headerPickerDisposer = null;
        this.closeDot = null;
        this.minimizeDot = null;
        this.maximizeDot = null;
        this.lockBtn = null;
        this.focusBtn = null;
        this.keyboardBtn = null;
        this.editorBtn = null;

        // "Open in text editor" discovery nudge — surfaced at most once per
        // session (and never again once dismissed/opened; see EDITOR_NUDGE_SEEN_KEY).
        this._editorNudgeShown = false;
        // Handle to the live nudge toast (if any), so opening the editor by any
        // route can dismiss it. Nulled when the toast closes.
        this._editorNudgeHandle = null;

        this.lockLayoutHandler = null;
        this.lockedPanel = null;
        this.lockScrollTop = 0;
        this.isScrollLocked = false;
        this.focusLayoutHandler = null;
        this.focusLayoutRaf = null;
        this.downloadMenu = null;
        this.historySidebar = null;
        this._themeSwatches = [];

        this.settings = null;

        // Session-scoped draft for the "Open in text editor" scratchpad. Lives on
        // the manager (not a tab) because it's a composing surface independent of
        // which tab is active; cleared on a successful send, dropped on destroy.
        this._editorDraft = '';

        // Terminal-scoped toast surface. Renders over the active tab's panel
        // (never inside the xterm host), so it can't disturb terminal sizing.
        this.toast = new TerminalToast(this);
        this.notifications = new TerminalNotifications(this);
    }

    isDestroyed() {
        return this._destroyed;
    }

    getEditorDraft() {
        return this._editorDraft || '';
    }

    setEditorDraft(text) {
        this._editorDraft = typeof text === 'string' ? text : '';
    }

    applySavedTerminalSettingsForTab(tabId) {
        const tab = this.tabs.get(tabId);
        const vibe = tab?.instance?.vibeTerminal;
        if (!vibe) return;
        this.settings?.applyToTerminal(vibe);
        this.settings?.bindTab(tabId, vibe);
    }

    async initialize() {
        if (this._destroyed) {
            return;
        }

        this.panel = this.container.querySelector('#vb-terminal-panel');
        this.tabList = this.container.querySelector('#vb-terminal-tab-list');
        this.tabScrollLeft = this.container.querySelector('#vb-terminal-tab-scroll-left');
        this.tabScrollRight = this.container.querySelector('#vb-terminal-tab-scroll-right');
        this.tabAdd = this.container.querySelector('#vb-terminal-tab-add-btn');
        this._undoWrapper = this.container.querySelector('#vb-terminal-tab-undo-wrapper');
        this._undoBtn = this.container.querySelector('#vb-terminal-tab-undo-btn');
        this._undoCount = this.container.querySelector('#vb-terminal-tab-undo-count');
        this._undoSelectEl = this.container.querySelector('#vb-terminal-tab-undo-select');
        this._initUndoControl();
        this.tabPanels = this.container.querySelector('#vb-terminal-tab-panels');
        this.placeholder = this.container.querySelector('#terminal-placeholder');
        this.terminalContainer = this.container.querySelector('#terminal-container');
        this.statusBadge = this.container.querySelector('#terminal-status-badge');
        this.windowTitle = this.container.querySelector('#vb-terminal-window-title');
        this.windowSession = this.container.querySelector('#vb-terminal-window-session');
        this.windowSessionValue = this.container.querySelector('#vb-terminal-window-session-value');

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
        this.editorBtn = this.container.querySelector('#terminal-editor-btn');

        this.zoomInBtn     = this.container.querySelector('#terminal-zoom-in-btn');
        this.zoomOutBtn    = this.container.querySelector('#terminal-zoom-out-btn');
        this.fontSizeLabel = this.container.querySelector('#vb-terminal-font-size-label');
        this.historyBtn    = null; // toggle lives on collapsed sidebar strip

        this.populateSelect();
        this.bindActions();
        this._mountDropdowns();
        this._initTabScrollArrows();

        // Re-home the persistent token-compression meter into this freshly-rendered controls bar.
        // The bar is rebuilt via innerHTML on every terminal-view render, so the app-level singleton
        // is re-parented here (state preserved) rather than recreated. No-op if the slot is absent.
        this.app.terminalTokenCompression?.relocate(this.container.querySelector('#terminal-proxy-activity-slot'));
        this.settings = new TerminalSettings(this.container, this);
        this.settings.init();
        const savedFontSize = this.settings.loadFontSize();
        if (this.fontSizeLabel) this.fontSizeLabel.textContent = savedFontSize;
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
        this._tabScrollRO?.disconnect();
        this._tabScrollRO = null;
        this.removeFocusLayoutHandler();
        this.disableLockedLayout(this.lockedPanel);
        this.downloadMenu?.destroy();
        this.downloadMenu = null;
        this.historySidebar?.destroy();
        this.historySidebar = null;
        this.toast?.dispose();
        document.body.classList.remove('vb-terminal-active-session');

        // Fire-and-forget DELETE for any tabs still in pending-close so we don't leak
        // child processes when the user navigates away.
        this._pendingCloses.forEach((pending, tabId) => {
            clearTimeout(pending.timeoutId);
            void this.app.apiCall(`/api/v1/terminal/tabs/${encodeURIComponent(tabId)}`, 'DELETE')
                .catch(() => { /* best effort */ });
        });
        this._pendingCloses.clear();
        this._stopUndoCountdown();
        this._headerPickerDisposer?.();
        this._headerPickerDisposer = null;
        if (this._undoBodyClickHandler) {
            document.removeEventListener('click', this._undoBodyClickHandler, true);
            this._undoBodyClickHandler = null;
        }
        if (this._undoTomSelect) {
            try { this._undoTomSelect.destroy(); } catch { /* no-op */ }
            this._undoTomSelect = null;
        }

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

    _sendDebugLog() {
        const active = this.getActiveTab();
        showSendDebugLogModal(this.app, active?.state?.sessionId || null);
    }

    bindActions() {
        this.tabAdd?.addEventListener('click', () => {
            void this.createAndActivateTab({ selection: DEFAULT_SELECTION });
        });

        this.headerSelect?.addEventListener('change', (e) => {
            const selection = e.target.value || null;
            this.launchSelection = selection;
            const active = this.getActiveTab();
            if (active && !active.state.hasActiveSession) {
                this.applySelection(active, selection);
            }
        });

        this.startBtn?.addEventListener('click', () => {
            void this.startActiveTab();
        });

        this.reconnectBtn?.addEventListener('click', () => {
            void this.reconnectActiveTab();
        });

        // Connect-only button: visible only while a session is disconnected
        // (see updateUi), so the only action it ever needs is reconnect.
        this.stopBtn?.addEventListener('click', () => {
            const active = this.getActiveTab();
            if (active?.state?.hasActiveSession && active.state.status === 'disconnected') {
                void this.reconnectActiveTab();
            }
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
    }

    _mountDropdowns() {
        // Multi Run and Send debug log moved from the old kebab (⋮) dropdown into
        // the settings panel's Actions block — see TerminalSettings.init(), which
        // binds them back to _showMultiRunModal / _sendDebugLog on this manager.

        // "Open in text editor" is a top-level control button, so it gets its own
        // click handler instead of a menu item.
        this.editorBtn?.addEventListener('click', () => this._showEditorModal());

        // VS Code mode has no file-system download path — hide the wrap entirely.
        if (window.__viberails_VSCODE__) {
            const downloadWrap = this.container.querySelector('#vb-terminal-download-wrap');
            if (downloadWrap) downloadWrap.style.display = 'none';
            return;
        }

        this.downloadMenu = new TerminalMenu(this.container, {
            buttonId: 'terminal-download-btn',
            menuId: 'vb-terminal-download-menu',
            items: [
                { id: 'terminal-save-text', onClick: () => this.saveActiveTerminalSession('text') },
                { id: 'terminal-save-html', onClick: () => this.saveActiveTerminalSession('html') }
            ]
        });
        this.downloadMenu.mount();
    }

    _showMultiRunModal() {
        new TerminalMultiRun(this).show();
    }

    _showEditorModal() {
        // The editor pastes into a live session; with none there's nothing to send
        // to. The button is hidden in that state (see updateUi) — this guard is
        // just defense-in-depth against any stray caller (e.g. the discovery toast).
        const active = this.getActiveTab();
        if (!active?.state?.hasActiveSession) {
            return;
        }

        // Opening the editor once means the user found the feature — retire the
        // nudge and dismiss its toast if still on screen (e.g. the user clicked the
        // toolbar button rather than the toast's own "Open editor" action).
        this._editorNudgeShown = true;
        this._markEditorNudgeSeen();
        this._editorNudgeHandle?.close();
        this._editorNudgeHandle = null;
        void new TerminalEditorModal(this).show();
    }

    // ── "Open in text editor" discovery nudge ────────────────────────────────
    //
    // Called by a tab when the user types (or pastes) a long run of input. Surfaces
    // a one-time terminal toast pointing at the editor, with an inline "Open editor"
    // action. Self-gating: fires at most once per session, and never again once the
    // user has dismissed it or opened the editor (persisted under EDITOR_NUDGE_SEEN_KEY).
    maybeShowEditorNudge() {
        if (this._editorNudgeShown || this._destroyed) {
            return;
        }

        try {
            if (window.localStorage.getItem(EDITOR_NUDGE_SEEN_KEY) === '1') {
                // Permanently dismissed — stop re-checking for the rest of the session.
                this._editorNudgeShown = true;
                return;
            }
        } catch { /* localStorage blocked — fall through and show it */ }

        const handle = this.toast.showToast(
            'Composing something long? Open it in the text editor for a nicer experience.',
            {
                type: 'info',
                durationSec: 14,
                action: {
                    label: 'Open editor',
                    onClick: () => this._showEditorModal()
                },
                // Auto-close leaves the tip eligible again next session; an explicit
                // dismiss (the ×) means "don't show again" and is persisted.
                onClose: (reason) => {
                    this._editorNudgeHandle = null;
                    if (reason === 'manual') {
                        this._markEditorNudgeSeen();
                    }
                }
            }
        );

        // Only burn the once-per-session shot if the toast actually rendered — it
        // needs an active terminal panel to live in. Otherwise let a later long
        // input try again.
        if (handle) {
            this._editorNudgeShown = true;
            this._editorNudgeHandle = handle;
        }
    }

    _markEditorNudgeSeen() {
        try { window.localStorage.setItem(EDITOR_NUDGE_SEEN_KEY, '1'); } catch { /* no-op */ }
    }

    // A background tab finished its turn (TabStatusController → READY off-screen,
    // fired alongside the ready-flash). Pop a small toast over whatever tab the
    // user is currently looking at, with a one-click jump to the tab that's ready.
    _notifyTabReady(state) {
        this.notifications.notifyTabReady(state);
    }

    // An opted-in tab reached READY ('ready') or "Waiting for user input" ('waiting').
    // The notification module builds and sends the web-push; this wrapper keeps
    // TabStatusController wired to the manager without owning notification details.
    _notifyTabPush(state, statusKey) {
        this.notifications.notifyTabPush(state, statusKey);
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
            const workingDirectory = metadata?.workingDirectory || tabInfo.workingDirectory || null;
            this.addLocalTab(tabInfo, {
                selection,
                title,
                label: metadata?.customLabel === true ? metadata?.label || null : null,
                icon: metadata?.icon || null,
                accentColor: metadata?.accentColor || null,
                taskKey: metadata?.taskKey || null,
                customLabel: metadata?.customLabel === true,
                minimized: metadata?.minimized === true,
                pinned: metadata?.pinned === true,
                notifyEnabled: metadata?.notifyEnabled === true,
                workingDirectory
            });
        });
    }

    addLocalTab(tabInfo, options = {}) {
        if (this._destroyed) {
            return null;
        }

        const selection = options.selection || DEFAULT_SELECTION;
        const meta = this.getSelectionMeta(selection);
        const workingDirectory = cleanString(options.workingDirectory) || cleanString(tabInfo.workingDirectory);
        // Tab labels are CLI-based (Claude/Codex/…). The working directory is tracked
        // for the launch cwd + native title only — it does NOT drive the tab label
        // (every tab shares the project root, so folder names would all be identical).
        const autoLabel = meta.displayName;

        const state = {
            id: tabInfo.tabId,
            selection,
            label: cleanString(options.label) || autoLabel,
            title: options.title || null,
            icon: options.icon || null,
            accentColor: this.normalizeAccentColor(options.accentColor),
            taskKey: cleanString(options.taskKey),
            customLabel: options.customLabel === true,
            minimized: options.minimized === true,
            pinned: options.pinned === true,
            notifyEnabled: options.notifyEnabled === true,
            workingDirectory,
            renaming: false,
            hasActiveSession: tabInfo.hasActiveSession === true,
            sessionId: tabInfo.sessionId || null,
            cli: tabInfo.cli || null,
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
                    this.showSelectionRequiredFeedback(this.startBtn || button);
                });
                return;
            }
            void this.activateTab(state.id, { connectIfNeeded: false });
        });
        button.addEventListener('dblclick', (event) => {
            if (!state.hasActiveSession) {
                return;
            }
            event.preventDefault();
            this.beginRenameTab(state.id);
        });

        // Right-click rename follows the same availability rule as the hover pencil.
        button.addEventListener('contextmenu', (event) => {
            if (!state.hasActiveSession) {
                return;
            }
            event.preventDefault();
            this.beginRenameTab(state.id);
        });

        const edit = document.createElement('button');
        edit.type = 'button';
        edit.className = 'vb-terminal-tab-edit';
        edit.innerHTML = '<i class="fa-solid fa-pen"></i>';
        edit.title = 'Rename tab';
        edit.setAttribute('aria-label', 'Rename tab');
        edit.addEventListener('click', (event) => {
            event.stopPropagation();
            this.beginRenameTab(state.id);
        });

        const min = document.createElement('button');
        min.type = 'button';
        min.className = 'vb-terminal-tab-min';
        min.addEventListener('click', (event) => {
            event.stopPropagation();
            this.toggleTabMinimized(state.id);
        });

        const pin = document.createElement('button');
        pin.type = 'button';
        pin.className = 'vb-terminal-tab-pin';
        pin.addEventListener('click', (event) => {
            event.stopPropagation();
            this.toggleTabPinned(state.id);
        });

        const notify = document.createElement('button');
        notify.type = 'button';
        notify.className = 'vb-terminal-tab-notify';
        notify.addEventListener('click', (event) => {
            event.stopPropagation();
            this.toggleTabNotify(state.id);
        });

        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'vb-terminal-tab-close';
        close.innerHTML = '&times;';
        close.title = 'Close tab';
        close.setAttribute('aria-label', 'Close tab');
        close.addEventListener('click', (event) => {
            event.stopPropagation();
            void this.closeTab(state.id);
        });

        // Action cluster: hidden until the tab is hovered (CSS overlays it on the
        // status section, so revealing it never shifts the tab's layout). Narrow
        // tabs stage the cluster down via container queries in style.css — rename
        // drops first, then minimize — so center-clicks always activate the tab.
        const actions = document.createElement('span');
        actions.className = 'vb-terminal-tab-actions';
        actions.appendChild(edit);
        actions.appendChild(pin);
        actions.appendChild(notify);
        actions.appendChild(min);
        actions.appendChild(close);

        // Persistent "watching" badge: a small eye pinned at the tab's left edge,
        // visible only while the tab is armed for push (is-notify-on). It's a
        // sibling of the tab button — deliberately NOT inside it — so the status
        // controller's periodic `button.innerHTML = ''` rebuild can never wipe it.
        // It's also a live un-watch control: since it only shows while watching,
        // clicking it always turns notifications off — so the user doesn't have to
        // hover to find the eye toggle in the action cluster.
        const watchBadge = document.createElement('button');
        watchBadge.type = 'button';
        watchBadge.className = 'vb-terminal-tab-watch-badge';
        watchBadge.innerHTML = '<i class="fa-solid fa-eye"></i>';
        const watchBadgeLabel = 'Stop watching this tab';
        watchBadge.title = watchBadgeLabel;
        watchBadge.setAttribute('aria-label', watchBadgeLabel);
        watchBadge.addEventListener('click', (event) => {
            event.stopPropagation();
            this.toggleTabNotify(state.id);
        });

        item.appendChild(watchBadge);
        item.appendChild(button);
        item.appendChild(actions);

        const panel = document.createElement('div');
        panel.className = 'vb-terminal-tab-panel';
        panel.dataset.tabId = state.id;
        panel.style.display = 'none';

        const terminalElement = document.createElement('div');
        terminalElement.className = 'vb-terminal-element';
        terminalElement.style.width = '100%';
        terminalElement.style.height = '100%';
        panel.appendChild(terminalElement);

        // Toast overlay: a sibling of the xterm host (NOT a child of it), so
        // xterm's FitAddon — which measures `.vb-terminal-element` for cols/rows
        // — never sees it. Absolutely positioned + pointer-events:none (see CSS),
        // so it adds no layout and can't trigger a resize or steal terminal input.
        const toastLayer = document.createElement('div');
        toastLayer.className = 'vb-terminal-toast-layer';
        panel.appendChild(toastLayer);

        if (this.tabList) {
            const addButtonInList = this.tabAdd?.parentElement === this.tabList ? this.tabAdd : null;
            this.tabList.insertBefore(item, addButtonInList);
        }
        this.tabPanels?.appendChild(panel);

        state.ui = { item, button, edit, pin, notify, min, close, actions, watchBadge, panel, terminalElement, toastLayer };

        const instance = new TerminalTab(this, state);
        instance.statusController = new TabStatusController(state, state.ui, {
            getCliKey: () => {
                const meta = this.getSelectionMeta(state.selection);
                return meta.cli || null;
            },
            isActiveTab: () => this.activeTabId === state.id,
            setProgress: (progress) => this.updateTabProgress(state.id, progress),
            notifyReady: () => this._notifyTabReady(state),
            isPushEnabled: () => state.notifyEnabled === true,
            notifyPush: (statusKey) => this._notifyTabPush(state, statusKey)
        });
        // Sync status controller with restored tab state (e.g. page reload
        // where the tab had an active session — state.status is 'disconnected'
        // but the controller was just created with _status = null).
        if (state.status === 'disconnected') {
            instance.statusController.onSocketClose();
        } else if (state.status === 'connected') {
            instance.statusController.onSocketOpen();
        }

        const tab = { state, instance };

        this.tabs.set(state.id, tab);
        this.tabOrder.push(state.id);

        this.saveTabSelection(state.id, selection);
        if (state.title) {
            this.saveTabTitle(state.id, state.title);
        }
        this.saveTabMeta(state.id, this._tabMetaPayload(state));
        this.renderTabButton(tab);
        this.applyTabAccent(tab);
        this.applyTabPinned(tab);
        this.applyTabMinimized(tab);
        this.applyTabNotify(tab);

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
        this.settings?.setActiveTab(target.state.id);

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
        this._scrollActiveTabIntoView();
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
        // Sessionless (blank/stopped) tabs are closeable too — otherwise + and Stop
        // leak un-dismissable tabs. _enterPendingClose handles the no-session case
        // (the DELETE 404s and falls through to local cleanup).
        const tab = this.tabs.get(tabId);
        if (tab?.state?.pinned === true) {
            return;
        }
        return this._enterPendingClose(tabId);
    }

    async _enterPendingClose(tabId) {
        const tab = this.tabs.get(tabId);
        if (!tab || this._pendingCloses.has(tabId)) {
            return;
        }
        if (tab.state.pinned === true) {
            return;
        }

        const wasActive = this.activeTabId === tabId;

        tab.state.ui.item.style.display = 'none';
        tab.state.ui.panel.style.display = 'none';
        tab.state.ui.item.classList.remove('active');
        tab.instance.setActive(false);

        if (wasActive) {
            this.activeTabId = null;
        }

        const expiresAt = Date.now() + this._pendingCloseMs;
        const timeoutId = setTimeout(() => {
            void this._commitClose(tabId);
        }, this._pendingCloseMs);

        this._pendingCloses.set(tabId, { expiresAt, timeoutId });

        this._refreshUndoControl({ flash: true });

        if (wasActive) {
            const nextId = this._nextVisibleTabId();
            if (nextId) {
                await this.activateTab(nextId, { connectIfNeeded: true });
            } else {
                this.applyPanelState();
                this.updateUi();
            }
        } else {
            this.updateUi();
        }
    }

    async _undoClose(tabId) {
        const tab = this.tabs.get(tabId);
        const pending = this._pendingCloses.get(tabId);
        if (!tab || !pending) {
            return;
        }

        clearTimeout(pending.timeoutId);
        this._pendingCloses.delete(tabId);
        tab.state.ui.item.style.display = '';

        this._refreshUndoControl();

        await this.activateTab(tabId, { connectIfNeeded: true });
    }

    async _commitClose(tabId) {
        const tab = this.tabs.get(tabId);
        if (!tab) {
            return;
        }
        if (tab.state.pinned === true && !this._pendingCloses.has(tabId)) {
            return;
        }

        const pending = this._pendingCloses.get(tabId);
        if (pending) {
            clearTimeout(pending.timeoutId);
            this._pendingCloses.delete(tabId);
            this._refreshUndoControl();
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

        this.settings?.unbindTab(tabId);
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

        if (this.tabOrder.length === 0 && this._pendingCloses.size === 0) {
            await this.createAndActivateTab({ selection: DEFAULT_SELECTION });
            return;
        }

        if (!this.activeTabId) {
            const nextId = this._nextVisibleTabId();
            if (nextId) {
                await this.activateTab(nextId, { connectIfNeeded: false });
            }
        }

        this.updateUi();
    }

    _nextVisibleTabId() {
        for (let i = this.tabOrder.length - 1; i >= 0; i--) {
            const id = this.tabOrder[i];
            if (!this._pendingCloses.has(id)) {
                return id;
            }
        }
        return null;
    }

    _initUndoControl() {
        if (!this._undoBtn) return;

        this._undoBtn.addEventListener('click', (event) => {
            event.preventDefault();
            event.stopPropagation();
            this._handleUndoClick();
        });

        if (!this._undoSelectEl || typeof window.TomSelect !== 'function') {
            // Tom-select missing: button still works for the 1-pending case via _handleUndoClick.
            return;
        }

        if (this._undoSelectEl.tomselect) {
            this._undoSelectEl.tomselect.destroy();
        }

        this._undoTomSelect = new window.TomSelect(this._undoSelectEl, {
            controlInput: null,
            allowEmptyOption: true,
            maxOptions: null,
            plugins: {
                dropdown_header: {
                    title: `Terminal will auto close in ${PENDING_CLOSE_LABEL}.`
                }
            },
            dropdownParent: 'body',
            dropdownClass: 'ts-dropdown vb-undo-dropdown',
            render: {
                option: (data, escape) => {
                    const time = this._formatTimeLeft(Number(data.expiresAt) || 0);
                    const filterValue = data.cliLogoFilter && SAFE_CSS_FILTER.test(data.cliLogoFilter) ? data.cliLogoFilter : '';
                    const logoStyle = filterValue ? ` style="filter: ${escape(filterValue)};"` : '';
                    const logoHtml = data.cliLogo
                        ? `<img class="vb-undo-row-logo" src="${escape(data.cliLogo)}" alt="${escape(data.cliLabel || '')}" loading="lazy"${logoStyle}>`
                        : `<span class="vb-undo-row-logo vb-undo-row-logo-fallback"><i class="fa-solid fa-terminal" aria-hidden="true"></i></span>`;
                    return `<div class="vb-undo-row" data-tab-id="${escape(data.value)}">`
                        + logoHtml
                        + `<div class="vb-undo-row-main">`
                        +   `<div class="vb-undo-row-label" title="${escape(data.label || data.value)}">${escape(data.label || data.value)}</div>`
                        +   `<div class="vb-undo-row-meta"><span class="vb-undo-row-time" data-tab-time="${escape(data.value)}">auto-close in ${escape(time)}</span></div>`
                        + `</div>`
                        + `<button type="button" class="vb-undo-row-kill" data-kill="${escape(data.value)}" title="Close permanently" aria-label="Close permanently">`
                        +   `<i class="fa-solid fa-xmark"></i>`
                        + `</button>`
                        + `</div>`;
                }
            },
            onItemAdd: (value) => {
                void this._undoClose(value);
                try { this._undoTomSelect.removeOption(value); } catch { /* no-op */ }
                try { this._undoTomSelect.clear(true); } catch { /* no-op */ }
                if (this._pendingCloses.size === 0) {
                    try { this._undoTomSelect.close(); } catch { /* no-op */ }
                }
            },
            onDropdownOpen: () => this._startUndoCountdown(),
            onDropdownClose: () => this._stopUndoCountdown()
        });

        // Body-level capture-phase delegate: clicking the per-row red X commits
        // the close immediately. Tom-select's dropdown is rendered to <body>
        // (dropdownParent: 'body'), so a body listener reaches it without
        // touching tom-select internals. Capture phase ensures we run before
        // tom-select's bubble-phase option-select handler.
        this._undoBodyClickHandler = (event) => {
            const killBtn = event.target.closest('[data-kill]');
            if (!killBtn || !killBtn.closest('.vb-undo-dropdown')) return;
            event.preventDefault();
            event.stopPropagation();
            const tabId = killBtn.getAttribute('data-kill');
            if (!tabId) return;
            void this._commitClose(tabId);
            if (this._pendingCloses.size <= 1) {
                try { this._undoTomSelect?.close(); } catch { /* no-op */ }
            }
        };
        document.addEventListener('click', this._undoBodyClickHandler, true);
    }

    _handleUndoClick() {
        if (this._pendingCloses.size === 0) return;
        if (this._undoTomSelect) {
            try { this._undoTomSelect.open(); } catch { /* no-op */ }
        } else {
            // Tom-select unavailable — fall back to restoring the most recent.
            const ids = Array.from(this._pendingCloses.keys());
            void this._undoClose(ids[ids.length - 1]);
        }
    }

    _refreshUndoControl({ flash = false } = {}) {
        if (!this._undoWrapper) return;

        const size = this._pendingCloses.size;
        if (size === 0) {
            this._undoWrapper.hidden = true;
            if (this._undoTomSelect) {
                try { this._undoTomSelect.clearOptions(); } catch { /* no-op */ }
                try { this._undoTomSelect.close(); } catch { /* no-op */ }
            }
            this._stopUndoCountdown();
            this._undoBtn?.classList.remove('vb-undo-pulse');
            return;
        }

        this._undoWrapper.hidden = false;
        if (this._undoCount) {
            this._undoCount.textContent = String(size);
        }

        if (this._undoTomSelect) {
            try {
                this._undoTomSelect.clearOptions();
                for (const [tabId, pending] of this._pendingCloses) {
                    const tab = this.tabs.get(tabId);
                    if (!tab) continue;
                    const meta = this.getSelectionMeta(tab.state.selection);
                    const brand = meta?.cli ? getCliBrand(meta.cli) : null;
                    this._undoTomSelect.addOption({
                        value: tabId,
                        text: tab.state.label || tabId,
                        label: tab.state.label || tabId,
                        cliLogo: brand?.logo || '',
                        cliLogoFilter: brand?.logoFilter || '',
                        cliLabel: brand?.label || (meta?.cli ? meta.cli.toString() : ''),
                        expiresAt: pending.expiresAt
                    });
                }
                this._undoTomSelect.refreshOptions(false);
            } catch { /* no-op */ }
        }

        if (flash && this._undoBtn) {
            this._undoBtn.classList.remove('vb-undo-pulse');
            // Force a reflow so the animation restarts on every new pending close.
            void this._undoBtn.offsetWidth;
            this._undoBtn.classList.add('vb-undo-pulse');
        }
    }

    _startUndoCountdown() {
        this._stopUndoCountdown();
        this._tickUndoCountdown();
        this._undoCountdownTimer = setInterval(() => this._tickUndoCountdown(), 1000);
    }

    _stopUndoCountdown() {
        if (this._undoCountdownTimer) {
            clearInterval(this._undoCountdownTimer);
            this._undoCountdownTimer = null;
        }
    }

    _tickUndoCountdown() {
        if (!this._undoTomSelect) return;
        const dropdown = this._undoTomSelect.dropdown_content;
        if (!dropdown) return;
        const escapeAttr = window.CSS && typeof window.CSS.escape === 'function'
            ? (s) => window.CSS.escape(s)
            : (s) => String(s).replace(/"/g, '\\"');
        for (const [tabId, pending] of this._pendingCloses) {
            const el = dropdown.querySelector(`[data-tab-time="${escapeAttr(tabId)}"]`);
            if (el) el.textContent = `auto-close in ${this._formatTimeLeft(pending.expiresAt)}`;
        }
    }

    _formatTimeLeft(expiresAt) {
        const ms = Math.max(0, expiresAt - Date.now());
        const totalSec = Math.ceil(ms / 1000);
        const m = Math.floor(totalSec / 60);
        const s = totalSec % 60;
        return m > 0 ? `${m}m ${s}s` : `${s}s`;
    }

    async startFromSelection(selection) {
        let tab = this.getActiveTab();
        const selectionForMeta = selection || (!tab?.state?.hasActiveSession ? tab?.state?.selection : null);
        const meta = this.getSelectionMeta(selectionForMeta);
        if (!meta.cli) {
            this.showSelectionRequiredFeedback(this.startBtn);
            return;
        }

        const promptResolution = await resolvePromptTemplateForLaunch(this.app, {
            cli: meta.cli,
            environmentName: meta.environmentName,
            title: meta.displayName
        });
        if (promptResolution.canceled) {
            return;
        }

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

        const workingDirectory = this.getDefaultWorkingDirectory();
        const sessionTitle = meta.cli === 'shell' ? meta.displayName : `${meta.displayName} Terminal`;
        const body = {
            cli: meta.cli,
            workingDirectory: workingDirectory || null,
            title: sessionTitle
        };
        if (meta.environmentName) {
            body.environmentName = meta.environmentName;
        }
        if (promptResolution.initialPrompt != null) {
            body.initialPrompt = promptResolution.initialPrompt;
        }

        const started = await tab.instance.startSession(body);
        if (!started) {
            return;
        }

        tab.state.hasActiveSession = true;
        const responseWorkingDirectory = cleanString(started?.workingDirectory) || workingDirectory;
        this.updateTabMetadata(tab, {
            label: meta.displayName,
            title: sessionTitle,
            workingDirectory: responseWorkingDirectory
        });
        this.updateUi();
    }

    async startWithOptions(options) {
        const selection = this.resolveSelectionFromOptions(options);
        const meta = this.getSelectionMeta(selection);
        const requestedWorkingDirectory = cleanString(options?.workingDirectory) || this.getDefaultWorkingDirectory();
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
            const forceNew = options?.forceNewTab === true;
            tab = forceNew ? null : this.getActiveTab();
            if (!tab || tab.state.hasActiveSession) {
                tab = await this.createAndActivateTab({
                    selection,
                    title: requestedTitle,
                    label: requestedLabel,
                    icon: requestedIcon,
                    accentColor: requestedColor,
                    taskKey: requestedTaskKey,
                    workingDirectory: requestedWorkingDirectory
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
            taskKey: requestedTaskKey,
            workingDirectory: requestedWorkingDirectory
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

        // Resuming an existing session must not pop the "fill prompt values" modal or
        // inject a freshly-resolved template — carry through only an explicit initialPrompt.
        const promptResolution = options.resumeSessionId
            ? { canceled: false, initialPrompt: typeof options.initialPrompt === 'string' ? options.initialPrompt : null }
            : await resolvePromptTemplateForLaunch(this.app, {
                cli: lower(options.cli),
                environmentName: options.environmentName || null,
                initialPrompt: typeof options.initialPrompt === 'string' ? options.initialPrompt : null,
                title: requestedLabel || meta.displayName
            });
        if (promptResolution.canceled) {
            return {
                tabId: tab.state.id,
                started: false,
                reusedExisting
            };
        }

        const body = {
            cli: lower(options.cli),
            environmentName: options.environmentName || null,
            workingDirectory: requestedWorkingDirectory || null,
            title: requestedTitle || null,
            resumeSessionId: options.resumeSessionId || null,
            resumeSummary: options.resumeSummary || null
        };
        if (promptResolution.initialPrompt != null) {
            body.initialPrompt = promptResolution.initialPrompt;
        }

        const started = await tab.instance.startSession(body);
        if (!started) {
            return {
                tabId: tab.state.id,
                started: false,
                reusedExisting
            };
        }

        tab.state.hasActiveSession = true;
        const responseWorkingDirectory = cleanString(started?.workingDirectory) || requestedWorkingDirectory;
        this.updateTabMetadata(tab, {
            label: requestedLabel,
            title: requestedTitle,
            icon: requestedIcon,
            accentColor: requestedColor,
            taskKey: requestedTaskKey,
            workingDirectory: responseWorkingDirectory
        });
        this.updateUi();

        return {
            tabId: tab.state.id,
            started: true,
            reusedExisting
        };
    }

    async startActiveTab() {
        const selection = this.getLaunchSelection();
        if (!selection) {
            this.showSelectionRequiredFeedback(this.startBtn);
            return;
        }

        await this.startFromSelection(selection);
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
        // Mirror the start path: the plain shell's label is already "Terminal", so don't
        // append " Terminal" again (avoids "Terminal Terminal").
        const stopMeta = this.getSelectionMeta(tab.state.selection);
        tab.state.title = stopMeta.cli === 'shell' ? tab.state.label : `${tab.state.label} Terminal`;
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
    }

    getSelectionMeta(selection) {
        const parsed = parseLlmSelection(selection, this.app.data.environments || []);
        if (!parsed.cli) {
            return {
                cli: null,
                envId: null,
                environmentName: null,
                displayName: 'Select LLM to launch.'
            };
        }

        return {
            cli: parsed.cli,
            envId: parsed.envId,
            environmentName: parsed.environmentName,
            displayName: parsed.displayName
        };
    }

    getDefaultWorkingDirectory() {
        return cleanString(this.app.data.configs?.rootPath)
            || cleanString(this.app.data.configs?.launchDirectory)
            || null;
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
        // A user-supplied name wins over the auto-generated CLI label.
        if (!tab.state.customLabel) {
            tab.state.label = meta.displayName;
        }

        // Update accent color from brand
        const brand = getCliBrand(meta.cli);
        if (brand.accentColor) {
            tab.state.accentColor = brand.accentColor;
        }

        this.saveTabSelection(tab.state.id, selection);
        this.saveTabMeta(tab.state.id, this._tabMetaPayload(tab.state));
        this.renderTabButton(tab);
        this.applyTabAccent(tab);
        this.updateUi();
    }

    normalizeAccentColor(value) {
        const color = cleanString(value);
        if (!color) return null;

        const colorPattern = /^(#[0-9a-fA-F]{3,8}|rgb\(\s*[\d\s.,%]+\)|rgba\(\s*[\d\s.,%]+\)|hsl\(\s*[\d\s.,%]+\)|hsla\(\s*[\d\s.,%]+\)|[a-zA-Z]+)$/;
        return colorPattern.test(color) ? color : null;
    }

    syncTabActionAvailability(tab) {
        const item = tab?.state?.ui?.item;
        const edit = tab?.state?.ui?.edit;
        const pin = tab?.state?.ui?.pin;
        const notify = tab?.state?.ui?.notify;
        const min = tab?.state?.ui?.min;
        const close = tab?.state?.ui?.close;
        const hasSession = tab?.state?.hasActiveSession === true;
        const pinned = tab?.state?.pinned === true;

        item?.classList.toggle('has-active-session', hasSession);
        this.applyTabPinned(tab);
        this.applyTabNotify(tab);

        if ((!hasSession || pinned) && tab?.state?.minimized === true) {
            tab.state.minimized = false;
            this.applyTabMinimized(tab);
            this.saveTabMeta(tab.state.id, this._tabMetaPayload(tab.state, { minimized: false }));
        }

        [
            [edit, hasSession],
            [pin, true],
            [notify, hasSession],
            [min, hasSession && !pinned],
            // Close stays available even with no session, so blank/stopped tabs can be dismissed.
            // Pinned tabs are the exception: unpin first, then close.
            [close, !pinned]
        ].forEach(([button, enabled]) => {
            if (!button) return;
            button.hidden = !enabled;
            button.disabled = !enabled;
            button.setAttribute('aria-hidden', enabled ? 'false' : 'true');
        });
    }

    renderTabButton(tab) {
        this.syncTabActionAvailability(tab);

        if (tab?.instance?.statusController) {
            tab.instance.statusController.render();
            return;
        }

        // Fallback: legacy rendering for tabs without a status controller
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

        const status = document.createElement('span');
        status.className = 'vb-terminal-tab-status';
        button.appendChild(status);

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

    toggleTabPinned(tabId) {
        const tab = this.tabs.get(tabId);
        if (!tab) return;

        tab.state.pinned = tab.state.pinned !== true;
        if (tab.state.pinned && tab.state.minimized === true) {
            tab.state.minimized = false;
            this.applyTabMinimized(tab);
        }

        this.saveTabMeta(tab.state.id, this._tabMetaPayload(tab.state));
        this.applyTabPinned(tab);
        this.renderTabButton(tab);
        requestAnimationFrame(() => this._updateTabScrollArrows());
    }

    applyTabPinned(tab) {
        const item = tab?.state?.ui?.item;
        const pin = tab?.state?.ui?.pin;
        if (!item) return;

        const pinned = tab.state.pinned === true;
        item.classList.toggle('is-pinned', pinned);
        item.dataset.pinned = pinned ? 'true' : 'false';

        if (pin) {
            pin.innerHTML = '<i class="fa-solid fa-thumbtack"></i>';
            const title = pinned ? 'Unpin tab' : 'Pin tab';
            pin.title = title;
            pin.setAttribute('aria-label', title);
            pin.setAttribute('aria-pressed', String(pinned));
        }
    }

    // ── Per-tab push notifications (opt-in "watch") ───────────────────────
    //
    // The eye toggle arms a tab: while watched, it fires a web-push (via
    // TerminalNotifications) each time it reaches READY or "Waiting for
    // user input" — see _notifyTabPush and TabStatusController._transitionTo. An
    // armed tab also wears a persistent eye badge + sky ring (applyTabNotify) so
    // the watched state reads at a glance, not just on hover. Persists in tab meta
    // like pin.
    toggleTabNotify(tabId) {
        const tab = this.tabs.get(tabId);
        if (!tab) return;

        tab.state.notifyEnabled = tab.state.notifyEnabled !== true;
        this.saveTabMeta(tab.state.id, this._tabMetaPayload(tab.state));
        this.applyTabNotify(tab);
        this.renderTabButton(tab);
    }

    applyTabNotify(tab) {
        const item = tab?.state?.ui?.item;
        const notify = tab?.state?.ui?.notify;
        if (!item) return;

        const on = tab.state.notifyEnabled === true;
        item.classList.toggle('is-notify-on', on);

        if (notify) {
            notify.innerHTML = on
                ? '<i class="fa-solid fa-eye"></i>'
                : '<i class="fa-solid fa-eye-slash"></i>';
            const title = on
                ? 'Watching this tab — you’ll be notified when it’s ready or needs input'
                : 'Watch this tab — get notified when it’s ready or needs input';
            notify.title = title;
            notify.setAttribute('aria-label', title);
            notify.setAttribute('aria-pressed', String(on));
        }
    }

    // ── Per-tab minimize (collapse to an icon chip) ───────────────────────
    //
    // A minimized tab shrinks to a brand-logo + status-icon chip and is grouped
    // at the right edge of the strip, next to the + button (CSS `order`, so
    // tabOrder/DOM order — which drive close/undo/restore logic — are
    // untouched). Clicking the chip still
    // activates the tab (its terminal panel is unaffected by minimizing); the
    // chip's hover button restores it. Built for "this tab just runs in the
    // background" sessions like a dev server.
    toggleTabMinimized(tabId) {
        const tab = this.tabs.get(tabId);
        if (!tab || tab.state.hasActiveSession !== true) return;
        if (tab.state.pinned === true) return;

        tab.state.minimized = tab.state.minimized !== true;
        this.saveTabMeta(tab.state.id, this._tabMetaPayload(tab.state));
        this.applyTabMinimized(tab);
        requestAnimationFrame(() => this._updateTabScrollArrows());
    }

    applyTabMinimized(tab) {
        const item = tab?.state?.ui?.item;
        const min = tab?.state?.ui?.min;
        if (!item) return;

        const minimized = tab.state.minimized === true;
        item.classList.toggle('is-minimized', minimized);

        if (min) {
            min.innerHTML = minimized
                ? '<i class="fa-solid fa-up-right-and-down-left-from-center"></i>'
                : '<i class="fa-solid fa-down-left-and-up-right-to-center"></i>';
            const title = minimized ? 'Restore tab' : 'Minimize tab to icon';
            min.title = title;
            min.setAttribute('aria-label', title);
            min.setAttribute('aria-pressed', String(minimized));
        }
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
        const workingDirectory = Object.prototype.hasOwnProperty.call(metadata, 'workingDirectory')
            ? cleanString(metadata.workingDirectory)
            : undefined;

        if (workingDirectory !== undefined) {
            tab.state.workingDirectory = workingDirectory;
        }
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

        this.saveTabMeta(tab.state.id, this._tabMetaPayload(tab.state));

        this.applyTabAccent(tab);
        this.renderTabButton(tab);
    }

    // ── Tab rename ────────────────────────────────────────────────────────

    // Swap the tab button for an inline <input> in place. The button (and its
    // status visuals, managed by TabStatusController) is only hidden, never torn
    // down, so the spinner/accent survive a rename. Enter/blur commits, Escape
    // cancels, an empty value reverts to the auto-generated CLI label.
    beginRenameTab(tabId) {
        const tab = this.tabs.get(tabId);
        if (!tab || tab.state.renaming || tab.state.hasActiveSession !== true) return;
        // An icon-only chip has no room for the inline rename input — restore first.
        if (tab.state.minimized === true) return;

        const item = tab.state.ui?.item;
        const button = tab.state.ui?.button;
        if (!item || !button) return;

        tab.state.renaming = true;

        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'vb-terminal-tab-rename-input';
        input.value = tab.state.label || '';
        input.maxLength = 60;
        input.setAttribute('aria-label', 'Tab name');

        item.classList.add('is-renaming');
        button.style.display = 'none';
        item.insertBefore(input, button);

        let settled = false;
        const cleanup = () => {
            input.removeEventListener('keydown', onKeydown);
            input.removeEventListener('blur', onBlur);
            input.remove();
            button.style.display = '';
            item.classList.remove('is-renaming');
            tab.state.renaming = false;
        };
        const commit = () => {
            if (settled) return;
            settled = true;
            const next = input.value;
            cleanup();
            this.renameTab(tabId, next);
        };
        const cancel = () => {
            if (settled) return;
            settled = true;
            cleanup();
        };
        const onKeydown = (event) => {
            if (event.key === 'Enter') {
                event.preventDefault();
                commit();
            } else if (event.key === 'Escape') {
                event.preventDefault();
                cancel();
            }
            event.stopPropagation();
        };
        const onBlur = () => commit();

        input.addEventListener('keydown', onKeydown);
        input.addEventListener('blur', onBlur);

        requestAnimationFrame(() => {
            input.focus();
            input.select();
        });
    }

    renameTab(tabId, label) {
        const tab = this.tabs.get(tabId);
        if (!tab) return;

        const clean = cleanString(label);
        if (clean) {
            tab.state.label = clean;
            tab.state.customLabel = true;
        } else {
            // Empty name → drop the custom flag and fall back to the CLI label.
            tab.state.customLabel = false;
            tab.state.label = this.getSelectionMeta(tab.state.selection).displayName;
        }

        this.saveTabMeta(tab.state.id, this._tabMetaPayload(tab.state));

        this.renderTabButton(tab);
    }

    populateSelect() {
        if (!this.headerSelect || this.headerSelect.tagName !== 'SELECT') return;
        this._headerPickerDisposer?.();
        this._headerPickerDisposer = mountLlmPicker(this.app, this.headerSelect, {
            context: 'terminal',
            placeholder: 'Select LLM...',
            includeDefaultSuffix: true
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

        const ts = selectEl.tomselect;
        if (ts) {
            try {
                ts.focus();
                ts.open();
                return;
            } catch {
                // Fall through to native fallback.
            }
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
            this.updateWindowTitleBar(null);
            this.keyboardBtn?.classList.add('d-none');
            this.editorBtn?.classList.add('d-none');
            this.setBadge('Not Started', 'bg-secondary');
            this.updateActionButtons({ start: true, connect: false });
            this.showPlaceholder();
            this.updateAddButtonState();
            return;
        }

        const badge = this.getBadge(active.state);
        this.setBadge(badge.text, badge.className);

        this.updateActionButtons({
            start: true,
            connect: active.state.hasActiveSession && active.state.status === 'disconnected'
        });

        if (this.keyboardBtn) {
            this.keyboardBtn.classList.toggle('d-none', !active.state.hasActiveSession);
        }

        // "Open in text editor" only makes sense with a live session to paste into
        // — hide it otherwise (it used to be clickable with no session, opening an
        // editor that could only error on send).
        if (this.editorBtn) {
            this.editorBtn.classList.toggle('d-none', !active.state.hasActiveSession);
        }

        if (active.state.hasActiveSession) {
            this.showTerminal();
        } else {
            this.showPlaceholder();
        }

        this.updateWindowTitleBar(active.state);
        this.updateWindowControlState();
        this.updateAddButtonState();
        this._updateTabScrollArrows();
    }

    updateWindowTitleBar(state) {
        if (this.windowTitle) {
            this.windowTitle.textContent = 'Terminals run safely in the background even if you navigate away.';
        }

        if (!this.windowSession || !this.windowSessionValue) {
            return;
        }

        const sessionId = cleanString(state?.sessionId);
        const showSession = state?.hasActiveSession === true && !!sessionId;
        this.windowSession.classList.toggle('d-none', !showSession);
        this.windowSession.title = showSession ? `Active session ID: ${sessionId}` : '';
        this.windowSessionValue.textContent = showSession ? sessionId : '';
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

    updateActionButtons({ start, connect }) {
        this.startBtn?.classList.toggle('d-none', !start);
        this.reconnectBtn?.classList.add('d-none');
        // #terminal-stop-btn is Connect-only — shown only when a disconnected
        // session can be reconnected. Its green "Connect" styling is static in
        // the panel HTML; there's no Disconnect mode to flip into.
        this.stopBtn?.classList.toggle('d-none', !connect);
        this.controlsBar?.classList.remove('d-none');
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
        const active = this.getActiveTab();
        const atLimit = this.tabOrder.length >= this.maxTabs;

        if (this.tabAdd) {
            this.tabAdd.disabled = atLimit;
            this.tabAdd.title = atLimit
                ? `Maximum of ${this.maxTabs} tabs reached`
                : 'Open a new terminal tab';
        }

        if (this.startBtn) {
            const canUseActiveBlankTab = !!active && !active.state.hasActiveSession;
            const disabled = atLimit && !canUseActiveBlankTab;
            this.startBtn.disabled = disabled;
            const title = disabled
                ? `Maximum of ${this.maxTabs} tabs reached`
                : active?.state.hasActiveSession
                    ? 'Start selected CLI in a new terminal tab'
                    : 'Start selected CLI';
            this.startBtn.title = title;
            this.startBtn.setAttribute('aria-label', title);
        }

        if (this.headerSelect) {
            const ts = this.headerSelect.tomselect;
            if (ts) {
                ts.enable();
            }

            if (this.headerSelect.disabled) {
                this.headerSelect.disabled = false;
            }

            const nextValue = active && !active.state.hasActiveSession
                ? (active.state.selection || '')
                : (this.launchSelection || active?.state.selection || '');
            if (active && !active.state.hasActiveSession) {
                this.launchSelection = active.state.selection || null;
            }
            setLlmPickerValue(this.app, this.headerSelect, nextValue);
        }
    }

    getLaunchSelection() {
        const tsValue = this.headerSelect?.tomselect?.getValue?.();
        const rawValue = Array.isArray(tsValue) ? tsValue[0] : tsValue;
        const selectedValue = cleanString(rawValue) || cleanString(this.headerSelect?.value);
        if (selectedValue) {
            this.launchSelection = selectedValue;
            return selectedValue;
        }

        const active = this.getActiveTab();
        if (active && !active.state.hasActiveSession) {
            return cleanString(active.state.selection) || DEFAULT_SELECTION;
        }

        const fallback = cleanString(this.launchSelection) || cleanString(active?.state?.selection);
        return fallback || DEFAULT_SELECTION;
    }

    _initTabScrollArrows() {
        if (!this.tabList) return;

        const scrollAmount = 200;

        this.tabScrollLeft?.addEventListener('click', () => {
            this.tabList.scrollBy({ left: -scrollAmount, behavior: 'smooth' });
        });
        this.tabScrollRight?.addEventListener('click', () => {
            this.tabList.scrollBy({ left: scrollAmount, behavior: 'smooth' });
        });

        this.tabList.addEventListener('scroll', () => this._updateTabScrollArrows(), { passive: true });

        this._tabScrollRO = new ResizeObserver(() => this._updateTabScrollArrows());
        this._tabScrollRO.observe(this.tabList);
    }

    _updateTabScrollArrows() {
        if (!this.tabList) return;

        const { scrollLeft, scrollWidth, clientWidth } = this.tabList;
        const overflows = scrollWidth > clientWidth + 1;
        const canScrollLeft = scrollLeft > 1;
        const canScrollRight = scrollLeft + clientWidth < scrollWidth - 1;

        if (this.tabScrollLeft) {
            this.tabScrollLeft.hidden = !(overflows && canScrollLeft);
        }
        if (this.tabScrollRight) {
            this.tabScrollRight.hidden = !(overflows && canScrollRight);
        }
    }

    _scrollActiveTabIntoView() {
        const active = this.getActiveTab();
        if (!active?.state?.ui?.item || !this.tabList) return;

        // Scroll only the horizontal tab-list container. Using Element.scrollIntoView
        // walks every scroll ancestor up to <html>, so when the terminal section
        // sits below the fold it drags the whole page down to it (e.g. after
        // navigating to Dashboard and hitting window.scrollTo(0,0)).
        const item = active.state.ui.item;
        const list = this.tabList;
        const itemRect = item.getBoundingClientRect();
        const listRect = list.getBoundingClientRect();

        if (itemRect.left < listRect.left) {
            list.scrollLeft -= (listRect.left - itemRect.left);
        } else if (itemRect.right > listRect.right) {
            list.scrollLeft += (itemRect.right - listRect.right);
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
        const availableHeight = Math.max(0, Math.round(viewportHeight - containerRect.top - 6));
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

    // Single source of truth for the metadata persisted via saveTabMeta. Every field that must
    // survive a reload (pin/minimize/notify/accent/…) lives here, so adding a new one can't be
    // silently forgotten at one of the many save sites. Pass `overrides` for the rare case that
    // diverges from tab state (e.g. forcing minimized:false).
    _tabMetaPayload(state, overrides = {}) {
        return {
            label: state.label,
            icon: state.icon,
            accentColor: state.accentColor,
            taskKey: state.taskKey,
            customLabel: state.customLabel,
            minimized: state.minimized === true,
            pinned: state.pinned === true,
            notifyEnabled: state.notifyEnabled === true,
            workingDirectory: state.workingDirectory,
            ...overrides
        };
    }

    saveTabMeta(tabId, metadata = {}) {
        try {
            const payload = {
                label: cleanString(metadata.label) || null,
                icon: cleanString(metadata.icon) || null,
                accentColor: this.normalizeAccentColor(metadata.accentColor),
                taskKey: cleanString(metadata.taskKey) || null,
                customLabel: metadata.customLabel === true,
                minimized: metadata.minimized === true,
                pinned: metadata.pinned === true,
                notifyEnabled: metadata.notifyEnabled === true,
                workingDirectory: cleanString(metadata.workingDirectory) || null
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
                taskKey: cleanString(parsed?.taskKey) || null,
                customLabel: parsed?.customLabel === true,
                minimized: parsed?.minimized === true,
                pinned: parsed?.pinned === true,
                notifyEnabled: parsed?.notifyEnabled === true,
                workingDirectory: cleanString(parsed?.workingDirectory) || null
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

    adjustFontSize(delta, absolute) {
        const current = this.settings?.loadFontSize() ?? 14;
        const next = Math.max(6, Math.min(72, absolute != null ? absolute : current + delta));
        this.settings?.saveFontSize(next);
        if (this.fontSizeLabel) this.fontSizeLabel.textContent = next;
        this.settings?.syncFontSizeInput(next);
        this.tabs.forEach((tab) => tab.instance.applyFontSize(next));
    }

    // -------------------------------------------------------------------------
    // Settings panel — implementation lives in terminal-settings.js
    // -------------------------------------------------------------------------

    toggleSettingsPanel(forceOpen) {
        this.settings?.togglePanel(forceOpen);
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

    applyTheme(key) { this.settings?.applyTheme(key); }
    applyRendererPreference(renderer) { this.settings?.applyRendererPreference(renderer); }
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
        const findTab = (payload) => {
            if (!this.manager) return null;
            // Fast path: use tabId injected by parent relay
            if (payload?.tabId) {
                return this.manager.tabs.get(payload.tabId) || null;
            }
            // Fallback: search by sessionId
            if (payload?.sessionId) {
                for (const tab of this.manager.tabs.values()) {
                    if (tab.state.sessionId === payload.sessionId) return tab;
                }
            }
            return null;
        };

        const setSessionState = (tab, state) => {
            const item = tab.state.ui.item;
            item.classList.remove('tab-idle', 'tab-busy', 'tab-completed');
            if (state) item.classList.add(state);
            // Re-trigger flash animation on idle entry
            if (state === 'tab-idle') {
                const el = item.querySelector('.vb-terminal-tab-status');
                if (el) {
                    el.style.animation = 'none';
                    el.offsetHeight;
                    el.style.animation = '';
                }
            }
        };

        appEventClient.on('session_started', (payload) => {
            const tab = findTab(payload);
            if (!tab) return;
            setSessionState(tab, 'tab-busy');
            tab.instance?.statusController?.onSessionBusy();
        });

        appEventClient.on('session_busy', (payload) => {
            const tab = findTab(payload);
            if (!tab) return;
            setSessionState(tab, 'tab-busy');
            tab.instance?.statusController?.onSessionBusy();
        });

        appEventClient.on('session_input', (payload) => {
            const tab = findTab(payload);
            if (!tab) return;
            if ((payload?.kind || '').toLowerCase() === 'submit') {
                setSessionState(tab, 'tab-busy');
            }
            tab.instance?.statusController?.onSessionInput(payload?.kind);
        });

        appEventClient.on('session_waiting_for_user', (payload) => {
            const tab = findTab(payload);
            if (!tab) return;
            setSessionState(tab, 'tab-idle');
            tab.instance?.statusController?.onWaitingForUserSelection();
        });

        appEventClient.on('session_idle', (payload) => {
            const tab = findTab(payload);
            const cli = payload?.cli || 'Session';
            if (tab) {
                setSessionState(tab, 'tab-idle');
                tab.instance?.statusController?.onSessionIdle();
            } else {
                this.app.showToast(cli, 'Terminal is idle', 'info', { compact: true });
            }
        });

        appEventClient.on('session_completed', (payload) => {
            const tab = findTab(payload);
            const cli = payload?.cli || 'Session';
            const exitCode = payload?.exitCode;
            const exitText = exitCode != null ? ` (exit ${exitCode})` : '';
            if (tab) {
                setSessionState(tab, 'tab-completed');
                tab.instance?.statusController?.onSessionCompleted();
                // Flash for ~5s (10 × 0.5s) then settle to Ready
                setTimeout(() => setSessionState(tab, 'tab-idle'), 5000);
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
        const historySidebar = new ChatHistorySidebar(this.app);
        historySidebar.mount(root, {
            onToggle: (open) => this.manager?.syncHistoryPanelState(open)
        });
        if (this.manager) {
            this.manager.historySidebar?.destroy();
            this.manager.historySidebar = historySidebar;
        } else {
            historySidebar.destroy();
        }
    }

    renderTerminalPanel(options = {}) {
        const isFocusView = options.focusView === true;
        const focusButtonHtml = isFocusView ? '' : `
                            <button type="button" class="vb-terminal-control-btn icon-btn" id="terminal-popout-btn" title="Open in fullscreen" aria-label="Open in fullscreen">
                                <i class="fa-solid fa-up-right-and-down-left-from-center"></i>
                                <span class="vb-terminal-control-text">Open In Fullscreen</span>
                            </button>
        `;

        const rootPath = this.app.data.configs?.rootPath || '';
        const projectDirHtml = rootPath ? `
                    <span class="vb-terminal-project-path" title="${this.app.escapeHtml(rootPath)}">
                        <i class="fa-solid fa-folder-open"></i>
                        ${this.app.escapeHtml(rootPath)}
                    </span>` : '';

        return `
            <div class="card ${isFocusView ? 'vb-terminal-page-mode vb-terminal-expanded vb-terminal-focus-card' : 'mb-4'}" id="vb-terminal-panel">
                <div class="card-header d-flex justify-content-between align-items-center gap-3 flex-wrap">
                    <div class="d-flex align-items-center gap-2 flex-wrap">
                        <span class="card-title d-inline-flex align-items-center gap-2 mb-0">
                            <svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" fill="currentColor" viewBox="0 0 576 512" style="opacity: 0.85;">
                                <path d="M9.4 86.6C-3.1 74.1-3.1 53.9 9.4 41.4s32.8-12.5 45.3 0l192 192c12.5 12.5 12.5 32.8 0 45.3l-192 192c-12.5 12.5-32.8 12.5-45.3 0s-12.5-32.8 0-45.3L178.7 256 9.4 86.6zM256 416l288 0c17.7 0 32 14.3 32 32s-14.3 32-32 32l-288 0c-17.7 0-32-14.3-32-32s14.3-32 32-32z"/>
                            </svg>
                            VibeRails Sessions
                        </span>
                        <span class="badge bg-secondary d-none" id="terminal-status-badge"></span>
                        ${projectDirHtml}
                    </div>
                    <div class="d-flex gap-2 align-items-center" id="terminal-actions">
                        <button class="btn btn-sm btn-outline-success d-none d-inline-flex align-items-center gap-1" id="terminal-stop-btn" title="Reconnect terminal session">
                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                <path d="m11.596 8.697-6.363 3.692C4.53 12.793 4 12.49 4 11.998V4.002c0-.492.53-.795.997-.51l6.363 3.692a.802.802 0 0 1 0 1.393z"/>
                            </svg>
                            <span>Connect</span>
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
                    <!-- Far-right home for the persistent token-compression meter (app.terminalTokenCompression,
                         re-parented here in TerminalManager.initialize()). ms-auto pushes it opposite
                         the LLM picker + Start button. -->
                    <div class="vb-terminal-proxy-activity-slot ms-auto" id="terminal-proxy-activity-slot"></div>
                </div>
                <div class="vb-terminal-window-shell">
                    <div class="vb-terminal-window-header">
                        <div class="vb-terminal-tab-strip" id="vb-terminal-tab-strip">
                            <button type="button" class="vb-terminal-tab-scroll vb-terminal-tab-scroll-left" id="vb-terminal-tab-scroll-left" aria-label="Scroll tabs left" hidden>
                                <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10" fill="currentColor" viewBox="0 0 16 16"><path fill-rule="evenodd" d="M11.354 1.646a.5.5 0 0 1 0 .708L5.707 8l5.647 5.646a.5.5 0 0 1-.708.708l-6-6a.5.5 0 0 1 0-.708l6-6a.5.5 0 0 1 .708 0"/></svg>
                            </button>
                            <div class="vb-terminal-tab-list" id="vb-terminal-tab-list">
                                <button type="button" class="vb-terminal-tab-add" id="vb-terminal-tab-add-btn" title="Open a new terminal tab" aria-label="Open a new terminal tab">+</button>
                            </div>
                            <button type="button" class="vb-terminal-tab-scroll vb-terminal-tab-scroll-right" id="vb-terminal-tab-scroll-right" aria-label="Scroll tabs right" hidden>
                                <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10" fill="currentColor" viewBox="0 0 16 16"><path fill-rule="evenodd" d="M4.646 1.646a.5.5 0 0 1 .708 0l6 6a.5.5 0 0 1 0 .708l-6 6a.5.5 0 0 1-.708-.708L10.293 8 4.646 2.354a.5.5 0 0 1 0-.708"/></svg>
                            </button>
                        </div>
                        <div class="vb-terminal-window-controls vb-terminal-window-controls-right">
                            <div class="vb-terminal-tab-undo-wrapper" id="vb-terminal-tab-undo-wrapper" hidden>
                                <button type="button" class="vb-terminal-tab-undo" id="vb-terminal-tab-undo-btn" title="Restore a recently closed terminal (terminals auto-close after ${PENDING_CLOSE_LABEL})" aria-label="Restore a recently closed terminal">
                                    <span class="vb-undo-icon" aria-hidden="true">
                                        <i class="fa-regular fa-window-restore"></i>
                                    </span>
                                    <span class="vb-undo-count" id="vb-terminal-tab-undo-count">0</span>
                                </button>
                                <div class="vb-terminal-tab-undo-select-wrap" aria-hidden="true">
                                    <select id="vb-terminal-tab-undo-select" multiple></select>
                                </div>
                            </div>
                            <button type="button" class="vb-terminal-control-btn icon-btn d-none" id="terminal-editor-btn" title="Open in text editor" aria-label="Open in text editor">
                                <i class="fa-solid fa-file-pen"></i>
                            </button>
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
                            <button type="button" class="vb-terminal-control-btn icon-btn" id="terminal-settings-btn" title="Terminal settings &amp; actions" aria-label="Terminal settings and actions">&#x2699;</button>
                            ${focusButtonHtml}
                        </div>
                    </div>
                    <div class="vb-terminal-window-title-bar">
                        <div class="vb-terminal-window-title-row">
                            <div class="vb-terminal-window-title" id="vb-terminal-window-title">Terminals run safely in the background even if you navigate away.</div>
                            <div class="vb-terminal-window-session d-none" id="vb-terminal-window-session" aria-live="polite">
                                <span class="vb-terminal-window-session-label">Session ID</span>
                                <span class="vb-terminal-window-session-value" id="vb-terminal-window-session-value"></span>
                            </div>
                        </div>
                    </div>
                    <div class="card-body p-0" id="terminal-container" style="display: none; overflow: hidden;">
                        <div id="vb-terminal-tab-panels" class="vb-terminal-tab-panels"></div>
                    </div>
                    <div class="card-body text-center text-muted" id="terminal-placeholder">
                        <p class="mb-3">Select a LLM to continue</p>
                        <p class="small mb-0">Use the <strong>+</strong> button to open a tab, then pick a CLI/environment.</p>
                    </div>
                    ${renderTerminalSettingsPanelHtml()}
                </div>
            </div>
        `;
    }
}



