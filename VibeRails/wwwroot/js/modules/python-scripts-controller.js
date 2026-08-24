import { confirmDialog, escapeHtml, formatRelativeTime, isConfirmDialogOpen } from './utils.js';
import { formatFileExplorerSize } from './file-explorer.js';

const API = '/api/v1/python-scripts';

// Mirrors PythonScriptService.ScriptNamePattern. Client-side it only buys a better
// message before the round trip; the backend is still the authority.
const NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._ -]{0,120}\.py$/;

const REFRESH_THROTTLE_MS = 2000;
const MAX_SCRIPT_BYTES = 5 * 1024 * 1024;

export function defaultPythonMcpToolName(scriptName) {
    const stem = String(scriptName || '').replace(/\.py$/i, '').toLowerCase();
    const slug = stem.replace(/[^a-z0-9_]+/g, '_').replace(/^_+|_+$/g, '') || 'script';
    return `python_${slug}`.slice(0, 64);
}

// One Run button while its script is in flight, wherever it is rendered.
const RUNNING_BUTTON_HTML = '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Running…';

// Shared with the workbench so the row pill and the page pill never drift apart.
export const PYTHON_SCRIPT_STATUS_META = Object.freeze({
    approved: { label: 'Signed', tone: 'success', icon: 'fa-circle-check' },
    modified: { label: 'Changed since signing', tone: 'warning', icon: 'fa-triangle-exclamation' },
    unapproved: { label: 'Not signed', tone: 'neutral', icon: 'fa-circle-minus' }
});
const STATUS_META = PYTHON_SCRIPT_STATUS_META;

function newScriptTemplate(name) {
    const stem = name.replace(/\.py$/i, '');
    return `"""${stem}\n\nRuns from the VibeRails Automation page once you sign it with your PIN.\n"""\n\n\ndef main() -> None:\n    print("${stem} ran")\n\n\nif __name__ == "__main__":\n    main()\n`;
}

/** A run "worked" only when it exited 0 without hitting the timeout. */
export function isPythonRunOk(run) {
    return Boolean(run) && run.exitCode === 0 && !run.timedOut;
}

/** stdout then stderr of one run, as the row and the workbench drawer both show it. */
export function formatPythonRunOutput(run) {
    const parts = [];
    if (run?.standardOutput?.trim()) parts.push(run.standardOutput.trimEnd());
    if (run?.standardError?.trim()) parts.push(`[stderr]\n${run.standardError.trimEnd()}`);
    return parts.join('\n\n') || '(no output)';
}

/**
 * "Python scripts" section of the Automation page: single-file scripts from
 * ~/.vibe_rails/scripts, gated by hash pinning. Approving ("signing") always prompts
 * for the PIN — deliberately no unlocked session — and Run is only offered while the
 * server reports the script's hash still matches its approval. The PIN modal posts
 * the PIN straight to the backend and never stores it anywhere client-side.
 *
 * Authoring (new / import / duplicate / rename / edit / delete) never asks for the PIN,
 * because none of it can create an approval: a changed script drops to "Changed since
 * signing" until the user re-signs, while an identical save keeps the same exact approval.
 * Clicking a script opens the workbench view ('python-script': Monaco editor over a
 * docked agent terminal, see python-script-workbench.js); "Open in VS Code" stays a
 * secondary action when the extension injected window.__viberails_openFile__.
 *
 * The signing / rename / delete / duplicate / run flows live here as public methods
 * that do not need the section to be mounted: the workbench drives the very same code
 * (state is refetched on demand, and `onStateChange` lets it follow the list).
 */
export class PythonScriptsController {
    constructor(app) {
        this.app = app;
        this.root = null;
        this.state = null;
        this.mcpConfigurations = [];
        this.lastRunByName = new Map();
        // Scripts with a run in flight (from this section, the workbench or the nav
        // launcher). Rows render their Run button from this, so a background rebuild
        // can never re-enable a button mid-run.
        this.runningNames = new Set();
        this.modal = null;
        this.confirm = confirmDialog;
        this._stateListeners = new Set();
        this._lastListHtml = null;
        this._lastRefreshAt = 0;
        this._onWindowFocus = () => this._refreshIfIdle();
        this._onDocumentClick = (event) => {
            if (!this.root || this.root.contains(event.target)) return;
            this._closeMenus();
        };
    }

    async mount(root) {
        this.root = root;
        if (!root) return;
        root.innerHTML = `
            <div class="jobs-section-heading">
                <div class="python-scripts-heading-copy">
                    <div class="jobs-section-title-row">
                        <h2 id="python-scripts-title"><i class="fa-brands fa-python me-2" aria-hidden="true"></i>Python scripts</h2>
                        <span class="jobs-count" data-python-scripts-count>0 scripts</span>
                    </div>
                    <p>Single-file scripts from <code data-python-scripts-dir></code>. A script only runs while it is signed with your PIN. Edit a script here with an agent terminal beside it.</p>
                </div>
                <div class="python-scripts-heading-actions">
                    <button class="btn btn-sm btn-primary" type="button" data-python-scripts-action="new">
                        <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>New script
                    </button>
                    ${this._canImportFromHost() ? `<button class="btn btn-sm btn-outline-secondary" type="button" data-python-scripts-action="import">
                        <i class="fa-solid fa-file-import me-1" aria-hidden="true"></i>Add from disk
                    </button>` : ''}
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-python-scripts-action="refresh"
                            title="Refresh" aria-label="Refresh the script list">
                        <i class="fa-solid fa-rotate-right" aria-hidden="true"></i>
                    </button>
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-python-scripts-action="pin">
                        <i class="fa-solid fa-key me-1" aria-hidden="true"></i><span data-python-scripts-pin-label>Set PIN</span>
                    </button>
                </div>
            </div>
            <div class="python-scripts-list" data-python-scripts-list>
                <div class="jobs-empty" role="status"><span class="spinner-border spinner-border-sm"></span> Loading scripts…</div>
            </div>`;

        root.addEventListener('click', (event) => this._onClick(event));
        root.addEventListener('keydown', (event) => this._onKeydown(event));
        // Tabbing out of an open row menu closes it (a click elsewhere is handled below).
        root.addEventListener('focusout', (event) => this._onFocusOut(event));
        // The "Last run" drawer remembers whether it was open across list rebuilds.
        // `toggle` does not bubble, so it is caught in the capture phase.
        root.addEventListener('toggle', (event) => this._onOutputToggle(event), true);
        // A row menu is a popup: a click anywhere else on the page dismisses it.
        document.addEventListener('click', this._onDocumentClick, true);
        this._bindDropTarget(root.querySelector('[data-python-scripts-list]'));
        // A script edited in VS Code (or any other editor) changes on disk behind our back;
        // coming back to this window is the natural moment to notice.
        window.addEventListener('focus', this._onWindowFocus);
        document.addEventListener('visibilitychange', this._onWindowFocus);
        await this.refresh({ quiet: true });
    }

    unmount() {
        this._closeMenus();
        this._closeModal();
        document.removeEventListener('click', this._onDocumentClick, true);
        window.removeEventListener('focus', this._onWindowFocus);
        document.removeEventListener('visibilitychange', this._onWindowFocus);
        this.root = null;
        this.state = null;
        this._lastListHtml = null;
    }

