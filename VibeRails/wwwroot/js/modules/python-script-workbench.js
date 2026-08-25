import { ensureMonaco } from './monaco-loader.js';
import { confirmDialog, escapeHtml, formatRelativeTime } from './utils.js';
import { formatFileExplorerSize } from './file-explorer.js';
import { PYTHON_SCRIPT_STATUS_META, formatPythonRunOutput } from './python-scripts-controller.js';

const API = '/api/v1/python-scripts';
const VIEW_NAME = 'python-script';

export const TERMINAL_HEIGHT_STORAGE_KEY = 'viberails.pythonWorkbench.terminalHeight';
export const TERMINAL_WIDTH_STORAGE_KEY = 'viberails.pythonWorkbench.terminalWidth';
export const TERMINAL_MIN_HEIGHT = 240;
// Side by side is THE layout: editor left, terminal right, both the full working height
// (Rob: "needs to be side by side and not stacked — the terminal will be too small").
// Stacking is only the fallback for a window too narrow to hold two usable columns, so
// this threshold sits just above EDITOR_MIN_WIDTH + TERMINAL_MIN_WIDTH + the splitter
// rather than at a comfortable desktop width — a docked VS Code webview must still get
// columns. The CSS media query on .python-workbench-panes uses the same number.
export const SIDE_BY_SIDE_MIN_VIEWPORT_WIDTH = 880;
export const TERMINAL_MIN_WIDTH = 320;
export const EDITOR_MIN_WIDTH = 380;
// Short viewports (a docked VS Code webview, a laptop with the panel maximised) trade
// terminal rows for code lines; the CSS media query below the workbench block uses
// the same threshold and floor so what the splitter allows matches what CSS renders.
export const TERMINAL_MIN_HEIGHT_COMPACT = 180;
export const COMPACT_VIEWPORT_MAX_HEIGHT = 720;
export const EDITOR_MIN_HEIGHT = 260;
export const SPLITTER_KEY_STEP = 24;
const RELOAD_POLL_MS = 4000;
const LAYOUT_REFRESH_THROTTLE_MS = 80;

const AGENT_CONSTRAINTS = "It's a VibeRails Automation script: keep it a single self-contained file at that path; "
    + "it only runs after I sign it in VibeRails, so tell me when you're done and what changed.";

/**
 * Pasted into a live agent session WITHOUT submitting: the user finishes the
 * "Change: " sentence themselves and presses Enter.
 */
export function buildAskAgentBrief({ name, path }) {
    return `Please help me change the Python script ${name} at ${path}.\n${AGENT_CONSTRAINTS}`;
}

/**
 * Submitted automatically as the first turn of a fresh session (initialPrompt travels
 * as argv), so it must stand on its own — no half-finished sentence to complete.
 */
export function buildAskAgentInitialPrompt({ name, path }) {
    return `Read ${path} and summarize what it does in 2-3 lines, `
        + `then wait for my change request. ${AGENT_CONSTRAINTS}`;
}

/** The terminal floor for the current viewport (mirrors the max-height media query in CSS). */
export function terminalMinHeight() {
    return (globalThis.innerHeight || Infinity) <= COMPACT_VIEWPORT_MAX_HEIGHT
        ? TERMINAL_MIN_HEIGHT_COMPACT
        : TERMINAL_MIN_HEIGHT;
}

/** Keeps the docked terminal between its floor and whatever leaves the editor its minimum. */
export function clampTerminalHeight(height, maxHeight = Infinity) {
    const floor = terminalMinHeight();
    const numeric = Number(height);
    if (!Number.isFinite(numeric)) return floor;
    const ceiling = Number.isFinite(maxHeight) ? Math.max(floor, maxHeight) : Infinity;
    return Math.round(Math.min(ceiling, Math.max(floor, numeric)));
}

/** True when the viewport is wide enough for the editor | terminal layout (matches the CSS query). */
export function isSideBySideLayout() {
    try {
        return globalThis.matchMedia?.(`(min-width: ${SIDE_BY_SIDE_MIN_VIEWPORT_WIDTH}px)`)?.matches === true;
    } catch {
        return false;
    }
}

/** Keeps the docked terminal column between its floor and whatever leaves the editor its minimum. */
export function clampTerminalWidth(width, maxWidth = Infinity) {
    const numeric = Number(width);
    if (!Number.isFinite(numeric)) return TERMINAL_MIN_WIDTH;
    const ceiling = Number.isFinite(maxWidth) ? Math.max(TERMINAL_MIN_WIDTH, maxWidth) : Infinity;
    return Math.round(Math.min(ceiling, Math.max(TERMINAL_MIN_WIDTH, numeric)));
}

function readStoredDimension(storage, key) {
    try {
        const raw = storage?.getItem?.(key);
        const parsed = Number.parseInt(raw, 10);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
    } catch {
        return null;
    }
}

/** The persisted splitter position (stacked layout), or null when nothing usable is stored. */
export function readStoredTerminalHeight(storage) {
    return readStoredDimension(storage, TERMINAL_HEIGHT_STORAGE_KEY);
}

/** The persisted terminal column width (side-by-side layout), or null. */
export function readStoredTerminalWidth(storage) {
    return readStoredDimension(storage, TERMINAL_WIDTH_STORAGE_KEY);
}

/** The server's optimistic-concurrency refusal (PythonScriptService.SaveContentAsync). */
export function isStaleSaveError(message) {
    return /changed after it was opened|reopen it before saving/i.test(String(message || ''));
}

/**
 * The Python script workbench view ('python-script', data = { name }): a Back bar with the
 * script's identity and signing actions, a Monaco editor with a rail of all scripts, and
 * the agent terminal docked underneath (the same panel the Code quality page mounts),
 * started in the scripts directory so "have Claude change the python" is one click.
 *
 * Every signing/authoring flow delegates to PythonScriptsController's public methods, so
 * the Automation page section and this view share one implementation. The workbench adds
 * what a page needs and a modal never did: unsaved-changes guards, live reload while an
 * agent (or VS Code) edits the file on disk, and a draggable editor/terminal split.
 */
export class PythonScriptWorkbench {
    constructor(app) {
        this.app = app;
        this.root = null;
        this.name = null;
        this.script = null;
        this.state = null;
        this.editor = null;
        this.monaco = null;
        this.baseline = '';
        this.version = '';
        this.status = 'unapproved';
        this.saving = false;
        this.lastRun = null;
        this.confirm = confirmDialog;
        this._running = false;
        this._generation = 0;
        this._loadToken = null;
        this._unsubscribeRun = null;
        // The in-flight Monaco mount: overlapping loads (a rail click during the first
        // open) share it instead of each creating an editor in the same host.
        this._editorMounting = null;
        this._pollTimer = null;
        this._pollInFlight = false;
        // Bumped by every save(); a poll that started before a save must not apply the
        // (older) content it fetched on top of the freshly saved editor.
        this._saveSerial = 0;
        // True while a shared rename/delete/duplicate/create flow is mid-flight, so the
        // poll cannot misread the momentary 404 as "deleted on disk".
        this._mutating = false;
        this._diskChange = null;
        this._deletedOnDisk = false;
        // The last explicit hint ('Saved.', 'Reloaded from disk · …'); it survives the
        // list refreshes that follow and clears on the next keystroke.
        this._hintNotice = '';
        this._terminalHeight = null;
        this._terminalWidth = null;
        // matchMedia list for the side-by-side threshold; its change event re-labels the
        // splitter and refits the terminal when the window crosses it.
        this._layoutMedia = null;
        this._onLayoutMediaChange = () => {
            this._syncSplitterAria();
            this.app.terminalController?.refreshLayout?.();
        };
        this._removeNavigationGuard = null;
        this._unsubscribeState = null;
        this._leaveConfirmed = false;
        this._leaveConfirmPending = false;
        this._lastLayoutRefreshAt = 0;
        this._layoutRefreshTimer = null;
        this._onWindowFocus = () => void this.checkDisk();
        this._onBeforeUnload = (event) => {
            if (!this.isDirty) return undefined;
            event.preventDefault();
            event.returnValue = '';
            return '';
        };
        this._onDocumentClick = (event) => {
            if (!this.root || this.root.contains(event.target)) return;
            this._closeMenu();
        };
    }

    /** The shared flows (PIN prompt, rename, delete, duplicate, run…) live on the section controller. */
    get scripts() {
        return this.app.jobController?.pythonScripts || null;
    }

