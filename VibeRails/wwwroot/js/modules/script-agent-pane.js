import { escapeHtml, parseLlmSelection } from './utils.js';
import { mountLlmPicker, setLlmPickerValue } from './pickers/llm-picker.js';
import { TerminalTab } from './terminal-tab.js';
import { TabStatusController } from './terminal-tab-status.js';
import { findEnvironmentForPrompt, resolvePromptTemplateForLaunch } from './prompt-template-modal.js';

const TABS_API = '/api/v1/terminal/tabs';
const OWNER_PREFIX = 'script-agent:';
export const SCRIPT_AGENT_SELECTION_KEY = 'viberails.scriptAgent.selection';
const DEFAULT_SELECTION = 'base:claude';

/** The owner label of the backend tab that holds one script's agent (VIBE-159). */
export function scriptAgentOwner(scriptId) {
    return `${OWNER_PREFIX}${scriptId}`;
}

/**
 * True for a backend tab that belongs to a view instead of the Terminals page. The tab list
 * reports `owner`; the Terminals page leaves these out of its strip and its adoption paths.
 */
export function isOwnedTab(tab) {
    return typeof tab?.owner === 'string' && tab.owner.trim() !== '';
}

function readStoredSelection() {
    try {
        return globalThis.localStorage?.getItem(SCRIPT_AGENT_SELECTION_KEY) || DEFAULT_SELECTION;
    } catch {
        return DEFAULT_SELECTION;
    }
}

function storeSelection(value) {
    try { globalThis.localStorage?.setItem(SCRIPT_AGENT_SELECTION_KEY, value); } catch { /* best effort */ }
}

/**
 * The script workbench's agent terminal (VIBE-159): one xterm for one script's agent, built
 * like the script run window rather than mounting the Terminals page's tab strip and picker.
 *
 * The agent is still an ordinary recorded session (TokenSaver proxy, MCP, history): it runs
 * in a backend terminal tab marked with an owner, so the Terminals page never lists it and
 * reopening the workbench reattaches to it. One TerminalTab draws it, with this pane standing
 * in as its manager, so every CLI's input handling (Codex newlines, Grok paste, the OpenCode
 * wheel, Escape) and the snapshot reconnect stay exactly what the Terminals page uses.
 */
export class ScriptAgentPane {
    /**
     * @param {object} app
     * @param {object} options
     *   owner:            scriptAgentOwner(id) of the script this pane serves
     *   label:            the script's display name, shown in the title bar
     *   getLaunchContext: () => ({ workingDirectory, initialPrompt }) | null, read at each Start
     */
    constructor(app, { owner, label = '', getLaunchContext = null } = {}) {
        this.app = app;
        this.owner = owner;
        this.label = label;
        this.getLaunchContext = getLaunchContext;
        this.root = null;
        this.tab = null;
        this._destroyed = false;
        this._busy = false;
        // The first look for this script's running agent; Start waits for it (never a second agent).
        this._discovery = null;
        this._discovering = false;
        this._statusCheck = null;
        this._disconnectChecked = false;
        this._disposePicker = null;
        this._onClick = (event) => this._handleClick(event);
    }

    // --- the TerminalTab manager contract ---