    async refresh({ quiet = false } = {}) {
        try {
            const state = await this.app.apiCall(API, 'GET', null, { showLoading: false });
            try {
                const mcp = await this.app.apiCall(`${API}/mcp`, 'GET', null, { showLoading: false });
                this.mcpConfigurations = Array.isArray(mcp?.configurations) ? mcp.configurations : [];
            } catch (error) {
                console.error('Could not load Python MCP configuration:', error);
                this.mcpConfigurations = [];
            }
            this._applyState(state);
        } catch (error) {
            this._applyState(null);
            if (!quiet) this.app.showError(error?.message || 'Could not load Python scripts.');
        }
    }

    /** The current list state, fetching it when nothing is cached (unmounted callers). */
    async ensureState() {
        if (!this.state) await this.refresh({ quiet: true });
        return this.state;
    }

    /**
     * Follows every list update (refresh, save, sign, rename…). Returns the unsubscribe.
     * @param {(state: object|null) => void} listener
     */
    onStateChange(listener) {
        if (typeof listener !== 'function') return () => {};
        this._stateListeners.add(listener);
        return () => this._stateListeners.delete(listener);
    }

    /** Refresh unless something on-screen would be yanked out from under the user. */
    _refreshIfIdle() {
        if (!this.root || document.visibilityState === 'hidden') return;
        if (this.modal || isConfirmDialogOpen()) return;
        if (this.runningNames.size > 0) return;
        if (this.root.querySelector('.python-script-menu:not([hidden])')) return;
        if (Date.now() - this._lastRefreshAt < REFRESH_THROTTLE_MS) return;
        void this.refresh({ quiet: true });
    }

    _applyState(state) {
        this.state = state;
        this._lastRefreshAt = Date.now();
        this._render();
        for (const listener of Array.from(this._stateListeners)) {
            try {
                listener(state);
            } catch (error) {
                console.error('Python scripts state listener failed:', error);
            }
        }
    }

    scriptByName(name) {
        return (this.state?.scripts || []).find((script) => script.name === name) || null;
    }

    mcpConfigurationByScript(name) {
        return this.mcpConfigurations.find((configuration) =>
            configuration.scriptName === name) || null;
    }

    _render() {
        const root = this.root;
        if (!root) return;
        const list = root.querySelector('[data-python-scripts-list]');
        const count = root.querySelector('[data-python-scripts-count]');
        const dir = root.querySelector('[data-python-scripts-dir]');
        const pinLabel = root.querySelector('[data-python-scripts-pin-label]');
        if (!list) return;

        if (!this.state) {
            this._lastListHtml = null;
            list.innerHTML = '<div class="jobs-empty">Could not load Python scripts.</div>';
            return;
        }

        const scripts = this.state.scripts || [];
        if (count) count.textContent = `${scripts.length} ${scripts.length === 1 ? 'script' : 'scripts'}`;
        if (dir) dir.textContent = this.state.scriptsDirectory || '';
        if (pinLabel) pinLabel.textContent = this.state.pinConfigured ? 'Change PIN' : 'Set PIN';

        const html = scripts.length === 0
            ? `<div class="jobs-empty python-scripts-empty">
                    <strong>No Python scripts yet</strong>
                    <span>Write one here, copy one in from disk, or drop a <code>.py</code> file onto this panel.</span>
                    <div class="python-scripts-empty-actions">
                        <button class="btn btn-sm btn-primary" type="button" data-python-scripts-action="new">
                            <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>New script
                        </button>
                        ${this._canImportFromHost() ? `<button class="btn btn-sm btn-outline-secondary" type="button" data-python-scripts-action="import">
                            <i class="fa-solid fa-file-import me-1" aria-hidden="true"></i>Add from disk
                        </button>` : ''}
                    </div>
                </div>`
            : scripts.map((script) => this._renderRow(script)).join('');

        // Re-assigning identical markup would close an open row menu (and restart the
        // spinner) on every background refresh.
        if (html === this._lastListHtml) return;
        this._lastListHtml = html;
        list.innerHTML = html;
    }

    _renderRow(script) {
        const meta = STATUS_META[script.status] || STATUS_META.unapproved;
        const name = escapeHtml(script.name);
        const running = this.runningNames.has(script.name);
        const canRun = script.status === 'approved' && !running;
        const lastRun = this.lastRunByName.get(script.name);
        const approveLabel = script.status === 'approved' ? 'Re-sign' : 'Sign';
        const mcpConfiguration = this.mcpConfigurationByScript(script.name);
        const mcpEnabled = Boolean(mcpConfiguration);
        const canEnableMcp = script.status === 'approved';
        const details = [
            formatFileExplorerSize(script.sizeBytes),
            script.modifiedUtc ? `edited ${formatRelativeTime(script.modifiedUtc).toLowerCase()}` : '',
            script.status === 'approved' && script.approvedUtc
                ? `signed ${formatRelativeTime(script.approvedUtc).toLowerCase()}`
                : ''
        ].filter(Boolean).join(' · ');

        return `
            <div class="python-script-row" data-python-script="${name}">
                <div class="python-script-main">
                    <div class="python-script-identity">
                        <button class="python-script-name" type="button" data-python-scripts-action="open"
                                data-name="${name}" title="Edit ${name}">
                            <span>${name}</span>
                            <i class="fa-solid fa-pen-to-square" aria-hidden="true"></i>
                        </button>
                        <span class="python-script-meta">${escapeHtml(details)}</span>
                    </div>
                    <span class="python-script-status" data-tone="${meta.tone}">
                        <i class="fa-solid ${meta.icon}" aria-hidden="true"></i>${meta.label}
                    </span>
                </div>
                <div class="python-script-actions">
                    <div class="python-script-mcp-control" title="${escapeHtml(mcpEnabled
                        ? `Disable ${mcpConfiguration.toolName} on the VibeRails MCP server`
                        : canEnableMcp ? `Configure ${name} as an MCP tool` : 'Sign the script before adding it to MCP')}">
                        <span>MCP</span>
                        <button class="python-script-mcp-switch" type="button" role="switch"
                                aria-checked="${mcpEnabled ? 'true' : 'false'}"
                                aria-label="Expose ${name} as an MCP tool"
                                data-python-scripts-action="mcp-toggle" data-name="${name}"
                                ${mcpEnabled || canEnableMcp ? '' : 'disabled'}><span></span></button>
                    </div>
                    <button class="btn btn-sm btn-outline-primary python-script-edit" type="button" data-python-scripts-action="edit"
                            data-name="${name}" title="Open ${name} in the editor with an agent terminal beside it">
                        <i class="fa-solid fa-pen-to-square me-1" aria-hidden="true"></i>Edit
                    </button>
                    <button class="btn btn-sm btn-primary" type="button" data-python-scripts-action="run"
                            data-name="${name}" ${canRun ? '' : 'disabled'}
                            title="${running ? `${name} is running` : canRun ? `Run ${name} now` : 'Sign the script before running it'}">
                        ${running ? RUNNING_BUTTON_HTML : '<i class="fa-solid fa-play me-1" aria-hidden="true"></i>Run'}
                    </button>
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-python-scripts-action="approve"
                            data-name="${name}" title="Approve this exact version with your PIN">
                        <i class="fa-solid fa-signature me-1" aria-hidden="true"></i>${approveLabel}
                    </button>
                    <div class="python-script-menu-wrap">
                        <button class="btn btn-sm btn-outline-secondary python-script-menu-toggle" type="button"
                                data-python-scripts-action="menu" data-name="${name}" aria-haspopup="menu"
                                aria-expanded="false" aria-label="More actions for ${name}" title="More actions">
                            <i class="fa-solid fa-ellipsis-vertical" aria-hidden="true"></i>
                        </button>
                        <div class="python-script-menu" role="menu" data-python-script-menu="${name}" hidden>
                            ${this._renderMenuItems(script, name)}
                        </div>
                    </div>
                </div>
                ${lastRun ? `
                <details class="python-script-output" ${lastRun.open ? 'open' : ''}>
                    <summary>Last run: exit ${lastRun.exitCode}${lastRun.timedOut ? ' (timed out)' : ''} · ${Math.round(lastRun.durationMs)} ms</summary>
                    <pre>${escapeHtml(this._combinedOutput(lastRun))}</pre>
                </details>` : ''}
            </div>`;
    }