    get isDirty() {
        return Boolean(this.editor) && this.editor.getValue() !== this.baseline;
    }

    get scriptsDirectory() {
        return this.state?.scriptsDirectory || '';
    }

    async loadView(data = {}) {
        this.unload();
        const generation = ++this._generation;
        const content = document.getElementById('app-content');
        if (!content) return;

        const name = typeof data?.name === 'string' ? data.name.trim() : '';
        if (!name) {
            this.app.showToast('No script selected', 'Pick a script on the Automation page.', 'info');
            this._leaveToAutomation();
            return;
        }

        this.name = name;
        content.innerHTML = this.renderShell(name);
        this.root = content.querySelector('[data-python-workbench]');
        if (!this.root) return;
        this._bindShell();
        this._applyStoredTerminalHeight();
        this._applyStoredTerminalWidth();
        this._watchLayoutMedia();
        // The separator's aria-valuenow/max come from measured panes, so they are filled
        // in once layout has run (a stored size already set them synchronously).
        requestAnimationFrame(() => {
            if (generation === this._generation) this._syncSplitterAria();
        });

        const scripts = this.scripts;
        this._unsubscribeState = scripts?.onStateChange?.((state) => this._onSharedStateChange(state)) || null;
        this._unsubscribeRun = scripts?.onRunChanged?.((name) => this._onRunChanged(name)) || null;
        this._removeNavigationGuard = this.app.registerNavigationGuard?.(
            (payload) => this._guardNavigation(payload)) || null;
        window.addEventListener('beforeunload', this._onBeforeUnload);
        // An agent or VS Code edits the file behind our back; coming back to this window
        // is the natural moment to notice, on top of the background poll.
        window.addEventListener('focus', this._onWindowFocus);
        document.addEventListener('visibilitychange', this._onWindowFocus);
        document.addEventListener('click', this._onDocumentClick, true);

        const state = scripts ? await scripts.ensureState() : null;
        if (generation !== this._generation) return;
        this.state = state;
        if (!state) {
            void this._mountTerminal();
            this._showEditorState({ error: 'Could not load the script list.', retry: true });
            return;
        }

        this.script = scripts.scriptByName(name);
        if (!this.script) {
            this.app.showToast('Script not found', `${name} is no longer in the scripts folder.`, 'warning');
            this._leaveToAutomation();
            return;
        }
        this.status = this.script.status || 'unapproved';
        this.lastRun = scripts.lastRunByName?.get?.(name) || null;
        this._renderIdentity();
        this._renderRail();
        this._renderOutput();
        void this._mountTerminal();
        await this._loadContent(generation);
        if (generation !== this._generation) return;
        this._startPolling();
    }

    unload() {
        this._generation += 1;
        this._loadToken = null;
        this._editorMounting = null;
        this._stopPolling();
        if (this._layoutRefreshTimer) {
            clearTimeout(this._layoutRefreshTimer);
            this._layoutRefreshTimer = null;
        }
        this._removeNavigationGuard?.();
        this._removeNavigationGuard = null;
        this._unsubscribeState?.();
        this._unsubscribeState = null;
        this._unsubscribeRun?.();
        this._unsubscribeRun = null;
        this._unwatchLayoutMedia();
        if (typeof window !== 'undefined') {
            window.removeEventListener('beforeunload', this._onBeforeUnload);
            window.removeEventListener('focus', this._onWindowFocus);
        }
        if (typeof document !== 'undefined') {
            document.removeEventListener('visibilitychange', this._onWindowFocus);
            document.removeEventListener('click', this._onDocumentClick, true);
        }
        try { this.editor?.dispose(); } catch { /* already gone */ }
        this.editor = null;
        this.root = null;
        this.name = null;
        this.script = null;
        this.state = null;
        this.baseline = '';
        this.version = '';
        this.status = 'unapproved';
        this.saving = false;
        this.lastRun = null;
        this._pollInFlight = false;
        this._mutating = false;
        this._diskChange = null;
        this._deletedOnDisk = false;
        this._hintNotice = '';
        this._running = false;
        this._leaveConfirmed = false;
        this._leaveConfirmPending = false;
    }

    // --- shell ---

    renderShell(name) {
        const safeName = escapeHtml(name);
        return `
            <div class="view python-workbench" data-view="${VIEW_NAME}" data-python-workbench>
                <header class="python-workbench-topbar">
                    <button class="btn btn-sm btn-outline-secondary rules-subpage-back" type="button"
                            data-action="go-back" title="Back to Automation">
                        <i class="fa-solid fa-arrow-left" aria-hidden="true"></i>
                        Back
                    </button>
                    <div class="python-workbench-identity">
                        <i class="fa-brands fa-python" aria-hidden="true"></i>
                        <div class="python-workbench-identity-copy">
                            <div class="python-workbench-title-row">
                                <h1 class="python-workbench-title">
                                    <span data-workbench-name>${safeName}</span><span class="python-workbench-dirty-mark" data-workbench-dirty hidden role="img" title="Unsaved changes" aria-label="Unsaved changes">•</span>
                                </h1>
                                <span class="python-script-status" data-tone="neutral" data-workbench-status></span>
                            </div>
                            <p class="python-workbench-meta" data-workbench-meta></p>
                        </div>
                    </div>
                    <div class="python-workbench-actions">
                        <button class="btn btn-sm btn-primary" type="button" data-workbench-action="run" disabled
                                title="Sign the script before running it">
                            <i class="fa-solid fa-play me-1" aria-hidden="true"></i>Run
                        </button>
                        <button class="btn btn-sm btn-outline-secondary" type="button" data-workbench-action="approve"
                                title="Sign the saved file with your PIN">
                            <i class="fa-solid fa-signature me-1" aria-hidden="true"></i><span data-workbench-approve-label>Sign</span>
                        </button>
                        <div class="python-script-menu-wrap">
                            <button class="btn btn-sm btn-outline-secondary python-script-menu-toggle" type="button"
                                    data-workbench-action="menu" aria-haspopup="menu" aria-expanded="false"
                                    aria-label="More actions for ${safeName}" title="More actions">
                                <i class="fa-solid fa-ellipsis-vertical" aria-hidden="true"></i>
                            </button>
                            <div class="python-script-menu" role="menu" data-workbench-menu hidden></div>
                        </div>
                    </div>
                </header>

                <div class="python-workbench-panes" data-workbench-panes>
                    <section class="rules-section card python-workbench-editor-card" aria-labelledby="python-workbench-editor-title">
                        <header class="rules-section-header python-workbench-editor-header">
                            <div class="rules-section-heading">
                                <h2 id="python-workbench-editor-title">Script</h2>
                                <span class="rules-section-note" data-workbench-hint></span>
                            </div>
                            <div class="python-workbench-editor-actions" aria-label="Editor actions">
                                <button class="btn btn-sm btn-outline-primary" type="button" data-workbench-action="ask-agent"
                                        title="Ask the agent in the terminal below to change this script">
                                    <i class="fa-solid fa-wand-magic-sparkles me-1" aria-hidden="true"></i>Ask agent
                                </button>
                                <button class="btn btn-sm btn-outline-secondary" type="button" data-workbench-action="save"
                                        title="Save (Ctrl/⌘+S)" disabled>
                                    <i class="fa-solid fa-floppy-disk me-1" aria-hidden="true"></i>Save
                                </button>
                                <button class="btn btn-sm btn-outline-primary" type="button" data-workbench-action="save-sign"
                                        title="Save, then sign this exact version with your PIN">
                                    <i class="fa-solid fa-signature me-1" aria-hidden="true"></i>Save &amp; sign
                                </button>
                            </div>
                        </header>
                        <div class="python-workbench-banner" role="status" data-workbench-banner hidden></div>
                        <div class="python-workbench-body">
                            <nav class="python-workbench-rail" aria-label="Scripts" data-workbench-rail>
                                <ul class="python-workbench-rail-list" role="list" data-workbench-rail-list></ul>
                            </nav>
                            <div class="python-workbench-editor-host" data-workbench-editor-host>
                                <div class="python-workbench-editor-mount" data-workbench-editor-mount></div>
                                <div class="python-workbench-editor-state" data-workbench-editor-state role="status">
                                    <span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Loading ${safeName}…
                                </div>
                            </div>
                        </div>
                        <details class="python-script-output python-workbench-output" data-workbench-output hidden>
                            <summary data-workbench-output-summary></summary>
                            <pre data-workbench-output-body></pre>
                        </details>
                    </section>

                    <div class="python-workbench-splitter" role="separator"
                         aria-orientation="${isSideBySideLayout() ? 'vertical' : 'horizontal'}"
                         aria-label="Resize terminal" aria-valuemin="${isSideBySideLayout() ? TERMINAL_MIN_WIDTH : terminalMinHeight()}" tabindex="0"
                         title="Drag to resize the terminal (Arrow keys when focused)" data-workbench-splitter></div>

                    <div class="rules-pane rules-pane-terminal python-workbench-terminal" data-terminal-section aria-label="Agent terminal">
                        <div class="rules-terminal-host" data-terminal-content></div>
                    </div>
                </div>
            </div>`;
    }

