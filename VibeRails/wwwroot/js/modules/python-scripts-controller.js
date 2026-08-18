import { confirmDialog, escapeHtml, formatRelativeTime, isConfirmDialogOpen } from './utils.js';
import { formatFileExplorerSize } from './file-explorer.js';
import { PythonScriptEditorModal } from './python-script-editor.js';

const API = '/api/v1/python-scripts';

// Mirrors PythonScriptService.ScriptNamePattern. Client-side it only buys a better
// message before the round trip; the backend is still the authority.
const NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._ -]{0,120}\.py$/;

const REFRESH_THROTTLE_MS = 2000;
const MAX_SCRIPT_BYTES = 5 * 1024 * 1024;

const STATUS_META = Object.freeze({
    approved: { label: 'Signed', tone: 'success', icon: 'fa-circle-check' },
    modified: { label: 'Changed since signing', tone: 'warning', icon: 'fa-triangle-exclamation' },
    unapproved: { label: 'Not signed', tone: 'neutral', icon: 'fa-circle-minus' }
});

function newScriptTemplate(name) {
    const stem = name.replace(/\.py$/i, '');
    return `"""${stem}\n\nRuns from the VibeRails Automation page once you sign it with your PIN.\n"""\n\n\ndef main() -> None:\n    print("${stem} ran")\n\n\nif __name__ == "__main__":\n    main()\n`;
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
 * Editing opens a real VS Code tab when we are hosted in the extension
 * (window.__viberails_openFile__) and a Monaco modal everywhere else.
 */
export class PythonScriptsController {
    constructor(app) {
        this.app = app;
        this.root = null;
        this.state = null;
        this.lastRunByName = new Map();
        this.modal = null;
        this.confirm = confirmDialog;
        this.editor = new PythonScriptEditorModal({
            app,
            onSave: (name, content, expectedVersion) =>
                this._saveContent(name, content, expectedVersion),
            onSign: (name) => this._approve(name)
        });
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
                    <p>Single-file scripts from <code data-python-scripts-dir></code>. A script only runs while it is signed with your PIN.</p>
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
        void this.editor.close({ force: true });
        document.removeEventListener('click', this._onDocumentClick, true);
        window.removeEventListener('focus', this._onWindowFocus);
        document.removeEventListener('visibilitychange', this._onWindowFocus);
        this.root = null;
        this.state = null;
        this._lastListHtml = null;
    }

    async refresh({ quiet = false } = {}) {
        try {
            this._applyState(await this.app.apiCall(API, 'GET', null, { showLoading: false }));
        } catch (error) {
            this._applyState(null);
            if (!quiet) this.app.showError(error?.message || 'Could not load Python scripts.');
        }
    }

    /** Refresh unless something on-screen would be yanked out from under the user. */
    _refreshIfIdle() {
        if (!this.root || document.visibilityState === 'hidden') return;
        if (this.modal || this.editor.isOpen || isConfirmDialogOpen()) return;
        if (this.root.querySelector('.python-script-menu:not([hidden])')) return;
        if (Date.now() - this._lastRefreshAt < REFRESH_THROTTLE_MS) return;
        void this.refresh({ quiet: true });
    }

    _applyState(state) {
        this.state = state;
        this._lastRefreshAt = Date.now();
        this._render();
    }

    _scriptByName(name) {
        return (this.state?.scripts || []).find((script) => script.name === name) || null;
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
        const canRun = script.status === 'approved';
        const lastRun = this.lastRunByName.get(script.name);
        const approveLabel = script.status === 'approved' ? 'Re-sign' : 'Sign';
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
                                data-name="${name}" title="${this._openHint(script.name)}">
                            <span>${name}</span>
                            <i class="fa-solid ${this._canOpenInVsCode() ? 'fa-arrow-up-right-from-square' : 'fa-pen-to-square'}" aria-hidden="true"></i>
                        </button>
                        <span class="python-script-meta">${escapeHtml(details)}</span>
                    </div>
                    <span class="python-script-status" data-tone="${meta.tone}">
                        <i class="fa-solid ${meta.icon}" aria-hidden="true"></i>${meta.label}
                    </span>
                </div>
                <div class="python-script-actions">
                    <button class="btn btn-sm btn-primary" type="button" data-python-scripts-action="run"
                            data-name="${name}" ${canRun ? '' : 'disabled'}
                            title="${canRun ? `Run ${name} now` : 'Sign the script before running it'}">
                        <i class="fa-solid fa-play me-1" aria-hidden="true"></i>Run
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

        return [
            this._canOpenInVsCode() ? item('open', 'fa-arrow-up-right-from-square', 'Open in VS Code') : '',
            item('edit', 'fa-pen-to-square', this._canOpenInVsCode() ? 'Edit here' : 'Edit script'),
            item('duplicate', 'fa-copy', 'Duplicate…'),
            item('rename', 'fa-i-cursor', 'Rename…'),
            item('copy-path', 'fa-clipboard', 'Copy full path'),
            script.status === 'unapproved' ? '' : item('revoke', 'fa-ban', 'Remove signature'),
            item('delete', 'fa-trash', 'Delete…', 'python-script-menu-item-danger')
        ].filter(Boolean).join('');
    }

    _openHint(name) {
        return this._canOpenInVsCode()
            ? `Open ${escapeHtml(name)} in a VS Code editor tab`
            : `Edit ${escapeHtml(name)}`;
    }

    /** True when the VS Code extension injected its open-a-file bridge (webview host). */
    _canOpenInVsCode() {
        // Read through globalThis so the row markup can be rendered under the unit tests,
        // and so an older extension host (no bridge injected) falls back to the in-app editor.
        const host = globalThis.window;
        return Boolean(host?.__viberails_VSCODE__ && typeof host.__viberails_openFile__ === 'function');
    }

    /** Host-path import exists only on the active root backend. Older hosts default to true. */
    _canImportFromHost() {
        return this.app.data?.configs?.isActiveRootBackend !== false;
    }

    _combinedOutput(run) {
        const parts = [];
        if (run.standardOutput?.trim()) parts.push(run.standardOutput.trimEnd());
        if (run.standardError?.trim()) parts.push(`[stderr]\n${run.standardError.trimEnd()}`);
        return parts.join('\n\n') || '(no output)';
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
        if (action === 'pin') return void this._openPinSetupModal();
        if (action === 'new') return void this._newScript();
        if (action === 'import') return void this._importScript(button);
        if (action === 'menu') return this._toggleMenu(button);
        if (action === 'open') return void this._openScript(name);
        if (action === 'edit') return void this._openScript(name, { inApp: true });
        if (action === 'duplicate') return void this._duplicate(name);
        if (action === 'rename') return void this._rename(name);
        if (action === 'copy-path') return void this._copyPath(name);
        if (action === 'approve') return void this._approve(name);
        if (action === 'revoke') return void this._revoke(name);
        if (action === 'delete') return void this._delete(name);
        if (action === 'run') return void this._run(name, button);
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

    // --- authoring ---

    async _newScript() {
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
        if (values === null) return;

        const name = values.name.trim();
        try {
            this._applyState(await this.app.apiCall(`${API}/create`, 'POST',
                { name, content: newScriptTemplate(name) },
                { showLoading: false, preferErrorResponseMessage: true }));
        } catch (error) {
            return this.app.showError(error?.message || 'Could not create the script.');
        }
        this.app.showToast('Script created', `${name} is ready to edit.`, 'success');
        await this._openScript(name);
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

    async _duplicate(name) {
        const script = this._scriptByName(name);
        if (!script) return;
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
        if (values === null) return;

        try {
            // Duplicate travels through content + create, so it works in every process role
            // without granting a terminal-child backend arbitrary host-path import.
            const source = await this.app.apiCall(
                `${API}/content?name=${encodeURIComponent(name)}`, 'GET', null,
                { showLoading: false, preferErrorResponseMessage: true });
            this._applyState(await this.app.apiCall(`${API}/create`, 'POST',
                { name: values.name.trim(), content: source.content },
                { showLoading: false, preferErrorResponseMessage: true }));
            this.app.showToast('Script duplicated', `${values.name.trim()} is ready to edit.`, 'success');
        } catch (error) {
            this.app.showError(error?.message || 'Could not duplicate the script.');
        }
    }

    async _rename(name) {
        const script = this._scriptByName(name);
        if (!script) return;
        const values = await this._promptForm({
            title: `Rename ${name}`,
            body: script.status === 'unapproved'
                ? 'Pick a new file name.'
                : 'The file name is part of what gets signed, so renaming clears the signature — re-sign it afterwards.',
            fields: [{ key: 'name', label: 'New name', type: 'text', value: name }],
            submitLabel: 'Rename',
            validate: (data) => this._validateNewName(data.name, { allow: name })
        });
        if (values === null) return;

        const newName = values.name.trim();
        try {
            this._applyState(await this.app.apiCall(`${API}/rename`, 'POST',
                { name, newName },
                { showLoading: false, preferErrorResponseMessage: true }));
            this.lastRunByName.delete(name);
            this.app.showToast('Script renamed', `${name} is now ${newName}.`, 'success');
        } catch (error) {
            this.app.showError(error?.message || 'Could not rename the script.');
        }
    }

    async _delete(name) {
        const script = this._scriptByName(name);
        const confirmed = await this.confirm({
            title: `Delete ${name}?`,
            message: script?.status === 'approved'
                ? 'The file is removed from the scripts folder and its signature is forgotten. This cannot be undone.'
                : 'The file is removed from the scripts folder. This cannot be undone.',
            confirmLabel: 'Delete script',
            danger: true
        });
        if (!confirmed) return;

        try {
            this._applyState(await this.app.apiCall(
                `${API}?name=${encodeURIComponent(name)}`, 'DELETE', null,
                { showLoading: false, preferErrorResponseMessage: true }));
            this.lastRunByName.delete(name);
            this.app.showToast('Script deleted', `${name} is gone.`, 'info');
            this.app.automationNavLauncher?.invalidate?.();
        } catch (error) {
            this.app.showError(error?.message || 'Could not delete the script.');
        }
    }

    async _copyPath(name) {
        const script = this._scriptByName(name);
        if (!script) return;
        const copied = await this.app.copyTextToClipboard(script.path);
        this.app.showToast(
            copied ? 'Path copied' : 'Could not copy',
            copied ? script.path : 'Copy the path from the folder line above instead.',
            copied ? 'success' : 'warning');
    }

    /** Opens the script — a VS Code editor tab when we are hosted there, Monaco otherwise. */
    async _openScript(name, { inApp = false } = {}) {
        const script = this._scriptByName(name);
        if (!script) return;

        if (!inApp && this._canOpenInVsCode()) {
            globalThis.window.__viberails_openFile__(script.path);
            this.app.showToast('Opened in VS Code', `${name} is open in an editor tab. Save there, then re-sign here.`, 'info');
            return;
        }

        let content;
        try {
            content = await this.app.apiCall(
                `${API}/content?name=${encodeURIComponent(name)}`, 'GET', null,
                { showLoading: false, preferErrorResponseMessage: true });
        } catch (error) {
            return this.app.showError(error?.message || `Could not open ${name}.`);
        }

        await this.editor.open({
            name: content.name,
            content: content.content,
            status: content.status,
            path: script.path,
            version: content.version
        });
        await this.refresh({ quiet: true });
    }

    /** Editor save hook: persists the file and resolves the script's new status. */
    async _saveContent(name, content, expectedVersion) {
        const result = await this.app.apiCall(`${API}/content`, 'POST',
            { name, content, expectedVersion },
            { showLoading: false, preferErrorResponseMessage: true });
        this._applyState(result.state);
        return {
            status: this._scriptByName(name)?.status || 'unapproved',
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
                this._applyState(await this.app.apiCall(`${API}/create`, 'POST',
                    { name, content },
                    { showLoading: false, preferErrorResponseMessage: true }));
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

    async _approve(name) {
        if (!this.state?.pinConfigured) {
            this.app.showToast('Set a PIN first', 'Create your signing PIN, then sign the script.', 'info');
            return this._openPinSetupModal();
        }

        const pin = await this._promptPin({
            title: `Sign ${name}`,
            body: 'Signing approves this exact version of the script to run. Enter your signing PIN.',
            submitLabel: 'Sign script'
        });
        if (pin === null) return;
        try {
            this._applyState(await this.app.apiCall(`${API}/approve`, 'POST', { name, pin },
                { showLoading: false, preferErrorResponseMessage: true }));
            this.app.showToast('Script signed', `${name} is approved to run.`, 'success');
            this.app.automationNavLauncher?.invalidate?.();
        } catch (error) {
            this.app.showError(error?.message || 'Could not sign the script.');
        }
    }

    async _revoke(name) {
        const pin = await this._promptPin({
            title: `Remove signature from ${name}`,
            body: 'The script will not run again until it is re-signed. Enter your signing PIN.',
            submitLabel: 'Remove signature'
        });
        if (pin === null) return;
        try {
            this._applyState(await this.app.apiCall(`${API}/revoke`, 'POST', { name, pin },
                { showLoading: false, preferErrorResponseMessage: true }));
            this.app.showToast('Signature removed', `${name} can no longer run.`, 'info');
        } catch (error) {
            this.app.showError(error?.message || 'Could not remove the signature.');
        }
    }

    async _run(name, button) {
        const original = button.innerHTML;
        button.disabled = true;
        button.innerHTML = '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Running…';
        try {
            const result = await this.app.apiCall(`${API}/run`, 'POST', { name },
                { showLoading: false, preferErrorResponseMessage: true });
            this.lastRunByName.set(name, { ...result, open: true });
            const ok = result.exitCode === 0 && !result.timedOut;
            this.app.showToast(
                ok ? 'Script finished' : 'Script failed',
                `${name}: exit ${result.exitCode}${result.timedOut ? ' (timed out)' : ''}.`,
                ok ? 'success' : 'error');
        } catch (error) {
            this.app.showError(error?.message || `Could not run ${name}.`);
        } finally {
            button.disabled = false;
            button.innerHTML = original;
            await this.refresh({ quiet: true });
        }
    }

    async _openPinSetupModal() {
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