    _renderMenuItems(script, name) {
        const item = (action, icon, label, extraClass = '') => `
            <button class="python-script-menu-item ${extraClass}" type="button" role="menuitem"
                    data-python-scripts-action="${action}" data-name="${name}">
                <i class="fa-solid ${icon}" aria-hidden="true"></i><span>${label}</span>
            </button>`;

        // Editing has its own visible button on the row (and the name opens the file too),
        // so the menu holds only the secondary actions.
        return [
            this.canOpenInVsCode() ? item('open-vscode', 'fa-arrow-up-right-from-square', 'Open in VS Code') : '',
            item('duplicate', 'fa-copy', 'Duplicate…'),
            item('rename', 'fa-i-cursor', 'Rename…'),
            item('copy-path', 'fa-clipboard', 'Copy full path'),
            this.mcpConfigurationByScript(script.name)
                ? item('mcp-configure', 'fa-plug', 'Edit MCP tool…')
                : '',
            script.status === 'unapproved' ? '' : item('revoke', 'fa-ban', 'Remove signature'),
            item('delete', 'fa-trash', 'Delete…', 'python-script-menu-item-danger')
        ].filter(Boolean).join('');
    }

    /** True when the VS Code extension injected its open-a-file bridge (webview host). */
    canOpenInVsCode() {
        // Read through globalThis so the row markup can be rendered under the unit tests,
        // and so an older extension host (no bridge injected) simply lacks the menu item.
        const host = globalThis.window;
        return Boolean(host?.__viberails_VSCODE__ && typeof host.__viberails_openFile__ === 'function');
    }

    /** Host-path import exists only on the active root backend. Older hosts default to true. */
    _canImportFromHost() {
        return this.app.data?.configs?.isActiveRootBackend !== false;
    }

    _combinedOutput(run) {
        return formatPythonRunOutput(run);
    }

    _onClick(event) {
        const button = event.target.closest('[data-python-scripts-action]');
        if (!button || !this.root?.contains(button)) {
            this._closeMenus();
            return;
        }
        const action = button.dataset.pythonScriptsAction;
        const name = button.dataset.name;
        if (action !== 'menu') this._closeMenus();
        if (action === 'refresh') return void this.refresh();
        if (action === 'pin') return void this.openPinSetupModal();
        if (action === 'new') return void this._newScriptAndOpen();
        if (action === 'import') return void this._importScript(button);
        if (action === 'menu') return this._toggleMenu(button);
        if (action === 'open' || action === 'edit') return this.openScript(name);
        if (action === 'open-vscode') return this.openInVsCode(name);
        if (action === 'duplicate') return void this._duplicateAndOpen(name);
        if (action === 'rename') return void this.rename(name);
        if (action === 'copy-path') return void this.copyPath(name);
        if (action === 'approve') return void this.approve(name);
        if (action === 'revoke') return void this.revoke(name);
        if (action === 'mcp-toggle') {
            return void (this.mcpConfigurationByScript(name)
                ? this.disableMcp(name)
                : this.configureMcp(name));
        }
        if (action === 'mcp-configure') return void this.configureMcp(name);
        if (action === 'delete') return void this.deleteScript(name);
        if (action === 'run') return void this.run(name, button);
    }

    _toggleMenu(toggle) {
        // Found through the row rather than a name selector: script names may contain
        // spaces and dots, and CSS.escape is not available under the unit-test DOM.
        const menu = toggle.closest('.python-script-menu-wrap')?.querySelector('.python-script-menu');
        const wasOpen = menu && !menu.hidden;
        this._closeMenus();
        if (!menu || wasOpen) return;
        menu.hidden = false;
        toggle.setAttribute('aria-expanded', 'true');
        menu.querySelector('.python-script-menu-item')?.focus();
    }

    _closeMenus() {
        this.root?.querySelectorAll('.python-script-menu').forEach((menu) => { menu.hidden = true; });
        this.root?.querySelectorAll('.python-script-menu-toggle')
            .forEach((toggle) => toggle.setAttribute('aria-expanded', 'false'));
    }

    _openMenu() {
        return this.root?.querySelector('.python-script-menu:not([hidden])') || null;
    }

    _onKeydown(event) {
        const menu = this._openMenu();
        if (!menu) return;
        if (event.key === 'Escape') {
            // Consumed here, so app.js's Escape shortcut does not also navigate back.
            event.preventDefault();
            const toggle = menu.closest('.python-script-menu-wrap')?.querySelector('.python-script-menu-toggle');
            this._closeMenus();
            toggle?.focus();
            return;
        }
        if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
        const items = Array.from(menu.querySelectorAll('.python-script-menu-item'));
        if (items.length === 0) return;
        event.preventDefault();
        const current = items.indexOf(event.target?.closest?.('.python-script-menu-item'));
        const step = event.key === 'ArrowDown' ? 1 : -1;
        const next = current < 0
            ? (step > 0 ? 0 : items.length - 1)
            : (current + step + items.length) % items.length;
        items[next].focus();
    }

    /** Focus moving out of the open menu's row (Tab, Shift+Tab) closes it. */
    _onFocusOut(event) {
        const menu = this._openMenu();
        if (!menu) return;
        const wrap = menu.closest('.python-script-menu-wrap');
        // A null relatedTarget is a click on something unfocusable (Safari/Firefox also
        // report it for a plain button click); the document click handler owns that case.
        const next = event.relatedTarget;
        if (!next || !wrap || wrap.contains(next)) return;
        this._closeMenus();
    }

    _onOutputToggle(event) {
        const details = event.target?.closest?.('.python-script-output');
        if (!details) return;
        const name = details.closest('.python-script-row')?.dataset?.pythonScript;
        const lastRun = name ? this.lastRunByName.get(name) : null;
        if (lastRun) lastRun.open = Boolean(details.open);
    }

    // --- authoring ---

    /** Opens the workbench view for a script — the same destination in every host. */
    openScript(name) {
        if (!name) return;
        this.app.navigate('python-script', { name });
    }

    /** Secondary action in the VS Code webview: hand the file to a real editor tab. */
    openInVsCode(name) {
        const script = this.scriptByName(name);
        if (!script || !this.canOpenInVsCode()) return;
        globalThis.window.__viberails_openFile__(script.path);
        const signHint = script.status === 'unapproved' ? 'sign it here when it is ready' : 're-sign it here';
        this.app.showToast('Opened in VS Code', `${name} is open in an editor tab. Save there, then ${signHint}.`, 'info');
    }

    async _newScriptAndOpen() {
        const name = await this.newScript();
        if (name) this.openScript(name);
    }

    async _duplicateAndOpen(name) {
        const copy = await this.duplicate(name);
        if (copy) this.openScript(copy);
    }