    updateUi() {
        if (!this.root) return;
        const state = this.tab?.state || null;
        const live = state?.hasActiveSession === true;
        const connected = live && this.tab.instance.hasOpenSocket();
        const terminal = this.root.querySelector('[data-script-agent-terminal]');
        const placeholder = this.root.querySelector('[data-script-agent-placeholder]');
        const showTerminal = Boolean(state) && (live || state.status === 'finished');
        if (terminal) terminal.hidden = !showTerminal;
        if (placeholder) placeholder.hidden = showTerminal;

        const toggle = (action, visible, disabled = this._busy) => {
            for (const button of this.root.querySelectorAll(`[data-script-agent-action="${action}"]`)) {
                button.hidden = !visible;
                button.disabled = disabled;
            }
        };
        toggle('start', !live, this._busy || this._discovering);
        toggle('restart', live);
        toggle('stop', live);
        toggle('connect', live && !connected && state.status !== 'connecting');

        const statusChip = this.root.querySelector('[data-script-agent-status]');
        if (statusChip) statusChip.hidden = !live;
        const lifecycle = this.root.querySelector('[data-script-agent-lifecycle]');
        if (lifecycle) lifecycle.textContent = this._lifecycleText(state);
        const select = this.root.querySelector('[data-script-agent-select]');
        if (select) select.disabled = live || this._busy || this._discovering;

        // A socket closing on its own may mean the CLI exited. Ask the server once per drop
        // rather than retry; a session that is still running offers Connect instead.
        if (connected) this._disconnectChecked = false;
        // Stop and Start close the socket themselves and settle the state when they return.
        if (live && !connected && state.status === 'disconnected' && !this._disconnectChecked && !this._busy
            && !this.tab.instance.autoReconnect?.isPending?.()) {
            this._disconnectChecked = true;
            void this._checkStatus();
        }
    }

    getWebSocketUrl(tabId, cols, rows) {
        const encodedId = encodeURIComponent(tabId);
        const baseUrl = globalThis.window?.__viberails_API_BASE__ || '';
        const url = baseUrl
            ? `${baseUrl.replace(/^http/, 'ws')}${TABS_API}/${encodedId}/ws`
            : `${globalThis.location?.protocol === 'https:' ? 'wss:' : 'ws:'}//${globalThis.location?.host}${TABS_API}/${encodedId}/ws`;
        return cols > 0 && rows > 0 ? `${url}?cols=${cols}&rows=${rows}` : url;
    }

    async autoReconnectTab(instance) {
        if (this._destroyed || !instance || this.tab?.instance !== instance) return false;
        if (!instance.state.hasActiveSession || instance.hasOpenSocket()) return false;
        return instance.connect();
    }

    // The Terminals page's per-tab settings, progress bar and editor nudge have no home here.
    applySavedTerminalSettingsForTab() {}
    updateTabProgress() {}
    maybeShowEditorNudge() {}
    updateFocusContainerHeight() {}

    // --- lifecycle ---

    async mount(host) {
        if (!host || this._destroyed) return;
        host.innerHTML = this._render();
        this.root = host.querySelector?.('[data-script-agent]') || null;
        if (!this.root) return;
        this.root.addEventListener('click', this._onClick);
        const select = this.root.querySelector('[data-script-agent-select]');
        if (select && this.app.llmPickerController) {
            this._disposePicker = mountLlmPicker(this.app, select, {
                context: 'terminal',
                placeholder: 'Choose a CLI or Quick Launch…',
                includeDefaultSuffix: true,
                selectedValue: readStoredSelection()
            });
        }
        this._discovering = true;
        this.updateUi();
        this._discovery = this._discover();
        await this._discovery;
        this._discovering = false;
        this.updateUi();
    }

    /**
     * Lets go of the xterm and socket only. The host is never closed from here (VIBE-159 review F3):
     * what this pane last saw can be stale, a Start may still be landing, and the server gives every
     * window the same host per owner, so a delete could stop a live agent. The next visit, or the
     * next Start in it, reuses the host; it ends with the root.
     */
    dispose() {
        if (this._destroyed) return;
        this._destroyed = true;
        this._unregisterTab();
        const tab = this.tab;
        this.tab = null;
        tab?.instance.dispose();
        this._disposePicker?.();
        this._disposePicker = null;
        this.root?.removeEventListener('click', this._onClick);
        this.root = null;
    }

    setLabel(label) {
        this.label = label || '';
        const chip = this.root?.querySelector('[data-script-agent-label]');
        if (chip) chip.textContent = this.label;
    }

    refreshLayout() {
        this.tab?.instance.scheduleFitPasses();
    }

    focus() {
        this.tab?.instance.focusInput();
    }

