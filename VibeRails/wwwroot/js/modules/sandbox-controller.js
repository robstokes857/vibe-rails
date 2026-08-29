import { ensureMonaco } from './monaco-loader.js';
import { mountLlmPicker } from './pickers/llm-picker.js';
import { parseLlmSelection } from './utils.js';

// Sandbox Controller - Manages sandbox creation, listing, and actions
export class SandboxController {
    constructor(app) {
        this.app = app;
        this._diffEditor = null;
        this._diffEscapeCleanup = null;
        this._pickerDisposers = [];
    }

    unload() {
        this._disposePickers();
        this._disposeDiffEditor();
    }

    _disposePickers() {
        this._pickerDisposers.splice(0).forEach((dispose) => dispose?.());
    }

    // Monaco diff editors do NOT dispose externally-set models with the editor, and
    // app.closeModal() just blanks the container — without this, every diff view
    // leaked a live DiffEditor (automaticLayout observer included) plus two
    // TextModels holding the full before/after text of the last file shown.
    _disposeDiffEditor() {
        this._diffEscapeCleanup?.();
        this._diffEscapeCleanup = null;
        const editor = this._diffEditor;
        if (!editor) return;
        this._diffEditor = null;
        let model = null;
        try { model = editor.getModel(); } catch (_) {}
        try { editor.dispose(); } catch (_) {}
        try { model?.original?.dispose(); } catch (_) {}
        try { model?.modified?.dispose(); } catch (_) {}
    }

    _installDiffEscapeCleanup() {
        this._diffEscapeCleanup?.();
        const onKeydown = (event) => {
            if (event.key !== 'Escape' || event.defaultPrevented) return;
            // Monaco owns Escape for its own widgets, and app.js deliberately leaves
            // those events alone. For every Escape that the app will use to dismiss
            // the modal, tear down the editor first.
            if (event.target?.closest?.('.monaco-editor')) return;
            this._disposeDiffEditor();
        };
        document.addEventListener('keydown', onKeydown, true);
        this._diffEscapeCleanup = () => document.removeEventListener('keydown', onKeydown, true);
    }

    // Fetch sandboxes from API
    async refreshSandboxes() {
        try {
            const response = await this.app.apiCall('/api/v1/sandboxes', 'GET', null, { showLoading: false });
            this.app.data.sandboxes = (response.sandboxes || []).map(sb => ({
                id: sb.id,
                name: sb.name,
                path: sb.path,
                branch: sb.branch,
                sourceBranch: sb.sourceBranch || null,
                commitHash: sb.commitHash,
                remoteUrl: sb.remoteUrl || null,
                // Set when this sandbox is an Environment's workspace rather than a
                // standalone one. Owned workspaces render on their environment's row; the
                // Sandboxes card shows only the standalone ones.
                environmentId: sb.environmentId ?? null,
                environmentName: sb.environmentName || null,
                created: this.app.formatRelativeTime(sb.createdUTC)
            }));
        } catch (error) {
            console.error('Failed to refresh sandboxes:', error);
            this.app.data.sandboxes = [];
        }
    }