    /**
     * Prompts for a name and creates the file from the template. Resolves with the new
     * script's name, or null when cancelled/failed. Callers decide where to go next.
     */
    async newScript() {
        await this.ensureState();
        const values = await this._promptForm({
            title: 'New Python script',
            body: `The file is created in ${this.state?.scriptsDirectory || 'the scripts folder'}. It starts unsigned — sign it when you are ready to run it.`,
            fields: [{
                key: 'name',
                label: 'File name',
                type: 'text',
                value: this._uniqueName('script.py'),
                placeholder: 'script.py'
            }],
            submitLabel: 'Create and edit',
            validate: (data) => this._validateNewName(data.name)
        });
        if (values === null) return null;

        const name = values.name.trim();
        try {
            await this.createScript(name, newScriptTemplate(name));
        } catch (error) {
            this.app.showError(error?.message || 'Could not create the script.');
            return null;
        }
        this.app.showToast('Script created', `${name} is ready to edit.`, 'success');
        return name;
    }

    /**
     * Creates a new, unsigned script file from text (new / duplicate / drop / re-create all
     * travel through here, so no process role needs host-path import). Resolves with the
     * refreshed list state; throws with the server's message on refusal.
     */
    async createScript(name, content) {
        const state = await this.app.apiCall(`${API}/create`, 'POST',
            { name, content },
            { showLoading: false, preferErrorResponseMessage: true });
        this._applyState(state);
        return state;
    }

    async _importScript(trigger) {
        if (!this._canImportFromHost()) {
            return this.app.showError('Adding a host file is available from the main VibeRails dashboard.');
        }
        if (typeof this.app.pickFileSystemEntry !== 'function') {
            return this.app.showError('The file picker is not available in this window.');
        }
        const picked = await this.app.pickFileSystemEntry({
            mode: 'file',
            title: 'Add a Python script',
            filters: [
                { label: 'Python files', extensions: ['py'] },
                { label: 'All files', extensions: [] }
            ],
            triggerElement: trigger instanceof HTMLElement ? trigger : undefined
        });
        if (!picked || picked.canceled || !picked.path) return;

        const suggestion = this._uniqueName(this._sanitizeName(picked.name || picked.path));
        const values = await this._promptForm({
            title: 'Add script from disk',
            body: `A copy of ${picked.path} is saved into the scripts folder. The original file is left alone.`,
            fields: [{ key: 'name', label: 'Save as', type: 'text', value: suggestion }],
            submitLabel: 'Copy in',
            validate: (data) => this._validateNewName(data.name)
        });
        if (values === null) return;

        try {
            this._applyState(await this.app.apiCall(`${API}/import`, 'POST',
                { sourcePath: picked.path, name: values.name.trim() },
                { showLoading: false, preferErrorResponseMessage: true }));
            this.app.showToast('Script added', `${values.name.trim()} is here — sign it before it can run.`, 'success');
        } catch (error) {
            this.app.showError(error?.message || 'Could not add that file.');
        }
    }

    /** Copies a script under a new name. Resolves with the copy's name, or null. */
    async duplicate(name) {
        await this.ensureState();
        const script = this.scriptByName(name);
        if (!script) return null;
        const values = await this._promptForm({
            title: `Duplicate ${name}`,
            body: 'The copy starts unsigned, even when the original is signed.',
            fields: [{
                key: 'name',
                label: 'Name for the copy',
                type: 'text',
                value: this._uniqueName(`${name.replace(/\.py$/i, '')}-copy.py`)
            }],
            submitLabel: 'Duplicate',
            validate: (data) => this._validateNewName(data.name)
        });
        if (values === null) return null;

        const copyName = values.name.trim();
        try {
            // Duplicate travels through content + create, so it works in every process role
            // without granting a terminal-child backend arbitrary host-path import.
            const source = await this.app.apiCall(
                `${API}/content?name=${encodeURIComponent(name)}`, 'GET', null,
                { showLoading: false, preferErrorResponseMessage: true });
            await this.createScript(copyName, source.content);
            this.app.showToast('Script duplicated', `${copyName} is ready to edit.`, 'success');
            return copyName;
        } catch (error) {
            this.app.showError(error?.message || 'Could not duplicate the script.');
            return null;
        }
    }

    /** Renames a script. Resolves with the new name, or null when cancelled/failed. */
    async rename(name) {
        await this.ensureState();
        const script = this.scriptByName(name);
        if (!script) return null;
        const values = await this._promptForm({
            title: `Rename ${name}`,
            body: script.status === 'unapproved'
                ? 'Pick a new file name.'
                : 'The file name is part of what gets signed, so renaming clears the signature — re-sign it afterwards.',
            fields: [{ key: 'name', label: 'New name', type: 'text', value: name }],
            submitLabel: 'Rename',
            validate: (data) => this._validateNewName(data.name, { allow: name })
        });
        if (values === null) return null;

        const newName = values.name.trim();
        try {
            const nextState = await this.app.apiCall(`${API}/rename`, 'POST',
                { name, newName },
                { showLoading: false, preferErrorResponseMessage: true });
            this.mcpConfigurations = this.mcpConfigurations.map((configuration) =>
                configuration.scriptName === name
                    ? { ...configuration, scriptName: newName }
                    : configuration);
            this._applyState(nextState);
            this.lastRunByName.delete(name);
            this.app.showToast('Script renamed', `${name} is now ${newName}.`, 'success');
            return newName;
        } catch (error) {
            this.app.showError(error?.message || 'Could not rename the script.');
            return null;
        }
    }

    /** Confirms, then deletes the file and its approval. Resolves true when it is gone. */
    async deleteScript(name) {
        await this.ensureState();
        const script = this.scriptByName(name);
        const confirmed = await this.confirm({
            title: `Delete ${name}?`,
            message: script?.status === 'approved'
                ? 'The file is removed from the scripts folder and its signature is forgotten. This cannot be undone.'
                : 'The file is removed from the scripts folder. This cannot be undone.',
            confirmLabel: 'Delete script',
            danger: true
        });
        if (!confirmed) return false;

        try {
            const nextState = await this.app.apiCall(
                `${API}?name=${encodeURIComponent(name)}`, 'DELETE', null,
                { showLoading: false, preferErrorResponseMessage: true });
            this.mcpConfigurations = this.mcpConfigurations.filter((configuration) =>
                configuration.scriptName !== name);
            this._applyState(nextState);
            this.lastRunByName.delete(name);
            this.app.showToast('Script deleted', `${name} is gone.`, 'info');
            return true;
        } catch (error) {
            this.app.showError(error?.message || 'Could not delete the script.');
            return false;
        }
    }

    async copyPath(name) {
        const script = this.scriptByName(name);
        if (!script) return;
        const copied = await this.app.copyTextToClipboard(script.path);
        this.app.showToast(
            copied ? 'Path copied' : 'Could not copy',
            copied ? script.path : 'Copy the path from the folder line above instead.',
            copied ? 'success' : 'warning');
    }

    /**
     * Workbench save hook: persists the file (optimistic `expectedVersion`; the server
     * answers 400 with a message when the file changed underneath) and resolves the
     * script's new status + version. Never carries a PIN — saving cannot sign.
     */
    async saveContent(name, content, expectedVersion) {
        const result = await this.app.apiCall(`${API}/content`, 'POST',
            { name, content, expectedVersion },
            { showLoading: false, preferErrorResponseMessage: true });
        this._applyState(result.state);
        return {
            status: this.scriptByName(name)?.status || 'unapproved',
            version: result.version
        };
    }

    _sanitizeName(rawName) {
        const base = String(rawName || '').split(/[\\/]/).pop() || '';
        const stem = base.replace(/\.py$/i, '')
            .replace(/[^A-Za-z0-9._ -]/g, '-')
            .replace(/^[^A-Za-z0-9]+/, '')
            .slice(0, 100);
        return `${stem || 'script'}.py`;
    }