    async start() {
        if (this._busy || this._destroyed || this.tab?.state.hasActiveSession) return false;
        const selection = this._selection();
        const meta = parseLlmSelection(selection, this.app.data?.environments || []);
        if (!meta.cli) {
            this.app.showError('Choose a CLI to start.');
            return false;
        }
        this._setBusy(true);
        try {
            // Never start a second agent beside one this pane has not found yet: wait for the first
            // look, and look again when it failed.
            await this._discovery;
            if (this._destroyed) return false;
            if (!this.tab && !(await this._discover())) {
                this.app.showError('Could not check whether this script\'s agent is already running. Try Start again.');
                return false;
            }
            if (this._destroyed || this.tab?.state.hasActiveSession) return false;
            const promptResolution = await resolvePromptTemplateForLaunch(this.app, {
                cli: meta.cli,
                environmentName: meta.environmentName,
                title: meta.displayName
            });
            if (promptResolution.canceled || this._destroyed) return false;
            // Read after any prompt dialog so a rename in the meantime cannot launch on an old path.
            const context = this.getLaunchContext?.() || {};
            let initialPrompt = promptResolution.initialPrompt;
            if (meta.cli !== 'shell' && context.initialPrompt) {
                const environment = findEnvironmentForPrompt(
                    this.app.data?.environments || [], meta.cli, meta.environmentName);
                initialPrompt = [initialPrompt ?? environment?.customPrompt, context.initialPrompt]
                    .filter(text => typeof text === 'string' && text.trim())
                    .join('\n\n');
            }

            const tab = await this._ensureTab(selection, meta);
            if (!tab || this._destroyed) return false;
            // The server hands an owner its existing host; an agent running there is attached, not replaced.
            if (tab.state.hasActiveSession) {
                await tab.instance.connect();
                if (this._destroyed) tab.instance.dispose();
                return false;
            }
            const title = meta.cli === 'shell' ? meta.displayName : `${meta.displayName} · ${this.label || 'script'}`;
            const body = { cli: meta.cli, workingDirectory: context.workingDirectory || null, title };
            if (meta.environmentName) body.environmentName = meta.environmentName;
            if (initialPrompt != null) body.initialPrompt = initialPrompt;
            tab.state.selection = selection;
            tab.state.label = meta.displayName;
            tab.instance.statusController?.render();
            storeSelection(selection);
            const started = await tab.instance.startSession(body);
            // Left while the launch was landing: the agent keeps running; drop the socket it opened.
            if (this._destroyed) tab.instance.dispose();
            return Boolean(started);
        } finally {
            this._setBusy(false);
        }
    }

    async stop() {
        const tab = this.tab;
        if (this._busy || !tab?.state.hasActiveSession) return false;
        this._setBusy(true);
        try {
            return await tab.instance.stopSession();
        } finally {
            this._setBusy(false);
        }
    }

    async restart() {
        if (this.tab?.state.hasActiveSession && !(await this.stop())) return false;
        return this.start();
    }

    async reconnect() {
        const tab = this.tab;
        if (!tab?.state.hasActiveSession || tab.instance.hasOpenSocket()) return false;
        return tab.instance.connect();
    }

    // --- internals ---

    /** Adopts this script's host from the tab list. False only when the list could not be read. */
    async _discover() {
        let response;
        try {
            response = await this.app.apiCall(TABS_API, 'GET', null, { showLoading: false });
        } catch {
            return false;
        }
        if (this._destroyed || this.tab) return true;
        const mine = (response?.tabs || []).filter(tab => tab?.owner === this.owner);
        const info = mine.find(tab => tab.hasActiveSession) || mine.find(tab => tab.statusAvailable === false) || mine[0];
        if (!info) return true;
        const tab = this._adopt(info, { selection: readStoredSelection() });
        if (tab.state.hasActiveSession) {
            await tab.instance.connect();
            if (this._destroyed) tab.instance.dispose();
        } else {
            this.updateUi();
        }
        return true;
    }