    // Show the create sandbox modal
    createSandbox() {
        this.app.showModal('Create New Sandbox', `
            <form id="create-sandbox-form">
                <div class="mb-3">
                    <label class="form-label">Sandbox Name</label>
                    <input type="text" class="form-control" id="sandbox-name"
                           required pattern="[a-zA-Z0-9_-]+"
                           placeholder="e.g., feature-auth, bugfix-login">
                    <small class="form-text text-muted">
                        Creates a shallow clone of the current branch with your uncommitted changes.
                        Alphanumeric characters, hyphens, and underscores only.
                    </small>
                </div>
                <div class="d-flex gap-2 justify-content-end">
                    <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                    <button type="submit" class="btn btn-primary d-flex align-items-center gap-2" id="create-sandbox-submit-btn">
                        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                            <path d="M8 15A7 7 0 1 1 8 1a7 7 0 0 1 0 14m0 1A8 8 0 1 0 8 0a8 8 0 0 0 0 16"/>
                            <path d="M8 4a.5.5 0 0 1 .5.5v3h3a.5.5 0 0 1 0 1h-3v3a.5.5 0 0 1-1 0v-3h-3a.5.5 0 0 1 0-1h3v-3A.5.5 0 0 1 8 4"/>
                        </svg>
                        Create Sandbox
                    </button>
                </div>
            </form>
        `);

        const form = document.getElementById('create-sandbox-form');
        if (form) {
            form.addEventListener('submit', async (e) => {
                e.preventDefault();
                const nameInput = document.getElementById('sandbox-name');
                const submitBtn = document.getElementById('create-sandbox-submit-btn');
                const name = nameInput?.value?.trim();
                if (!name) return;

                // Disable button and show loading
                if (submitBtn) {
                    submitBtn.disabled = true;
                    submitBtn.textContent = 'Creating...';
                }

                try {
                    await this.app.apiCall('/api/v1/sandboxes', 'POST', { name });
                    this.app.closeModal();
                    this.app.showToast('Sandbox Created',
                        `Sandbox "${name}" created successfully`, 'success');
                    await this.refreshSandboxes();
                    this._reloadCurrentView();
                } catch (error) {
                    this.app.showError(`Failed to create sandbox: ${error.message}`);
                    if (submitBtn) {
                        submitBtn.disabled = false;
                        submitBtn.textContent = 'Create Sandbox';
                    }
                }
            });
        }
    }

    _reloadCurrentView() {
        if (this.app.currentView === 'sandboxes') {
            this.loadSandboxes();
        } else if (this.app.currentView === 'environments') {
            this.app.environmentController.loadEnvironments();
        } else {
            this.app.dashboardController.loadDashboard();
        }
    }

    async loadSandboxes() {
        await this.app.refreshDashboardData();

        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = `
            <div class="view" data-view="sandboxes">
                <div class="d-flex justify-content-between align-items-center mb-4">
                    <div>
                        <h2 class="mb-1">Sandboxes</h2>
                        <p class="text-muted mb-0">Isolated git repos for multi-LLM parallel processing.</p>
                    </div>
                    <button class="btn btn-primary d-inline-flex align-items-center gap-2" type="button" data-action="create-sandbox">
                        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16"><path d="M8 4a.5.5 0 0 1 .5.5v3h3a.5.5 0 0 1 0 1h-3v3a.5.5 0 0 1-1 0v-3h-3a.5.5 0 0 1 0-1h3v-3A.5.5 0 0 1 8 4"/></svg>
                        New Sandbox
                    </button>
                </div>
                <div class="list-group project-history-list" data-sandbox-list></div>
            </div>
        `;

        const root = content.querySelector('[data-view="sandboxes"]');
        this.app.bindAction(root, '[data-action="create-sandbox"]', () => this.createSandbox());

        const list = root.querySelector('[data-sandbox-list]');
        this.populateSandboxesList(list);
    }