    /** The candidate name, or the first "-2", "-3", … variant nothing else is using. */
    _uniqueName(candidate) {
        const taken = new Set((this.state?.scripts || []).map((script) => script.name.toLowerCase()));
        if (!taken.has(candidate.toLowerCase())) return candidate;
        const stem = candidate.replace(/\.py$/i, '');
        for (let index = 2; index <= 999; index += 1) {
            const next = `${stem}-${index}.py`;
            if (!taken.has(next.toLowerCase())) return next;
        }
        return candidate;
    }

    _validateNewName(rawName, { allow = null } = {}) {
        const name = (rawName || '').trim();
        if (!NAME_PATTERN.test(name)) {
            return 'Use a plain .py file name — letters, digits, dots, dashes and spaces only.';
        }
        if (name.includes('..')) return 'File names cannot contain "..".';
        if (allow && name.toLowerCase() === allow.toLowerCase()) return null;
        const clash = (this.state?.scripts || [])
            .some((script) => script.name.toLowerCase() === name.toLowerCase());
        return clash ? `A script named ${name} already exists.` : null;
    }

    // --- drag and drop ---

    _bindDropTarget(list) {
        if (!list) return;
        const setActive = (active) => list.classList.toggle('python-scripts-list-dropping', active);
        list.addEventListener('dragover', (event) => {
            if (!this._hasFiles(event)) return;
            event.preventDefault();
            event.dataTransfer.dropEffect = 'copy';
            setActive(true);
        });
        list.addEventListener('dragleave', (event) => {
            if (event.target === list) setActive(false);
        });
        list.addEventListener('drop', (event) => {
            if (!this._hasFiles(event)) return;
            event.preventDefault();
            setActive(false);
            void this._acceptDroppedFiles([...event.dataTransfer.files]);
        });
    }

    _hasFiles(event) {
        return [...(event.dataTransfer?.types || [])].includes('Files');
    }

    /**
     * Dropped files are read in the browser and posted as text, so this works from a
     * remote frontend too — unlike "Add from disk", which copies a path on the host.
     */
    async _acceptDroppedFiles(files) {
        const scripts = files.filter((file) => /\.py$/i.test(file.name));
        const skipped = files.length - scripts.length;
        if (scripts.length === 0) {
            return this.app.showToast('Nothing added', 'Drop .py files to add them as scripts.', 'warning');
        }

        const added = [];
        for (const file of scripts) {
            const name = this._uniqueName(this._sanitizeName(file.name));
            try {
                if (Number.isFinite(file.size) && file.size > MAX_SCRIPT_BYTES) {
                    throw new Error('the file is larger than the 5 MB limit');
                }
                const bytes = await file.arrayBuffer();
                let content;
                try {
                    content = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
                } catch {
                    throw new Error('the file is not valid UTF-8');
                }
                await this.createScript(name, content);
                added.push(name);
            } catch (error) {
                this.app.showError(`Could not add ${file.name}: ${error?.message || 'unknown error'}`);
            }
        }

        if (added.length === 0) return;
        this.app.showToast(
            added.length === 1 ? 'Script added' : `${added.length} scripts added`,
            `${added.join(', ')} — sign ${added.length === 1 ? 'it' : 'them'} before running.`
            + (skipped > 0 ? ` (${skipped} non-.py file${skipped === 1 ? '' : 's'} skipped.)` : ''),
            'success');
    }

    // --- signing and running ---

    async configureMcp(name) {
        const script = this.scriptByName(name);
        const existing = this.mcpConfigurationByScript(name);
        if (!script) return this.app.showError(`Script '${name}' was not found.`);
        if (script.status !== 'approved') {
            return this.app.showToast('Sign first', 'Sign this exact script version before adding it to MCP.', 'warning');
        }

        // The PIN approves the finished tool, so it is asked for last, in its own prompt.
        // A cancelled or rejected PIN reopens the form on what the user already filled in.
        let prefill = existing;
        while (true) {
            const request = await this._openMcpConfigurationModal(script, existing, prefill);
            if (!request) return;
            prefill = request;

            const pin = await this._promptPin({
                title: existing ? `Update ${request.toolName}` : `Expose ${name} as ${request.toolName}`,
                body: 'Exposing a signed script lets agents run it with arguments they choose.'
                    + ' Enter your signing PIN to approve. It is never stored.',
                submitLabel: existing ? 'Save MCP tool' : 'Add to MCP'
            });
            if (pin === null) continue;

            // Only the request is guarded: once it lands the tool is saved, and a
            // rendering failure must not send the user back to the form.
            let response;
            try {
                response = await this.app.apiCall(`${API}/mcp`, 'PUT', { ...request, pin },
                    { preferErrorResponseMessage: true });
            } catch (error) {
                this.app.showError(error?.message || 'Could not save the MCP tool.');
                continue;
            }

            this.mcpConfigurations = Array.isArray(response?.configurations) ? response.configurations : [];
            this._lastListHtml = null;
            this._render();
            this.app.showToast(
                existing ? 'MCP tool updated' : 'Added to MCP',
                `${request.toolName} now runs ${name} through the signed-script gate.`,
                'success');
            return;
        }
    }

    async disableMcp(name) {
        const configuration = this.mcpConfigurationByScript(name);
        if (!configuration) return;
        try {
            const response = await this.app.apiCall(
                `${API}/mcp?name=${encodeURIComponent(name)}`,
                'DELETE');
            this.mcpConfigurations = Array.isArray(response?.configurations) ? response.configurations : [];
            this._lastListHtml = null;
            this._render();
            this.app.showToast('Removed from MCP', `${configuration.toolName} is no longer exposed.`, 'success');
        } catch (error) {
            this.app.showError(error?.message || 'Could not remove the MCP tool.');
        }
    }