    async _ensureTab(selection, meta) {
        if (this.tab) {
            // The root may have restarted or reclaimed the host since this pane adopted it, and
            // another window sharing the host may have started an agent in it (review F4).
            const tab = this.tab;
            try {
                const status = await this.app.apiCall(`${TABS_API}/${encodeURIComponent(tab.state.id)}/status`,
                    'GET', null, { showLoading: false });
                if (status?.hasActiveSession === true && this.tab === tab) {
                    tab.state.hasActiveSession = true;
                    tab.state.sessionId = status.sessionId || tab.state.sessionId;
                    tab.state.cli = status.cli || tab.state.cli;
                    tab.state.status = 'disconnected';
                }
                return this.tab;
            } catch {
                this._dropTab();
            }
        }
        let info;
        try {
            info = await this.app.apiCall(`${TABS_API}?owner=${encodeURIComponent(this.owner)}`, 'POST',
                null, { showLoading: false });
        } catch (error) {
            this.app.showError(`Could not open the script agent: ${error?.message || error}`);
            return null;
        }
        // Left meanwhile: the host stays for the next visit (it may be one another window runs).
        if (this._destroyed) return null;
        return this._adopt(info, { selection, label: meta.displayName });
    }

    _adopt(info, { selection, label = null }) {
        const terminalElement = this.root.querySelector('[data-script-agent-terminal]');
        const statusElement = this.root.querySelector('[data-script-agent-status]');
        const meta = parseLlmSelection(selection, this.app.data?.environments || []);
        // A host whose status could not be read may be running an agent: treat it as live, so
        // Start never launches over it (Connect and Stop stay offered).
        const live = info.hasActiveSession === true || info.statusAvailable === false;
        const state = {
            id: info.tabId,
            selection,
            label: label || meta.displayName || 'Agent',
            title: null,
            taskKey: null,
            workingDirectory: info.workingDirectory || null,
            hasActiveSession: live,
            sessionId: info.sessionId || null,
            boardCard: null,
            needsAttention: false,
            notifyEnabled: false,
            pinned: false,
            cli: info.cli || meta.cli || null,
            status: live ? 'disconnected' : 'not-started',
            ui: { item: statusElement, button: statusElement, terminalElement }
        };
        const instance = this._createTab(state);
        instance.setActive(true);
        this.tab = { state, instance };
        this.app.terminalController?.registerOwnedTab?.(state.id, this.tab);
        this.updateUi();
        return this.tab;
    }

    /** The xterm and its socket, exactly as a Terminals page tab draws them. A seam for tests. */
    _createTab(state) {
        const instance = new TerminalTab(this, state);
        // Thinking / Ready / Waiting for user input, in the pane's status strip.
        instance.statusController = new TabStatusController(state, state.ui, {
            getCliKey: () => state.cli || parseLlmSelection(state.selection, this.app.data?.environments || []).cli,
            isActiveTab: () => true,
            setProgress: () => {},
            notifyReady: () => {},
            isPushEnabled: () => false,
            notifyPush: () => {}
        });
        instance.statusController.render();
        return instance;
    }

    _dropTab() {
        const tab = this.tab;
        if (!tab) return;
        this._unregisterTab();
        this.tab = null;
        tab.instance.dispose();
        this.updateUi();
    }

    _unregisterTab() {
        if (this.tab) this.app.terminalController?.unregisterOwnedTab?.(this.tab.state.id, this.tab);
    }

    async _checkStatus() {
        const tab = this.tab;
        if (!tab || this._statusCheck) return;
        this._statusCheck = (async () => {
            let status = null;
            try {
                status = await this.app.apiCall(`${TABS_API}/${encodeURIComponent(tab.state.id)}/status`,
                    'GET', null, { showLoading: false, preferErrorResponseMessage: true });
            } catch (error) {
                // Only a host the root no longer has is forgotten; an unanswered one may still run.
                if (this.tab === tab && /not found/i.test(error?.message || '')) this._dropTab();
                else if (this.tab === tab) this.updateUi();
                return;
            }
            // A Stop or a new Start that landed meanwhile already decided the state.
            if (this.tab !== tab || this._destroyed || this._busy || !tab.state.hasActiveSession) return;
            if (status?.hasActiveSession === true) {
                // Still running: a takeover or a dropped socket. Connect is offered.
                this.updateUi();
                return;
            }
            tab.state.hasActiveSession = false;
            tab.instance.autoReconnect?.cancel?.();
            // The last screen stays in the xterm; input is off until the next Start.
            tab.state.status = 'finished';
            if (tab.instance.terminal) tab.instance.terminal.options.disableStdin = true;
            this.updateUi();
        })().finally(() => { this._statusCheck = null; });
        await this._statusCheck;
    }

