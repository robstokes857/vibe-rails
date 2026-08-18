import { escapeHtml, isConfirmDialogOpen } from './utils.js';

const API = '/api/v1/python-scripts';

const STATUS_META = Object.freeze({
    approved: { label: 'Signed', tone: 'success', icon: 'fa-circle-check' },
    modified: { label: 'Changed since signing', tone: 'warning', icon: 'fa-triangle-exclamation' },
    unapproved: { label: 'Not signed', tone: 'neutral', icon: 'fa-circle-minus' }
});

/**
 * "Python scripts" section of the Automation page: single-file scripts from
 * ~/.vibe_rails/scripts, gated by hash pinning. Approving ("signing") always prompts
 * for the PIN — deliberately no unlocked session — and Run is only offered while the
 * server reports the script's hash still matches its approval. The PIN modal posts
 * the PIN straight to the backend and never stores it anywhere client-side.
 */
export class PythonScriptsController {
    constructor(app) {
        this.app = app;
        this.root = null;
        this.state = null;
        this.lastRunByName = new Map();
        this.modal = null;
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
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-python-scripts-action="refresh">
                        <i class="fa-solid fa-rotate-right me-1" aria-hidden="true"></i>Refresh
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
        await this.refresh({ quiet: true });
    }

    unmount() {
        this._closeModal();
        this.root = null;
        this.state = null;
    }

    async refresh({ quiet = false } = {}) {
        try {
            this.state = await this.app.apiCall(API, 'GET', null, { showLoading: false });
            this._render();
        } catch (error) {
            this.state = null;
            this._render();
            if (!quiet) this.app.showError(error?.message || 'Could not load Python scripts.');
        }
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
            list.innerHTML = '<div class="jobs-empty">Could not load Python scripts.</div>';
            return;
        }

        const scripts = this.state.scripts || [];
        if (count) count.textContent = `${scripts.length} ${scripts.length === 1 ? 'script' : 'scripts'}`;
        if (dir) dir.textContent = this.state.scriptsDirectory || '';
        if (pinLabel) pinLabel.textContent = this.state.pinConfigured ? 'Change PIN' : 'Set PIN';

        if (scripts.length === 0) {
            list.innerHTML = `
                <div class="jobs-empty">
                    <strong>No Python scripts yet</strong>
                    <span>Save a <code>.py</code> file into the folder above and it will appear here for signing.</span>
                </div>`;
            return;
        }

        list.innerHTML = scripts.map((script) => this._renderRow(script)).join('');
    }

    _renderRow(script) {
        const meta = STATUS_META[script.status] || STATUS_META.unapproved;
        const name = escapeHtml(script.name);
        const canRun = script.status === 'approved';
        const lastRun = this.lastRunByName.get(script.name);
        const approveLabel = script.status === 'approved' ? 'Re-sign' : 'Sign';
        return `
            <div class="python-script-row" data-python-script="${name}">
                <div class="python-script-main">
                    <span class="python-script-name" title="${name}">${name}</span>
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
                    ${script.status === 'unapproved' ? '' : `
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-python-scripts-action="revoke"
                            data-name="${name}" title="Remove this script's signature">
                        <i class="fa-solid fa-ban" aria-hidden="true"></i>
                    </button>`}
                </div>
                ${lastRun ? `
                <details class="python-script-output" ${lastRun.open ? 'open' : ''}>
                    <summary>Last run: exit ${lastRun.exitCode}${lastRun.timedOut ? ' (timed out)' : ''} · ${Math.round(lastRun.durationMs)} ms</summary>
                    <pre>${escapeHtml(this._combinedOutput(lastRun))}</pre>
                </details>` : ''}
            </div>`;
    }

    _combinedOutput(run) {
        const parts = [];
        if (run.standardOutput?.trim()) parts.push(run.standardOutput.trimEnd());
        if (run.standardError?.trim()) parts.push(`[stderr]\n${run.standardError.trimEnd()}`);
        return parts.join('\n\n') || '(no output)';
    }

    _onClick(event) {
        const button = event.target.closest('[data-python-scripts-action]');
        if (!button || !this.root?.contains(button)) return;
        const action = button.dataset.pythonScriptsAction;
        const name = button.dataset.name;
        if (action === 'refresh') return void this.refresh();
        if (action === 'pin') return void this._openPinSetupModal();
        if (action === 'approve') return void this._approve(name);
        if (action === 'revoke') return void this._revoke(name);
        if (action === 'run') return void this._run(name, button);
    }

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
            this.state = await this.app.apiCall(`${API}/approve`, 'POST', { name, pin },
                { showLoading: false, preferErrorResponseMessage: true });
            this._render();
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
            this.state = await this.app.apiCall(`${API}/revoke`, 'POST', { name, pin },
                { showLoading: false, preferErrorResponseMessage: true });
            this._render();
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
            this.state = await this.app.apiCall(`${API}/pin`, 'POST',
                { currentPin: values.currentPin || null, newPin: values.newPin },
                { showLoading: false, preferErrorResponseMessage: true });
            this._render();
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
     * Minimal password-input modal (window.prompt is banned and silently broken in the
     * VS Code webview). Resolves with the field values, or null on cancel/Escape.
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
                                        <input class="form-control" type="password" id="python-scripts-pin-${index}"
                                               data-pin-field="${escapeHtml(field.key)}" autocomplete="off" required>
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
            requestAnimationFrame(() => layer.querySelector('[data-pin-field]')?.focus());
        });
    }

    _closeModal() {
        this.modal?.close?.();
        this.modal = null;
    }
}