    /**
     * Collects the tool shape only. `existing` is the saved configuration (it decides
     * "Add" vs "Edit" wording); `prefill` is what the fields start from, so a rejected
     * PIN can reopen the form with the user's work intact instead of a blank slate.
     */
    _openMcpConfigurationModal(script, existing, prefill = existing) {
        this._closeModal();
        const parameters = Array.isArray(prefill?.parameters) ? prefill.parameters : [];
        const layer = document.createElement('div');
        layer.className = 'python-scripts-modal-layer';
        layer.innerHTML = `
            <div class="modal fade show d-block python-scripts-pin-modal" tabindex="-1" role="dialog"
                 aria-modal="true" aria-labelledby="python-mcp-config-title">
                <div class="modal-dialog modal-lg modal-dialog-scrollable">
                    <form class="modal-content" data-python-mcp-form>
                        <div class="modal-header">
                            <div>
                                <h5 class="modal-title" id="python-mcp-config-title">${existing ? 'Edit' : 'Add'} MCP tool</h5>
                                <div class="text-muted small mt-1">Signed script: <code>${escapeHtml(script.name)}</code></div>
                            </div>
                            <button type="button" class="btn-close" data-python-mcp-cancel aria-label="Close"></button>
                        </div>
                        <div class="modal-body python-mcp-config-body">
                            <div class="alert alert-danger d-none" data-python-mcp-error role="alert"></div>

                            <section class="python-mcp-section">
                                <h6 class="python-mcp-section-title">Identity</h6>
                                <div>
                                    <label class="form-label" for="python-mcp-tool-name">Tool name</label>
                                    <input class="form-control" id="python-mcp-tool-name" data-python-mcp-field="toolName"
                                           value="${escapeHtml(prefill?.toolName || defaultPythonMcpToolName(script.name))}"
                                           maxlength="64" autocomplete="off" required>
                                    <div class="form-text">The name agents call. Letters, numbers, <code>_</code>, <code>-</code>, and <code>.</code>.</div>
                                </div>
                                <div>
                                    <label class="form-label" for="python-mcp-description">When should an agent use this?</label>
                                    <textarea class="form-control" id="python-mcp-description" data-python-mcp-field="description"
                                              rows="3" maxlength="500" required>${escapeHtml(prefill?.description || '')}</textarea>
                                </div>
                            </section>

                            <section class="python-mcp-section">
                                <h6 class="python-mcp-section-title">Behavior</h6>
                                <div class="form-text">Callers see this as the tool's MCP annotations. Declare it honestly: agents read it to decide whether to check with you before running.</div>
                                ${this._renderMcpBehaviorSection(prefill)}
                            </section>

                            <section class="python-mcp-section">
                                <div class="python-mcp-section-head">
                                    <div>
                                        <h6 class="python-mcp-section-title">Parameters</h6>
                                        <div class="form-text">Each input becomes MCP JSON and is safely passed in the script's argv array. Positional values use the order shown.</div>
                                    </div>
                                    <button class="btn btn-sm btn-outline-primary" type="button" data-python-mcp-add-parameter>
                                        <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>Add parameter
                                    </button>
                                </div>
                                <div class="python-mcp-parameters" data-python-mcp-parameters></div>
                                <div class="python-mcp-no-parameters" data-python-mcp-empty ${parameters.length ? 'hidden' : ''}>
                                    No parameters. The MCP tool will run the script without extra command-line arguments.
                                </div>
                            </section>
                        </div>
                        <div class="modal-footer">
                            <button type="button" class="btn btn-secondary" data-python-mcp-cancel>Cancel</button>
                            <button type="submit" class="btn btn-primary">${existing ? 'Save MCP tool' : 'Add to MCP'}</button>
                        </div>
                    </form>
                </div>
            </div>
            <div class="modal-backdrop fade show"></div>`;
        document.body.appendChild(layer);

        const container = layer.querySelector('[data-python-mcp-parameters]');
        const empty = layer.querySelector('[data-python-mcp-empty]');
        const appendParameter = (parameter = null) => {
            container.insertAdjacentHTML('beforeend', this._renderMcpParameterEditor(parameter));
            empty.hidden = true;
            this._syncMcpParameterRows(container);
        };
        parameters.forEach((parameter) => appendParameter(parameter));

        return new Promise((resolve) => {
            const finish = (value) => {
                if (this.modal?.layer === layer) this.modal = null;
                document.removeEventListener('keydown', onKeydown, true);
                layer.remove();
                resolve(value);
            };
            const onKeydown = (event) => {
                if (isConfirmDialogOpen() || event.key !== 'Escape') return;
                event.preventDefault();
                event.stopImmediatePropagation();
                finish(null);
            };
            document.addEventListener('keydown', onKeydown, true);
            layer.querySelectorAll('[data-python-mcp-cancel]')
                .forEach((button) => button.addEventListener('click', () => finish(null)));
            layer.querySelector('[data-python-mcp-add-parameter]')
                ?.addEventListener('click', () => appendParameter());
            layer.querySelectorAll('[data-python-mcp-field="behavior"]').forEach((radio) =>
                radio.addEventListener('change', () => this._syncMcpBehaviorChecks(layer)));
            container.addEventListener('click', (event) => {
                const remove = event.target.closest('[data-python-mcp-remove-parameter]');
                if (!remove) return;
                remove.closest('[data-python-mcp-parameter]')?.remove();
                empty.hidden = container.children.length > 0;
                this._syncMcpParameterRows(container);
            });
            container.addEventListener('change', () => this._syncMcpParameterRows(container));
            layer.querySelector('[data-python-mcp-form]')?.addEventListener('submit', (event) => {
                event.preventDefault();
                const result = this._collectMcpConfiguration(layer, script.name);
                const alert = layer.querySelector('[data-python-mcp-error]');
                if (result.error) {
                    alert.textContent = result.error;
                    alert.classList.remove('d-none');
                    return;
                }
                finish(result.value);
            });
            this.modal = { layer, close: () => finish(null) };
            requestAnimationFrame(() => layer.querySelector('[data-python-mcp-field="toolName"]')?.focus());
        });
    }

    /**
     * The author's declaration of what running the script does. The server maps it onto MCP's
     * four annotation hints, which is the only thing a calling agent ever sees.
     */
    _renderMcpBehaviorSection(prefill) {
        const behavior = prefill?.behavior || '';
        const choice = (value, title, hint) => `
                <label class="python-mcp-behavior-option">
                    <input class="form-check-input" type="radio" name="python-mcp-behavior"
                           data-python-mcp-field="behavior" value="${value}"
                           ${behavior === value ? 'checked' : ''} required>
                    <span class="python-mcp-behavior-copy">
                        <strong>${title}</strong>
                        <span class="form-text">${hint}</span>
                    </span>
                </label>`;
        const readOnly = behavior === 'read-only';
        return `
            <div class="python-mcp-behavior" role="radiogroup" aria-label="What this script does">
                ${choice('read-only', 'Reads and reports only', 'Changes nothing on this machine.')}
                ${choice('additive', 'Creates or updates', 'Writes files or records, but destroys nothing.')}
                ${choice('destructive', 'Overwrites or deletes', 'Can destroy data. Callers should assume the worst.')}
            </div>
            <div class="python-mcp-behavior-checks">
                <label>
                    <input class="form-check-input" type="checkbox" data-python-mcp-field="repeatSafe"
                           ${readOnly || prefill?.repeatSafe ? 'checked' : ''} ${readOnly ? 'disabled' : ''}>
                    Running it again with the same inputs changes nothing more
                </label>
                <label>
                    <input class="form-check-input" type="checkbox" data-python-mcp-field="reachesNetwork"
                           ${prefill?.reachesNetwork ? 'checked' : ''}>
                    Reaches the network or outside services
                </label>
            </div>`;
    }

    /** A script that changes nothing is idempotent by definition, so don't make the author say so. */
    _syncMcpBehaviorChecks(layer) {
        const readOnly = layer.querySelector('[data-python-mcp-field="behavior"]:checked')?.value === 'read-only';
        const repeatSafe = layer.querySelector('[data-python-mcp-field="repeatSafe"]');
        if (!repeatSafe) return;
        if (readOnly) repeatSafe.checked = true;
        repeatSafe.disabled = readOnly;
    }

