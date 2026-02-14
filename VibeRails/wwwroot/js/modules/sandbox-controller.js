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
                    <button type="submit" class="btn btn-primary" id="create-sandbox-submit-btn">Create Sandbox</button>
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
            <p>Are you sure you want to delete the sandbox <strong>"${escapedName}"</strong>?</p>
            <p class="text-muted small">This will permanently delete the sandbox directory and all its contents. This cannot be undone.</p>
            <div class="d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" onclick="app.closeModal()">Cancel</button>
                <button type="button" class="btn btn-danger" id="confirm-delete-sandbox-btn">Delete</button>
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
        this.app.showToast('Web UI Terminal',
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
