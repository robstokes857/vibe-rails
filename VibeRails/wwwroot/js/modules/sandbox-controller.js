// Sandbox Controller - Manages sandbox creation, listing, and actions
export class SandboxController {
    constructor(app) {
        this.app = app;
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
                    <button type="button" class="btn btn-secondary" onclick="app.closeModal()">Cancel</button>
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
                <button type="button" class="btn btn-secondary" onclick="app.closeModal()">Cancel</button>
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
}