    _renderMcpParameterEditor(parameter = null) {
        const type = parameter?.type || 'string';
        const mode = parameter?.argumentMode || 'positional';
        const hasDefault = parameter?.defaultValue !== null && parameter?.defaultValue !== undefined;
        const selected = (value, actual) => value === actual ? 'selected' : '';
        return `
            <div class="python-mcp-parameter" data-python-mcp-parameter>
                <div class="python-mcp-parameter-head">
                    <strong data-python-mcp-parameter-title>Parameter</strong>
                    <div class="python-mcp-parameter-head-controls">
                        <select class="form-select form-select-sm" data-python-mcp-param="argumentMode"
                                aria-label="How this value reaches Python">
                            <option value="positional" ${selected(mode, 'positional')}>Positional value</option>
                            <option value="option" ${selected(mode, 'option')}>Named option</option>
                        </select>
                        <button class="btn btn-sm btn-outline-danger" type="button" data-python-mcp-remove-parameter
                                aria-label="Remove parameter" title="Remove parameter"><i class="fa-solid fa-trash" aria-hidden="true"></i></button>
                    </div>
                </div>
                <div class="python-mcp-parameter-grid">
                    <div>
                        <label class="form-label">MCP input name</label>
                        <input class="form-control" data-python-mcp-param="name" value="${escapeHtml(parameter?.name || '')}" maxlength="64" placeholder="output_path">
                    </div>
                    <div>
                        <label class="form-label">Type</label>
                        <select class="form-select" data-python-mcp-param="type">
                            <option value="string" ${selected(type, 'string')}>Text</option>
                            <option value="integer" ${selected(type, 'integer')}>Integer</option>
                            <option value="number" ${selected(type, 'number')}>Number</option>
                            <option value="boolean" ${selected(type, 'boolean')}>True / false</option>
                        </select>
                    </div>
                    <div data-python-mcp-param-flag-wrap ${mode === 'option' ? '' : 'hidden'}>
                        <label class="form-label">Python flag</label>
                        <input class="form-control" data-python-mcp-param="flag" value="${escapeHtml(parameter?.flag || '')}" placeholder="--output">
                    </div>
                    <div class="python-mcp-parameter-description">
                        <label class="form-label">Description</label>
                        <input class="form-control" data-python-mcp-param="description" value="${escapeHtml(parameter?.description || '')}" maxlength="300" placeholder="What the agent should provide">
                    </div>
                </div>
                <div class="python-mcp-parameter-checks">
                    <label><input class="form-check-input" type="checkbox" data-python-mcp-param="required" ${parameter?.required ? 'checked' : ''}> Required</label>
                    <label><input class="form-check-input" type="checkbox" data-python-mcp-param="defaultEnabled" ${hasDefault ? 'checked' : ''}> Use default</label>
                    <input class="form-control form-control-sm" data-python-mcp-param="defaultValue"
                           value="${escapeHtml(hasDefault ? parameter.defaultValue : '')}" placeholder="Default value" ${hasDefault ? '' : 'disabled'}>
                </div>
            </div>`;
    }

    _syncMcpParameterRows(container) {
        container.querySelectorAll('[data-python-mcp-parameter]').forEach((row, index) => {
            const name = row.querySelector('[data-python-mcp-param="name"]')?.value.trim();
            row.querySelector('[data-python-mcp-parameter-title]').textContent = name || `Parameter ${index + 1}`;
            const option = row.querySelector('[data-python-mcp-param="argumentMode"]')?.value === 'option';
            row.querySelector('[data-python-mcp-param-flag-wrap]').hidden = !option;
            const defaultEnabled = row.querySelector('[data-python-mcp-param="defaultEnabled"]')?.checked;
            row.querySelector('[data-python-mcp-param="defaultValue"]').disabled = !defaultEnabled;
        });
    }

    _collectMcpConfiguration(layer, scriptName) {
        const value = (key) => layer.querySelector(`[data-python-mcp-field="${key}"]`)?.value ?? '';
        const toolName = value('toolName').trim();
        const description = value('description').trim();
        if (!/^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$/.test(toolName)) {
            return { error: 'Enter a valid MCP tool name (1-64 letters, numbers, underscores, periods, or hyphens).' };
        }
        if (!description) return { error: 'Describe when an agent should use this tool.' };

        const behavior = layer.querySelector('[data-python-mcp-field="behavior"]:checked')?.value || '';
        if (!behavior) return { error: 'Say what this script does so callers know what they are running.' };
        const checked = (key) => Boolean(layer.querySelector(`[data-python-mcp-field="${key}"]`)?.checked);
        const reachesNetwork = checked('reachesNetwork');
        // Read-only implies it, and that box is disabled rather than cleared.
        const repeatSafe = behavior === 'read-only' || checked('repeatSafe');

        const parameters = [];
        for (const row of layer.querySelectorAll('[data-python-mcp-parameter]')) {
            const field = (key) => row.querySelector(`[data-python-mcp-param="${key}"]`);
            const name = field('name')?.value.trim() || '';
            const parameterDescription = field('description')?.value.trim() || '';
            const argumentMode = field('argumentMode')?.value || 'positional';
            const flag = field('flag')?.value.trim() || null;
            const required = Boolean(field('required')?.checked);
            const defaultEnabled = Boolean(field('defaultEnabled')?.checked);
            if (!/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(name)) {
                return { error: 'Every parameter needs a unique Python-style MCP input name.' };
            }
            if (!parameterDescription) return { error: `Describe the '${name}' parameter.` };
            if (argumentMode === 'option' && !/^--?[A-Za-z0-9][A-Za-z0-9-]*$/.test(flag || '')) {
                return { error: `The Python flag for '${name}' must look like --output or -o.` };
            }
            if (required && defaultEnabled) return { error: `'${name}' cannot be required and have a default.` };
            parameters.push({
                name,
                description: parameterDescription,
                type: field('type')?.value || 'string',
                required,
                defaultValue: defaultEnabled ? field('defaultValue')?.value ?? '' : null,
                argumentMode,
                flag: argumentMode === 'option' ? flag : null
            });
        }
        if (new Set(parameters.map((parameter) => parameter.name)).size !== parameters.length) {
            return { error: 'Parameter names must be unique.' };
        }
        return {
            value: { scriptName, toolName, description, parameters, behavior, repeatSafe, reachesNetwork }
        };
    }

    /** PIN-gated approval of the script's current bytes. Resolves true once signed. */
    async approve(name) {
        await this.ensureState();
        if (!this.state?.pinConfigured) {
            this.app.showToast('Set a PIN first', 'Create your signing PIN, then sign the script.', 'info');
            await this.openPinSetupModal();
            return false;
        }

        const pin = await this._promptPin({
            title: `Sign ${name}`,
            body: 'Signing approves this exact version of the script to run. Enter your signing PIN.',
            submitLabel: 'Sign script'
        });
        if (pin === null) return false;
        try {
            this._applyState(await this.app.apiCall(`${API}/approve`, 'POST', { name, pin },
                { showLoading: false, preferErrorResponseMessage: true }));
            this.app.showToast('Script signed', `${name} is approved to run.`, 'success');
            return true;
        } catch (error) {
            this.app.showError(error?.message || 'Could not sign the script.');
            return false;
        }
    }

    /** PIN-gated removal of the approval. Resolves true once revoked. */
    async revoke(name) {
        const pin = await this._promptPin({
            title: `Remove signature from ${name}`,
            body: 'The script will not run again until it is re-signed. Enter your signing PIN.',
            submitLabel: 'Remove signature'
        });
        if (pin === null) return false;
        try {
            this._applyState(await this.app.apiCall(`${API}/revoke`, 'POST', { name, pin },
                { showLoading: false, preferErrorResponseMessage: true }));
            this.app.showToast('Signature removed', `${name} can no longer run.`, 'info');
            return true;
        } catch (error) {
            this.app.showError(error?.message || 'Could not remove the signature.');
            return false;
        }
    }