    populateSandboxesList(container) {
        if (!container) return;
        this._disposePickers();
        container.innerHTML = '';

        // Standalone sandboxes only. A sandbox owned by an environment is that environment's
        // workspace and is driven from its row on the Environments page — rendering it here
        // would offer a Delete button that can destroy an in-flight run's working tree.
        const sandboxes = (this.app.data.sandboxes || []).filter(sb => !sb.environmentId);

        if (sandboxes.length === 0) {
            container.innerHTML = '<p class="text-muted text-center py-3">No sandboxes yet. Create one to work in an isolated copy of your project.</p>';
            return;
        }

        const template = document.getElementById('sandbox-item-template');
        if (!template) {
            container.innerHTML = '<p class="text-muted text-center py-3">Template not found.</p>';
            return;
        }

        const fragment = document.createDocumentFragment();

        sandboxes.forEach((sb) => {
            const node = template.content.cloneNode(true);

            const name = node.querySelector('[data-sandbox-name]');
            if (name) name.textContent = sb.name;

            const branch = node.querySelector('[data-sandbox-branch]');
            if (branch) branch.textContent = sb.branch;

            const path = node.querySelector('[data-sandbox-path]');
            if (path) path.textContent = sb.path;

            const time = node.querySelector('[data-sandbox-time]');
            if (time) time.textContent = sb.created;

            // Diff button
            const diffBtn = node.querySelector('[data-sandbox-diff]');
            if (diffBtn) {
                diffBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.showDiff(sb.id, sb.name);
                });
            }

            // Merge local button
            const mergeLocalBtn = node.querySelector('[data-sandbox-merge-local]');
            if (mergeLocalBtn) {
                mergeLocalBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.mergeLocally(sb.id, sb.name, sb.branch);
                });
            }

            // Push to remote button
            const pushRemoteBtn = node.querySelector('[data-sandbox-push-remote]');
            if (pushRemoteBtn) {
                pushRemoteBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.pushToRemote(sb.id, sb.name);
                });
            }

            // CLI select
            const cliSelect = node.querySelector('[data-sandbox-cli-select]');
            if (cliSelect) {
                this.populateSandboxCliSelect(cliSelect);
            }

            // Helper to resolve CLI selection — returns null and shakes if nothing selected
            const resolveCli = () => {
                const result = this.parseSandboxCliSelection(cliSelect);
                if (!result) {
                    if (cliSelect) {
                        const ts = cliSelect.tomselect;
                        const shakeTarget = ts?.wrapper || cliSelect;
                        shakeTarget.classList.remove('vb-terminal-selection-shake');
                        void shakeTarget.offsetWidth;
                        shakeTarget.classList.add('vb-terminal-selection-shake');
                        if (ts) {
                            try { ts.focus(); ts.open(); } catch {}
                        } else {
                            cliSelect.focus();
                            if (typeof cliSelect.showPicker === 'function') {
                                try { cliSelect.showPicker(); } catch {}
                            }
                        }
                    }
                    return null;
                }
                return result;
            };

            // CLI launch button
            const cliBtn = node.querySelector('[data-sandbox-launch-cli]');
            if (cliBtn) {
                cliBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const result = resolveCli();
                    if (!result) return;
                    this.launchInExternalTerminal(sb.id, sb.name, result.cli, result.environmentName);
                });
            }

            // Web Terminal launch button
            const webUiBtn = node.querySelector('[data-sandbox-launch-webui]');
            if (webUiBtn) {
                webUiBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const result = resolveCli();
                    if (!result) return;
                    this.launchInWebUI(sb.id, sb.name, result.cli, result.environmentName);
                });
            }

            // VS Code button
            const vscodeBtn = node.querySelector('[data-sandbox-vscode]');
            if (vscodeBtn) {
                vscodeBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.launchVSCode(sb.id, sb.name);
                });
            }

            // Delete button
            const deleteBtn = node.querySelector('[data-sandbox-delete]');
            if (deleteBtn) {
                deleteBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.deleteSandbox(sb.id, sb.name);
                });
            }

            fragment.appendChild(node);
        });

        container.appendChild(fragment);
    }

    populateSandboxCliSelect(selectEl) {
        const dispose = mountLlmPicker(this.app, selectEl, {
            context: 'sandbox',
            placeholder: 'Select CLI...',
            includeDefaultSuffix: false
        });
        this._pickerDisposers.push(dispose);
        return dispose;
    }

    parseSandboxCliSelection(selectEl) {
        const parsed = parseLlmSelection(selectEl?.value, this.app.data.environments || []);
        return parsed.cli
            ? { cli: parsed.cli, environmentName: parsed.environmentName }
            : null;
    }

    // Delete a sandbox with confirmation
    async deleteSandbox(id, name) {
        const escapedName = this.app.escapeHtml(name);
        this.app.showModal('Delete Sandbox', `
            <div class="text-center py-3">
                <div class="mb-3 text-danger">
                    <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" fill="currentColor" viewBox="0 0 16 16">
                        <path d="M11 1.5v1h3.5a.5.5 0 0 1 0 1h-.538l-.853 10.66A2 2 0 0 1 11.115 16h-6.23a2 2 0 0 1-1.994-1.84L2.038 3.5H1.5a.5.5 0 0 1 0-1H5v-1A1.5 1.5 0 0 1 6.5 0h3A1.5 1.5 0 0 1 11 1.5m-5 0v1h4v-1a.5.5 0 0 0-.5-.5h-3a.5.5 0 0 0-.5.5M4.5 5.029l.5 8.5a.5.5 0 1 0 .998-.06l-.5-8.5a.5.5 0 1 0-.998.06m6.53-.06a.5.5 0 0 0-.515.479l-.5 8.5a.5.5 0 1 0 .998.06l.5-8.5a.5.5 0 0 0-.484-.539M8 5.5a.5.5 0 0 0-.5.5v8.5a.5.5 0 0 0 1 0V6a.5.5 0 0 0-.5-.5"/>
                    </svg>
                </div>
                <h5>Are you sure you want to delete "${escapedName}"?</h5>
                <p class="text-muted small px-4">This will permanently delete the sandbox directory and all its contents. This action cannot be undone.</p>
            </div>
            <div class="d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="button" class="btn btn-danger" id="confirm-delete-sandbox-btn">Delete Sandbox</button>
            </div>
        `);

        const confirmBtn = document.getElementById('confirm-delete-sandbox-btn');
        if (confirmBtn) {
            confirmBtn.addEventListener('click', async () => {
                this.app.closeModal();
                try {
                    await this.app.apiCall(`/api/v1/sandboxes/${id}`, 'DELETE');
                    this.app.showToast('Sandbox Deleted', `Sandbox "${name}" deleted`, 'info');
                    await this.refreshSandboxes();
                    this._reloadCurrentView();
                } catch (error) {
                    this.app.showError(`Failed to delete sandbox: ${error.message}`);
                }
            });
        }
    }

    // Launch terminal into sandbox directory
    async launchInWebUI(sandboxId, sandboxName, cli, environmentName) {
        if (!cli) {
            this.app.showError('Please select a CLI to launch');
            return;
        }

        this.app.showToast('Web Terminal',
            `Launching ${cli} in sandbox "${sandboxName}"...`, 'info');

        const sandbox = this.app.data.sandboxes.find(s => s.id === sandboxId);
        if (!sandbox) {
            this.app.showError('Sandbox not found');
            return;
        }

        return this.app.terminalController.launchInFocus({
            cli: cli,
            environmentName: environmentName || null,
            workingDirectory: sandbox.path,
            title: `Sandbox: ${sandboxName}`,
            tabLabel: sandboxName,
            forceNewTab: true
        });
    }

    // Launch CLI in external terminal in sandbox directory
    async launchInExternalTerminal(sandboxId, sandboxName, cli, environmentName) {
        try {
            const body = {};
            if (environmentName) body.environmentName = environmentName;

            const response = await this.app.apiCall(
                `/api/v1/sandboxes/${sandboxId}/launch/${cli}`, 'POST', body);
            this.app.showToast('External Terminal',
                response.message || `${cli} launched in sandbox "${sandboxName}"`, 'success');
        } catch (error) {
            this.app.showError(`Failed to launch ${cli} in external terminal: ${error.message}`);
        }
    }

    // Launch a plain shell in sandbox directory
    async launchShell(sandboxId, sandboxName) {
        try {
            const response = await this.app.apiCall(
                `/api/v1/sandboxes/${sandboxId}/launch/shell`, 'POST');
            this.app.showToast('Shell',
                response.message || `Shell launched in sandbox "${sandboxName}"`, 'success');
        } catch (error) {
            this.app.showError('Failed to launch shell in sandbox');
        }
    }

    // Launch VS Code in sandbox
    async launchVSCode(sandboxId, sandboxName) {
        try {
            const response = await this.app.apiCall(
                `/api/v1/sandboxes/${sandboxId}/launch/vscode`, 'POST');
            this.app.showToast('VS Code',
                response.message || `VS Code launched in sandbox "${sandboxName}"`, 'success');
        } catch (error) {
            this.app.showError('Failed to launch VS Code in sandbox');
        }
    }

    // ============================================
    // Diff Viewer
    // ============================================

    async showDiff(sandboxId, sandboxName) {
        const escapedName = this.app.escapeHtml(sandboxName);

        // Show loading modal
        const modalContainer = document.getElementById('modal-container');
        modalContainer.innerHTML = `
            <div class="modal fade show d-block sandbox-diff-modal" tabindex="-1">
                <div class="modal-dialog">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">Code Changes &mdash; ${escapedName}</h5>
                            <button type="button" class="btn-close" data-action="close-modal"></button>
                        </div>
                        <div class="modal-body">
                            <div class="sandbox-diff-empty">Loading changes...</div>
                        </div>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show"></div>
        `;
        // Bind close button (CSP-safe, this modal isn't created via showModal)
        modalContainer.querySelectorAll('[data-action="close-modal"]')
            .forEach(btn => btn.addEventListener('click', () => this.app.closeModal()));

        try {
            // Start both in parallel, but check diff data before waiting for Monaco
            const monacoPromise = ensureMonaco();
            const diffData = await this.app.apiCall(`/api/v1/sandboxes/${sandboxId}/diff`, 'GET');

            const files = (diffData && diffData.files) || [];

            if (files.length === 0) {
                modalContainer.querySelector('.sandbox-diff-empty').textContent = 'No changes detected in this sandbox.';
                return;
            }

            // Only wait for Monaco if we have files to display
            const monacoInstance = await monacoPromise;

            if (!monacoInstance) {
                this.app.closeModal();
                this.app.showError('Failed to load Monaco Editor');
                return;
            }

            // Build the full diff modal UI
            this._renderDiffModal(escapedName, files, monacoInstance);

        } catch (error) {
            this.app.closeModal();
            this.app.showError(`Failed to load diff: ${error.message}`);
        }
    }

    _renderDiffModal(escapedName, files, monacoInstance) {
        const modalContainer = document.getElementById('modal-container');

        // Build file list HTML
        const fileListHtml = files.map((f, i) => {
            const status = !f.originalContent ? 'A' : !f.modifiedContent ? 'D' : 'M';
            const statusClass = status === 'A' ? 'added' : status === 'D' ? 'deleted' : 'modified';
            const fileName = f.fileName.split('/').pop();
            const dirPath = f.fileName.includes('/') ? f.fileName.substring(0, f.fileName.lastIndexOf('/') + 1) : '';
            return `<div class="sandbox-diff-file-item ${i === 0 ? 'active' : ''}" data-file-index="${i}" title="${this.app.escapeHtml(f.fileName)}">
                <span class="file-status ${statusClass}">${status}</span>
                <span><span style="opacity: 0.5;">${this.app.escapeHtml(dirPath)}</span>${this.app.escapeHtml(fileName)}</span>
            </div>`;
        }).join('');

        modalContainer.innerHTML = `
            <div class="modal fade show d-block sandbox-diff-modal" tabindex="-1">
                <div class="modal-dialog">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">Code Changes &mdash; ${escapedName}</h5>
                            <button type="button" class="btn-close" data-action="close-modal"></button>
                        </div>
                        <div class="modal-body">
                            <div class="sandbox-diff-sidebar">
                                <div style="padding: 8px 12px; font-size: 0.75rem; color: #6A6A7D; text-transform: uppercase; letter-spacing: 0.5px;">
                                    Changed Files (${files.length})
                                </div>
                                ${fileListHtml}
                            </div>
                            <div class="sandbox-diff-main">
                                <div class="sandbox-diff-toolbar">
                                    <button class="diff-btn active" id="diff-btn-side-by-side">Side by Side</button>
                                    <button class="diff-btn" id="diff-btn-inline">Inline</button>
                                    <div class="diff-stat" id="diff-stats">
                                        <span class="added">+0</span>&nbsp;<span class="removed">-0</span>
                                    </div>
                                </div>
                                <div class="sandbox-diff-editor-container" id="sandbox-diff-editor"></div>
                                <div class="sandbox-diff-statusbar">
                                    <div class="status-left">
                                        <span id="diff-change-count">0 changes</span>
                                    </div>
                                    <div class="status-right">
                                        <span>UTF-8</span>
                                        <span id="diff-language">${this.app.escapeHtml(files[0]?.language || 'plaintext')}</span>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show"></div>
        `;

        // Bind close button (CSP-safe, this modal isn't created via showModal).
        // Dispose the editor on close — closeModal alone leaves it (and its two
        // full-text models) alive behind the blanked container.
        modalContainer.querySelectorAll('[data-action="close-modal"]')
            .forEach(btn => btn.addEventListener('click', () => {
                this._disposeDiffEditor();
                this.app.closeModal();
            }));

        // Dispose any previous diff editor (and its models) before creating a new one
        this._disposeDiffEditor();

        // Create diff editor
        const editorContainer = document.getElementById('sandbox-diff-editor');
        const diffEditor = monacoInstance.editor.createDiffEditor(editorContainer, {
            theme: 'viberails-dark',
            automaticLayout: true,
            fontSize: 14,
            fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace',
            renderSideBySide: true,
            enableSplitViewResizing: true,
            renderIndicators: true,
            renderMarginRevertIcon: true,
            smoothScrolling: true,
            padding: { top: 8 },
            minimap: { enabled: false },
            scrollBeyondLastLine: false,
            originalEditable: false,
            readOnly: true,
        });

        this._diffEditor = diffEditor;
        this._installDiffEscapeCleanup();

        // Load first file
        this._loadFileInDiff(diffEditor, monacoInstance, files[0]);

        // Update stats when diff is computed
        diffEditor.onDidUpdateDiff(() => {
            this._updateDiffStats(diffEditor);
        });

        // File list click handlers
        const fileItems = modalContainer.querySelectorAll('.sandbox-diff-file-item');
        fileItems.forEach(item => {
            item.addEventListener('click', () => {
                const idx = parseInt(item.getAttribute('data-file-index'));
                fileItems.forEach(fi => fi.classList.remove('active'));
                item.classList.add('active');
                this._loadFileInDiff(diffEditor, monacoInstance, files[idx]);
                const langEl = document.getElementById('diff-language');
                if (langEl) langEl.textContent = files[idx].language || 'plaintext';
            });
        });

        // Side by side / inline toggles
        const btnSideBySide = document.getElementById('diff-btn-side-by-side');
        const btnInline = document.getElementById('diff-btn-inline');

        btnSideBySide?.addEventListener('click', () => {
            diffEditor.updateOptions({ renderSideBySide: true });
            btnSideBySide.classList.add('active');
            btnInline.classList.remove('active');
        });

        btnInline?.addEventListener('click', () => {
            diffEditor.updateOptions({ renderSideBySide: false });
            btnInline.classList.add('active');
            btnSideBySide.classList.remove('active');
        });
    }

    _loadFileInDiff(diffEditor, monacoInstance, file) {
        const oldModel = diffEditor.getModel();
        const originalModel = monacoInstance.editor.createModel(file.originalContent || '', file.language || 'plaintext');
        const modifiedModel = monacoInstance.editor.createModel(file.modifiedContent || '', file.language || 'plaintext');
        diffEditor.setModel({ original: originalModel, modified: modifiedModel });
        if (oldModel) {
            try { oldModel.original?.dispose(); } catch (_) {}
            try { oldModel.modified?.dispose(); } catch (_) {}
        }
    }

    _updateDiffStats(diffEditor) {
        const changes = diffEditor.getLineChanges();
        if (!changes) return;

        let added = 0, removed = 0;
        changes.forEach(change => {
            if (change.modifiedEndLineNumber >= change.modifiedStartLineNumber) {
                added += change.modifiedEndLineNumber - change.modifiedStartLineNumber + 1;
            }
            if (change.originalEndLineNumber >= change.originalStartLineNumber) {
                removed += change.originalEndLineNumber - change.originalStartLineNumber + 1;
            }
            if (change.originalEndLineNumber === 0) removed -= 1;
            if (change.modifiedEndLineNumber === 0) added -= 1;
        });

        const statsEl = document.getElementById('diff-stats');
        if (statsEl) {
            statsEl.querySelector('.added').textContent = '+' + added;
            statsEl.querySelector('.removed').textContent = '-' + removed;
        }
        const countEl = document.getElementById('diff-change-count');
        if (countEl) {
            countEl.textContent = changes.length + ' change' + (changes.length !== 1 ? 's' : '');
        }
    }

    // ============================================
    // Push to Remote
    // ============================================

    async pushToRemote(sandboxId, sandboxName) {
        const escapedName = this.app.escapeHtml(sandboxName);
        const sandbox = this.app.data.sandboxes.find(s => s.id === sandboxId);
        const branchName = sandbox?.branch || 'sandbox branch';

        this.app.showModal('Push to Remote', `
            <div class="text-center py-3">
                <h5>Push "${escapedName}" to remote?</h5>
                <p class="text-muted small px-4">
                    This will push branch <strong>${this.app.escapeHtml(branchName)}</strong> to the remote repository.
                    You can then create a pull request from your Git hosting provider.
                </p>
            </div>
            <div class="d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="button" class="btn btn-info" id="confirm-push-btn">Push to Remote</button>
            </div>
        `);

        const confirmBtn = document.getElementById('confirm-push-btn');
        if (confirmBtn) {
            confirmBtn.addEventListener('click', async () => {
                confirmBtn.disabled = true;
                confirmBtn.textContent = 'Pushing...';
                try {
                    const response = await this.app.apiCall(`/api/v1/sandboxes/${sandboxId}/push`, 'POST');
                    this.app.closeModal();
                    this.app.showToast('Push Successful',
                        response.message || `Branch pushed to remote`, 'success');
                } catch (error) {
                    this.app.closeModal();
                    this.app.showError(`Push failed: ${error.message}`);
                }
            });
        }
    }

    // ============================================
    // Merge Locally
    // ============================================

    async mergeLocally(sandboxId, sandboxName, sourceBranch) {
        const escapedName = this.app.escapeHtml(sandboxName);
        const branchDisplay = sourceBranch ? this.app.escapeHtml(sourceBranch) : 'source branch';

        this.app.showModal('Merge to Local', `
            <div class="text-center py-3">
                <div class="mb-3 text-info">
                    <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" fill="currentColor" viewBox="0 0 16 16">
                        <path d="M8 15A7 7 0 1 1 8 1a7 7 0 0 1 0 14m0 1A8 8 0 1 0 8 0a8 8 0 0 0 0 16"/>
                        <path d="m8.93 6.588-2.29.287-.082.38.45.083c.294.07.352.176.288.469l-.738 3.468c-.194.897.105 1.319.808 1.319.545 0 .877-.252 1.02-.598l.088-.416c.066-.316.168-.45.469-.5l.451-.083.082-.381-2.29-.287zM8 5.5a1 1 0 1 0 0-2 1 1 0 0 0 0 2"/>
                    </svg>
                </div>
                <h5>Merge "${escapedName}" into local project?</h5>
                <p class="text-muted small px-4">
                    This will merge the sandbox changes into <strong>${branchDisplay}</strong> in your source project.
                    Both the sandbox and source project must have all changes committed.
                </p>
            </div>
            <div class="d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="button" class="btn btn-primary" id="confirm-merge-btn">Merge</button>
            </div>
        `);

        const confirmBtn = document.getElementById('confirm-merge-btn');
        if (confirmBtn) {
            confirmBtn.addEventListener('click', async () => {
                confirmBtn.disabled = true;
                confirmBtn.textContent = 'Merging...';
                try {
                    const response = await this.app.apiCall(`/api/v1/sandboxes/${sandboxId}/merge`, 'POST');
                    this.app.closeModal();
                    this.app.showToast('Merge Successful',
                        response.message || `Sandbox merged successfully`, 'success');
                } catch (error) {
                    this.app.closeModal();
                    this.app.showError(`Merge failed: ${error.message}`);
                }
            });
        }
    }
}