    _selection() {
        const select = this.root?.querySelector('[data-script-agent-select]');
        return String(select?.tomselect?.getValue?.() || select?.value || readStoredSelection() || '');
    }

    _setBusy(busy) {
        this._busy = busy;
        this.updateUi();
    }

    _lifecycleText(state) {
        if (this._discovering && !state) return 'Looking for this script\'s agent…';
        if (this._busy) return 'Starting…';
        if (!state) return 'Not started';
        if (state.status === 'finished') return 'Finished. Start runs a new session.';
        if (!state.hasActiveSession) return 'Not started';
        if (state.status === 'connecting') return 'Connecting…';
        if (state.status === 'disconnected') return 'Disconnected. Connect to see it again.';
        return '';
    }

    _handleClick(event) {
        const action = event.target.closest?.('[data-script-agent-action]')?.dataset.scriptAgentAction;
        if (action === 'start') void this.start();
        if (action === 'stop') void this.stop();
        if (action === 'restart') void this.restart();
        if (action === 'connect') void this.reconnect();
    }

    _render() {
        const label = escapeHtml(this.label);
        return `
            <section class="vb-script-agent" data-script-agent aria-labelledby="vb-script-agent-title">
                <div class="vb-run-titlebar vb-script-agent-titlebar">
                    <i class="fa-solid fa-terminal" aria-hidden="true"></i>
                    <h5 id="vb-script-agent-title">Script agent</h5>
                    <span class="vb-run-runtime" data-script-agent-label>${label}</span>
                    <div class="vb-script-agent-picker">
                        <select class="form-select form-select-xs" data-script-agent-select aria-label="CLI to start"></select>
                    </div>
                    <div class="vb-run-window-controls">
                        <button type="button" data-script-agent-action="start" aria-label="Start agent" title="Start the agent in this script's folder"><i class="fa-solid fa-play" aria-hidden="true"></i></button>
                        <button type="button" data-script-agent-action="connect" aria-label="Connect" title="Connect to the running agent" hidden><i class="fa-solid fa-plug" aria-hidden="true"></i></button>
                        <button type="button" data-script-agent-action="restart" aria-label="Restart agent" title="Stop this session and start a new one" hidden><i class="fa-solid fa-rotate-right" aria-hidden="true"></i></button>
                        <button type="button" data-script-agent-action="stop" aria-label="Stop agent" title="Stop the agent" hidden><i class="fa-solid fa-stop" aria-hidden="true"></i></button>
                    </div>
                </div>
                <div class="vb-run-terminal vb-script-agent-terminal" data-script-agent-terminal hidden></div>
                <div class="vb-script-agent-placeholder" data-script-agent-placeholder>
                    <p>Start an agent to read this script and change it with you.</p>
                    <button class="btn btn-sm btn-primary" type="button" data-script-agent-action="start">
                        <i class="fa-solid fa-play me-1" aria-hidden="true"></i>Start
                    </button>
                </div>
                <div class="vb-run-bottom-bar vb-script-agent-bottom-bar">
                    <span class="vb-script-agent-state">
                        <span class="vb-script-agent-status" data-script-agent-status hidden></span>
                        <span data-script-agent-lifecycle role="status" aria-live="polite">Not started</span>
                    </span>
                    <span>Only this script's agent · not listed in Terminals</span>
                </div>
            </section>`;
    }
}

export const __test__ = { readStoredSelection, DEFAULT_SELECTION, OWNER_PREFIX };