    _bindShell() {
        const root = this.root;
        if (!root) return;
        // data-action="go-back" is bound once, globally, by app.bindGlobalActions.
        root.addEventListener('click', (event) => this._handleClick(event));
        root.addEventListener('keydown', (event) => this._handleKeydown(event));
        this._bindSplitter();
    }

    _handleClick(event) {
        const root = this.root;
        if (!root) return;
        const opener = event.target.closest?.('[data-workbench-open]');
        if (opener && root.contains(opener)) {
            this._closeMenu();
            void this.switchTo(opener.dataset.workbenchOpen);
            return;
        }
        const button = event.target.closest?.('[data-workbench-action]');
        if (!button || !root.contains(button)) {
            this._closeMenu();
            return;
        }
        const action = button.dataset.workbenchAction;
        if (action !== 'menu') this._closeMenu();
        switch (action) {
            case 'menu': return this._toggleMenu(button);
            case 'run': return void this.run();
            case 'run-terminal': return void this.runInTerminal(button);
            case 'approve': return void this.sign();
            case 'revoke': return void this.revoke();
            case 'save': return void this.save();
            case 'save-sign': return void this.save({ sign: true });
            case 'ask-agent': return void this.askAgent();
            case 'open-vscode': return this.scripts?.openInVsCode?.(this.name);
            case 'duplicate': return void this.duplicate();
            case 'rename': return void this.rename();
            case 'copy-path': return void this.scripts?.copyPath?.(this.name);
            case 'delete': return void this.deleteScript();
            case 'new': return void this.newScript();
            case 'reload': return void this.reloadFromDisk();
            case 'keep-edits': return void this.keepMyEdits();
            case 'recreate': return void this.recreateFromEditor();
            case 'leave': return this._leaveDiscarding();
            case 'retry': return void this.retryLoad();
            default: return undefined;
        }
    }

    _handleKeydown(event) {
        if (event.key === 'Escape') {
            const menu = this.root?.querySelector('[data-workbench-menu]');
            if (menu && !menu.hidden) {
                // Consumed here, so app.js's Escape shortcut does not also navigate back.
                event.preventDefault();
                this._closeMenu();
                this.root?.querySelector('[data-workbench-action="menu"]')?.focus();
                return;
            }
            // Escape from a toolbar button (focused after a click) or the splitter hands
            // focus back to the code instead of meaning "leave the workbench" — app.js
            // stands down on defaultPrevented. Inside the editor and the terminal the
            // widgets own the key, so those are left alone.
            const target = event.target;
            if (target?.closest?.('button, [data-workbench-splitter]') && !target.closest?.('.monaco-editor, .xterm')) {
                event.preventDefault();
                this.editor?.focus?.();
            }
            return;
        }
        if ((event.ctrlKey || event.metaKey) && !event.altKey && String(event.key).toLowerCase() === 's') {
            // Monaco binds its own Ctrl/⌘+S; the terminal owns its keys (Ctrl+S is XOFF).
            if (event.target.closest?.('.monaco-editor, .xterm')) return;
            event.preventDefault();
            void this.save();
        }
    }

    _toggleMenu(toggle) {
        const menu = this.root?.querySelector('[data-workbench-menu]');
        const wasOpen = menu && !menu.hidden;
        this._closeMenu();
        if (!menu || wasOpen) return;
        menu.hidden = false;
        toggle.setAttribute('aria-expanded', 'true');
        menu.querySelector('.python-script-menu-item')?.focus();
    }

    _closeMenu() {
        const menu = this.root?.querySelector('[data-workbench-menu]');
        if (menu) menu.hidden = true;
        this.root?.querySelector('[data-workbench-action="menu"]')?.setAttribute('aria-expanded', 'false');
    }

    // --- rendering ---

    _renderIdentity() {
        const root = this.root;
        if (!root) return;
        const script = this.script;
        const status = this.status || 'unapproved';
        const meta = PYTHON_SCRIPT_STATUS_META[status] || PYTHON_SCRIPT_STATUS_META.unapproved;

        const nameEl = root.querySelector('[data-workbench-name]');
        if (nameEl) nameEl.textContent = this.name || '';
        const pill = root.querySelector('[data-workbench-status]');
        if (pill) {
            pill.dataset.tone = meta.tone;
            pill.innerHTML = `<i class="fa-solid ${meta.icon}" aria-hidden="true"></i>${meta.label}`;
        }
        const details = root.querySelector('[data-workbench-meta]');
        if (details) {
            details.textContent = [
                script?.path || '',
                script ? formatFileExplorerSize(script.sizeBytes) : '',
                script?.modifiedUtc ? `edited ${formatRelativeTime(script.modifiedUtc).toLowerCase()}` : '',
                status === 'approved' && script?.approvedUtc
                    ? `signed ${formatRelativeTime(script.approvedUtc).toLowerCase()}`
                    : ''
            ].filter(Boolean).join(' · ');
        }
        this._renderRunAvailability();
        const approveLabel = root.querySelector('[data-workbench-approve-label]');
        // 'modified' still carries a (stale) signature, so signing again is a re-sign.
        if (approveLabel) approveLabel.textContent = status === 'unapproved' ? 'Sign' : 'Re-sign';
        const menuToggle = root.querySelector('[data-workbench-action="menu"]');
        if (menuToggle) menuToggle.setAttribute('aria-label', `More actions for ${this.name}`);
        const menu = root.querySelector('[data-workbench-menu]');
        if (menu) menu.innerHTML = this.renderMenuItems();
    }

    /**
     * A captured run outlives its little window — close the window mid-run and the interpreter
     * keeps going — so the button follows the controller's runningNames, not the window. The
     * old early-return on _running left it enabled and titled as if nothing were happening;
     * it was only ever hidden by the modal on top of it.
     */
    _renderRunAvailability() {
        const run = this.root?.querySelector('[data-workbench-action="run"]');
        if (!run) return;
        const running = this._running || this.scripts?.runningNames?.has(this.name) === true;
        const dirty = this.isDirty;
        const canRun = this.status === 'approved' && !dirty && !running;
        run.disabled = !canRun;
        run.innerHTML = running
            ? '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Running…'
            : '<i class="fa-solid fa-play me-1" aria-hidden="true"></i>Run';
        run.title = running
            ? `${this.name} is already running`
            : dirty
                ? 'Save and sign your changes before running'
                : canRun
                    ? `Run ${this.name} and read its output here`
                    : 'Sign the script before running it';
    }

    /**
     * A run for this script started, finished, or recorded its result. Reading lastRunByName
     * when the window closes is too early: close it while the POST is still in flight and the
     * result lands here afterwards, with nothing else to repaint the output panel.
     */
    _onRunChanged(name) {
        if (!this.root || !name || name !== this.name) return;
        const recorded = this.scripts?.lastRunByName?.get?.(name);
        if (recorded) this.lastRun = recorded;
        this._renderRunAvailability();
        this._renderOutput();
    }

    renderMenuItems() {
        const item = (action, icon, label, extraClass = '') => `
            <button class="python-script-menu-item ${extraClass}" type="button" role="menuitem"
                    data-workbench-action="${action}">
                <i class="fa-solid ${icon}" aria-hidden="true"></i><span>${label}</span>
            </button>`;
        return [
            this.status === 'approved' ? item('run-terminal', 'fa-terminal', 'Run in terminal…') : '',
            this.scripts?.canOpenInVsCode?.() ? item('open-vscode', 'fa-arrow-up-right-from-square', 'Open in VS Code') : '',
            item('duplicate', 'fa-copy', 'Duplicate…'),
            item('rename', 'fa-i-cursor', 'Rename…'),
            item('copy-path', 'fa-clipboard', 'Copy full path'),
            this.status === 'unapproved' ? '' : item('revoke', 'fa-ban', 'Remove signature'),
            item('delete', 'fa-trash', 'Delete…', 'python-script-menu-item-danger')
        ].filter(Boolean).join('');
    }