    /**
     * Starts a signed script in a backend-created interactive terminal tab. `button`
     * (optional, the workbench's own) shows a spinner until that tab is ready; section rows
     * follow runningNames. Resolves with the launch response, or null when a launch is already
     * in flight.
     */
    async run(name, button = null) {
        if (!name || this.runningNames.has(name)) return null;
        const original = button?.innerHTML;
        if (button) {
            button.disabled = true;
            button.innerHTML = RUNNING_BUTTON_HTML;
        }
        this.runningNames.add(name);
        this._render();
        let result = null;
        try {
            result = await this.app.apiCall(`${API}/run/interactive`, 'POST', { name },
                { showLoading: false, preferErrorResponseMessage: true });
            const tabId = String(result?.tabId || '').trim();
            if (!tabId) throw new Error('The interactive terminal did not return a tab id.');

            this.app.terminalController?.rememberTabLaunch?.(tabId, {
                selection: 'base:shell',
                label: name,
                title: `${name} · Python`,
                icon: '🐍',
                taskKey: `python-script:${name}`,
                workingDirectory: this.state?.scriptsDirectory || null
            });
            this.app.showToast(
                'Script started',
                `${name} is running in an interactive terminal.`,
                'success');
            this.app.navigate?.('terminal-focus', {
                preferredSelection: 'base:shell',
                preferredTabId: tabId
            });
        } catch (error) {
            this.app.showError(error?.message || `Could not run ${name}.`);
        } finally {
            this.runningNames.delete(name);
            if (button) {
                button.disabled = false;
                button.innerHTML = original;
            }
            await this.refresh({ quiet: true });
        }
        return result;
    }

    /** Remembers a run for the row's "Last run" drawer (opened, since it is news). */
    recordRun(name, result) {
        if (!name || !result) return;
        this.lastRunByName.set(name, { ...result, open: true });
    }

    /**
     * The one toast for a finished run — section, workbench and nav launcher all report
     * through here so the wording never drifts. Returns whether the run succeeded.
     */
    notifyRunResult(name, result, { outputHint = '' } = {}) {
        const ok = isPythonRunOk(result);
        let detail = `exit ${result?.exitCode}${result?.timedOut ? ' (timed out)' : ''}`;
        if (!ok && outputHint) detail += ` — ${outputHint}`;
        this.app.showToast(
            ok ? 'Script finished' : 'Script failed',
            `${name}: ${detail}.`,
            ok ? 'success' : 'error');
        return ok;
    }

    async openPinSetupModal() {
        const changing = Boolean(this.state?.pinConfigured);
        const values = await this._promptForm({
            title: changing ? 'Change signing PIN' : 'Create signing PIN',
            body: changing
                ? 'The signing PIN approves Python scripts to run. Enter the current PIN and the new one.'
                : 'The signing PIN approves Python scripts to run. Only you should know it — agents are told they can never ask for it.',
            fields: [
                ...(changing ? [{ key: 'currentPin', label: 'Current PIN' }] : []),
                { key: 'newPin', label: 'New PIN (4+ characters)' },
                { key: 'confirmPin', label: 'Confirm new PIN' }
            ],
            submitLabel: changing ? 'Change PIN' : 'Create PIN',
            validate: (data) => {
                if ((data.newPin || '').length < 4) return 'The PIN must be at least 4 characters.';
                if (data.newPin !== data.confirmPin) return 'The PINs do not match.';
                return null;
            }
        });
        if (values === null) return;
        try {
            this._applyState(await this.app.apiCall(`${API}/pin`, 'POST',
                { currentPin: values.currentPin || null, newPin: values.newPin },
                { showLoading: false, preferErrorResponseMessage: true }));
            this.app.showToast('Signing PIN saved', 'Use it to sign scripts from now on.', 'success');
        } catch (error) {
            this.app.showError(error?.message || 'Could not save the PIN.');
        }
    }

    _promptPin({ title, body, submitLabel }) {
        return this._promptForm({
            title,
            body,
            fields: [{ key: 'pin', label: 'Signing PIN' }],
            submitLabel
        }).then((values) => (values === null ? null : values.pin || ''));
    }

    /**
     * Minimal input modal (window.prompt is banned and silently broken in the VS Code
     * webview). Fields default to type="password" — the PIN case — and opt into "text"
     * for names. Resolves with the field values, or null on cancel/Escape.
     */
    _promptForm({ title, body, fields, submitLabel, validate = null }) {
        this._closeModal();
        const host = document.getElementById('modal-container');
        if (!host) return Promise.resolve(null);

        return new Promise((resolve) => {
            const layer = document.createElement('div');
            layer.className = 'llm-picker-modal-layer';
            layer.innerHTML = `
                <div class="modal fade show d-block python-scripts-pin-modal" tabindex="-1" role="dialog"
                     aria-modal="true" aria-labelledby="python-scripts-pin-title">
                    <div class="modal-dialog">
                        <div class="modal-content">
                            <div class="modal-header">
                                <h5 class="modal-title" id="python-scripts-pin-title">${escapeHtml(title)}</h5>
                                <button type="button" class="btn-close" data-pin-action="cancel" aria-label="Cancel"></button>
                            </div>
                            <form data-pin-form autocomplete="off">
                                <div class="modal-body">
                                    <p class="text-muted small">${escapeHtml(body)}</p>
                                    ${fields.map((field, index) => `
                                    <div class="mb-2">
                                        <label class="form-label" for="python-scripts-pin-${index}">${escapeHtml(field.label)}</label>
                                        <input class="form-control" type="${field.type === 'text' ? 'text' : 'password'}"
                                               id="python-scripts-pin-${index}" data-pin-field="${escapeHtml(field.key)}"
                                               value="${escapeHtml(field.value || '')}"
                                               placeholder="${escapeHtml(field.placeholder || '')}"
                                               autocomplete="off" autocapitalize="off" spellcheck="false" required>
                                    </div>`).join('')}
                                    <div class="alert alert-danger mt-2 mb-0 d-none" role="alert" data-pin-error></div>
                                </div>
                                <div class="modal-footer">
                                    <button type="button" class="btn btn-secondary" data-pin-action="cancel">Cancel</button>
                                    <button type="submit" class="btn btn-primary">${escapeHtml(submitLabel)}</button>
                                </div>
                            </form>
                        </div>
                    </div>
                </div>
                <div class="modal-backdrop fade show"></div>`;

            host.appendChild(layer);
            const finish = (value) => {
                if (this.modal?.layer === layer) this.modal = null;
                document.removeEventListener('keydown', onKeydown, true);
                layer.remove();
                resolve(value);
            };
            const onKeydown = (event) => {
                // A confirmDialog overlay owns Escape while it is up (see utils.js).
                if (isConfirmDialogOpen()) return;
                if (event.key !== 'Escape') return;
                event.preventDefault();
                event.stopImmediatePropagation();
                finish(null);
            };
            document.addEventListener('keydown', onKeydown, true);
            layer.querySelectorAll('[data-pin-action="cancel"]')
                .forEach((el) => el.addEventListener('click', () => finish(null)));
            layer.querySelector('[data-pin-form]')?.addEventListener('submit', (event) => {
                event.preventDefault();
                const values = {};
                layer.querySelectorAll('[data-pin-field]').forEach((input) => {
                    values[input.dataset.pinField] = input.value;
                });
                const problem = validate ? validate(values) : null;
                const alert = layer.querySelector('[data-pin-error]');
                if (problem) {
                    if (alert) {
                        alert.textContent = problem;
                        alert.classList.remove('d-none');
                    }
                    return;
                }
                finish(values);
            });

            this.modal = { layer, close: () => finish(null) };
            requestAnimationFrame(() => {
                const first = layer.querySelector('[data-pin-field]');
                first?.focus();
                // Name fields open pre-filled with a suggestion; select it so typing replaces it.
                if (first?.type === 'text') first.select?.();
            });
        });
    }

    _closeModal() {
        this.modal?.close?.();
        this.modal = null;
    }
}
