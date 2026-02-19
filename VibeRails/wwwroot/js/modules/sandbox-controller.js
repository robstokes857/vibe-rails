// Sandbox Controller - Manages sandbox creation, listing, and actions
export class SandboxController {
    constructor(app) {
        this.app = app;
        this._monacoReady = null;
        this._diffEditor = null;
    }

    // Fetch sandboxes from API
    async refreshSandboxes() {
        try {
            const response = await this.app.apiCall('/api/v1/sandboxes', 'GET');
            this.app.data.sandboxes = (response.sandboxes || []).map(sb => ({
                id: sb.id,
                name: sb.name,
                path: sb.path,
                branch: sb.branch,
                sourceBranch: sb.sourceBranch || null,
                commitHash: sb.commitHash,
                remoteUrl: sb.remoteUrl || null,
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
                    this.app.dashboardController.loadDashboard();
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
                    this.app.dashboardController.loadDashboard();
                } catch (error) {
                    this.app.showError(`Failed to delete sandbox: ${error.message}`);
                }
            });
        }
    }

    // Launch terminal into sandbox directory
    async launchInWebUI(sandboxId, sandboxName, cli, environmentName) {
        this.app.showToast('Web Terminal',
            `Launching ${cli} in sandbox "${sandboxName}"...`, 'info');

        const sandbox = this.app.data.sandboxes.find(s => s.id === sandboxId);
        if (!sandbox) {
            this.app.showError('Sandbox not found');
            return;
        }

        const terminalContent = document.querySelector('[data-terminal-content]');
        if (terminalContent) {
            await this.app.terminalController.startTerminalWithOptions({
                cli: cli,
                environmentName: environmentName || null,
                workingDirectory: sandbox.path,
                title: `Sandbox: ${sandboxName}`
            }, terminalContent);
        }
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

    _ensureMonaco() {
        if (this._monacoReady) return this._monacoReady;

        this._monacoReady = new Promise((resolve) => {
            if (typeof require === 'undefined' || !require.config) {
                console.error('Monaco loader not found');
                resolve(null);
                return;
            }

            require.config({ paths: { vs: 'assets/monaco/vs' } });

            require(['vs/editor/editor.main'], function () {
                // Define custom theme once
                monaco.editor.defineTheme('viberails-dark', {
                    base: 'vs-dark',
                    inherit: true,
                    rules: [
                        { token: 'comment', foreground: '6A6A7D', fontStyle: 'italic' },
                        { token: 'keyword', foreground: 'C586C0' },
                        { token: 'string', foreground: '9AC6C5' },
                        { token: 'number', foreground: 'B5CEA8' },
                        { token: 'type', foreground: '4EC9B0' },
                        { token: 'function', foreground: 'DCDCAA' },
                        { token: 'variable', foreground: '9CDCFE' },
                        { token: 'constant', foreground: '569CD6' },
                    ],
                    colors: {
                        'editor.background': '#1a1a22',
                        'editor.foreground': '#f0f0f5',
                        'editor.lineHighlightBackground': '#2b2b3640',
                        'editor.selectionBackground': '#5b2a8650',
                        'editorCursor.foreground': '#9ac6c5',
                        'editor.inactiveSelectionBackground': '#3e3e4a40',
                        'editorLineNumber.foreground': '#6A6A7D',
                        'editorLineNumber.activeForeground': '#9ac6c5',
                        'editorGutter.background': '#1a1a22',
                        'editorWidget.background': '#2b2b36',
                        'editorWidget.border': '#3e3e4a',
                        'input.background': '#1e1e24',
                        'input.border': '#3e3e4a',
                        'dropdown.background': '#2b2b36',
                        'dropdown.border': '#3e3e4a',
                        'list.hoverBackground': '#32323f',
                        'list.activeSelectionBackground': '#5b2a86',
                        'minimap.background': '#1a1a22',
                        'scrollbar.shadow': '#00000033',
                        'scrollbarSlider.background': '#3e3e4a80',
                        'scrollbarSlider.hoverBackground': '#7785ac80',
                        'scrollbarSlider.activeBackground': '#9ac6c580',
                        'diffEditor.insertedTextBackground': '#4caf5020',
                        'diffEditor.removedTextBackground': '#e5737320',
                        'diffEditor.insertedLineBackground': '#4caf5015',
                        'diffEditor.removedLineBackground': '#e5737315',
                    }
                });
                resolve(monaco);
            });
        });

        return this._monacoReady;
    }

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
            const [monacoInstance, diffData] = await Promise.all([
                this._ensureMonaco(),
                this.app.apiCall(`/api/v1/sandboxes/${sandboxId}/diff`, 'GET')
            ]);

            if (!monacoInstance) {
                this.app.closeModal();
                this.app.showError('Failed to load Monaco Editor');
                return;
            }

            const files = diffData.files || [];

            if (files.length === 0) {
                modalContainer.querySelector('.sandbox-diff-empty').textContent = 'No changes detected in this sandbox.';
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

        // Bind close button (CSP-safe, this modal isn't created via showModal)
        modalContainer.querySelectorAll('[data-action="close-modal"]')
            .forEach(btn => btn.addEventListener('click', () => this.app.closeModal()));

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
        const originalModel = monacoInstance.editor.createModel(file.originalContent || '', file.language || 'plaintext');
        const modifiedModel = monacoInstance.editor.createModel(file.modifiedContent || '', file.language || 'plaintext');
        diffEditor.setModel({ original: originalModel, modified: modifiedModel });
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
                <div class="mb-3 text-warning">
                    <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" fill="currentColor" viewBox="0 0 16 16">
                        <path d="M8.982 1.566a1.13 1.13 0 0 0-1.96 0L.165 13.233c-.457.778.091 1.767.98 1.767h13.713c.889 0 1.438-.99.98-1.767zM8 5c.535 0 .954.462.9.995l-.35 3.507a.552.552 0 0 1-1.1 0L7.1 5.995A.905.905 0 0 1 8 5m.002 6a1 1 0 1 1 0 2 1 1 0 0 1 0-2"/>
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
                <button type="button" class="btn btn-warning" id="confirm-merge-btn">Merge</button>
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