    _renderRail() {
        const list = this.root?.querySelector('[data-workbench-rail-list]');
        if (list) list.innerHTML = this.renderRailItems();
    }

    /** Compact rows: name + signing dot; the open script is highlighted; "+ New script" last. */
    renderRailItems() {
        const rows = (this.state?.scripts || []).map((script) => {
            const meta = PYTHON_SCRIPT_STATUS_META[script.status] || PYTHON_SCRIPT_STATUS_META.unapproved;
            const name = escapeHtml(script.name);
            const current = script.name === this.name;
            return `
            <li>
                <button type="button" class="python-workbench-rail-item" data-workbench-open="${name}"
                        ${current ? 'aria-current="page"' : ''} title="${name} · ${meta.label}">
                    <span class="python-workbench-rail-dot" data-tone="${meta.tone}" aria-hidden="true"></span>
                    <span class="python-workbench-rail-name">${name}</span>
                </button>
            </li>`;
        });
        rows.push(`
            <li>
                <button type="button" class="python-workbench-rail-item python-workbench-rail-new" data-workbench-action="new"
                        title="Create a new script in the scripts folder">
                    <i class="fa-solid fa-plus" aria-hidden="true"></i><span>New script</span>
                </button>
            </li>`);
        return rows.join('');
    }

    /**
     * The editor header's hint line, the dirty mark and the Save button's enabled state.
     * An explicit `message` sticks (list refreshes re-render the hint) until the next
     * keystroke, so "Saved." / "Reloaded from disk" are not wiped by the refresh that
     * follows them.
     */
    _renderHint(message = '') {
        const root = this.root;
        if (!root) return;
        const dirty = this.isDirty;
        if (message) this._hintNotice = message;
        else if (dirty) this._hintNotice = '';
        const hint = root.querySelector('[data-workbench-hint]');
        if (hint) {
            hint.textContent = message || (dirty
                ? 'Unsaved changes · Ctrl/⌘+S saves'
                : this._hintNotice || (this.status === 'approved'
                    ? 'Signed. Saving an edit clears the signature until you sign again.'
                    : 'Saving does not sign the script — sign it before it can run.'));
        }
        const mark = root.querySelector('[data-workbench-dirty]');
        if (mark) mark.hidden = !dirty;
        // Nothing to save until something changed (Ctrl/⌘+S stays a harmless no-op).
        const save = root.querySelector('[data-workbench-action="save"]');
        if (save) save.disabled = !dirty;
        this._renderRunAvailability();
    }

    _renderOutput() {
        const details = this.root?.querySelector('[data-workbench-output]');
        if (!details) return;
        const run = this.lastRun;
        if (!run) {
            details.hidden = true;
            return;
        }
        const summary = details.querySelector('[data-workbench-output-summary]');
        const body = details.querySelector('[data-workbench-output-body]');
        if (summary) {
            summary.textContent = `Last run: exit ${run.exitCode}${run.timedOut ? ' (timed out)' : ''} · ${Math.round(run.durationMs)} ms`;
        }
        if (body) body.textContent = formatPythonRunOutput(run);
        details.hidden = false;
        if (run.open) details.open = true;
    }

    _showEditorState({ error = '', retry = true } = {}) {
        const box = this.root?.querySelector('[data-workbench-editor-state]');
        if (!box) return;
        box.hidden = false;
        if (!error) {
            box.innerHTML = `<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Loading ${escapeHtml(this.name || '')}…`;
            return;
        }
        box.innerHTML = `
            <span>${escapeHtml(error)}</span>
            ${retry ? `<button class="btn btn-sm btn-outline-secondary" type="button" data-workbench-action="retry">
                <i class="fa-solid fa-rotate-right me-1" aria-hidden="true"></i>Retry
            </button>` : ''}`;
    }

    _hideEditorState() {
        const box = this.root?.querySelector('[data-workbench-editor-state]');
        if (box) box.hidden = true;
    }

    /**
     * Inline notice above the editor. `disk`: the file changed while the editor is dirty;
     * `stale`: a save was refused because the file changed first; `deleted`: the file went
     * away while the editor is dirty. Re-rendering an identical banner is skipped so a
     * focused button is not yanked away by the next poll.
     */
    _showBanner({ kind = 'disk', message = '' } = {}) {
        const banner = this.root?.querySelector('[data-workbench-banner]');
        if (!banner) return;
        const text = kind === 'stale'
            ? (message || `${this.name} changed after it was opened, so the save was refused.`)
            : kind === 'deleted'
                ? `${this.name} was deleted from the scripts folder. Your unsaved edits are still here.`
                : `${this.name} changed on disk.`;
        if (!banner.hidden && banner.dataset.kind === kind && banner.dataset.text === text) return;
        const button = (action, label, title) => `
                <button class="btn btn-sm btn-outline-secondary" type="button" data-workbench-action="${action}"
                        title="${escapeHtml(title)}">${escapeHtml(label)}</button>`;
        const actions = kind === 'deleted'
            ? button('recreate', 'Re-create from my edits', 'Write the editor contents back as a new, unsigned script')
              + button('leave', 'Back to Automation', 'Leave without saving')
            : button('reload', 'Reload', 'Replace the editor contents with the file on disk')
              + button('keep-edits', 'Keep my edits', 'Keep editing; the next save overwrites the disk copy');
        banner.dataset.kind = kind;
        banner.dataset.text = text;
        banner.innerHTML = `
            <i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i>
            <span>${escapeHtml(text)}</span>
            <div class="python-workbench-banner-actions">${actions}</div>`;
        banner.hidden = false;
    }

    _hideBanner() {
        const banner = this.root?.querySelector('[data-workbench-banner]');
        if (banner) banner.hidden = true;
    }

    // --- content ---

    async _loadContent(generation) {
        const token = Symbol('load');
        this._loadToken = token;
        const name = this.name;
        this._showEditorState();
        let response;
        try {
            response = await this.app.apiCall(
                `${API}/content?name=${encodeURIComponent(name)}`, 'GET', null,
                { showLoading: false, preferErrorResponseMessage: true });
        } catch (error) {
            if (generation !== this._generation || token !== this._loadToken) return false;
            this._showEditorState({ error: error?.message || `Could not open ${name}.` });
            return false;
        }
        if (generation !== this._generation || token !== this._loadToken) return false;

        const mounted = await this._ensureEditor(generation);
        if (generation !== this._generation || token !== this._loadToken || !mounted) return false;
        this._loadToken = null;
        // The model normalizes line endings; the baseline must be what the editor reports,
        // otherwise a CRLF file reads as dirty before the first keystroke.
        this._setEditorText(response.content ?? '');
        this.baseline = this.editor.getValue();
        this.version = response.version || '';
        this.status = response.status || this.status;
        this._diskChange = null;
        this._hintNotice = '';
        this._hideBanner();
        this._hideEditorState();
        this._renderIdentity();
        this._renderHint();
        this._renderRail();
        return true;
    }

    /** Seam for the unit tests; production always goes through the shared loader. */
    _loadMonaco() {
        return ensureMonaco();
    }

    async _ensureEditor(generation) {
        if (this.editor) return true;
        // Two overlapping loads (a rail click while the first open is still waiting for
        // Monaco) share one mount; a second create() would stack an editor in the host.
        if (this._editorMounting) return this._editorMounting;
        const mounting = (async () => {
            const monaco = await this._loadMonaco();
            if (generation !== this._generation) return false;
            if (this.editor) return true;
            const mount = this.root?.querySelector('[data-workbench-editor-mount]');
            if (!monaco || !mount) {
                this._showEditorState({ error: 'Could not load the code editor.' });
                return false;
            }
            this.monaco = monaco;
            const editor = monaco.editor.create(mount, {
                value: '',
                language: 'python',
                theme: 'viberails-dark',
                automaticLayout: true,
                minimap: { enabled: false },
                scrollBeyondLastLine: false,
                tabSize: 4,
                insertSpaces: true,
                renderWhitespace: 'selection',
                fontSize: 13,
                fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
                padding: { top: 8 }
            });
            this.editor = editor;
            editor.onDidChangeModelContent(() => {
                if (generation === this._generation) this._renderHint();
            });
            // Ctrl/⌘+S saves in place, like an editor rather than a dialog.
            editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => void this.save());
            requestAnimationFrame(() => {
                if (generation !== this._generation || this.editor !== editor) return;
                editor.layout();
                editor.focus();
            });
            return true;
        })().finally(() => {
            // unload() may have started a newer mount meanwhile: only clear our own.
            if (this._editorMounting === mounting) this._editorMounting = null;
        });
        this._editorMounting = mounting;
        return mounting;
    }

    /**
     * Replaces the document text. `preserveView` keeps cursor + scroll (and the undo
     * stack) for a live reload of the same file; a plain load resets both.
     */
    _setEditorText(text, { preserveView = false } = {}) {
        const editor = this.editor;
        if (!editor) return;
        const model = editor.getModel?.();
        if (!model || !preserveView) {
            editor.setValue(text);
            return;
        }
        const position = editor.getPosition?.();
        const scrollTop = editor.getScrollTop?.();
        const scrollLeft = editor.getScrollLeft?.();
        model.pushEditOperations([], [{ range: model.getFullModelRange(), text }], () => null);
        if (position) editor.setPosition?.(position);
        if (Number.isFinite(scrollTop)) editor.setScrollTop?.(scrollTop);
        if (Number.isFinite(scrollLeft)) editor.setScrollLeft?.(scrollLeft);
    }

    async retryLoad() {
        if (!this.state) {
            await this.loadView({ name: this.name });
            return;
        }
        await this._loadContent(this._generation);
    }

    /** Switches the editor to another script in place (rail click, new, duplicate). */
    async switchTo(name) {
        if (!name || name === this.name || !this.root) return false;
        if (this.isDirty) {
            const discard = await this.confirm({
                title: `Discard changes to ${this.name}?`,
                message: 'The edits in the editor have not been saved to the script file.',
                confirmLabel: 'Discard changes',
                danger: true
            });
            if (!discard || !this.root) return false;
        }
        const scripts = this.scripts;
        const state = scripts ? await scripts.ensureState() : null;
        if (!this.root) return false;
        this.state = state;
        const script = scripts?.scriptByName(name);
        if (!script) {
            this.app.showToast('Script not found', `${name} is no longer in the scripts folder.`, 'warning');
            this._renderRail();
            return false;
        }
        this.name = name;
        this.script = script;
        this.status = script.status || 'unapproved';
        this.lastRun = scripts.lastRunByName?.get?.(name) || null;
        this._diskChange = null;
        this._deletedOnDisk = false;
        this._hideBanner();
        // Keep Back and duplicate-tab pointing at what is on screen without a full reload
        // (a reload would remount the terminal and reconnect every tab).
        this.app.updateCurrentViewData?.({ name });
        this._renderIdentity();
        this._renderRail();
        this._renderOutput();
        return this._loadContent(this._generation);
    }

    // --- live reload ---

    _startPolling() {
        this._stopPolling();
        this._pollTimer = setInterval(() => void this.checkDisk(), RELOAD_POLL_MS);
    }

    _stopPolling() {
        if (this._pollTimer) clearInterval(this._pollTimer);
        this._pollTimer = null;
    }

    /** Cheap version probe; swaps the text or raises the banner via applyDiskCheck. */
    async checkDisk() {
        if (!this.root || !this.name || !this.editor || this.saving || this._pollInFlight || this._loadToken) return;
        if (this._mutating) return;
        if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return;
        const generation = this._generation;
        const name = this.name;
        const saveSerial = this._saveSerial;
        // Still the same file, and no save landed while the request was out — a poll
        // that started before a save would otherwise put the older content back.
        const stale = () => generation !== this._generation || name !== this.name || saveSerial !== this._saveSerial;
        this._pollInFlight = true;
        try {
            const response = await this.app.apiCall(
                `${API}/content?name=${encodeURIComponent(name)}`, 'GET', null,
                { showLoading: false, preferErrorResponseMessage: true });
            if (stale() || this.saving) return;
            this.applyDiskCheck(response);
        } catch (error) {
            if (stale()) return;
            // Transient failures (host briefly unreachable) are simply retried on the next tick.
            if (!/was not found/i.test(error?.message || '')) return;
            // A 404 can be a rename/delete that was mid-flight when the request went out;
            // the list endpoint has the final word before the file counts as gone.
            await this.scripts?.refresh?.({ quiet: true });
            if (stale() || this._mutating || this.scripts?.scriptByName?.(name)) return;
            this._onDeletedOnDisk(name);
        } finally {
            this._pollInFlight = false;
        }
    }

    /**
     * Runs one of the shared authoring flows with the disk poll held off, so the
     * moment between "old name gone" and "new name listed" is never read as a deletion.
     */
    async _whileMutating(work) {
        this._mutating = true;
        try {
            return await work();
        } finally {
            this._mutating = false;
        }
    }

    /** The file vanished underneath us: leave when there is nothing to lose, else offer a way out. */
    _onDeletedOnDisk(name) {
        if (!this.isDirty) {
            this.app.showToast('Script removed', `${name} was deleted from the scripts folder.`, 'warning');
            this._leaveDiscarding();
            return;
        }
        if (this._deletedOnDisk) return;
        this._deletedOnDisk = true;
        // The banner carries the message and the way out; a toast on top would repeat it.
        this._showBanner({ kind: 'deleted' });
    }

    /** Writes the editor text back as a fresh, unsigned script after a deletion on disk. */
    async recreateFromEditor() {
        const name = this.name;
        const scripts = this.scripts;
        const editor = this.editor;
        if (!name || !scripts || !editor) return false;
        const content = editor.getValue();
        let response;
        try {
            response = await this._whileMutating(async () => {
                await scripts.createScript(name, content);
                return this.app.apiCall(
                    `${API}/content?name=${encodeURIComponent(name)}`, 'GET', null,
                    { showLoading: false, preferErrorResponseMessage: true });
            });
        } catch (error) {
            this.app.showError(error?.message || `Could not re-create ${name}.`);
            return false;
        }
        if (name !== this.name || this.editor !== editor) return false;
        this._deletedOnDisk = false;
        // Only the captured bytes above were submitted. Edits made while the requests
        // were in flight must stay dirty instead of being mistaken for persisted text.
        this.baseline = content;
        this.version = response.version || '';
        this.status = response.status || 'unapproved';
        this._diskChange = null;
        this._hideBanner();
        this._renderIdentity();
        this._renderHint();
        this.app.showToast('Script re-created', `${name} is back, unsigned.`, 'success', { compact: true });
        return true;
    }

    _leaveDiscarding() {
        this._leaveConfirmed = true;
        this._leaveToAutomation();
    }

    /**
     * @param {{version: string, content: string, status?: string}} response the disk copy
     * @returns {'none'|'reloaded'|'banner'} what happened
     */
    applyDiskCheck(response) {
        if (!response || !response.version) return 'none';
        if (response.version === this.version) {
            // Same bytes, but the signature may have moved (signed/revoked from another
            // window): keep the pill honest without touching the editor.
            if (response.status && response.status !== this.status) {
                this.status = response.status;
                this._renderIdentity();
                this._renderHint();
            }
            return 'none';
        }
        this._deletedOnDisk = false;
        if (this.isDirty) {
            this._diskChange = response;
            this._showBanner({ kind: 'disk' });
            return 'banner';
        }
        // No toast: an agent editing the file would raise one every few seconds. The
        // hint line under the editor title says when the text was last picked up.
        this._adoptDiskCopy(response, { preserveView: true });
        return 'reloaded';
    }

    _adoptDiskCopy(response, { preserveView = true } = {}) {
        this._setEditorText(response.content ?? '', { preserveView });
        this.baseline = this.editor ? this.editor.getValue() : (response.content ?? '');
        this.version = response.version || this.version;
        this.status = response.status || this.status;
        this._diskChange = null;
        this._hideBanner();
        this._renderIdentity();
        this._renderHint(`Reloaded from disk · ${new Date().toLocaleTimeString()}`);
        // Size / edited / status on the rail and pill come from the list endpoint.
        void this.scripts?.refresh?.({ quiet: true });
    }

    async reloadFromDisk() {
        const name = this.name;
        if (!name) return false;
        if (this.isDirty) {
            const discard = await this.confirm({
                title: `Reload ${name} from disk?`,
                message: 'Your unsaved edits are replaced with the file on disk.',
                confirmLabel: 'Reload',
                danger: true
            });
            if (!discard || name !== this.name) return false;
        }
        let response;
        try {
            response = await this.app.apiCall(
                `${API}/content?name=${encodeURIComponent(name)}`, 'GET', null,
                { showLoading: false, preferErrorResponseMessage: true });
        } catch (error) {
            this.app.showError(error?.message || `Could not reload ${name}.`);
            return false;
        }
        if (name !== this.name || !this.editor) return false;
        this._adoptDiskCopy(response, { preserveView: true });
        return true;
    }

    /**
     * The user keeps the editor's text. Adopt the disk version token so the next save is
     * allowed to overwrite the disk copy — otherwise every save would bounce with 400.
     */
    async keepMyEdits() {
        const name = this.name;
        let version = this._diskChange?.version || null;
        let status = this._diskChange?.status || null;
        if (!version) {
            try {
                const response = await this.app.apiCall(
                    `${API}/content?name=${encodeURIComponent(name)}`, 'GET', null,
                    { showLoading: false, preferErrorResponseMessage: true });
                version = response.version;
                status = response.status || status;
            } catch (error) {
                this.app.showError(error?.message || `Could not read ${name}.`);
                return false;
            }
        }
        if (name !== this.name) return false;
        this.version = version || this.version;
        if (status) this.status = status;
        this._diskChange = null;
        this._hideBanner();
        this._renderIdentity();
        this._renderHint();
        this.app.showToast('Kept your edits', 'Saving now overwrites the disk copy.', 'info', { compact: true });
        return true;
    }

    // --- saving and signing ---

    async save({ sign = false } = {}) {
        const editor = this.editor;
        const scripts = this.scripts;
        if (!editor || !scripts || this.saving || !this.name) return false;
        const generation = this._generation;
        const name = this.name;
        const expectedVersion = this.version;
        const content = editor.getValue();
        this.saving = true;
        // Invalidates any poll response still in flight (see checkDisk).
        this._saveSerial += 1;
        this._renderHint('Saving…');
        let result;
        try {
            result = await scripts.saveContent(name, content, expectedVersion);
        } catch (error) {
            if (generation !== this._generation) return false;
            this.saving = false;
            this._renderHint();
            const message = error?.message || `Could not save ${name}.`;
            if (isStaleSaveError(message)) {
                this._showBanner({ kind: 'stale', message });
            } else {
                this.app.showError(message);
            }
            return false;
        }
        if (generation !== this._generation || name !== this.name || this.editor !== editor) return false;
        this.saving = false;
        this.baseline = content;
        this.version = result.version || this.version;
        this.status = result.status || this.status;
        this._diskChange = null;
        this._hideBanner();
        this._renderIdentity();
        this._renderHint('Saved.');
        this._renderRail();
        if (sign) await this.sign({ skipDirtyCheck: true });
        return true;
    }

    async sign({ skipDirtyCheck = false } = {}) {
        const name = this.name;
        const scripts = this.scripts;
        if (!name || !scripts) return false;
        if (!skipDirtyCheck && this.isDirty) {
            const proceed = await this.confirm({
                title: 'Sign the saved file?',
                message: 'Signing approves the file on disk, not the unsaved edits in the editor. Use "Save & sign" to include them.',
                confirmLabel: 'Sign the saved file',
                cancelLabel: 'Cancel'
            });
            if (!proceed || name !== this.name) return false;
        }
        return Boolean(await scripts.approve(name));
    }

    async revoke() {
        const name = this.name;
        if (!name || !this.scripts) return false;
        return Boolean(await this.scripts.revoke(name));
    }

    /** Opens the run window over the workbench; the editor and terminal stay where they are. */
    async run() {
        return this._withRunnableScript((name, scripts) => scripts.run(name));
    }

    /** Hands the script to a PTY tab instead — for runs that need typing or Ctrl+C. */
    async runInTerminal(button = null) {
        return this._withRunnableScript((name, scripts) => scripts.runInTerminal(name, button));
    }

    async _withRunnableScript(start) {
        const name = this.name;
        const scripts = this.scripts;
        if (!name || !scripts) return null;
        if (this.status !== 'approved') {
            this.app.showToast('Not signed', 'Sign the script before running it.', 'info');
            return null;
        }
        if (this.isDirty) {
            this.app.showToast('Unsaved changes', 'Save and sign the script before running it.', 'info');
            return null;
        }
        // Covers the gap between the click and the run actually being marked running; from
        // there on _onRunChanged drives the button, because scripts.run() resolves when the
        // window CLOSES, which can be long before (or long after) the POST completes.
        this._running = true;
        this._renderRunAvailability();
        let result = null;
        try {
            result = await start(name, scripts);
        } finally {
            this._running = false;
        }
        if (name !== this.name) return result;
        this.lastRun = scripts.lastRunByName?.get(name) || this.lastRun;
        this._renderIdentity();
        this._renderOutput();
        return result;
    }

    // --- authoring (shared flows) ---

    async rename() {
        const scripts = this.scripts;
        const previous = this.name;
        if (!previous || !scripts) return null;
        const newName = await this._whileMutating(() => scripts.rename(previous));
        if (!newName || newName === previous) return newName;
        // The agent tab follows the script identity. Otherwise the exact task-key
        // lookup in askAgent can no longer find the live session after a rename and
        // starts a duplicate agent for the same workbench.
        this._migrateAgentTaskKey(previous, newName);
        if (previous !== this.name || !this.root) return newName;
        // Same bytes under a new name: the editor (and any unsaved edits) stays; only the
        // identity, the status (renaming clears a signature) and the stack entry move.
        this.name = newName;
        this.state = scripts.state || this.state;
        this.script = scripts.scriptByName(newName) || this.script;
        this.status = this.script?.status || 'unapproved';
        this.lastRun = null;
        this._diskChange = null;
        this._hideBanner();
        this.app.updateCurrentViewData?.({ name: newName });
        this._renderIdentity();
        this._renderRail();
        this._renderHint();
        this._renderOutput();
        return newName;
    }

    async duplicate() {
        const scripts = this.scripts;
        const name = this.name;
        if (!name || !scripts) return null;
        const copy = await this._whileMutating(() => scripts.duplicate(name));
        if (!copy || !this.root) return copy;
        this.state = scripts.state || this.state;
        this._renderRail();
        await this.switchTo(copy);
        return copy;
    }

    async newScript() {
        const scripts = this.scripts;
        if (!scripts || !this.root) return null;
        const created = await this._whileMutating(() => scripts.newScript());
        if (!created || !this.root) return created;
        this.state = scripts.state || this.state;
        this._renderRail();
        await this.switchTo(created);
        return created;
    }

    async deleteScript() {
        const scripts = this.scripts;
        const name = this.name;
        if (!name || !scripts) return false;
        const gone = await this._whileMutating(() => scripts.deleteScript(name));
        if (!gone || name !== this.name) return gone;
        // Nothing left to protect: the file is gone, so the unsaved-changes guard stands down.
        this._leaveDiscarding();
        return true;
    }

    // --- agent terminal ---

    async _mountTerminal() {
        const host = this.root?.querySelector('[data-terminal-content]');
        const controller = this.app.terminalController;
        if (!host || !controller?.renderTerminalPanel) return;
        const workingDirectory = this.scriptsDirectory || null;
        host.innerHTML = controller.renderTerminalPanel({ workingDirectory });
        // Existing tabs reconnect when the panel mounts, exactly like the Code quality page;
        // new sessions (Start button or Ask agent) open in the scripts directory.
        await controller.bindTerminalActions(host, null, { defaultWorkingDirectory: workingDirectory });
    }

    /**
     * Pastes a change brief into the live agent session (without submitting), or starts
     * one in the scripts folder that reads the script and waits for the request.
     * @returns {Promise<{mode: 'inject'|'start'|'none', cli?: string}>}
     */
    async askAgent() {
        const name = this.name;
        const path = this.script?.path;
        if (!name || !path) {
            this.app.showToast('Script path unknown', 'Reload the page and try again.', 'warning');
            return { mode: 'none' };
        }
        const controller = this.app.terminalController;
        const host = this.root?.querySelector('[data-terminal-content]');
        const pasteBrief = `${buildAskAgentBrief({ name, path })}\n\nChange: `;

        // The brief goes to THIS script's own agent session, never to whatever tab
        // happened to be active last (that used to paste the brief into completely
        // unrelated sessions). The script's task tab wins; failing that, an agent the
        // user started from this panel — no task key, working in the scripts folder —
        // is the one the button's tooltip promises. Anything else falls through to
        // reusing/starting the dedicated task tab below. A plain shell never receives
        // the brief (it would just echo it; the wire name is "Shell", hence the
        // case-insensitive compare).
        const taskKey = `python-script:${name}`;
        const manager = controller?.manager;
        const tabCli = (tab) => String(tab?.state?.cli
            || manager?.getSelectionMeta?.(tab?.state?.selection)?.cli
            || '').toLowerCase();
        let taskTab = manager?.findTabByTaskKey?.(taskKey) || null;
        // A pre-rename build tagged RUN shell tabs with the agent key; pasting the
        // brief into a running python process is never right. Migrate the stale key
        // so the fresh-start path below cannot re-adopt that tab by key either.
        if (taskTab && tabCli(taskTab) === 'shell') {
            manager?.updateTabMetadata?.(taskTab, { taskKey: `python-script-run:${name}` });
            taskTab = null;
        }
        const active = manager?.getActiveTab?.();
        const activeCli = active ? tabCli(active) : '';
        const activeIsAgent = active?.state?.hasActiveSession === true && activeCli && activeCli !== 'shell';
        const activeBelongsHere = activeIsAgent && (active.state?.taskKey === taskKey
            || (!active.state?.taskKey && this._isScriptsDirectory(active.state?.workingDirectory)));
        const target = taskTab?.state?.hasActiveSession ? taskTab : (activeBelongsHere ? active : null);
        if (target) {
            if (manager?.activeTabId !== target.state.id) {
                try { await manager.activateTab(target.state.id, { connectIfNeeded: true }); } catch { /* fall through to a fresh start */ }
            } else if (!target.instance?.hasOpenSocket?.()) {
                try { await target.instance?.connect?.(); } catch { /* fall through to a fresh start */ }
            }
            // Still no socket after the connect attempt: the session died under a flag
            // that never clears itself (an /exit'd agent, a vb restart) and the server
            // refused the WS. Mark the tab sessionless so the fresh start below reuses
            // it by task key and launches a NEW session in it — the alternative is a
            // silent dead-end (reuse dead tab, inject fails, nothing happens, forever).
            if (!target.instance?.hasOpenSocket?.()) {
                target.state.hasActiveSession = false;
                target.state.sessionId = null;
                target.state.status = 'not-started';
                manager?.updateUi?.();
            } else if (this._injectBrief(target, pasteBrief)) {
                return { mode: 'inject' };
            }
        }

        if (!host || typeof controller?.startTerminalWithOptions !== 'function') {
            this.app.showToast('Terminal unavailable', 'Start an agent in the terminal below, then try again.', 'warning');
            return { mode: 'none' };
        }

        // The panel's picker decides which agent; nothing (or the plain shell) means Claude.
        const selection = manager?.getLaunchSelection?.() || null;
        const meta = selection && typeof manager?.getSelectionMeta === 'function'
            ? manager.getSelectionMeta(selection)
            : null;
        const isAgent = Boolean(meta?.cli) && meta.cli !== 'shell';
        const cli = isAgent ? meta.cli : 'claude';
        // The toast names the agent the way the picker does ("Antigravity"), not by its
        // wire id ("agy"); the returned { cli } stays the id.
        const agentLabel = isAgent ? (meta.displayName || cli) : 'Claude';
        const environmentName = isAgent ? (meta.environmentName || null) : null;
        const result = await controller.startTerminalWithOptions({
            cli,
            environmentName,
            workingDirectory: this.scriptsDirectory || null,
            tabLabel: name,
            taskKey: `python-script:${name}`,
            initialPrompt: buildAskAgentInitialPrompt({ name, path })
        }, host);

        if (result?.reusedExisting && !result.started) {
            // The task tab already had a session: it was activated (and reconnected), so
            // the brief can go straight in.
            const tab = controller.manager?.getActiveTab?.();
            if (this._injectBrief(tab, pasteBrief)) return { mode: 'inject', cli };
        }
        if (result?.started) {
            this.app.showToast('Agent started',
                `${agentLabel} opened in the scripts folder — it reads ${name}, then waits for your request.`,
                'info', { compact: true });
        }
        return { mode: 'start', cli, environmentName };
    }

    _injectBrief(tab, text) {
        // Defense in depth: only a known agent tab may take the brief. A shell would
        // echo it — or feed a running script's stdin — no matter which caller slipped
        // it through, so the promise "a shell never receives the brief" lives here.
        const cli = String(tab?.state?.cli
            || this.app.terminalController?.manager?.getSelectionMeta?.(tab?.state?.selection)?.cli
            || '').toLowerCase();
        if (!cli || cli === 'shell') return false;
        if (!tab?.instance?.injectText?.(text)) return false;
        tab.instance.focusInput?.();
        tab.instance.focus?.();
        this.app.showToast('Brief pasted', 'Describe the change and press Enter.', 'info', { compact: true });
        return true;
    }

    /** True when `path` is the scripts folder (case-insensitive, separator-agnostic). */
    _isScriptsDirectory(path) {
        const normalize = (value) => String(value || '').replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
        const scriptsDir = normalize(this.scriptsDirectory);
        return Boolean(scriptsDir) && normalize(path) === scriptsDir;
    }

    /** Moves the dedicated agent tab from a script's old name to its new name. */
    _migrateAgentTaskKey(previousName, nextName) {
        const manager = this.app.terminalController?.manager;
        const tab = manager?.findTabByTaskKey?.(`python-script:${previousName}`);
        if (!tab) return;
        manager.updateTabMetadata?.(tab, { taskKey: `python-script:${nextName}` });
    }

    // --- splitter ---

    _bindSplitter() {
        const splitter = this.root?.querySelector('[data-workbench-splitter]');
        if (!splitter) return;
        splitter.addEventListener('pointerdown', (event) => {
            if (event.pointerType === 'mouse' && event.button !== 0) return;
            event.preventDefault();
            const pointerId = event.pointerId;
            // The axis is decided when the drag starts: side by side the handle sits
            // between the columns and moves the terminal's WIDTH; stacked it moves the height.
            const sideBySide = isSideBySideLayout();
            const startX = event.clientX;
            const startY = event.clientY;
            const startWidth = this._currentTerminalWidth();
            const startHeight = this._currentTerminalHeight();
            try { splitter.setPointerCapture(pointerId); } catch { /* not supported */ }
            this.root?.classList.add('python-workbench-resizing');
            const onMove = (move) => {
                if (move.pointerId !== pointerId) return;
                if (sideBySide) {
                    // The terminal is the right column: dragging right shrinks it.
                    this.setTerminalWidth(startWidth - (move.clientX - startX), { persist: false });
                } else {
                    // The terminal sits below the handle: dragging down shrinks it.
                    this.setTerminalHeight(startHeight - (move.clientY - startY), { persist: false });
                }
                this._refreshTerminalLayoutThrottled();
            };
            const finish = () => {
                splitter.removeEventListener('pointermove', onMove);
                splitter.removeEventListener('pointerup', finish);
                splitter.removeEventListener('pointercancel', finish);
                try { splitter.releasePointerCapture(pointerId); } catch { /* already released */ }
                this.root?.classList.remove('python-workbench-resizing');
                if (sideBySide) this._persistTerminalWidth();
                else this._persistTerminalHeight();
                this.app.terminalController?.refreshLayout?.();
            };
            splitter.addEventListener('pointermove', onMove);
            splitter.addEventListener('pointerup', finish);
            splitter.addEventListener('pointercancel', finish);
        });
        splitter.addEventListener('keydown', (event) => {
            if (isSideBySideLayout()) {
                // Left grows the terminal column (the handle moves left), Right shrinks it.
                const delta = event.key === 'ArrowLeft' ? SPLITTER_KEY_STEP : event.key === 'ArrowRight' ? -SPLITTER_KEY_STEP : 0;
                if (!delta) return;
                event.preventDefault();
                this.setTerminalWidth(this._currentTerminalWidth() + delta);
            } else {
                const delta = event.key === 'ArrowUp' ? SPLITTER_KEY_STEP : event.key === 'ArrowDown' ? -SPLITTER_KEY_STEP : 0;
                if (!delta) return;
                event.preventDefault();
                this.setTerminalHeight(this._currentTerminalHeight() + delta);
            }
            this.app.terminalController?.refreshLayout?.();
        });
    }

    _watchLayoutMedia() {
        this._unwatchLayoutMedia();
        try {
            const media = globalThis.matchMedia?.(`(min-width: ${SIDE_BY_SIDE_MIN_VIEWPORT_WIDTH}px)`);
            if (!media?.addEventListener) return;
            media.addEventListener('change', this._onLayoutMediaChange);
            this._layoutMedia = media;
        } catch { /* matchMedia unavailable (unit tests, exotic hosts): the CSS still lays out */ }
    }

    _unwatchLayoutMedia() {
        try { this._layoutMedia?.removeEventListener?.('change', this._onLayoutMediaChange); } catch { /* gone */ }
        this._layoutMedia = null;
    }

    _currentTerminalWidth() {
        const pane = this.root?.querySelector('[data-terminal-section]');
        const measured = pane?.getBoundingClientRect?.().width;
        if (Number.isFinite(measured) && measured > 0) return Math.round(measured);
        return this._terminalWidth || TERMINAL_MIN_WIDTH;
    }

    /** Leave the editor column its minimum; when nothing is measurable there is no ceiling. */
    _maxTerminalWidth() {
        const panes = this.root?.querySelector('[data-workbench-panes]');
        const total = panes?.getBoundingClientRect?.().width;
        if (!Number.isFinite(total) || total <= 0) return Infinity;
        const splitter = this.root?.querySelector('[data-workbench-splitter]');
        const splitterWidth = splitter?.getBoundingClientRect?.().width || 0;
        return Math.floor(total - EDITOR_MIN_WIDTH - splitterWidth);
    }

    /** Applies (and by default persists) the terminal column width; returns the clamped value. */
    setTerminalWidth(width, { persist = true } = {}) {
        const clamped = clampTerminalWidth(width, this._maxTerminalWidth());
        this._terminalWidth = clamped;
        this.root?.style?.setProperty?.('--python-workbench-terminal-width', `${clamped}px`);
        this._syncSplitterAria();
        if (persist) this._persistTerminalWidth();
        return clamped;
    }

    _persistTerminalWidth() {
        if (!Number.isFinite(this._terminalWidth)) return;
        try {
            globalThis.localStorage?.setItem(TERMINAL_WIDTH_STORAGE_KEY, String(this._terminalWidth));
        } catch { /* storage may be unavailable (private mode / webview policy) */ }
    }

    _applyStoredTerminalWidth() {
        const stored = readStoredTerminalWidth(globalThis.localStorage);
        if (stored) this.setTerminalWidth(stored, { persist: false });
    }

    _currentTerminalHeight() {
        const pane = this.root?.querySelector('[data-terminal-section]');
        const measured = pane?.getBoundingClientRect?.().height;
        if (Number.isFinite(measured) && measured > 0) return Math.round(measured);
        return this._terminalHeight || terminalMinHeight();
    }

    /** Leave the editor its minimum; when nothing is measurable there is no ceiling. */
    _maxTerminalHeight() {
        const panes = this.root?.querySelector('[data-workbench-panes]');
        const total = panes?.getBoundingClientRect?.().height;
        if (!Number.isFinite(total) || total <= 0) return Infinity;
        const splitter = this.root?.querySelector('[data-workbench-splitter]');
        const splitterHeight = splitter?.getBoundingClientRect?.().height || 0;
        return Math.floor(total - EDITOR_MIN_HEIGHT - splitterHeight);
    }

    /** Applies (and by default persists) a terminal pane height; returns the clamped value. */
    setTerminalHeight(height, { persist = true } = {}) {
        const clamped = clampTerminalHeight(height, this._maxTerminalHeight());
        this._terminalHeight = clamped;
        this.root?.style?.setProperty?.('--python-workbench-terminal-height', `${clamped}px`);
        this._syncSplitterAria(isSideBySideLayout() ? undefined : clamped);
        if (persist) this._persistTerminalHeight();
        return clamped;
    }

    /**
     * Keeps the separator's aria-valuenow/min/max honest. Without a stored height the
     * pane is sized by CSS alone, so this is also called once after mount (measured),
     * leaving the CSS default fluid rather than freezing it into an inline pixel value.
     */
    _syncSplitterAria(size) {
        const splitter = this.root?.querySelector?.('[data-workbench-splitter]');
        if (!splitter) return;
        const sideBySide = isSideBySideLayout();
        splitter.setAttribute('aria-orientation', sideBySide ? 'vertical' : 'horizontal');
        const floor = sideBySide ? TERMINAL_MIN_WIDTH : terminalMinHeight();
        const now = Number.isFinite(size)
            ? size
            : (sideBySide ? this._currentTerminalWidth() : this._currentTerminalHeight());
        splitter.setAttribute('aria-valuemin', String(floor));
        splitter.setAttribute('aria-valuenow', String(Math.round(now)));
        const max = sideBySide ? this._maxTerminalWidth() : this._maxTerminalHeight();
        if (Number.isFinite(max)) splitter.setAttribute('aria-valuemax', String(Math.max(floor, max)));
    }

    _persistTerminalHeight() {
        if (!Number.isFinite(this._terminalHeight)) return;
        try {
            globalThis.localStorage?.setItem(TERMINAL_HEIGHT_STORAGE_KEY, String(this._terminalHeight));
        } catch { /* storage may be unavailable (private mode / webview policy) */ }
    }

    _applyStoredTerminalHeight() {
        const stored = readStoredTerminalHeight(globalThis.localStorage);
        if (stored) this.setTerminalHeight(stored, { persist: false });
    }

    _refreshTerminalLayoutThrottled() {
        const now = Date.now();
        if (now - this._lastLayoutRefreshAt >= LAYOUT_REFRESH_THROTTLE_MS) {
            this._lastLayoutRefreshAt = now;
            this.app.terminalController?.refreshLayout?.();
            return;
        }
        if (this._layoutRefreshTimer) return;
        this._layoutRefreshTimer = setTimeout(() => {
            this._layoutRefreshTimer = null;
            this._lastLayoutRefreshAt = Date.now();
            this.app.terminalController?.refreshLayout?.();
        }, LAYOUT_REFRESH_THROTTLE_MS);
    }

    // --- navigation ---

    /** State updates from the shared controller (sign, revoke, run refresh, rename…). */
    _onSharedStateChange(state) {
        if (!this.root || !state) return;
        this.state = state;
        const script = this.scripts?.scriptByName(this.name);
        // A missing entry mid-rename is transient; the poll decides when the file is gone.
        if (script) {
            this.script = script;
            this.status = script.status || 'unapproved';
        }
        this._renderIdentity();
        this._renderRail();
        this._renderHint();
    }

    // Guards are synchronous, but the in-app confirm is not (window.confirm is a silent
    // no-op in the VS Code webview): block now, ask, and replay via `retry` on a yes.
    _guardNavigation({ from, retry }) {
        if (from !== VIEW_NAME || !this.isDirty || this._leaveConfirmed) return true;
        void this._confirmLeave(retry);
        return false;
    }

    async _confirmLeave(retry) {
        if (this._leaveConfirmPending) return;
        this._leaveConfirmPending = true;
        try {
            const leave = await this.confirm({
                title: `Leave ${this.name}?`,
                message: 'Your unsaved edits will be lost.',
                confirmLabel: 'Leave without saving',
                cancelLabel: 'Stay',
                danger: true
            });
            if (!leave) return;
            this._leaveConfirmed = true;
            try { retry?.(); } finally { this._leaveConfirmed = false; }
        } finally {
            this._leaveConfirmPending = false;
        }
    }

    _leaveToAutomation() {
        const stack = this.app.navigationStack || [];
        const previous = stack.length > 1 ? stack[stack.length - 2] : null;
        const previousView = previous ? (previous.view || previous) : null;
        if (previousView === 'jobs' && this.app.goBack?.()) return;
        this.app.navigate?.('jobs', {}, { resetStack: true });
    }
}
